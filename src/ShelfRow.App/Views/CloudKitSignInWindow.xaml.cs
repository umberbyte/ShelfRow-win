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
    public const string CallbackUrlPrefix = "http://localhost:49152/";

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

        // WebView2 otherwise keeps its cookies beside the executable, so every rebuild
        // would throw away Apple's "keep me signed in" cookie and demand a full sign-in
        // with two-factor again.
        string userDataFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ShelfRow",
            "WebView2");
        Directory.CreateDirectory(userDataFolder);

        var environment = await CoreWebView2Environment.CreateWithOptionsAsync(
            string.Empty, userDataFolder, new CoreWebView2EnvironmentOptions());

        await AuthWebView.EnsureCoreWebView2Async(environment);
        AuthWebView.CoreWebView2.NavigationStarting += OnNavigationStarting;
        AuthWebView.CoreWebView2.Navigate(_signInUrl);
    }

    private void OnNavigationStarting(CoreWebView2 sender, CoreWebView2NavigationStartingEventArgs args)
    {
        if (!args.Uri.StartsWith(CallbackUrlPrefix, StringComparison.OrdinalIgnoreCase))
            return;

        // The callback address has no server behind it, so letting the navigation run
        // would only show an error page.
        args.Cancel = true;

        string? token = ExtractToken(args.Uri);
        StatusText.Text = token == null ? "トークンを取得できませんでした。" : "サインインしました。";

        _result.TrySetResult(token);
        Close();
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
