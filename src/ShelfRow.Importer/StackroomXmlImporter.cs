using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ShelfRow.Core.Models;
using ShelfRow.Storage;

namespace ShelfRow.Importer;

public record ImportProgress(int ProcessedBooks, int TotalBooks, int ProcessedShelves, int TotalShelves);

public class StackroomImportResult
{
    public List<Item> ImportedBooks { get; } = new();
    public List<Shelf> ImportedShelves { get; } = new();
    public List<Volume> DiscoveredVolumes { get; } = new();
    public List<Item> ItemsWithUpdatedShelfMembership { get; } = new();
    public int SkippedBooks { get; internal set; }
    public int SkippedShelves { get; internal set; }
}

public sealed record StackroomImportMergeContext(
    IReadOnlyCollection<Item> ExistingItems,
    IReadOnlyCollection<Shelf> ExistingShelves,
    IReadOnlyCollection<Volume> ExistingVolumes,
    string? LegacyAssetsRoot = null,
    string? LocalThumbnailDirectory = null)
{
    public static StackroomImportMergeContext Empty { get; } = new(
        Array.Empty<Item>(), Array.Empty<Shelf>(), Array.Empty<Volume>());
}

public class StackroomXmlImporter
{
    public async Task<StackroomImportResult> ImportAsync(
        Stream xmlStream,
        IProgress<ImportProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        return await ImportAsync(
            xmlStream,
            StackroomImportMergeContext.Empty,
            progress,
            cancellationToken);
    }

    public async Task<StackroomImportResult> ImportAsync(
        Stream xmlStream,
        StackroomImportMergeContext mergeContext,
        IProgress<ImportProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mergeContext);
        var result = new StackroomImportResult();

        // Parse plist in background task
        object? plistObj = await Task.Run(() => ApplePlistParser.Parse(xmlStream), cancellationToken);
        if (plistObj is not Dictionary<string, object?> rootDict)
        {
            throw new InvalidDataException("Invalid Stackroom Library XML structure.");
        }

        var booksDict = rootDict.TryGetValue("Books", out var b) && b is Dictionary<string, object?> bd ? bd : new();
        var playlistsArray = rootDict.TryGetValue("Playlists", out var p) && p is List<object?> pa ? pa : new();

        int totalBooks = booksDict.Count;
        int totalShelves = playlistsArray.Count;
        progress?.Report(new ImportProgress(0, totalBooks, 0, totalShelves));

        var volumesCache = new Dictionary<string, Volume>(StringComparer.OrdinalIgnoreCase);
        foreach (var volume in mergeContext.ExistingVolumes)
        {
            if (!string.IsNullOrWhiteSpace(volume.LastKnownPath))
            {
                volumesCache[volume.LastKnownPath] = volume;
            }
        }

