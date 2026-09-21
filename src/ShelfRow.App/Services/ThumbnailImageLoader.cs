using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media.Imaging;
using ShelfRow.Storage;
using Windows.Storage.Streams;

namespace ShelfRow.App.Services;

/// <summary>
/// High-performance asynchronous thumbnail image loader with multi-tier caching (Memory -> Disk -> NAS).
/// Designed for low memory consumption with DecodePixelWidth and UI-thread non-blocking loading.
/// </summary>
public class ThumbnailImageLoader
{
    private readonly ThumbnailStorageManager _storageManager;
    private readonly DispatcherQueue _dispatcherQueue;

    // In-memory cache capped with an LRU strategy (default 500 images)
    private readonly int _maxMemoryCacheCount;
    private readonly Dictionary<Guid, BitmapImage> _memoryCache = new();
    private readonly LinkedList<Guid> _lruOrder = new();
    private readonly object _cacheLock = new();

    // Deduplicate concurrent load requests for the same Item ID
    private readonly ConcurrentDictionary<Guid, Task<BitmapImage?>> _inFlightTasks = new();

    public ThumbnailImageLoader(
        ThumbnailStorageManager storageManager,
        DispatcherQueue? dispatcherQueue = null,
        int maxMemoryCacheCount = 500)
    {
        _storageManager = storageManager;
        _dispatcherQueue = dispatcherQueue ?? DispatcherQueue.GetForCurrentThread();
        _maxMemoryCacheCount = maxMemoryCacheCount;
    }

    /// <summary>
    /// Loads thumbnail image asynchronously through Memory -> Local Disk -> NAS Distribution.
    /// </summary>
    public async Task<BitmapImage?> LoadThumbnailAsync(Guid itemId, string? nasDistributionRoot = null, CancellationToken cancellationToken = default)
    {
        // 1. Memory Cache check (instant, 0ms)
        lock (_cacheLock)
        {
            if (_memoryCache.TryGetValue(itemId, out var cached))
            {
                _lruOrder.Remove(itemId);
                _lruOrder.AddFirst(itemId);
                return cached;
            }
        }

        // 2. Deduplicate in-flight loads
        return await _inFlightTasks.GetOrAdd(itemId, async id =>
        {
            try
            {
                return await LoadFromStorageTiersAsync(id, nasDistributionRoot, cancellationToken);
            }
            finally
            {
                _inFlightTasks.TryRemove(id, out _);
            }
        });
    }

    private async Task<BitmapImage?> LoadFromStorageTiersAsync(Guid itemId, string? nasDistributionRoot, CancellationToken cancellationToken)
    {
        string localPath = _storageManager.GetLocalThumbnailPath(itemId);

        // Tier 2: Check Local Disk Cache
        if (!File.Exists(localPath))
        {
            // Tier 3: Fetch from NAS Distribution Folder if available
            if (!string.IsNullOrWhiteSpace(nasDistributionRoot))
            {
                bool copied = await _storageManager.CopyFromNasDistributionAsync(nasDistributionRoot, itemId, cancellationToken);
                if (!copied)
                    return null;
            }
            else
            {
                return null;
            }
        }

        // Read bytes asynchronously on background thread to keep UI completely stutter-free
        byte[] imageBytes = await File.ReadAllBytesAsync(localPath, cancellationToken);
        if (imageBytes == null || imageBytes.Length == 0)
            return null;

        // Decode into BitmapImage on UI thread with DecodePixelWidth restriction to save memory
        var tcs = new TaskCompletionSource<BitmapImage?>();

        _dispatcherQueue.TryEnqueue(async () =>
        {
            try
            {
                var bitmap = new BitmapImage();
                bitmap.DecodePixelWidth = 320; // High quality on screen while keeping VRAM < 150KB per cover

                using var stream = new InMemoryRandomAccessStream();
                using var writer = new DataWriter(stream);
                writer.WriteBytes(imageBytes);
                await writer.StoreAsync();
                stream.Seek(0);

                await bitmap.SetSourceAsync(stream);

                // Store in memory cache
                lock (_cacheLock)
                {
                    if (!_memoryCache.ContainsKey(itemId))
                    {
                        if (_memoryCache.Count >= _maxMemoryCacheCount && _lruOrder.Last != null)
                        {
                            var oldestId = _lruOrder.Last.Value;
                            _lruOrder.RemoveLast();
                            _memoryCache.Remove(oldestId);
                        }

                        _memoryCache[itemId] = bitmap;
                        _lruOrder.AddFirst(itemId);
                    }
                }

                tcs.SetResult(bitmap);
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        });

        return await tcs.Task;
    }

    /// <summary>
    /// Evicts all in-memory thumbnail images.
    /// </summary>
    public void ClearMemoryCache()
    {
        lock (_cacheLock)
        {
            _memoryCache.Clear();
            _lruOrder.Clear();
        }
    }
}
