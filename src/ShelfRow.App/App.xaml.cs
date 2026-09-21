using System;
using System.IO;
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
    }

    public static MainViewModel? MainViewModel { get; private set; }
    public static ThumbnailImageLoader? ImageLoader { get; private set; }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        string appData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ShelfRow"
        );
        Directory.CreateDirectory(appData);

        string dbPath = Path.Combine(appData, "shelfrow.db");
        var repository = new SqliteShelfRowRepository(dbPath);
        var thumbnailStorage = new ThumbnailStorageManager();

        // CloudKit engine (client with empty tokens for initial offline mode)
        var cloudKitClient = new CloudKitClient(new CloudKitConfiguration());
        var syncEngine = new CloudKitSyncEngine(cloudKitClient, repository);

        _mainWindow = new MainWindow();
        var dispatcherQueue = _mainWindow.DispatcherQueue;

        ImageLoader = new ThumbnailImageLoader(thumbnailStorage, dispatcherQueue);
        MainViewModel = new MainViewModel(repository, thumbnailStorage, syncEngine, ImageLoader);

        if (_mainWindow is MainWindow mainWin)
        {
            mainWin.ViewModel = MainViewModel;
        }

        _mainWindow.Activate();

        _ = MainViewModel.InitializeAsync();
    }
}
