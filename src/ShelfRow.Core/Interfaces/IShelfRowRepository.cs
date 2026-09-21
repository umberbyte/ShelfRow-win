using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ShelfRow.Core.Models;

namespace ShelfRow.Core.Interfaces;

public interface IShelfRowRepository
{
    Task InitializeAsync(CancellationToken cancellationToken = default);

    Task<Item?> GetItemByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task<Item?> GetItemByLegacyIdAsync(int legacyId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Item>> GetItemsAsync(int skip = 0, int take = 100, Guid? shelfId = null, string? search = null, CancellationToken cancellationToken = default);
    Task<int> GetItemCountAsync(Guid? shelfId = null, string? search = null, CancellationToken cancellationToken = default);
    Task UpsertItemAsync(Item item, CancellationToken cancellationToken = default);
    Task UpsertItemsBatchAsync(IEnumerable<Item> items, CancellationToken cancellationToken = default);
    Task DeleteItemAsync(Guid id, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Shelf>> GetShelvesAsync(CancellationToken cancellationToken = default);
    Task UpsertShelfAsync(Shelf shelf, CancellationToken cancellationToken = default);
    Task DeleteShelfAsync(Guid id, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Volume>> GetVolumesAsync(CancellationToken cancellationToken = default);
    Task<Volume?> GetVolumeByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task UpsertVolumeAsync(Volume volume, CancellationToken cancellationToken = default);

    Task<string?> GetSyncMetadataAsync(string key, CancellationToken cancellationToken = default);
    Task SetSyncMetadataAsync(string key, string value, CancellationToken cancellationToken = default);

    Task<int> ApplyItemShelfLinksAsync(IEnumerable<ItemShelfLink> links, CancellationToken cancellationToken = default);

    Task<int> ResolveVolumeReferencesAsync(CancellationToken cancellationToken = default);

    Task DeleteByCloudKitRecordNameAsync(string recordName, CancellationToken cancellationToken = default);
}
