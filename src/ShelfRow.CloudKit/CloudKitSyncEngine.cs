using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ShelfRow.Core.Interfaces;
using ShelfRow.Core.Models;

namespace ShelfRow.CloudKit;

public class CloudKitSyncEngine
{
    private const string SyncTokenKey = "CloudKit_SyncToken";
    private readonly CloudKitClient _client;
    private readonly IShelfRowRepository _repository;

    public CloudKitSyncEngine(CloudKitClient client, IShelfRowRepository repository)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
    }

    public record SyncResult(int Items, int Shelves, int Volumes, int Links, int Deletions);

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
                                await _repository.UpsertShelfAsync(shelf, cancellationToken);
                                shelves++;
                            }
                            break;

                        case CloudKitMapper.VolumeRecordType:
                            if (CloudKitMapper.ToVolume(record) is { } volume)
                            {
                                await _repository.UpsertVolumeAsync(volume, cancellationToken);
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
                    await _repository.UpsertItemsBatchAsync(itemsBatch, cancellationToken);

                progress?.Report(processed);
            }

            if (!string.IsNullOrEmpty(syncToken))
                await _repository.SetSyncMetadataAsync(SyncTokenKey, syncToken, cancellationToken);
        }

        await _repository.ResolveVolumeReferencesAsync(cancellationToken);

        if (links.Count > 0)
            await _repository.ApplyItemShelfLinksAsync(links, cancellationToken);

        return new SyncResult(items, shelves, volumes, links.Count, deletions);
    }
}
