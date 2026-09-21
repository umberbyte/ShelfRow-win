using System;
using System.Threading;
using System.Threading.Tasks;
using ShelfRow.Core.Interfaces;

namespace ShelfRow.CloudKit;

/// <summary>
/// Shows the Apple ID sign-in page and returns the web auth token it hands back.
/// Implemented by the UI layer, because the flow is a web page the user has to use.
/// </summary>
public interface ICloudKitWebAuth
{
    Task<string?> RequestWebAuthTokenAsync(string signInUrl, CancellationToken cancellationToken = default);
}

/// <summary>
/// Holds the credentials CloudKit Web Services needs and keeps them current.
///
/// The private database requires a per-user web auth token that expires and that the
/// server may replace on any response, so operations run through
/// <see cref="ExecuteAsync"/>, which signs in and retries rather than surfacing an
/// authentication error to the caller.
/// </summary>
public class CloudKitAccount
{
    public const string ApiTokenKey = "CloudKit_ApiToken";
    public const string WebAuthTokenKey = "CloudKit_WebAuthToken";

    private readonly CloudKitClient _client;
    private readonly ISecureStorageService _secureStorage;
    private readonly ICloudKitWebAuth _webAuth;

    public CloudKitAccount(CloudKitClient client, ISecureStorageService secureStorage, ICloudKitWebAuth webAuth)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _secureStorage = secureStorage ?? throw new ArgumentNullException(nameof(secureStorage));
        _webAuth = webAuth ?? throw new ArgumentNullException(nameof(webAuth));

        _client.WebAuthTokenRenewed += token =>
            _ = _secureStorage.SetSecretAsync(WebAuthTokenKey, token);
    }

    /// <summary>The container cannot be reached at all without an API token.</summary>
    public bool HasApiToken => !string.IsNullOrEmpty(_client.Configuration.ApiToken);

    public bool IsSignedIn => !string.IsNullOrEmpty(_client.Configuration.WebAuthToken);

    public string Environment
    {
        get => _client.Configuration.Environment;
        set => _client.Configuration.Environment = value;
    }

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        // Falling back to the built-in token means a fresh install is ready to sign in,
        // with no credential for the user to go and find first.
        _client.Configuration.ApiToken =
            await _secureStorage.GetSecretAsync(ApiTokenKey, cancellationToken)
            ?? CloudKitConfiguration.DefaultApiToken;

        _client.Configuration.WebAuthToken = await _secureStorage.GetSecretAsync(WebAuthTokenKey, cancellationToken);
    }

    public async Task SetApiTokenAsync(string apiToken, CancellationToken cancellationToken = default)
    {
        apiToken = apiToken.Trim();
        _client.Configuration.ApiToken = apiToken;
        await _secureStorage.SetSecretAsync(ApiTokenKey, apiToken, cancellationToken);
    }

    public async Task SignOutAsync(CancellationToken cancellationToken = default)
    {
        _client.Configuration.WebAuthToken = null;
        await _secureStorage.DeleteSecretAsync(WebAuthTokenKey, cancellationToken);
    }

    /// <summary>
    /// Runs a CloudKit operation, signing the user in if the server asks for it and
    /// retrying once. Returns false from <paramref name="operation"/>'s perspective only
    /// by throwing; a refused or abandoned sign-in surfaces as the original error.
    /// </summary>
    public async Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken = default)
    {
        if (!HasApiToken)
            throw new InvalidOperationException("CloudKit API token has not been set. Enter it in Preferences > iCloud.");

        try
        {
            return await operation(cancellationToken);
        }
        catch (CloudKitException ex) when (ex.IsAuthenticationRequired && ex.RedirectUrl != null)
        {
            string? token = await _webAuth.RequestWebAuthTokenAsync(ex.RedirectUrl, cancellationToken);
            if (string.IsNullOrEmpty(token))
                throw;

            _client.Configuration.WebAuthToken = token;
            await _secureStorage.SetSecretAsync(WebAuthTokenKey, token, cancellationToken);

            return await operation(cancellationToken);
        }
    }
}
