using System.Net;
using System.Text;
using System.Text.Json;

namespace ShelfRow.CloudKit.Tests;

public sealed class CloudKitZoneTests
{
    [Fact]
    public async Task DeleteCoreDataZone_UsesZoneModifyDeleteContract()
    {
        var handler = new CaptureHandler();
        var client = new CloudKitClient(
            new CloudKitConfiguration { ApiToken = "test", WebAuthToken = "user" },
            new HttpClient(handler));

        await client.DeleteCoreDataZoneAsync();

        Assert.EndsWith("/zones/modify?ckAPIToken=test&ckWebAuthToken=user", handler.Uri!.PathAndQuery);
        using var request = JsonDocument.Parse(handler.Body!);
        var operation = Assert.Single(request.RootElement.GetProperty("operations").EnumerateArray());
        Assert.Equal("delete", operation.GetProperty("operationType").GetString());
        Assert.Equal(
            CloudKitMapper.CoreDataZoneName,
            operation.GetProperty("zone").GetProperty("zoneID").GetProperty("zoneName").GetString());
    }

    private sealed class CaptureHandler : HttpMessageHandler
    {
        public Uri? Uri { get; private set; }
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Uri = request.RequestUri;
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"zones\":[{\"zoneID\":{\"zoneName\":\"com.apple.coredata.cloudkit.zone\"}}]}",
                    Encoding.UTF8,
                    "application/json")
            };
        }
    }
}
