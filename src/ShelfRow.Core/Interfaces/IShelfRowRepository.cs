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
    /// <summary>
    /// Loads the complete item identity set and shelf memberships used to merge a
    /// Stackroom import. This deliberately avoids paging and the N+1 membership
    /// queries that would result from calling <see cref="GetItemByIdAsync"/>.
    /// </summary>
    Task<IReadOnlyList<Item>> GetItemsForImportMergeAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Item>> GetItemsAsync(int skip = 0, int take = 100, Guid? shelfId = null, string? search = null, CancellationToken cancellationToken = default);
    Task<int> GetItemCountAsync(Guid? shelfId = null, string? search = null, CancellationToken cancellationToken = default);
    /// <param name="markPendingUpload">
    /// True for an edit made here, which has to reach iCloud; false when the record
    /// came from iCloud in the first place.
    /// </param>
    Task UpsertItemAsync(Item item, bool markPendingUpload = true, CancellationToken cancellationToken = default);
    Task UpsertItemsBatchAsync(IEnumerable<Item> items, bool markPendingUpload = true, CancellationToken cancellationToken = default);
    Task DeleteItemAsync(Guid id, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Guid>> GetAllItemIdsAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<LocalCoverState>> GetLocalCoverStatesAsync(CancellationToken cancellationToken = default);
    Task<LocalCoverState?> GetLocalCoverStateAsync(Guid itemId, CancellationToken cancellationToken = default);
    Task UpsertLocalCoverStatesAsync(IEnumerable<LocalCoverState> states, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Shelf>> GetShelvesAsync(CancellationToken cancellationToken = default);
    Task UpsertShelfAsync(Shelf shelf, bool markPendingUpload = true, CancellationToken cancellationToken = default);
    Task DeleteShelfAsync(Guid id, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Volume>> GetVolumesAsync(CancellationToken cancellationToken = default);
    Task<Volume?> GetVolumeByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task UpsertVolumeAsync(Volume volume, bool markPendingUpload = true, CancellationToken cancellationToken = default);

    Task<string?> GetSyncMetadataAsync(string key, CancellationToken cancellationToken = default);
    Task SetSyncMetadataAsync(string key, string value, CancellationToken cancellationToken = default);
    Task DeleteSyncMetadataAsync(string key, CancellationToken cancellationToken = default);

    Task<int> ApplyItemShelfLinksAsync(IEnumerable<ItemShelfLink> links, CancellationToken cancellationToken = default);

    Task<int> ResolveVolumeReferencesAsync(CancellationToken cancellationToken = default);

    Task DeleteByCloudKitRecordNameAsync(string recordName, CancellationToken cancellationToken = default);

    Task<PendingUploads> GetPendingUploadsAsync(int limit = 200, CancellationToken cancellationToken = default);

    Task ConfirmUploadedAsync(string table, Guid id, string recordName, string? changeTag, CancellationToken cancellationToken = default);
    Task ConfirmItemShelfUploadedAsync(Guid itemId, Guid shelfId, string recordName, bool wasDelete, CancellationToken cancellationToken = default);
    Task ConfirmDeletionUploadedAsync(string recordName, CancellationToken cancellationToken = default);

    Task MarkAllPendingUploadAsync(CancellationToken cancellationToken = default);
    Task<string> PrepareCloudSyncAsync(bool replaceLocalLibrary, string environment, CancellationToken cancellationToken = default);
}
