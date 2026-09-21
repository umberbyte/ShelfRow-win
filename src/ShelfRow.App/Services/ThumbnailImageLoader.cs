using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media.Imaging;
using ShelfRow.Core.Interfaces;
using ShelfRow.Core.Models;
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
    private readonly IShelfRowRepository? _repository;
    private readonly Func<string?>? _distributionRootProvider;

    // In-memory cache capped with an LRU strategy (default 500 images)
    private readonly int _maxMemoryCacheCount;
    private readonly Dictionary<Guid, MemoryCacheEntry> _memoryCache = new();
    private readonly LinkedList<Guid> _lruOrder = new();
    private readonly object _cacheLock = new();

    // Deduplicate concurrent load requests for the same Item ID
    private readonly ConcurrentDictionary<(Guid ItemId, int Version), Lazy<Task<BitmapImage?>>> _inFlightTasks = new();
    private readonly SemaphoreSlim _manifestLock = new(1, 1);
    private string? _manifestRoot;
    private string? _activeDistributionRoot;
    private ThumbnailDistributionManifest? _manifest;
    private DateTime _manifestReadAt;

    private sealed record MemoryCacheEntry(int Version, BitmapImage Image);
    private sealed record CoverTarget(int Version, long Bytes);

    public ThumbnailImageLoader(
        ThumbnailStorageManager storageManager,
        DispatcherQueue? dispatcherQueue = null,
        int maxMemoryCacheCount = 500,
        IShelfRowRepository? repository = null,
        Func<string?>? distributionRootProvider = null)
    {
        _storageManager = storageManager;
        _dispatcherQueue = dispatcherQueue ?? DispatcherQueue.GetForCurrentThread();
        _maxMemoryCacheCount = maxMemoryCacheCount;
        _repository = repository;
        _distributionRootProvider = distributionRootProvider;
    }

    /// <summary>
    /// Loads thumbnail image asynchronously through Memory -> Local Disk -> NAS Distribution.
    /// </summary>
    public async Task<BitmapImage?> LoadThumbnailAsync(Guid itemId, string? nasDistributionRoot = null, CancellationToken cancellationToken = default)
    {
        string? root = string.IsNullOrWhiteSpace(nasDistributionRoot)
            ? _activeDistributionRoot ?? _distributionRootProvider?.Invoke()
            : nasDistributionRoot;
        var target = await GetCoverTargetAsync(root, itemId, cancellationToken);
        int targetVersion = target?.Version ?? 0;

        // 1. Memory Cache check (instant, 0ms)
        lock (_cacheLock)
        {
            if (_memoryCache.TryGetValue(itemId, out var cached))
            {
                if (target is null || cached.Version == targetVersion)
                {
                    _lruOrder.Remove(itemId);
                    _lruOrder.AddFirst(itemId);
                    return cached.Image;
                }
                _memoryCache.Remove(itemId);
                _lruOrder.Remove(itemId);
            }
        }

        // Lazy is required here: ConcurrentDictionary may invoke a value factory
        // more than once, but only the winning Lazy is allowed to start I/O.
        var key = (itemId, targetVersion);
        var lazy = _inFlightTasks.GetOrAdd(key, _ => new Lazy<Task<BitmapImage?>>(
            () => LoadFromStorageTiersAsync(itemId, root, target, cancellationToken),
            LazyThreadSafetyMode.ExecutionAndPublication));
        try
        {
            return await lazy.Value;
        }
        finally
        {
            _inFlightTasks.TryRemove(key, out _);
        }
    }

    private async Task<BitmapImage?> LoadFromStorageTiersAsync(
        Guid itemId,
        string? nasDistributionRoot,
        CoverTarget? target,
        CancellationToken cancellationToken)
    {
        string localPath = _storageManager.GetLocalThumbnailPath(itemId);

        if (target is not null && !string.IsNullOrWhiteSpace(nasDistributionRoot))
        {
            var state = _repository is null
                ? null
                : await _repository.GetLocalCoverStateAsync(itemId, cancellationToken);
            long localBytes = File.Exists(localPath) ? new FileInfo(localPath).Length : 0;
            bool sizeMatches = localBytes > 0 && (target.Bytes <= 0 || localBytes == target.Bytes);

            if (state?.Version != target.Version || !sizeMatches)
            {
                if (sizeMatches)
                {
                    await RecordSuccessAsync(itemId, target.Version, localBytes, cancellationToken);
                }
                else
                {
                    if (state is not null
                        && state.AttemptedVersion == target.Version
                        && state.Attempts >= 3
                        && state.LastErrorCode != 0)
                        return null;

                    try
                    {
                        bool copied = await _storageManager.CopyFromNasDistributionAsync(nasDistributionRoot, itemId, cancellationToken);
                        long copiedBytes = copied && File.Exists(localPath) ? new FileInfo(localPath).Length : 0;
                        if (!copied || copiedBytes == 0 || (target.Bytes > 0 && copiedBytes != target.Bytes))
                        {
                            await RecordFailureAsync(state, itemId, target.Version, copied ? 13 : 2, cancellationToken);
                            return null;
                        }
                        await RecordSuccessAsync(itemId, target.Version, copiedBytes, cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        await RecordFailureAsync(state, itemId, target.Version, ex.HResult, cancellationToken);
                        return null;
                    }
                }
            }
        }

        // Tier 2: Check Local Disk Cache
        if (!File.Exists(localPath))
        {
            // Tier 3: Fetch from NAS Distribution Folder if available
            if (target is not null && !string.IsNullOrWhiteSpace(nasDistributionRoot))
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

        bool queued = _dispatcherQueue.TryEnqueue(async () =>
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

                        _memoryCache[itemId] = new MemoryCacheEntry(target?.Version ?? 0, bitmap);
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

        if (!queued)
            tcs.TrySetException(new InvalidOperationException("The UI dispatcher rejected thumbnail decoding."));

        return await tcs.Task;
    }

    private async Task<CoverTarget?> GetCoverTargetAsync(string? root, Guid itemId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            return null;

        await _manifestLock.WaitAsync(cancellationToken);
        try
        {
            if (!string.Equals(_manifestRoot, root, StringComparison.OrdinalIgnoreCase)
                || DateTime.UtcNow - _manifestReadAt > TimeSpan.FromSeconds(30))
            {
                _manifest = await _storageManager.ReadManifestAsync(root, cancellationToken);
                _manifestRoot = root;
                _manifestReadAt = DateTime.UtcNow;
            }
            var entry = _manifest?.GetEntry(itemId);
            return entry is null ? null : new CoverTarget(entry.Version, entry.Bytes);
        }
        finally
        {
            _manifestLock.Release();
        }
    }

    private Task RecordSuccessAsync(Guid itemId, int version, long bytes, CancellationToken cancellationToken)
    {
        if (_repository is null)
            return Task.CompletedTask;
        return _repository.UpsertLocalCoverStatesAsync(new[]
        {
            new LocalCoverState
            {
                ItemId = itemId,
                Version = version,
                Bytes = bytes,
                UpdatedAt = DateTime.UtcNow,
                Attempts = 0,
                AttemptedVersion = version,
                LastErrorCode = 0
            }
        }, cancellationToken);
    }

    private Task RecordFailureAsync(LocalCoverState? previous, Guid itemId, int version, int errorCode, CancellationToken cancellationToken)
    {
        if (_repository is null)
            return Task.CompletedTask;
        return _repository.UpsertLocalCoverStatesAsync(new[]
        {
            new LocalCoverState
            {
                ItemId = itemId,
                Version = previous?.Version ?? 0,
                Bytes = previous?.Bytes ?? 0,
                UpdatedAt = DateTime.UtcNow,
                PendingUpload = previous?.PendingUpload ?? false,
                Attempts = previous?.AttemptedVersion == version ? previous.Attempts + 1 : 1,
                AttemptedVersion = version,
                LastErrorCode = errorCode == 0 ? -1 : errorCode
            }
        }, cancellationToken);
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

    public void InvalidateManifest()
    {
        _manifestReadAt = DateTime.MinValue;
    }

    public void SetDistributionRoot(string? root)
    {
        if (string.Equals(_activeDistributionRoot, root, StringComparison.OrdinalIgnoreCase))
            return;
        _activeDistributionRoot = root;
        _manifestReadAt = DateTime.MinValue;
    }
}
