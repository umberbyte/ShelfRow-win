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

    public App()
    {
        this.InitializeComponent();

        string logPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ShelfRow", "launch.log");
        void Log(string msg) => File.AppendAllText(logPath, $"[{DateTime.UtcNow:HH:mm:ss.fff}] {msg}\n");

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

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        string logPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ShelfRow", "launch.log");
        void Log(string msg) => File.AppendAllText(logPath, $"[{DateTime.UtcNow:HH:mm:ss.fff}] {msg}\n");

        Log("OnLaunched started");

        try
        {
            string appData = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ShelfRow"
            );
            Directory.CreateDirectory(appData);

            string dbPath = Path.Combine(appData, "shelfrow.db");
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
            ImageLoader = new ThumbnailImageLoader(thumbnailStorage, dispatcherQueue);
            MainViewModel = new MainViewModel(repository, thumbnailStorage, syncEngine, CloudKitAccount, ImageLoader, dispatcherQueue);

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
