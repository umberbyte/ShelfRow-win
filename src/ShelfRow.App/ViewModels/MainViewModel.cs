using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using ShelfRow.Core.Interfaces;
using ShelfRow.Core.Models;
using ShelfRow.Importer;
using ShelfRow.Storage;
using ShelfRow.CloudKit;

namespace ShelfRow.App.ViewModels;

public class MainViewModel : INotifyPropertyChanged
{
    private readonly IShelfRowRepository _repository;
    private readonly ThumbnailStorageManager _thumbnailManager;
    private readonly CloudKitSyncEngine _syncEngine;
    private readonly Services.ThumbnailImageLoader? _imageLoader;
    private readonly Microsoft.UI.Dispatching.DispatcherQueue? _dispatcherQueue;

    private bool _isLoading;
    private string _searchQuery = string.Empty;
    private Shelf? _selectedShelf;
    private ItemViewModel? _selectedItem;
    private string _statusMessage = "準備完了";

    public MainViewModel(
        IShelfRowRepository repository,
        ThumbnailStorageManager thumbnailManager,
        CloudKitSyncEngine syncEngine,
        Services.ThumbnailImageLoader? imageLoader = null,
        Microsoft.UI.Dispatching.DispatcherQueue? dispatcherQueue = null)
    {
        _repository = repository;
        _thumbnailManager = thumbnailManager;
        _syncEngine = syncEngine;
        _imageLoader = imageLoader;
        _dispatcherQueue = dispatcherQueue;

        Books = new ObservableCollection<ItemViewModel>();
        Shelves = new ObservableCollection<Shelf>();

        Books.CollectionChanged += (s, e) =>
        {
            OnPropertyChanged(nameof(IsEmpty));
            OnPropertyChanged(nameof(TotalBooksCountText));
        };

        SyncCommand = new RelayCommand(async () => await SyncWithCloudKitAsync());
    }

    public System.Windows.Input.ICommand SyncCommand { get; }

