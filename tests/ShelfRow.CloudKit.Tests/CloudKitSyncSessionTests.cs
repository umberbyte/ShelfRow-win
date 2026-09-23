using Microsoft.Data.Sqlite;
using ShelfRow.Core.Models;
using ShelfRow.Data;

namespace ShelfRow.CloudKit.Tests;

public sealed class CloudKitSyncSessionTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"shelfrow-mode-{Guid.NewGuid():N}");

    [Fact]
    public async Task Replica_BackupRetainsLibrary_ResetRemovesLocalRowsAndPendingDeletes()
    {
        using var repository = new SqliteShelfRowRepository(Path.Combine(_directory, "library.db"));
        await repository.InitializeAsync();
        var volume = new Volume { Name = "NAS", WindowsMountPath = @"\\nas\books" };
        var shelf = new Shelf { Title = "Local shelf" };
        await repository.UpsertVolumeAsync(volume);
        await repository.UpsertShelfAsync(shelf);
        var item = new Item { Title = "Local book", VolumeId = volume.Id, ShelfIds = [shelf.Id] };
        await repository.UpsertItemAsync(item);
        var deleted = new Item { Title = "Deleted locally", CloudKitRecordName = "remote-deleted" };
        await repository.UpsertItemAsync(deleted);
        await repository.DeleteItemAsync(deleted.Id);
        Assert.Single((await repository.GetPendingUploadsAsync()).Deletions);
        await repository.SetSyncMetadataAsync(CloudKitSyncEngine.SyncTokenKey, "old-token");

        string backup = await repository.PrepareCloudSyncAsync(true, "production");
        using var backupRepository = new SqliteShelfRowRepository(backup);
        Assert.Equal("Local book", (await backupRepository.GetItemByIdAsync(item.Id))!.Title);
        Assert.Equal(@"\\nas\books", (await backupRepository.GetVolumeByIdAsync(volume.Id))!.WindowsMountPath);
        Assert.Equal(0, await repository.GetItemCountAsync());
        Assert.Empty(await repository.GetShelvesAsync());
        Assert.Empty(await repository.GetVolumesAsync());
        Assert.Equal(0, (await repository.GetPendingUploadsAsync()).Count);
        Assert.Null(await repository.GetSyncMetadataAsync(CloudKitSyncEngine.SyncTokenKey));
        var session = new CloudKitSyncSession(repository);
        Assert.Equal("replica", await session.GetModeAsync());
        Assert.True(await session.RequiresInitialDownloadAsync("production"));
        // Pending initial download survives reopening the database after interruption.
        using var reopened = new SqliteShelfRowRepository(Path.Combine(_directory, "library.db"));
        Assert.True(await new CloudKitSyncSession(reopened).RequiresInitialDownloadAsync("production"));
        await session.CompleteInitialDownloadAsync();
        Assert.False(await session.RequiresInitialDownloadAsync("production"));
    }

    [Fact]
    public async Task Primary_QueuesExistingLibrary_OffAndEnvironmentMismatchPreventSync()
    {
        using var repository = new SqliteShelfRowRepository(Path.Combine(_directory, "library.db"));
        await repository.InitializeAsync();
        var item = new Item { Title = "Keep me" };
        await repository.UpsertItemAsync(item, markPendingUpload: false);
        var session = new CloudKitSyncSession(repository);
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.RequiresInitialDownloadAsync("production"));
        await repository.PrepareCloudSyncAsync(false, "production");
        Assert.False(await session.RequiresInitialDownloadAsync("production"));
        Assert.Single((await repository.GetPendingUploadsAsync()).Items);
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.RequiresInitialDownloadAsync("development"));
        await session.DisableAsync();
        Assert.Equal("Keep me", (await repository.GetItemByIdAsync(item.Id))!.Title);
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.RequiresInitialDownloadAsync("production"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => repository.PrepareCloudSyncAsync(false, "development"));
    }

    [Fact]
    public async Task BackupFailure_LeavesLibraryAndModeUntouched()
    {
        using var repository = new SqliteShelfRowRepository(Path.Combine(_directory, "library.db"));
        await repository.InitializeAsync();
        await repository.UpsertItemAsync(new Item { Title = "Keep me" });
        await File.WriteAllTextAsync(Path.Combine(_directory, "Backups"), "blocks directory creation");
        await Assert.ThrowsAsync<IOException>(() => repository.PrepareCloudSyncAsync(true, "production"));
        Assert.Equal(1, await repository.GetItemCountAsync());
        Assert.Null(await new CloudKitSyncSession(repository).GetModeAsync());
    }

    [Fact]
    public async Task Replica_FailedInitialDownloadRetriesWithoutUpload_ThenSyncsBothWays()
    {
        using var repository = new SqliteShelfRowRepository(Path.Combine(_directory, "library.db"));
        await repository.InitializeAsync();
        await repository.PrepareCloudSyncAsync(true, "production");
        var session = new CloudKitSyncSession(repository);
        var calls = new List<string>();
        Task<CloudKitSyncEngine.UploadResult> Upload(CancellationToken ct)
        {
            calls.Add("upload");
            return Task.FromResult(new CloudKitSyncEngine.UploadResult(1, 0, 0));
        }
        Task<CloudKitSyncEngine.SyncResult> Download(CancellationToken ct)
        {
            calls.Add("download");
            return Task.FromResult(new CloudKitSyncEngine.SyncResult(1, 0, 0, 0, 0));
        }
        await Assert.ThrowsAsync<IOException>(() => session.SyncAsync("production", Upload,
            ct => throw new IOException("network interrupted")));
        Assert.Empty(calls);
        Assert.True(await session.RequiresInitialDownloadAsync("production"));
        await session.SyncAsync("production", Upload, Download);
        Assert.Equal(new[] { "download" }, calls);
        calls.Clear();
        await session.SyncAsync("production", Upload, Download);
        Assert.Equal(new[] { "upload", "download" }, calls);
        await session.DisableAsync();
        calls.Clear();
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.SyncAsync("production", Upload, Download));
        Assert.Empty(calls);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
