using System.Net;
using System.Text;
using ShelfRow.Data;

namespace ShelfRow.CloudKit.Tests;

public sealed class CloudKitSyncDownTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"shelfrow_down_{Guid.NewGuid():N}.db");

    [Fact]
    public async Task SyncDown_DoesNotCommitIntermediateTokenWhenLaterPageFails()
    {
        using var repository = new SqliteShelfRowRepository(_dbPath);
        await repository.InitializeAsync();
        var client = new CloudKitClient(
            new CloudKitConfiguration { ApiToken = "test", WebAuthToken = "test" },
            new HttpClient(new FailingSecondPageHandler()));

        await Assert.ThrowsAsync<CloudKitException>(() =>
            new CloudKitSyncEngine(client, repository).SyncDownAsync());

        Assert.Null(await repository.GetSyncMetadataAsync(CloudKitSyncEngine.SyncTokenKey));
    }

    public void Dispose()
    {
        try { File.Delete(_dbPath); } catch { }
        try { File.Delete(_dbPath + "-wal"); } catch { }
        try { File.Delete(_dbPath + "-shm"); } catch { }
    }

    [Theory]
    [InlineData("{\"zones\":[]}")]
    [InlineData("{\"zones\":[{\"serverErrorCode\":\"ZONE_NOT_FOUND\",\"reason\":\"missing zone\"}]}")]
    public async Task SyncDown_InvalidZoneResponseDoesNotCompleteReplicaSetup(string body)
    {
        using var repository = new SqliteShelfRowRepository(_dbPath);
        await repository.InitializeAsync();
        await repository.SetSyncMetadataAsync("CloudKit_Mode", "replica");
        await repository.SetSyncMetadataAsync("CloudKit_Environment", "production");
        await repository.SetSyncMetadataAsync("CloudKit_InitialDownload", "1");
        var client = new CloudKitClient(new CloudKitConfiguration { ApiToken = "test" },
            new HttpClient(new ResponseHandler(body)));
        var engine = new CloudKitSyncEngine(client, repository);
        var session = new CloudKitSyncSession(repository);
        await Assert.ThrowsAnyAsync<Exception>(() => session.SyncAsync("production",
            ct => throw new Exception("Upload must not run"), ct => engine.SyncDownAsync(cancellationToken: ct)));
        Assert.True(await session.RequiresInitialDownloadAsync("production"));
    }

    private sealed class ResponseHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
    }

    private sealed class FailingSecondPageHandler : HttpMessageHandler
    {
        private int _calls;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            _calls++;
            if (_calls == 1)
            {
                const string firstPage = """
                    {"zones":[{"zoneID":{"zoneName":"com.apple.coredata.cloudkit.zone"},"syncToken":"page-1","moreComing":true,"records":[]}]}
                    """;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(firstPage, Encoding.UTF8, "application/json")
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                Content = new StringContent(
                    "{\"serverErrorCode\":\"INTERNAL_ERROR\",\"reason\":\"test failure\"}",
                    Encoding.UTF8,
                    "application/json")
            });
        }
    }
}