    private bool _isImporting;
    public bool IsImporting
    {
        get => _isImporting;
        set
        {
            _isImporting = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanImport));
            OnPropertyChanged(nameof(ImportOverlayVisibility));
        }
    }

    public bool CanImport => !IsImporting;
    public Microsoft.UI.Xaml.Visibility ImportOverlayVisibility => IsImporting ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;

    private string _importProgressText = string.Empty;
    public string ImportProgressText
    {
        get => _importProgressText;
        set { _importProgressText = value; OnPropertyChanged(); }
    }

    public event Action<string>? ImportCompletedNotification;

    public async Task ImportXmlFileAsync(string filePath, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(filePath)) return;

        IsImporting = true;
        ImportProgressText = "XMLファイルを解析中...";
        StatusMessage = "Stackroom XML インポート中...";

        try
        {
            var importer = new StackroomXmlImporter();
            var progress = new Progress<ImportProgress>(p =>
            {
                if (p.ProcessedBooks < p.TotalBooks)
                {
                    ImportProgressText = $"書籍を解析中: {p.ProcessedBooks:N0} / {p.TotalBooks:N0} 冊";
                }
                else
                {
                    ImportProgressText = $"本棚を解析中: {p.ProcessedShelves} / {p.TotalShelves} 件";
                }
            });

            using var stream = File.OpenRead(filePath);
            var result = await importer.ImportAsync(stream, progress, cancellationToken);

            ImportProgressText = "データベースに書き込み中...";

            // 1. Volumes
            foreach (var vol in result.DiscoveredVolumes)
            {
                await _repository.UpsertVolumeAsync(vol, cancellationToken);
            }

            // 2. Shelves
            foreach (var shelf in result.ImportedShelves)
            {
                await _repository.UpsertShelfAsync(shelf, cancellationToken);
            }

            // 3. Books (Batch in single transaction)
            await _repository.UpsertItemsBatchAsync(result.ImportedBooks, cancellationToken);

            ImportProgressText = "ライブラリを更新中...";
            await LoadShelvesAsync(cancellationToken);
            await RefreshBooksAsync(cancellationToken);

            string successMsg = $"{result.ImportedBooks.Count:N0} 冊の書籍と {result.ImportedShelves.Count} 個の本棚をインポートしました。";
            StatusMessage = successMsg;
            ImportCompletedNotification?.Invoke(successMsg);
        }
        catch (Exception ex)
        {
            StatusMessage = $"インポート失敗: {ex.Message}";
            ImportCompletedNotification?.Invoke($"インポートエラー: {ex.Message}");
        }
        finally
        {
            IsImporting = false;
        }
    }

    public ObservableCollection<ItemViewModel> Books { get; }
    public ObservableCollection<Shelf> Shelves { get; }

    public bool IsEmpty => !IsLoading && Books.Count == 0;
    public string TotalBooksCountText => $"{Books.Count} 冊";

    public bool IsLoading
    {
        get => _isLoading;
        set { _isLoading = value; OnPropertyChanged(); }
    }

    public string SearchQuery
    {
        get => _searchQuery;
        set
        {
            if (_searchQuery != value)
            {
                _searchQuery = value;
                OnPropertyChanged();
                _ = RefreshBooksAsync();
            }
        }
    }

    public Shelf? SelectedShelf
    {
        get => _selectedShelf;
        set
        {
            if (_selectedShelf != value)
            {
                _selectedShelf = value;
                OnPropertyChanged();
                _ = RefreshBooksAsync();
            }
        }
    }

    public ItemViewModel? SelectedItem
    {
        get => _selectedItem;
        set { _selectedItem = value; OnPropertyChanged(); }
    }

    public string StatusMessage
    {
        get => _statusMessage;
        set { _statusMessage = value; OnPropertyChanged(); }
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        string logPath = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ShelfRow", "launch.log");
        void Log(string msg) => System.IO.File.AppendAllText(logPath, $"[{DateTime.UtcNow:HH:mm:ss.fff}] [VM] {msg}\n");

        Log("InitializeAsync started");
        IsLoading = true;
        StatusMessage = "ライブラリ読み込み中...";
        try
        {
            Log("Repository.InitializeAsync starting");
            await _repository.InitializeAsync(cancellationToken);
            Log("Repository.InitializeAsync finished");

            Log("LoadShelvesAsync starting");
            await LoadShelvesAsync(cancellationToken);
            Log("LoadShelvesAsync finished");

            Log("RefreshBooksAsync starting");
            await RefreshBooksAsync(cancellationToken);
            Log($"RefreshBooksAsync finished, {Books.Count} books");

            StatusMessage = $"{Books.Count} 冊の書籍を読み込みました";
        }
        catch (Exception ex)
        {
            Log($"InitializeAsync error: {ex}");
            StatusMessage = $"読み込みエラー: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
            Log("InitializeAsync finished completely");
        }
    }

    public async Task LoadShelvesAsync(CancellationToken cancellationToken = default)
    {
        var list = await _repository.GetShelvesAsync(cancellationToken);
        RunOnUIThread(() =>
        {
            Shelves.Clear();
            foreach (var s in list)
            {
                Shelves.Add(s);
            }
        });
    }

    public async Task RefreshBooksAsync(CancellationToken cancellationToken = default)
    {
        IsLoading = true;
        try
        {
            var items = await _repository.GetItemsAsync(
                skip: 0,
                take: 500,
                shelfId: SelectedShelf?.Id,
                search: string.IsNullOrWhiteSpace(SearchQuery) ? null : SearchQuery,
                cancellationToken: cancellationToken
            );

            RunOnUIThread(() =>
            {
                Books.Clear();
                foreach (var item in items)
                {
                    Books.Add(new ItemViewModel(item, _thumbnailManager, _imageLoader));
                }
            });
        }
        finally
        {
            IsLoading = false;
        }
    }

    public async Task SyncWithCloudKitAsync(CancellationToken cancellationToken = default)
    {
        IsLoading = true;
        StatusMessage = "CloudKitと同期中...";
        try
        {
            int updated = await _syncEngine.SyncDownAsync(cancellationToken);
            await RefreshBooksAsync(cancellationToken);
            StatusMessage = $"CloudKit同期完了 ({updated} 件の変更を反映)";
        }
        catch (Exception ex)
        {
            StatusMessage = $"同期エラー: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void RunOnUIThread(Action action)
    {
        if (_dispatcherQueue != null && !_dispatcherQueue.HasThreadAccess)
        {
            _dispatcherQueue.TryEnqueue(() => action());
        }
        else
        {
            action();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        RunOnUIThread(() =>
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        });
    }
}
