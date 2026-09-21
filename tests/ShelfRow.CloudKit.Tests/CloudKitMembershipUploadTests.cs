using System.Net;
using System.Text;
using System.Text.Json;
using ShelfRow.Core.Models;
using ShelfRow.Data;

namespace ShelfRow.CloudKit.Tests;

public sealed class CloudKitMembershipUploadTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"shelfrow_ck_{Guid.NewGuid():N}.db");

    [Fact]
    public async Task SyncUp_SendsMembershipCreateAndDeleteAndClearsQueue()
    {
        using var repository = new SqliteShelfRowRepository(_dbPath);
        await repository.InitializeAsync();
        var shelf = new Shelf { Title = "Shelf", CloudKitRecordName = "SHELF-RECORD" };
        var item = new Item { Title = "Item", RelativePath = "a.zip", CloudKitRecordName = "ITEM-RECORD", CloudKitChangeTag = "item-v1" };
        await repository.UpsertShelfAsync(shelf, markPendingUpload: false);
        await repository.UpsertItemAsync(item, markPendingUpload: false);

        var handler = new EchoModifyHandler();
        var client = new CloudKitClient(
            new CloudKitConfiguration { ApiToken = "test", WebAuthToken = "test" },
            new HttpClient(handler));
        var engine = new CloudKitSyncEngine(client, repository);

        item.ShelfIds.Add(shelf.Id);
        await repository.UpsertItemAsync(item);
        await engine.SyncUpAsync();

        Assert.Contains(handler.Requests.SelectMany(Operations), operation =>
            operation.GetProperty("operationType").GetString() == "create"
            && operation.GetProperty("record").GetProperty("recordType").GetString() == "CDMR");
        Assert.Empty((await repository.GetPendingUploadsAsync()).ItemShelfChanges);

        handler.Requests.Clear();
        item.ShelfIds.Clear();
        await repository.UpsertItemAsync(item);
        await engine.SyncUpAsync();

        Assert.Contains(handler.Requests.SelectMany(Operations), operation =>
            operation.GetProperty("operationType").GetString() == "delete"
            && operation.GetProperty("record").GetProperty("recordType").GetString() == "CDMR");
        Assert.Empty((await repository.GetItemByIdAsync(item.Id))!.ShelfIds);
        Assert.Empty((await repository.GetPendingUploadsAsync()).ItemShelfChanges);
    }

    private static IEnumerable<JsonElement> Operations(JsonDocument request) =>
        request.RootElement.GetProperty("operations").EnumerateArray();

    public void Dispose()
    {
        try { File.Delete(_dbPath); } catch { }
        try { File.Delete(_dbPath + "-wal"); } catch { }
        try { File.Delete(_dbPath + "-shm"); } catch { }
    }

    private sealed class EchoModifyHandler : HttpMessageHandler
    {
        public List<JsonDocument> Requests { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string json = await request.Content!.ReadAsStringAsync(cancellationToken);
            var document = JsonDocument.Parse(json);
            Requests.Add(document);
            var records = Operations(document).Select(operation =>
            {
                var record = operation.GetProperty("record");
                return new
                {
                    recordName = record.GetProperty("recordName").GetString(),
                    recordType = record.GetProperty("recordType").GetString(),
                    recordChangeTag = "server-v2",
                    fields = new Dictionary<string, object?>()
                };
            });
            string response = JsonSerializer.Serialize(new { records });
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(response, Encoding.UTF8, "application/json")
            };
        }
    }
}

