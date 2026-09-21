using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ShelfRow.Core.Interfaces;
using ShelfRow.Core.Models;

namespace ShelfRow.CloudKit;

public class CloudKitSyncEngine
{
    public const string SyncTokenKey = "CloudKit_SyncToken";
    private readonly CloudKitClient _client;
    private readonly IShelfRowRepository _repository;

    public CloudKitSyncEngine(CloudKitClient client, IShelfRowRepository repository)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
    }

    public record SyncResult(int Items, int Shelves, int Volumes, int Links, int Deletions);

    public record UploadResult(int Uploaded, int Conflicted, int Failed);

    /// <summary>
    /// CloudKit rejects a modify request carrying more operations than this.
    /// </summary>
    private const int ModifyBatchSize = 200;

    /// <summary>
    /// Sends rows edited on this machine. Each row quotes the record version it was last
    /// seen at, so a record another device changed in the meantime is refused rather than
    /// overwritten; it stays queued and the next download brings the newer copy.
    /// </summary>
    public async Task<UploadResult> SyncUpAsync(IProgress<int>? progress = null, CancellationToken cancellationToken = default)
    {
        int uploaded = 0, conflicted = 0, failed = 0;

        while (true)
        {
            var pending = await _repository.GetPendingUploadsAsync(ModifyBatchSize, cancellationToken);
            if (pending.Count == 0)
                break;

            var owners = new Dictionary<string, (string Table, Guid Id)>(StringComparer.Ordinal);
            var linkOwners = new Dictionary<string, PendingItemShelfChange>(StringComparer.Ordinal);
            var deletionOwners = new HashSet<string>(StringComparer.Ordinal);
            var request = new CKModifyRecordsRequest();

            foreach (var deletion in pending.Deletions)
            {
                if (request.Operations.Count >= ModifyBatchSize) break;
                request.Operations.Add(new CKRecordOperation
                {
                    // The deletion queue intentionally retains only recordName after
                    // the local row is gone. CloudKit's ordinary delete requires a
                    // recordChangeTag; forceDelete is the tag-free equivalent.
                    OperationType = "forceDelete",
                    Record = new CKDeleteRecord { RecordName = deletion.RecordName }
                });
                deletionOwners.Add(deletion.RecordName);
            }

            foreach (var item in pending.Items)
            {
                if (request.Operations.Count >= ModifyBatchSize) break;
                AddOperation(request, owners, CloudKitMapper.ToCKRecord(item), "Items", item.Id);
            }

            foreach (var shelf in pending.Shelves)
            {
                if (request.Operations.Count >= ModifyBatchSize) break;
                AddOperation(request, owners, CloudKitMapper.ToCKRecord(shelf), "Shelves", shelf.Id);
            }

            foreach (var volume in pending.Volumes)
            {
                if (request.Operations.Count >= ModifyBatchSize) break;
                AddOperation(request, owners, CloudKitMapper.ToCKRecord(volume), "Volumes", volume.Id);
            }

            foreach (var link in pending.ItemShelfChanges)
            {
                if (request.Operations.Count >= ModifyBatchSize) break;
                var record = CloudKitMapper.ToCKRecord(link);
                request.Operations.Add(new CKRecordOperation
                {
                    OperationType = link.IsDelete ? "forceDelete" : "create",
                    Record = link.IsDelete
                        ? new CKDeleteRecord { RecordName = record.RecordName }
                        : record
                });
                linkOwners[record.RecordName] = link;
            }

            var response = await _client.ModifyRecordsAsync(request, cancellationToken);

            foreach (var record in response.Records ?? new List<CKRecord>())
            {
                if (!owners.TryGetValue(record.RecordName, out var owner)
                    && !linkOwners.ContainsKey(record.RecordName)
                    && !deletionOwners.Contains(record.RecordName))
                    continue;

                if (record.ServerErrorCode != null)
                {
                    if (record.ServerErrorCode == "CONFLICT")
                        conflicted++;
                    else
                        failed++;
                    continue;
                }

                if (deletionOwners.Contains(record.RecordName))
                {
                    await _repository.ConfirmDeletionUploadedAsync(record.RecordName, cancellationToken);
                }
                else if (linkOwners.TryGetValue(record.RecordName, out var linkOwner))
                {
                    await _repository.ConfirmItemShelfUploadedAsync(
                        linkOwner.ItemId, linkOwner.ShelfId, record.RecordName,
                        linkOwner.IsDelete, cancellationToken);
                }
                else
                {
                    await _repository.ConfirmUploadedAsync(owner.Table, owner.Id, record.RecordName, record.RecordChangeTag, cancellationToken);
                }
                uploaded++;
            }

            progress?.Report(uploaded);

            // Everything refused stays flagged, so without this the same batch would be
            // retried forever.
            if (uploaded == 0)
                break;
        }

        return new UploadResult(uploaded, conflicted, failed);
    }

    private static void AddOperation(
        CKModifyRecordsRequest request,
        Dictionary<string, (string Table, Guid Id)> owners,
        CKRecord record,
        string table,
        Guid id)
    {
        request.Operations.Add(new CKRecordOperation
        {
            OperationType = record.RecordChangeTag == null ? "create" : "update",
            Record = record
        });

        owners[record.RecordName] = (table, id);
    }

    /// <summary>
    /// Pulls every change since the stored sync token. Shelf membership and volume
    /// references are resolved after the records land, because the zone delivers them as
    /// CloudKit record names and gives no guarantee that a target arrives before the
    /// record pointing at it.
    /// </summary>
    public async Task<SyncResult> SyncDownAsync(IProgress<int>? progress = null, CancellationToken cancellationToken = default)
    {
        int items = 0, shelves = 0, volumes = 0, deletions = 0, processed = 0;
        var links = new List<ItemShelfLink>();

        bool moreComing = true;
        string? syncToken = await _repository.GetSyncMetadataAsync(SyncTokenKey, cancellationToken);

        while (moreComing)
        {
            var response = await _client.FetchZoneChangesAsync(syncToken, cancellationToken: cancellationToken);
            if (response.Zones == null || response.Zones.Count == 0)
                break;

            var zone = response.Zones[0];
            syncToken = zone.SyncToken;
            moreComing = zone.MoreComing;

            if (zone.Records != null)
            {
                var itemsBatch = new List<Item>();

                foreach (var record in zone.Records)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (record.Deleted)
                    {
                        await _repository.DeleteByCloudKitRecordNameAsync(record.RecordName, cancellationToken);
                        deletions++;
                        processed++;
                        continue;
                    }

                    switch (record.RecordType)
                    {
                        case CloudKitMapper.ItemRecordType:
                            if (CloudKitMapper.ToItem(record) is { } item)
                            {
                                itemsBatch.Add(item);
                                items++;
                            }
                            break;

                        case CloudKitMapper.ShelfRecordType:
                            if (CloudKitMapper.ToShelf(record) is { } shelf)
                            {
                                await _repository.UpsertShelfAsync(shelf, markPendingUpload: false, cancellationToken);
                                shelves++;
                            }
                            break;

                        case CloudKitMapper.VolumeRecordType:
                            if (CloudKitMapper.ToVolume(record) is { } volume)
                            {
                                await _repository.UpsertVolumeAsync(volume, markPendingUpload: false, cancellationToken);
                                volumes++;
                            }
                            break;

                        case CloudKitMapper.ManyToManyRecordType:
                            if (CloudKitMapper.ToManyToManyLink(record) is { } link &&
                                link.LeftEntity == "Item" && link.RightEntity == "Shelf")
                            {
                                links.Add(new ItemShelfLink(record.RecordName, link.LeftRecordName, link.RightRecordName));
                            }
                            break;
                    }

                    processed++;
                }

                if (itemsBatch.Count > 0)
                    await _repository.UpsertItemsBatchAsync(itemsBatch, markPendingUpload: false, cancellationToken);

                progress?.Report(processed);
            }

        }

        await _repository.ResolveVolumeReferencesAsync(cancellationToken);

        if (links.Count > 0)
            await _repository.ApplyItemShelfLinksAsync(links, cancellationToken);

        // The token is the commit point. Saving it per page could permanently skip
        // CDMR links whose target arrived on a later page, or any final relationship
        // work interrupted after the page token was stored.
        if (!string.IsNullOrEmpty(syncToken))
            await _repository.SetSyncMetadataAsync(SyncTokenKey, syncToken, cancellationToken);

        return new SyncResult(items, shelves, volumes, links.Count, deletions);
    }
}
