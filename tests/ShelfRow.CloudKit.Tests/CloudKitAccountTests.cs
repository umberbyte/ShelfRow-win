using System.Net;
using System.Text;
using ShelfRow.Core.Interfaces;

namespace ShelfRow.CloudKit.Tests;

public sealed class CloudKitAccountTests
{
    [Fact]
    public async Task Load_UsesCredentialsForSelectedEnvironment()
    {
        var storage = new MemorySecureStorage
        {
            ["CloudKit_ApiToken:development"] = "dev-api",
            ["CloudKit_WebAuthToken:development"] = "dev-user",
            ["CloudKit_ApiToken:production"] = "prod-api",
            ["CloudKit_WebAuthToken:production"] = "prod-user"
        };
        var configuration = new CloudKitConfiguration { Environment = "development" };
        var account = new CloudKitAccount(
            new CloudKitClient(configuration), storage, new FakeWebAuth());

        await account.LoadAsync();
        Assert.Equal("dev-api", configuration.ApiToken);
        Assert.Equal("dev-user", configuration.WebAuthToken);

        account.Environment = "production";
        Assert.Null(configuration.ApiToken);
        Assert.Null(configuration.WebAuthToken);

        await account.LoadAsync();
        Assert.Equal("prod-api", configuration.ApiToken);
        Assert.Equal("prod-user", configuration.WebAuthToken);
    }

    [Fact]
    public async Task Load_UsesCorrectBuiltInTokenForEachEnvironment()
    {
        var storage = new MemorySecureStorage();
        var configuration = new CloudKitConfiguration { Environment = "development" };
        var account = new CloudKitAccount(
            new CloudKitClient(configuration), storage, new FakeWebAuth());

        await account.LoadAsync();
        Assert.Equal(CloudKitConfiguration.DevelopmentApiToken, configuration.ApiToken);

        account.Environment = "production";
        await account.LoadAsync();
        Assert.Equal(CloudKitConfiguration.ProductionApiToken, configuration.ApiToken);
        Assert.NotEqual(CloudKitConfiguration.DevelopmentApiToken, configuration.ApiToken);
    }

    [Fact]
    public async Task SignOut_RemovesOnlyCurrentEnvironmentUserToken()
    {
        var storage = new MemorySecureStorage
        {
            ["CloudKit_WebAuthToken:development"] = "dev-user",
            ["CloudKit_WebAuthToken:production"] = "prod-user"
        };
        var configuration = new CloudKitConfiguration { Environment = "production" };
        var account = new CloudKitAccount(
            new CloudKitClient(configuration), storage, new FakeWebAuth());

        await account.LoadAsync();
        await account.SignOutAsync();

        Assert.Null(configuration.WebAuthToken);
        Assert.Equal("dev-user", storage["CloudKit_WebAuthToken:development"]);
        Assert.False(storage.ContainsKey("CloudKit_WebAuthToken:production"));
    }

    [Fact]
    public async Task Execute_ClearsRejectedUserToken_ThenSignsInAgain()
    {
        var storage = new MemorySecureStorage
        {
            ["CloudKit_WebAuthToken:development"] = "expired-user"
        };
        var webAuth = new FakeWebAuth { Token = "fresh-user" };
        var handler = new AuthenticationSequenceHandler();
        var configuration = new CloudKitConfiguration { Environment = "development" };
        var client = new CloudKitClient(configuration, new HttpClient(handler));
        var account = new CloudKitAccount(client, storage, webAuth);
        await account.LoadAsync();

        await account.ExecuteAsync(ct => client.FetchZoneChangesAsync(cancellationToken: ct));

        Assert.Equal(3, handler.Queries.Count);
        Assert.Contains("ckWebAuthToken=expired-user", handler.Queries[0]);
        Assert.DoesNotContain("ckWebAuthToken", handler.Queries[1]);
        Assert.Contains("ckWebAuthToken=fresh-user", handler.Queries[2]);
        Assert.Equal("https://example.test/sign-in", webAuth.RequestedUrl);
        Assert.Equal("renewed-user", storage["CloudKit_WebAuthToken:development"]);
    }

    private sealed class MemorySecureStorage : Dictionary<string, string>, ISecureStorageService
    {
        public Task SetSecretAsync(string key, string secret, CancellationToken cancellationToken = default)
        {
            this[key] = secret;
            return Task.CompletedTask;
        }

        public Task<string?> GetSecretAsync(string key, CancellationToken cancellationToken = default) =>
            Task.FromResult(TryGetValue(key, out string? value) ? value : null);

        public Task DeleteSecretAsync(string key, CancellationToken cancellationToken = default)
        {
            Remove(key);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeWebAuth : ICloudKitWebAuth
    {
        public string? Token { get; init; }
        public string? RequestedUrl { get; private set; }

        public Task<string?> RequestWebAuthTokenAsync(string signInUrl, CancellationToken cancellationToken = default)
        {
            RequestedUrl = signInUrl;
            return Task.FromResult(Token);
        }
    }

    private sealed class AuthenticationSequenceHandler : HttpMessageHandler
    {
        public List<string> Queries { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Queries.Add(request.RequestUri!.Query);
            return Task.FromResult(Queries.Count switch
            {
                1 => JsonResponse(
                    HttpStatusCode.Unauthorized,
                    "{\"serverErrorCode\":\"AUTHENTICATION_FAILED\",\"reason\":\"expired\"}"),
                2 => JsonResponse(
                    (HttpStatusCode)421,
                    "{\"serverErrorCode\":\"AUTHENTICATION_REQUIRED\",\"reason\":\"sign in\",\"redirectURL\":\"https://example.test/sign-in\"}"),
                _ => JsonResponse(
                    HttpStatusCode.OK,
                    "{\"zones\":[],\"ckWebAuthToken\":\"renewed-user\"}")
            });
        }

        private static HttpResponseMessage JsonResponse(HttpStatusCode status, string json) =>
            new(status)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
    }
}
