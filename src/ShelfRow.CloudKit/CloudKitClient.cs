using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ShelfRow.CloudKit;

public class CloudKitConfiguration
{
    public string ContainerIdentifier { get; set; } = "iCloud.com.eureka.ShelfRow";
    public string Environment { get; set; } = "development"; // or "production"
    public string? WebAuthToken { get; set; }
}

public class CloudKitClient
{
    private readonly HttpClient _httpClient;
    private readonly CloudKitConfiguration _config;
    private readonly JsonSerializerOptions _jsonOptions;

    public CloudKitClient(CloudKitConfiguration config, HttpClient? httpClient = null)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _httpClient = httpClient ?? new HttpClient();
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        };
    }

    private string BuildUrl(string relativePath)
    {
        string baseUri = $"https://api.apple-cloudkit.com/database/1/{_config.ContainerIdentifier}/{_config.Environment}/private";
        string url = $"{baseUri}/{relativePath.TrimStart('/')}";
        if (!string.IsNullOrEmpty(_config.WebAuthToken))
        {
            url += $"?ckWebAuthToken={Uri.EscapeDataString(_config.WebAuthToken)}";
        }
        return url;
    }

    public async Task<CKChangesZoneResponse> FetchZoneChangesAsync(string? syncToken = null, CancellationToken cancellationToken = default)
    {
        string url = BuildUrl("changes/zone");
        var requestBody = new CKChangesZoneRequest
        {
            Zones = new()
            {
                new CKZoneRequestItem
                {
                    ZoneID = new CKZoneID { ZoneName = CloudKitMapper.CoreDataZoneName },
                    SyncToken = syncToken
                }
            }
        };

        string json = JsonSerializer.Serialize(requestBody, _jsonOptions);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");

        using var response = await _httpClient.PostAsync(url, content, cancellationToken);
        response.EnsureSuccessStatusCode();

        string responseJson = await response.Content.ReadAsStringAsync(cancellationToken);
        return JsonSerializer.Deserialize<CKChangesZoneResponse>(responseJson, _jsonOptions)
            ?? new CKChangesZoneResponse();
    }

    public async Task<CKModifyRecordsResponse> ModifyRecordsAsync(CKModifyRecordsRequest request, CancellationToken cancellationToken = default)
    {
        string url = BuildUrl("records/modify");
        string json = JsonSerializer.Serialize(request, _jsonOptions);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");

        using var response = await _httpClient.PostAsync(url, content, cancellationToken);
        response.EnsureSuccessStatusCode();

        string responseJson = await response.Content.ReadAsStringAsync(cancellationToken);
        return JsonSerializer.Deserialize<CKModifyRecordsResponse>(responseJson, _jsonOptions)
            ?? new CKModifyRecordsResponse();
    }
}
