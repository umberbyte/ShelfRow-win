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
