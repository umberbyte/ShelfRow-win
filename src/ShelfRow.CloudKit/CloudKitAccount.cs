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
            _ = _secureStorage.SetSecretAsync(EnvironmentKey(WebAuthTokenKey), token);
    }

    /// <summary>The container cannot be reached at all without an API token.</summary>
    public bool HasApiToken => !string.IsNullOrEmpty(_client.Configuration.ApiToken);

    public bool IsSignedIn => !string.IsNullOrEmpty(_client.Configuration.WebAuthToken);

    public string Environment
    {
        get => _client.Configuration.Environment;
        set
        {
            string normalized = NormalizeEnvironment(value);
            if (string.Equals(_client.Configuration.Environment, normalized, StringComparison.Ordinal))
                return;

            _client.Configuration.Environment = normalized;
            // API and user tokens are issued for one CloudKit environment. Never
            // send a development credential to production (or vice versa).
            _client.Configuration.ApiToken = null;
            _client.Configuration.WebAuthToken = null;
        }
    }

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        string environment = NormalizeEnvironment(Environment);
        _client.Configuration.Environment = environment;

        // A fresh install is ready to sign in to either environment. Overrides and
        // user tokens remain environment-specific so switching cannot poison auth.
        _client.Configuration.ApiToken =
            await _secureStorage.GetSecretAsync(EnvironmentKey(ApiTokenKey), cancellationToken)
            ?? CloudKitConfiguration.BuiltInApiTokenFor(environment);

        _client.Configuration.WebAuthToken =
            await _secureStorage.GetSecretAsync(EnvironmentKey(WebAuthTokenKey), cancellationToken);
    }

    public async Task SetApiTokenAsync(string apiToken, CancellationToken cancellationToken = default)
    {
        apiToken = apiToken.Trim();
        _client.Configuration.ApiToken = apiToken;
        await _secureStorage.SetSecretAsync(EnvironmentKey(ApiTokenKey), apiToken, cancellationToken);
    }

    public async Task SignOutAsync(CancellationToken cancellationToken = default)
    {
        _client.Configuration.WebAuthToken = null;
        await _secureStorage.DeleteSecretAsync(EnvironmentKey(WebAuthTokenKey), cancellationToken);
        // Remove credentials written by versions that did not distinguish the
        // environments. They are unsafe to adopt because their origin is unknown.
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
        catch (CloudKitException ex) when (
            ex.IsAuthenticationRequired
            && ex.RedirectUrl is null
            && !string.IsNullOrEmpty(_client.Configuration.WebAuthToken))
        {
            // CloudKit commonly reports an expired or wrong-environment web token
            // as AUTHENTICATION_FAILED without a redirect. Retry without it; that
            // response supplies the fresh sign-in URL.
            await ClearWebAuthTokenAsync(cancellationToken);
            try
            {
                return await operation(cancellationToken);
            }
            catch (CloudKitException retryEx) when (retryEx.IsAuthenticationRequired && retryEx.RedirectUrl != null)
            {
                return await SignInAndRetryAsync(operation, retryEx.RedirectUrl, cancellationToken);
            }
        }
        catch (CloudKitException ex) when (ex.IsAuthenticationRequired && ex.RedirectUrl != null)
        {
            return await SignInAndRetryAsync(operation, ex.RedirectUrl, cancellationToken);
        }
    }

    private async Task<T> SignInAndRetryAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        string redirectUrl,
        CancellationToken cancellationToken)
    {
        string? token = await _webAuth.RequestWebAuthTokenAsync(redirectUrl, cancellationToken);
        if (string.IsNullOrEmpty(token))
            throw new CloudKitException(
                System.Net.HttpStatusCode.Unauthorized,
                "AUTHENTICATION_REQUIRED",
                "Apple ID sign-in was cancelled or did not return a token.",
                redirectUrl);

        _client.Configuration.WebAuthToken = token;
        await _secureStorage.SetSecretAsync(EnvironmentKey(WebAuthTokenKey), token, cancellationToken);
        return await operation(cancellationToken);
    }

    public async Task SignInAsync(CancellationToken cancellationToken = default)
    {
        // Read a single change to validate credentials without uploading or changing the local library.
        await ExecuteAsync(ct => _client.FetchZoneChangesAsync(resultsLimit: 1, cancellationToken: ct), cancellationToken);
    }

    private async Task ClearWebAuthTokenAsync(CancellationToken cancellationToken)
    {
        _client.Configuration.WebAuthToken = null;
        await _secureStorage.DeleteSecretAsync(EnvironmentKey(WebAuthTokenKey), cancellationToken);
    }

    private string EnvironmentKey(string key) => $"{key}:{NormalizeEnvironment(Environment)}";

    private static string NormalizeEnvironment(string environment) =>
        environment.Equals("production", StringComparison.OrdinalIgnoreCase)
            ? "production"
            : "development";

    public Task<CKModifyZonesResponse> DeleteCoreDataZoneAsync(CancellationToken cancellationToken = default)
        => ExecuteAsync(ct => _client.DeleteCoreDataZoneAsync(ct), cancellationToken);
}
