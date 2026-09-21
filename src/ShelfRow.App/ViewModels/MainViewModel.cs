using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using ShelfRow.Core.Interfaces;
using ShelfRow.Core.Models;
using ShelfRow.Storage;
using ShelfRow.CloudKit;

namespace ShelfRow.App.ViewModels;

public class MainViewModel : INotifyPropertyChanged
{
    private readonly IShelfRowRepository _repository;
    private readonly ThumbnailStorageManager _thumbnailManager;
    private readonly CloudKitSyncEngine _syncEngine;
    private readonly Services.ThumbnailImageLoader? _imageLoader;

    private bool _isLoading;
    private string _searchQuery = string.Empty;
    private Shelf? _selectedShelf;
    private ItemViewModel? _selectedItem;
    private string _statusMessage = "準備完了";

    public MainViewModel(
        IShelfRowRepository repository,
        ThumbnailStorageManager thumbnailManager,
        CloudKitSyncEngine syncEngine,
        Services.ThumbnailImageLoader? imageLoader = null)
    {
        _repository = repository;
        _thumbnailManager = thumbnailManager;
        _syncEngine = syncEngine;
        _imageLoader = imageLoader;

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
        IsLoading = true;
        StatusMessage = "ライブラリ読み込み中...";
        try
        {
            await _repository.InitializeAsync(cancellationToken);
            await LoadShelvesAsync(cancellationToken);
            await RefreshBooksAsync(cancellationToken);
            StatusMessage = $"{Books.Count} 冊の書籍を読み込みました";
        }
        catch (Exception ex)
        {
            StatusMessage = $"読み込みエラー: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    public async Task LoadShelvesAsync(CancellationToken cancellationToken = default)
    {
        Shelves.Clear();
        var list = await _repository.GetShelvesAsync(cancellationToken);
        foreach (var s in list)
        {
            Shelves.Add(s);
        }
    }

    public async Task RefreshBooksAsync(CancellationToken cancellationToken = default)
    {
        IsLoading = true;
        try
        {
            Books.Clear();
            var items = await _repository.GetItemsAsync(
                skip: 0,
                take: 500,
                shelfId: SelectedShelf?.Id,
                search: string.IsNullOrWhiteSpace(SearchQuery) ? null : SearchQuery,
                cancellationToken: cancellationToken
            );

            foreach (var item in items)
            {
                Books.Add(new ItemViewModel(item, _thumbnailManager, _imageLoader));
            }
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

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
