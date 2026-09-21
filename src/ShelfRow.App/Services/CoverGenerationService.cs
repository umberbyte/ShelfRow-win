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
    public sealed record CoverCandidates(string ArchivePath, IReadOnlyList<string> Entries);
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

    public async Task<bool> GenerateAsync(Item item, CancellationToken cancellationToken = default)
    {
        var job = _jobs.GetOrAdd(item.Id, _ => new Lazy<Task<bool>>(
            () => GenerateLimitedAsync(item, cancellationToken),
            LazyThreadSafetyMode.ExecutionAndPublication));
        try
        {
            return await job.Value;
        }
        finally
        {
            // Failed, cancelled and unsupported jobs must be retryable. Remove only
            // this Lazy so a later job cannot be removed by another waiter finishing.
            if (_jobs.TryGetValue(item.Id, out var current) && ReferenceEquals(current, job))
                _jobs.TryRemove(item.Id, out _);
        }
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

        return await RegisterGeneratedCoverAsync(item, volume, cancellationToken);
    }

    public async Task<CoverCandidates?> GetCandidatesAsync(Item item, CancellationToken cancellationToken = default)
    {
        Volume? volume = item.VolumeId.HasValue
            ? await _repository.GetVolumeByIdAsync(item.VolumeId.Value, cancellationToken)
            : null;
        string archivePath = _pathResolver.ResolveToWindowsPath(item, volume);
        string extension = Path.GetExtension(archivePath);
        if (!extension.Equals(".zip", StringComparison.OrdinalIgnoreCase)
            && !extension.Equals(".cbz", StringComparison.OrdinalIgnoreCase))
            return null;
        var entries = await _extractor.ListImagesAsync(archivePath, cancellationToken);
        return entries.Count == 0 ? null : new CoverCandidates(archivePath, entries);
    }

    public Task<byte[]?> ReadCandidateAsync(CoverCandidates candidates, string entry, CancellationToken cancellationToken = default) =>
        _extractor.ReadImageAsync(candidates.ArchivePath, entry, cancellationToken);

    public async Task<bool> ReplaceAsync(Item item, CoverCandidates candidates, string entry, CancellationToken cancellationToken = default)
    {
        string localPath = _thumbnails.GetLocalThumbnailPath(item.Id);
        if (!await _extractor.ExtractImageAsync(candidates.ArchivePath, entry, localPath, cancellationToken))
            return false;
        Volume? volume = item.VolumeId.HasValue
            ? await _repository.GetVolumeByIdAsync(item.VolumeId.Value, cancellationToken)
            : null;
        return await RegisterGeneratedCoverAsync(item, volume, cancellationToken);
    }

    private async Task<bool> RegisterGeneratedCoverAsync(Item item, Volume? volume, CancellationToken cancellationToken)
    {
        string localPath = _thumbnails.GetLocalThumbnailPath(item.Id);

        long bytes = new FileInfo(localPath).Length;
        string? root = _configuredRoot();
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            root = ThumbnailStorageManager.FindDistributionRoot(volume?.WindowsMountPath);

        var previous = await _repository.GetLocalCoverStateAsync(item.Id, cancellationToken);
        var state = new LocalCoverState
        {
            ItemId = item.Id,
            Version = previous?.Version ?? 0,
            Bytes = bytes,
            UpdatedAt = DateTime.UtcNow,
            PendingUpload = true,
            Attempts = 0,
            AttemptedVersion = previous?.AttemptedVersion ?? 0,
            LastErrorCode = 0
        };

        // Persist the retry marker before touching the NAS. A share can disappear
        // after Directory.Exists succeeds, and that must not strand the new cover.
        // The distribution manifest, not Item.CoverVersion, owns NAS revisions;
        // changing the Item here would create needless CloudKit traffic.
        await _repository.UpsertLocalCoverStatesAsync(new[] { state }, cancellationToken);

        if (!string.IsNullOrWhiteSpace(root))
        {
            await _publishLock.WaitAsync(cancellationToken);
            try
            {
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
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // The local cover is complete. Keep PendingUpload so the next
                    // maintenance sync retries after the share is reachable again.
                }
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
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
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
