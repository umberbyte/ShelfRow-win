using ShelfRow.Core.Interfaces;

namespace ShelfRow.CloudKit;

/// <summary>Persists device role and prevents upload before a replica's initial download completes.</summary>
public sealed class CloudKitSyncSession(IShelfRowRepository repository)
{
    public Task<string?> GetModeAsync(CancellationToken cancellationToken = default) =>
        repository.GetSyncMetadataAsync("CloudKit_Mode", cancellationToken);

    public Task DisableAsync(CancellationToken cancellationToken = default) =>
        repository.SetSyncMetadataAsync("CloudKit_Mode", "off", cancellationToken);

    public async Task<bool> RequiresInitialDownloadAsync(string environment, CancellationToken cancellationToken = default)
    {
        if (await GetModeAsync(cancellationToken) is not ("primary" or "replica"))
            throw new InvalidOperationException("設定 > iCloud で同期をオンにし、1台目／2台目以降を選択してください。");
        if (await repository.GetSyncMetadataAsync("CloudKit_Environment", cancellationToken) != environment)
            throw new InvalidOperationException("接続環境が変わっています。同期をオフにしてから、新しい環境で同期を設定してください。");
        return await repository.GetSyncMetadataAsync("CloudKit_InitialDownload", cancellationToken) == "1";
    }

    public Task CompleteInitialDownloadAsync(CancellationToken cancellationToken = default) =>
        repository.SetSyncMetadataAsync("CloudKit_InitialDownload", "0", cancellationToken);

    public async Task<(CloudKitSyncEngine.UploadResult Upload, CloudKitSyncEngine.SyncResult Download)> SyncAsync(
        string environment,
        Func<CancellationToken, Task<CloudKitSyncEngine.UploadResult>> upload,
        Func<CancellationToken, Task<CloudKitSyncEngine.SyncResult>> download,
        CancellationToken cancellationToken = default)
    {
        bool initialDownload = await RequiresInitialDownloadAsync(environment, cancellationToken);
        var sent = initialDownload ? new CloudKitSyncEngine.UploadResult(0, 0, 0) : await upload(cancellationToken);
        var received = await download(cancellationToken);
        if (initialDownload) await CompleteInitialDownloadAsync(cancellationToken);
        return (sent, received);
    }
}
