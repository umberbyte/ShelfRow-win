using System.Text.Json;
using Microsoft.Data.Sqlite;
using ShelfRow.Core.Models;

namespace ShelfRow.Data.Tests;

public sealed class ShelfRowBackupManagerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "shelfrow-backup-tests-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task BackupUsesMacCompatibleLayoutAndIncludesCommittedWalRows()
    {
        string appData = Path.Combine(_root, "AppData");
        string thumbnails = Path.Combine(appData, "Thumbnails");
        string settings = Path.Combine(appData, "settings.json");
        string database = Path.Combine(appData, "shelfrow.db");
        string destination = Path.Combine(_root, "Destination");
        Directory.CreateDirectory(thumbnails);
        await File.WriteAllTextAsync(settings, "{\"appearanceMode\":\"Dark\"}");
        await File.WriteAllTextAsync(Path.Combine(thumbnails, "cover.jpg"), "cover");

        using (var repository = new SqliteShelfRowRepository(database))
        {
            await repository.InitializeAsync();
            await repository.UpsertItemAsync(new Item { Title = "WAL book", RelativePath = "book.cbz" });

            var manager = new ShelfRowBackupManager(appData, thumbnails, settings, database);
            ShelfRowBackupSummary first = await manager.BackUpAsync(destination);
            Assert.Equal(3, first.CopiedFiles);

            string backupRoot = Path.Combine(destination, ShelfRowBackupManager.BackupFolderName);
            Assert.True(File.Exists(Path.Combine(backupRoot, "ApplicationSupport", "shelfrow.db")));
            Assert.True(File.Exists(Path.Combine(backupRoot, "Thumbnails", "cover.jpg")));
            Assert.True(File.Exists(Path.Combine(backupRoot, "Preferences", "settings.json")));

            using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(backupRoot, "manifest.json")));
            JsonElement databaseEntry = manifest.RootElement.GetProperty("ApplicationSupport/shelfrow.db");
            Assert.True(databaseEntry.TryGetProperty("size", out _));
            Assert.True(databaseEntry.TryGetProperty("modificationTime", out _));

            using var backupRepository = new SqliteShelfRowRepository(Path.Combine(backupRoot, "ApplicationSupport", "shelfrow.db"));
            Assert.Equal("WAL book", (await backupRepository.GetItemsAsync())!.Single().Title);

            ShelfRowBackupSummary second = await manager.BackUpAsync(destination);
            Assert.Equal(0, second.CopiedFiles);
            Assert.Equal(3, second.SkippedFiles);
        }
    }

    [Fact]
    public async Task RestoreIsStagedThenAppliedBeforeDatabaseIsOpened()
    {
        string appData = Path.Combine(_root, "AppData");
        string thumbnails = Path.Combine(appData, "Thumbnails");
        string settings = Path.Combine(appData, "settings.json");
        string database = Path.Combine(appData, "shelfrow.db");
        string destination = Path.Combine(_root, "Destination");
        Directory.CreateDirectory(thumbnails);
        await File.WriteAllTextAsync(settings, "original-settings");
        await File.WriteAllTextAsync(Path.Combine(thumbnails, "kept.jpg"), "original-cover");

        var manager = new ShelfRowBackupManager(appData, thumbnails, settings, database);
        using (var repository = new SqliteShelfRowRepository(database))
        {
            await repository.InitializeAsync();
            await repository.UpsertItemAsync(new Item { Title = "Original", RelativePath = "original.cbz" });
            await manager.BackUpAsync(destination);
        }

        using (var changedRepository = new SqliteShelfRowRepository(database))
        {
            await changedRepository.InitializeAsync();
            await changedRepository.UpsertItemAsync(new Item { Title = "Later", RelativePath = "later.cbz" });
        }
        await File.WriteAllTextAsync(settings, "later-settings");
        await File.WriteAllTextAsync(Path.Combine(thumbnails, "extra.jpg"), "extra-cover");

        ShelfRowBackupSummary staged = await manager.StageRestoreAsync(destination);
        Assert.True(manager.HasPendingRestore);
        Assert.Equal("later-settings", await File.ReadAllTextAsync(settings));
        Assert.True(staged.CopiedFiles >= 3);

        ShelfRowBackupSummary? restored = await manager.ApplyPendingRestoreAsync();
        Assert.NotNull(restored);
        Assert.False(manager.HasPendingRestore);
        Assert.Equal("original-settings", await File.ReadAllTextAsync(settings));
        Assert.False(File.Exists(Path.Combine(thumbnails, "extra.jpg")));
        Assert.Equal("original-cover", await File.ReadAllTextAsync(Path.Combine(thumbnails, "kept.jpg")));

        using var restoredRepository = new SqliteShelfRowRepository(database);
        var items = await restoredRepository.GetItemsAsync(0, 100);
        Assert.Single(items);
        Assert.Equal("Original", items[0].Title);
    }

    [Fact]
    public async Task RestoreRejectsBackupWithoutWindowsDatabase()
    {
        string appData = Path.Combine(_root, "AppData");
        string backupRoot = Path.Combine(_root, "Destination", ShelfRowBackupManager.BackupFolderName);
        Directory.CreateDirectory(backupRoot);
        await File.WriteAllTextAsync(Path.Combine(backupRoot, "manifest.json"),
            "{\"ApplicationSupport/default.store\":{\"size\":1,\"modificationTime\":0}}");

        var manager = new ShelfRowBackupManager(
            appData,
            Path.Combine(appData, "Thumbnails"),
            Path.Combine(appData, "settings.json"),
            Path.Combine(appData, "shelfrow.db"));

        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            manager.StageRestoreAsync(Path.Combine(_root, "Destination")));
        Assert.Contains("Windows版", error.Message);
    }

    [Fact]
    public async Task RestoreRejectsTruncatedDatabaseBeforeChangingCurrentFiles()
    {
        string appData = Path.Combine(_root, "AppData");
        string thumbnails = Path.Combine(appData, "Thumbnails");
        string settings = Path.Combine(appData, "settings.json");
        string database = Path.Combine(appData, "shelfrow.db");
        string destination = Path.Combine(_root, "Destination");
        Directory.CreateDirectory(thumbnails);
        await File.WriteAllTextAsync(settings, "current");

        var manager = new ShelfRowBackupManager(appData, thumbnails, settings, database);
        using (var repository = new SqliteShelfRowRepository(database))
        {
            await repository.InitializeAsync();
            await manager.BackUpAsync(destination);
        }

        string backupDatabase = Path.Combine(destination, ShelfRowBackupManager.BackupFolderName,
            "ApplicationSupport", "shelfrow.db");
        await File.AppendAllTextAsync(backupDatabase, "corrupt");

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => manager.StageRestoreAsync(destination));
        Assert.Contains("サイズ", error.Message);
        Assert.Equal("current", await File.ReadAllTextAsync(settings));
        Assert.False(manager.HasPendingRestore);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
