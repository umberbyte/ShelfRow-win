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

    public async Task<int> SyncDownAsync(CancellationToken cancellationToken = default)
    {
        int recordsProcessed = 0;
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
                    if (record.Deleted)
                    {
                        if (Guid.TryParse(record.RecordName, out var delId))
                        {
                            await _repository.DeleteItemAsync(delId, cancellationToken);
                            recordsProcessed++;
                        }
                        continue;
                    }

                    switch (record.RecordType)
                    {
                        case CloudKitMapper.ItemRecordType:
                            var item = CloudKitMapper.ToItem(record);
                            if (item != null)
                            {
                                itemsBatch.Add(item);
                                recordsProcessed++;
                            }
                            break;

                        case CloudKitMapper.ShelfRecordType:
                            var shelf = CloudKitMapper.ToShelf(record);
                            if (shelf != null)
                            {
                                await _repository.UpsertShelfAsync(shelf, cancellationToken);
                                recordsProcessed++;
                            }
                            break;

                        case CloudKitMapper.VolumeRecordType:
                            var volume = CloudKitMapper.ToVolume(record);
                            if (volume != null)
                            {
                                await _repository.UpsertVolumeAsync(volume, cancellationToken);
                                recordsProcessed++;
                            }
                            break;
                    }
                }

                if (itemsBatch.Count > 0)
                {
                    await _repository.UpsertItemsBatchAsync(itemsBatch, cancellationToken);
                }
            }

            if (!string.IsNullOrEmpty(syncToken))
            {
                await _repository.SetSyncMetadataAsync(SyncTokenKey, syncToken, cancellationToken);
            }
        }

        return recordsProcessed;
    }
}
