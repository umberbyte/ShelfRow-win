using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using ShelfRow.App.Services;
using ShelfRow.App.ViewModels;
using ShelfRow.CloudKit;
using ShelfRow.Data;
using ShelfRow.Storage;

namespace ShelfRow.App;

public partial class App : Application
{
    private Window? _mainWindow;

    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ShelfRow",
        "launch.log");

    public static void Log(string message)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            File.AppendAllText(LogPath, $"[{DateTime.UtcNow:HH:mm:ss.fff}] {message}\n");
        }
        catch (IOException)
        {
        }
    }

    public App()
    {
        this.InitializeComponent();

        // WebView2 otherwise keeps its profile beside the executable, so every rebuild
        // would discard Apple's "keep me signed in" cookie and demand two-factor again.
        // It has to be set before any WebView2 is created.
        string webViewProfile = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ShelfRow",
            "WebView2");
        Directory.CreateDirectory(webViewProfile);
        Environment.SetEnvironmentVariable("WEBVIEW2_USER_DATA_FOLDER", webViewProfile);

        AppDomain.CurrentDomain.ProcessExit += (sender, e) =>
        {
            Log($"[ProcessExit] Exiting. StackTrace:\n{Environment.StackTrace}");
        };

        this.UnhandledException += (sender, e) =>
        {
            string err = $"[UnhandledException] {e.Message}\n{e.Exception}\n";
            Log(err);
        };

        DebugSettings.BindingFailed += (sender, args) =>
        {
            Log($"[BindingFailed] {args.Message}");
        };
        DebugSettings.XamlResourceReferenceFailed += (sender, args) =>
        {
            Log($"[ResourceFailed] {args.Message}");
        };
    }

    public static Window? MainWindowInstance { get; private set; }
    public static MainViewModel? MainViewModel { get; private set; }
    public static ThumbnailImageLoader? ImageLoader { get; private set; }
    public static CloudKitAccount? CloudKitAccount { get; private set; }
    public static ShelfRowBackupManager? BackupManager { get; private set; }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        Log("OnLaunched started");

        try
        {
            string appData = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ShelfRow"
            );
            Directory.CreateDirectory(appData);

            string dbPath = Path.Combine(appData, "shelfrow.db");
            string settingsPath = Path.Combine(appData, "settings.json");
            string thumbnailPath = Path.Combine(appData, "Thumbnails");
            BackupManager = new ShelfRowBackupManager(appData, thumbnailPath, settingsPath, dbPath);
            try
            {
                var restore = BackupManager.ApplyPendingRestoreAsync().GetAwaiter().GetResult();
                if (restore != null)
                    Log($"Applied pending restore: copied={restore.CopiedFiles}, removed={restore.RemovedFiles}");
            }
            catch (Exception ex)
            {
                // A failed restore must never prevent access to the current library.
                Log($"Pending restore failed; current library was preserved: {ex}");
            }
            var repository = new SqliteShelfRowRepository(dbPath);
            var thumbnailStorage = new ThumbnailStorageManager();

            Log("Repository and storage created");

            Log("Creating MainWindow");
            var mainWindow = new MainWindow();
            MainWindowInstance = mainWindow;
            _mainWindow = mainWindow;

            mainWindow.Closed += (s, e) => Log("MainWindow closed event triggered");

            Log("Getting DispatcherQueue");
            var dispatcherQueue = mainWindow.DispatcherQueue;

            var settingsService = new AppSettingsService();
            var cloudKitClient = new CloudKitClient(new CloudKitConfiguration
            {
                Environment = settingsService.Current.CloudKitEnvironment
            });
            var syncEngine = new CloudKitSyncEngine(cloudKitClient, repository);
            CloudKitAccount = new CloudKitAccount(
                cloudKitClient,
                new CredentialLockerStorageService(),
                new WebView2CloudKitWebAuth(dispatcherQueue));

            Log("Creating ViewModels");
            ImageLoader = new ThumbnailImageLoader(
                thumbnailStorage,
                dispatcherQueue,
                repository: repository,
                distributionRootProvider: () => settingsService.Current.ThumbnailAutoFetchEnabled
                    ? settingsService.Current.ThumbnailDistributionRoot
                    : null);
            var coverGenerator = new CoverGenerationService(
                repository,
                thumbnailStorage,
                () => settingsService.Current.ThumbnailDistributionRoot);
            MainViewModel = new MainViewModel(
                repository, thumbnailStorage, syncEngine, CloudKitAccount, ImageLoader,
                dispatcherQueue, settingsService, coverGenerator);

            mainWindow.ViewModel = MainViewModel;

            Log("Activating MainWindow");
            mainWindow.Activate();
            Log("MainWindow activated");

            _ = MainViewModel.InitializeAsync();
        }
        catch (Exception ex)
        {
            Log($"OnLaunched exception: {ex}");
        }
    }
}
