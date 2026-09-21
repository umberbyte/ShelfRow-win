using System;
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

        App.Log("Auth: opening the sign-in window");

        bool enqueued = _dispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                var window = new CloudKitSignInWindow(signInUrl);
                window.Activate();
                App.Log("Auth: sign-in window activated");
                opened.SetResult(window.TokenTask);
            }
            catch (Exception ex)
            {
                App.Log($"Auth: sign-in window failed to open: {ex}");
                opened.SetException(ex);
            }
        });

        if (!enqueued)
        {
            App.Log("Auth: could not reach the UI thread");
            return Task.FromResult<string?>(null);
        }

        return opened.Task.Unwrap();
    }
}
