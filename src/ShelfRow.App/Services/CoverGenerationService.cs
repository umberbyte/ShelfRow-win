using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ShelfRow.Core.Interfaces;
using ShelfRow.Core.Models;
using ShelfRow.Storage;

namespace ShelfRow.App.Services;

/// <summary>Generates a missing cover locally and publishes it when the NAS is available.</summary>
public sealed class CoverGenerationService
{
    private readonly IShelfRowRepository _repository;
    private readonly ThumbnailStorageManager _thumbnails;
    private readonly ZipCoverExtractor _extractor = new();
    private readonly VolumePathResolver _pathResolver = new();
    private readonly Func<string?> _configuredRoot;
    private readonly ConcurrentDictionary<Guid, Lazy<Task<bool>>> _jobs = new();
    private readonly SemaphoreSlim _generationSlots = new(2, 2);
    private readonly SemaphoreSlim _publishLock = new(1, 1);

    public CoverGenerationService(
        IShelfRowRepository repository,
        ThumbnailStorageManager thumbnails,
        Func<string?> configuredRoot)
    {
        _repository = repository;
        _thumbnails = thumbnails;
        _configuredRoot = configuredRoot;
    }

    public Task<bool> GenerateAsync(Item item, CancellationToken cancellationToken = default)
    {
        return _jobs.GetOrAdd(item.Id, _ => new Lazy<Task<bool>>(
            () => GenerateLimitedAsync(item, cancellationToken),
            LazyThreadSafetyMode.ExecutionAndPublication)).Value;
    }

    private async Task<bool> GenerateLimitedAsync(Item item, CancellationToken cancellationToken)
    {
        await _generationSlots.WaitAsync(cancellationToken);
        try
        {
            return await GenerateCoreAsync(item, cancellationToken);
        }
        finally
        {
            _generationSlots.Release();
        }
    }

    private async Task<bool> GenerateCoreAsync(Item item, CancellationToken cancellationToken)
    {
        string localPath = _thumbnails.GetLocalThumbnailPath(item.Id);
        if (File.Exists(localPath) && new FileInfo(localPath).Length > 0)
            return true;

        Volume? volume = item.VolumeId.HasValue
            ? await _repository.GetVolumeByIdAsync(item.VolumeId.Value, cancellationToken)
            : null;
        string bookPath = _pathResolver.ResolveToWindowsPath(item, volume);
        string extension = Path.GetExtension(bookPath);
        if (!extension.Equals(".zip", StringComparison.OrdinalIgnoreCase)
            && !extension.Equals(".cbz", StringComparison.OrdinalIgnoreCase))
            return false;

        if (!await _extractor.ExtractFirstImageAsync(bookPath, localPath, cancellationToken))
            return false;

        long bytes = new FileInfo(localPath).Length;
        item.CoverVersion = Math.Max(1, item.CoverVersion + 1);
        item.CoverBytes = bytes;
        await _repository.UpsertItemAsync(item, cancellationToken: cancellationToken);

        string? root = _configuredRoot();
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            root = ThumbnailStorageManager.FindDistributionRoot(volume?.WindowsMountPath);

        var state = new LocalCoverState
        {
            ItemId = item.Id,
            Version = item.CoverVersion,
            Bytes = bytes,
            UpdatedAt = DateTime.UtcNow,
            PendingUpload = true
        };

        if (!string.IsNullOrWhiteSpace(root))
        {
            await _publishLock.WaitAsync(cancellationToken);
            try
            {
                await _thumbnails.CopyToNasDistributionAsync(root, item.Id, cancellationToken);
                var manifest = await _thumbnails.ReadManifestAsync(root, cancellationToken)
                               ?? new ThumbnailDistributionManifest();
                int version = (manifest.GetEntry(item.Id)?.Version ?? 0) + 1;
                manifest.SetEntry(item.Id, version, bytes);
                await _thumbnails.WriteManifestAsync(root, manifest, cancellationToken);
                state.Version = version;
                state.PendingUpload = false;
            }
            finally
            {
                _publishLock.Release();
            }
        }

        await _repository.UpsertLocalCoverStatesAsync(new[] { state }, cancellationToken);
        return true;
    }

    public async Task<int> PublishPendingAsync(string root, CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(root))
            return 0;

        await _publishLock.WaitAsync(cancellationToken);
        try
        {
            var states = (await _repository.GetLocalCoverStatesAsync(cancellationToken))
                .Where(state => state.PendingUpload && _thumbnails.HasLocalThumbnail(state.ItemId))
                .ToList();
            if (states.Count == 0)
                return 0;

            var manifest = await _thumbnails.ReadManifestAsync(root, cancellationToken)
                           ?? new ThumbnailDistributionManifest();
            var completed = new List<LocalCoverState>();
            foreach (var state in states)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    await _thumbnails.CopyToNasDistributionAsync(root, state.ItemId, cancellationToken);
                    long bytes = new FileInfo(_thumbnails.GetLocalThumbnailPath(state.ItemId)).Length;
                    int version = (manifest.GetEntry(state.ItemId)?.Version ?? 0) + 1;
                    manifest.SetEntry(state.ItemId, version, bytes);
                    state.Version = version;
                    state.Bytes = bytes;
                    state.PendingUpload = false;
                    state.UpdatedAt = DateTime.UtcNow;
                    completed.Add(state);
                }
                catch (IOException)
                {
                    // A temporarily unavailable NAS is normal; leave it pending.
                }
            }

            if (completed.Count == 0)
                return 0;
            await _thumbnails.WriteManifestAsync(root, manifest, cancellationToken);
            await _repository.UpsertLocalCoverStatesAsync(completed, cancellationToken);
            return completed.Count;
        }
        finally
        {
            _publishLock.Release();
        }
    }
}
