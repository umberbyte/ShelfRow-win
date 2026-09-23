using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ShelfRow.CloudKit;

public class CloudKitConfiguration
{
    public string ContainerIdentifier { get; set; } = "iCloud.com.eureka.ShelfRow";

    /// <summary>"development" or "production". The Mac app's released builds write to production.</summary>
    public string Environment { get; set; } = "production";

    public string Database { get; set; } = "private";

    /// <summary>
    /// This container's API token, from CloudKit Console under API Access.
    ///
    /// It is shipped with the app on purpose. An API token identifies the container,
    /// not a person: Apple has CloudKit JS pages carry it in plain HTML. On its own it
    /// reaches only the public database, which ShelfRow does not use — every book, shelf
    /// and volume lives in the private database, which no one can touch without that
    /// user's own Apple ID sign-in. Making each person find and paste this would be a
    /// chore that buys no safety.
    /// </summary>
    public const string DevelopmentApiToken = "e0c24a83106304e699285699f1efa6b660fb9c753289d514102edcb24dc9a91c";
    public const string ProductionApiToken = "b0123ff1bf52276f7a5fb81b0206698493b69a9b48d70aace43097e435b6d269";

    // Kept for source compatibility with the probe and existing callers.
    public const string DefaultApiToken = ProductionApiToken;

    public static string BuiltInApiTokenFor(string environment) =>
        environment.Equals("production", StringComparison.OrdinalIgnoreCase)
            ? ProductionApiToken
            : DevelopmentApiToken;

    /// <summary>
    /// Required on every request, including ones that also carry a web auth token.
    /// Overridable so a different container can be pointed at without a rebuild.
    /// </summary>
    public string? ApiToken { get; set; } = ProductionApiToken;

    /// <summary>
    /// Identifies the signed-in Apple ID. Required for the private database.
    /// Obtained by sending the user through <see cref="CloudKitException.RedirectUrl"/>.
    /// </summary>
    public string? WebAuthToken { get; set; }
}

public class CloudKitClient
{
    private readonly HttpClient _httpClient;
    private readonly CloudKitConfiguration _config;
    private readonly JsonSerializerOptions _jsonOptions;

    /// <summary>
    /// Raised when CloudKit hands back a replacement web auth token, which it may do
    /// on any response. The old one stops working, so the new one must be persisted.
    /// </summary>
    public event Action<string>? WebAuthTokenRenewed;

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

    public CloudKitConfiguration Configuration => _config;

    private string BuildUrl(string relativePath)
    {
        if (string.IsNullOrEmpty(_config.ApiToken))
            throw new InvalidOperationException("CloudKit API token is not configured. Create one in CloudKit Console under API Access.");

        string url = $"https://api.apple-cloudkit.com/database/1/{_config.ContainerIdentifier}/{_config.Environment}/{_config.Database}/{relativePath.TrimStart('/')}" +
                     $"?ckAPIToken={Uri.EscapeDataString(_config.ApiToken)}";

        if (!string.IsNullOrEmpty(_config.WebAuthToken))
            url += $"&ckWebAuthToken={Uri.EscapeDataString(_config.WebAuthToken)}";

        return url;
    }

    private async Task<TResponse> PostAsync<TResponse>(string relativePath, object requestBody, CancellationToken cancellationToken)
        where TResponse : new()
    {
        string json = JsonSerializer.Serialize(requestBody, requestBody.GetType(), _jsonOptions);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");

        using var response = await _httpClient.PostAsync(BuildUrl(relativePath), content, cancellationToken);
        string responseJson = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
            throw ParseError(response.StatusCode, responseJson);

        CaptureRenewedToken(responseJson);

        return JsonSerializer.Deserialize<TResponse>(responseJson, _jsonOptions) ?? new TResponse();
    }

    private CloudKitException ParseError(System.Net.HttpStatusCode statusCode, string responseJson)
    {
        try
        {
            var error = JsonSerializer.Deserialize<CKErrorResponse>(responseJson, _jsonOptions);
            if (error != null)
                return new CloudKitException(statusCode, error.ServerErrorCode, error.Reason, error.RedirectURL);
        }
        catch (JsonException)
        {
        }

        return new CloudKitException(statusCode, null, responseJson, null);
    }

    private void CaptureRenewedToken(string responseJson)
    {
        try
        {
            using var document = JsonDocument.Parse(responseJson);
            if (document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("ckWebAuthToken", out var renewed) &&
                renewed.GetString() is { Length: > 0 } token &&
                token != _config.WebAuthToken)
            {
                _config.WebAuthToken = token;
                WebAuthTokenRenewed?.Invoke(token);
            }
        }
        catch (JsonException)
        {
        }
    }

    /// <summary>
    /// Fetches records changed in the Core Data zone since <paramref name="syncToken"/>,
    /// or every record in it when the token is null. Doubles as the authentication probe:
    /// an unauthenticated call throws <see cref="CloudKitException"/> carrying the sign-in URL.
    /// </summary>
    public Task<CKChangesZoneResponse> FetchZoneChangesAsync(string? syncToken = null, int resultsLimit = 200, CancellationToken cancellationToken = default)
    {
        var requestBody = new CKChangesZoneRequest
        {
            Zones =
            {
                new CKZoneRequestItem
                {
                    ZoneID = new CKZoneID { ZoneName = CloudKitMapper.CoreDataZoneName },
                    SyncToken = syncToken,
                    ResultsLimit = resultsLimit
                }
            }
        };

        return PostAsync<CKChangesZoneResponse>("changes/zone", requestBody, cancellationToken);
    }

    public Task<CKModifyRecordsResponse> ModifyRecordsAsync(CKModifyRecordsRequest request, CancellationToken cancellationToken = default)
    {
        request.ZoneID ??= new CKZoneID { ZoneName = CloudKitMapper.CoreDataZoneName };
        return PostAsync<CKModifyRecordsResponse>("records/modify", request, cancellationToken);
    }

    /// <summary>Deletes ShelfRow's custom Core Data zone from the private database.</summary>
    public async Task<CKModifyZonesResponse> DeleteCoreDataZoneAsync(CancellationToken cancellationToken = default)
    {
        var request = new CKModifyZonesRequest
        {
            Operations =
            {
                new CKZoneOperation
                {
                    OperationType = "delete",
                    Zone = new CKZone
                    {
                        ZoneID = new CKZoneID { ZoneName = CloudKitMapper.CoreDataZoneName }
                    }
                }
            }
        };
        var response = await PostAsync<CKModifyZonesResponse>("zones/modify", request, cancellationToken);
        var result = response.Zones?.Count > 0 ? response.Zones[0] : null;
        if (result is null)
            throw new CloudKitException(System.Net.HttpStatusCode.OK, "INVALID_RESPONSE", "Zone deletion returned no result.", null);
        if (result.ServerErrorCode is not null)
            throw new CloudKitException(System.Net.HttpStatusCode.OK, result.ServerErrorCode, result.Reason, result.RedirectURL);
        return response;
    }
}