        var existingItemsByLegacyId = new Dictionary<int, Item>();
        var existingItemsByPath = new Dictionary<string, Item>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in mergeContext.ExistingItems)
        {
            if (item.LegacyId.HasValue)
            {
                existingItemsByLegacyId[item.LegacyId.Value] = item;
            }
            if (!string.IsNullOrWhiteSpace(item.RelativePath))
            {
                existingItemsByPath[item.RelativePath] = item;
            }
        }

        var existingShelfKeys = new HashSet<(string Title, int Type)>(
            mergeContext.ExistingShelves.Select(shelf => (shelf.Title, shelf.Type)));
        var importedItemsByLegacyId = new Dictionary<int, Item>();
        var importedItemIds = new HashSet<Guid>();
        var membershipUpdates = new HashSet<Guid>();

        // 1. Process Books
        int booksProcessed = 0;
        foreach (var kvp in booksDict)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (kvp.Value is not Dictionary<string, object?> bookData)
                continue;

            int? legacyId = bookData.TryGetValue("ID", out var idObj) && idObj is long idL ? (int)idL : null;
            string filePath = bookData.TryGetValue("Path", out var pathObj) && pathObj is string sPath ? sPath : string.Empty;

            var (volumePath, volumeName, relativePath) = VolumePathResolver.SplitPosixPath(filePath);

            Item? existingItem = null;
            if (legacyId.HasValue)
            {
                existingItemsByLegacyId.TryGetValue(legacyId.Value, out existingItem);
            }
            if (existingItem is null && !string.IsNullOrWhiteSpace(relativePath))
            {
                existingItemsByPath.TryGetValue(relativePath, out existingItem);
            }

            if (existingItem is not null)
            {
                if (legacyId.HasValue)
                {
                    importedItemsByLegacyId[legacyId.Value] = existingItem;
                    await CopyLegacyThumbnailIfMissingAsync(
                        mergeContext, legacyId.Value, existingItem.Id, cancellationToken);
                }
                result.SkippedBooks++;
                booksProcessed++;
                if (booksProcessed % 100 == 0 || booksProcessed == totalBooks)
                {
                    progress?.Report(new ImportProgress(booksProcessed, totalBooks, 0, totalShelves));
                }
                continue;
            }

            if (!volumesCache.TryGetValue(volumePath, out var volume))
            {
                volume = new Volume
                {
                    Name = volumeName,
                    LastKnownPath = volumePath,
                    WindowsMountPath = VolumePathResolver.ConvertPosixToWindows(volumePath)
                };
                volumesCache[volumePath] = volume;
                result.DiscoveredVolumes.Add(volume);
            }

            var item = new Item
            {
                LegacyId = legacyId,
                VolumeId = volume.Id,
                RelativePath = relativePath,
                Title = bookData.TryGetValue("Title", out var tObj) && tObj is string sT ? sT : "Unknown Title",
                Author = bookData.TryGetValue("Author", out var aObj) && aObj is string sA ? sA : string.Empty,
                Rating = bookData.TryGetValue("My Rate", out var rObj) && rObj is long rL ? (int)rL : 0,
                IsUnread = bookData.TryGetValue("Unseen", out var uObj) && uObj is bool bU ? bU : true,
                Pages = bookData.TryGetValue("Pages", out var pgObj) && pgObj is long pgL ? (int)pgL : 0,
                BookType = bookData.TryGetValue("Book Type", out var btObj) && btObj is long btL ? (int)btL : 0,
                FileType = bookData.TryGetValue("File Type", out var ftObj) && ftObj is long ftL ? (int)ftL : 0,
                CoverImageName = bookData.TryGetValue("Cover Image Name", out var cinObj) && cinObj is string cin ? cin : string.Empty,
                CoverImagePath = bookData.TryGetValue("Cover Image Path", out var cipObj) && cipObj is string cip ? cip : string.Empty,
                KeywordA = bookData.TryGetValue("Keyword A", out var kaObj) && kaObj is string ka ? ka : string.Empty,
                KeywordB = bookData.TryGetValue("Keyword B", out var kbObj) && kbObj is string kb ? kb : string.Empty,
                Memo = bookData.TryGetValue("Neta", out var nObj) && nObj is string memo ? memo : string.Empty,
                AddedDate = bookData.TryGetValue("Date Added", out var daObj) && daObj is DateTime dtAdded ? dtAdded : DateTime.UtcNow,
                LastReadDate = bookData.TryGetValue("Play Date", out var pdObj) && pdObj is DateTime dtPlay ? dtPlay : null
            };

            result.ImportedBooks.Add(item);
            importedItemIds.Add(item.Id);
            if (legacyId.HasValue)
            {
                existingItemsByLegacyId[legacyId.Value] = item;
            }
            if (!string.IsNullOrWhiteSpace(relativePath))
            {
                existingItemsByPath[relativePath] = item;
            }
            if (legacyId.HasValue)
            {
                importedItemsByLegacyId[legacyId.Value] = item;
                await CopyLegacyThumbnailIfMissingAsync(
                    mergeContext, legacyId.Value, item.Id, cancellationToken);
            }

            booksProcessed++;
            if (booksProcessed % 100 == 0 || booksProcessed == totalBooks)
            {
                progress?.Report(new ImportProgress(booksProcessed, totalBooks, 0, totalShelves));
            }
        }

        // 2. Process Playlists / Shelves
        int shelvesProcessed = 0;
        foreach (var pObj in playlistsArray)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (pObj is not Dictionary<string, object?> pData)
                continue;

            string title = pData.TryGetValue("Title", out var tObj) && tObj is string sT ? sT : "Unnamed Shelf";
            int icon = pData.TryGetValue("Icon", out var iObj) && iObj is long iL ? (int)iL : 0;
            int type = pData.TryGetValue("Type", out var typeObj) && typeObj is long typL ? (int)typL : 0;

            if (existingShelfKeys.Contains((title, type)))
            {
                result.SkippedShelves++;
                shelvesProcessed++;
                progress?.Report(new ImportProgress(totalBooks, totalBooks, shelvesProcessed, totalShelves));
                continue;
            }

            string? conditionsJson = null;
            if (type == 1 && pData.TryGetValue("Conditions", out var condObj) && condObj != null)
            {
                conditionsJson = JsonSerializer.Serialize(condObj);
            }

            bool sortAsc = true;
            string sortKey = "title";
            if (pData.TryGetValue("Sort", out var sortObj) && sortObj is Dictionary<string, object?> sortDict)
            {
                if (sortDict.TryGetValue("ascending", out var ascObj) && ascObj is bool bAsc) sortAsc = bAsc;
                if (sortDict.TryGetValue("key", out var kObj) && kObj is string sK) sortKey = sK;
            }

            var shelf = new Shelf
            {
                Title = title,
                Icon = icon,
                Type = type,
                SortOrder = (shelvesProcessed + 1) * 10,
                SortAscending = sortAsc,
                SortKey = sortKey,
                SmartConditionsJson = conditionsJson
            };

            // Associate items for standard shelf
            if (type == 0 && pData.TryGetValue("Items", out var itemsObj) && itemsObj is List<object?> itemsList)
            {
                foreach (var itemRef in itemsList)
                {
                    if (itemRef is long legacyId && importedItemsByLegacyId.TryGetValue((int)legacyId, out var matchedItem))
                    {
                        shelf.ItemIds.Add(matchedItem.Id);
                        if (!matchedItem.ShelfIds.Contains(shelf.Id))
                        {
                            matchedItem.ShelfIds.Add(shelf.Id);
                            if (!importedItemIds.Contains(matchedItem.Id)
                                && membershipUpdates.Add(matchedItem.Id))
                            {
                                result.ItemsWithUpdatedShelfMembership.Add(matchedItem);
                            }
                        }
                    }
                }
            }

            result.ImportedShelves.Add(shelf);
            existingShelfKeys.Add((title, type));
            shelvesProcessed++;
            progress?.Report(new ImportProgress(totalBooks, totalBooks, shelvesProcessed, totalShelves));
        }

        return result;
    }

    private static async Task CopyLegacyThumbnailIfMissingAsync(
        StackroomImportMergeContext context,
        int legacyId,
        Guid itemId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(context.LegacyAssetsRoot)
            || string.IsNullOrWhiteSpace(context.LocalThumbnailDirectory))
            return;

        string source = Path.Combine(context.LegacyAssetsRoot, legacyId.ToString(), "thumbnail.jpg");
        string destination = Path.Combine(context.LocalThumbnailDirectory, $"{itemId:D}.jpg");
        if (!File.Exists(source) || File.Exists(destination))
            return;

        Directory.CreateDirectory(context.LocalThumbnailDirectory);
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
        await input.CopyToAsync(output, cancellationToken);
    }
}
