using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.Web.WebView2.Core;

namespace ShelfRow.App.Views;

/// <summary>
/// Hosts Apple's sign-in page and captures the web auth token it redirects back with.
/// </summary>
public sealed partial class CloudKitSignInWindow : Window
{
    /// <summary>
    /// Must match the Sign In Callback registered on the API token in CloudKit Console.
    /// Nothing serves this address; the navigation to it is intercepted here.
    /// </summary>
    public const string CallbackUrl = "https://localhost:49152/shelfrow-auth";

    private readonly TaskCompletionSource<string?> _result = new();
    private readonly string _signInUrl;

    public CloudKitSignInWindow(string signInUrl)
    {
        _signInUrl = signInUrl;
        InitializeComponent();

        AppWindow.Resize(new Windows.Graphics.SizeInt32(560, 760));
        Closed += (_, _) => _result.TrySetResult(null);

        _ = StartAsync();
    }

    public Task<string?> TokenTask => _result.Task;

    private async Task StartAsync()
    {
        StatusText.Text = "サインインページを読み込んでいます...";

        try
        {
            // The control starts creating its own environment as soon as it is shown, so
            // the profile location is set through the environment variable App reads at
            // startup rather than by passing an environment in here, which would arrive
            // too late and throw.
            await AuthWebView.EnsureCoreWebView2Async();

            AuthWebView.CoreWebView2.NavigationStarting += OnNavigationStarting;
            AuthWebView.CoreWebView2.Navigate(_signInUrl);
            App.Log("Auth: navigating to the Apple sign-in page");
        }
        catch (Exception ex)
        {
            App.Log($"Auth: WebView2 failed to start: {ex}");
            StatusText.Text = $"サインインページを表示できませんでした: {ex.Message}";
        }
    }

    private void OnNavigationStarting(CoreWebView2 sender, CoreWebView2NavigationStartingEventArgs args)
    {
        if (!IsCallbackUrl(args.Uri))
            return;

        // The callback address has no server behind it, so letting the navigation run
        // would only show an error page.
        args.Cancel = true;

        string? token = ExtractToken(args.Uri);
        StatusText.Text = token == null ? "トークンを取得できませんでした。" : "サインインしました。";

        _result.TrySetResult(token);
        Close();
    }

    private static bool IsCallbackUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
            return false;

        return uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
               && uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
               && uri.Port == 49152
               && uri.AbsolutePath.TrimEnd('/').Equals("/shelfrow-auth", StringComparison.Ordinal);
    }

    private static string? ExtractToken(string callbackUrl)
    {
        if (!Uri.TryCreate(callbackUrl, UriKind.Absolute, out var uri))
            return null;

        foreach (string pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] parts = pair.Split('=', 2);
            if (parts.Length == 2 && parts[0].Equals("ckWebAuthToken", StringComparison.OrdinalIgnoreCase))
                return Uri.UnescapeDataString(parts[1]);
        }

        return null;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        _result.TrySetResult(null);
        Close();
    }
}
