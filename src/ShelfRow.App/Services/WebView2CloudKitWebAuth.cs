using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using ShelfRow.App.Views;
using ShelfRow.CloudKit;

namespace ShelfRow.App.Services;

/// <summary>
/// Runs the Apple ID sign-in in a window. Sync runs off the UI thread, so the window
/// is opened through the dispatcher.
/// </summary>
public class WebView2CloudKitWebAuth : ICloudKitWebAuth
{
    private readonly DispatcherQueue _dispatcherQueue;

    public WebView2CloudKitWebAuth(DispatcherQueue dispatcherQueue)
    {
        _dispatcherQueue = dispatcherQueue;
    }

    public Task<string?> RequestWebAuthTokenAsync(string signInUrl, CancellationToken cancellationToken = default)
    {
        var opened = new TaskCompletionSource<Task<string?>>();

        _dispatcherQueue.TryEnqueue(() =>
        {
            var window = new CloudKitSignInWindow(signInUrl);
            window.Activate();
            opened.SetResult(window.TokenTask);
        });

        return opened.Task.Unwrap();
    }
}
