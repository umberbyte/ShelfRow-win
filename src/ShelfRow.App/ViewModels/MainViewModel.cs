using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using ShelfRow.App.Models;
using ShelfRow.App.Services;
using ShelfRow.CloudKit;
using ShelfRow.Core.Interfaces;
using ShelfRow.Core.Models;
using ShelfRow.Importer;
using ShelfRow.Storage;

namespace ShelfRow.App.ViewModels;

public class MainViewModel : INotifyPropertyChanged
{
    private readonly IShelfRowRepository _repository;
    private readonly ThumbnailStorageManager _thumbnailManager;
    private readonly CloudKitSyncEngine _syncEngine;
    private readonly CloudKitAccount _cloudKitAccount;
    private readonly BookLauncher _bookLauncher;
    private readonly ThumbnailImageLoader? _imageLoader;
    private readonly CoverGenerationService? _coverGenerator;
    private readonly Microsoft.UI.Dispatching.DispatcherQueue? _dispatcherQueue;
    private readonly AppSettingsService _settingsService;

    private readonly List<Item> _allItemModels = new();
    private readonly HashSet<int> _selectedTypeFilters = new();
    private readonly HashSet<int> _selectedRatingFilters = new();

    private bool _isLoading;
    private string _searchQuery = string.Empty;
    private string _currentCollectionTitle = "すべての項目";
    private Shelf? _selectedShelf;
    private bool _isUnreadCollectionSelected;
    private ItemViewModel? _selectedItem;
    private string _statusMessage = "準備完了";

    private bool _isGridView = true;
    private string _sortKey = "Title";
    private bool _sortAscending = true;
    private bool _isUnreadOnlyFilter;

    public MainViewModel(
        IShelfRowRepository repository,
        ThumbnailStorageManager thumbnailManager,
        CloudKitSyncEngine syncEngine,
        CloudKitAccount cloudKitAccount,
        ThumbnailImageLoader? imageLoader = null,
        Microsoft.UI.Dispatching.DispatcherQueue? dispatcherQueue = null,
        AppSettingsService? settingsService = null,
        CoverGenerationService? coverGenerator = null)
    {
        _repository = repository;
        _thumbnailManager = thumbnailManager;
        _syncEngine = syncEngine;
        _cloudKitAccount = cloudKitAccount;
        _imageLoader = imageLoader;
        _dispatcherQueue = dispatcherQueue;
        _settingsService = settingsService ?? new AppSettingsService();
        _coverGenerator = coverGenerator;
        _bookLauncher = new BookLauncher(_settingsService, new VolumePathResolver());

        Books = new ObservableCollection<ItemViewModel>();
        Shelves = new ObservableCollection<Shelf>();
        StaticShelves = new ObservableCollection<Shelf>();
        SmartShelves = new ObservableCollection<Shelf>();
        Volumes = new ObservableCollection<VolumeViewModel>();
        Stamps = new ObservableCollection<string>(ParseStamps(Settings.StampsList));

        // Load persisted view preferences
        _isGridView = _settingsService.Current.MainViewIsGrid;
        _sortKey = _settingsService.Current.MainSortKey;
        _sortAscending = _settingsService.Current.MainSortAscending;

        Volumes.CollectionChanged += (s, e) =>
        {
            OnPropertyChanged(nameof(HasVolumes));
            OnPropertyChanged(nameof(HasNoVolumes));
        };

        SyncCommand = new RelayCommand(async () => await SyncWithCloudKitAsync());
        SyncThumbnailsCommand = new RelayCommand(async () => await SyncAllThumbnailsAsync());
        OpenSettingsCommand = new RelayCommand(() => OpenSettingsRequested?.Invoke(this, EventArgs.Empty));
        ClearFiltersCommand = new RelayCommand(ClearFilters);
        ToggleViewModeCommand = new RelayCommand<string>(mode => SetViewMode(mode == "Grid"));
        SetSortKeyCommand = new RelayCommand<string>(SetSortKey);
        ToggleSortDirectionCommand = new RelayCommand(ToggleSortDirection);
        AddStandardShelfCommand = new RelayCommand(async () => await CreateStandardShelfAsync());
        AddSmartShelfCommand = new RelayCommand(async () => await CreateSmartShelfAsync());
    }

    public event EventHandler? OpenSettingsRequested;
    public event Action<string>? ImportCompletedNotification;

    /// <summary>
    /// Replaced wholesale rather than refilled. Adding twenty thousand books one at a
    /// time raises a change notification per book, and the grid rebuilds on each one,
    /// which locks up the window for minutes.
    /// </summary>
    public ObservableCollection<ItemViewModel> Books { get; private set; }
    public ObservableCollection<Shelf> Shelves { get; }
    public ObservableCollection<Shelf> StaticShelves { get; }
    public ObservableCollection<Shelf> SmartShelves { get; }
    public ObservableCollection<VolumeViewModel> Volumes { get; }
    public ObservableCollection<string> Stamps { get; }

    public AppSettings Settings => _settingsService.Current;
    public CoverGenerationService? CoverGenerator => _coverGenerator;
    public string AuthorFieldLabel => Settings.EffectiveAuthorLabel + ":";
    public string GenreFieldLabel => Settings.EffectiveGenreLabel + ":";
    public string RelationFieldLabel => Settings.EffectiveRelationLabel + ":";
    public string KeywordAFieldLabel => Settings.EffectiveKeywordALabel + ":";
    public string KeywordBFieldLabel => Settings.EffectiveKeywordBLabel + ":";
    public string SearchAuthorLabel => $"この{Settings.EffectiveAuthorLabel}を検索";
    public string SearchGenreLabel => $"この{Settings.EffectiveGenreLabel}を検索";
    public string SearchRelationLabel => $"この{Settings.EffectiveRelationLabel}を検索";
    public string SearchKeywordALabel => $"この{Settings.EffectiveKeywordALabel}を検索";
    public string SearchKeywordBLabel => $"この{Settings.EffectiveKeywordBLabel}を検索";
    public string TypeName0 => Settings.GetEffectiveTypeName(0);
    public string TypeName1 => Settings.GetEffectiveTypeName(1);
    public string TypeName2 => Settings.GetEffectiveTypeName(2);
    public string TypeName3 => Settings.GetEffectiveTypeName(3);
    public string TypeName4 => Settings.GetEffectiveTypeName(4);
    public string TypeName5 => Settings.GetEffectiveTypeName(5);

    public void ReloadSettings()
    {
        _settingsService.Load();
        foreach (string property in new[]
        {
            nameof(Settings), nameof(AuthorFieldLabel), nameof(GenreFieldLabel),
            nameof(RelationFieldLabel), nameof(KeywordAFieldLabel), nameof(KeywordBFieldLabel),
            nameof(SearchAuthorLabel), nameof(SearchGenreLabel), nameof(SearchRelationLabel),
            nameof(SearchKeywordALabel), nameof(SearchKeywordBLabel),
            nameof(TypeName0), nameof(TypeName1), nameof(TypeName2),
            nameof(TypeName3), nameof(TypeName4), nameof(TypeName5), nameof(SortKeyLabel),
            nameof(AuthorSortHeader), nameof(GenreSortHeader)
        }) OnPropertyChanged(property);
        Stamps.Clear();
        foreach (string stamp in ParseStamps(Settings.StampsList)) Stamps.Add(stamp);
        ApplyFilterAndSort();
    }

    public void SaveStamps(string text)
    {
        var stamps = ParseStamps(text).Distinct(StringComparer.Ordinal).ToArray();
        Settings.StampsList = string.Join('\n', stamps);
        _settingsService.Save();
        Stamps.Clear();
        foreach (string stamp in stamps) Stamps.Add(stamp);
    }

    public void SaveMainWindowSize(int width, int height)
    {
        if (width <= 0 || height <= 0
            || (Settings.MainWindowWidth == width && Settings.MainWindowHeight == height))
            return;

        Settings.MainWindowWidth = width;
        Settings.MainWindowHeight = height;
        _settingsService.Save();
    }

    public void ApplyStamp(string field, string stamp)
    {
        if (SelectedItem is null || string.IsNullOrEmpty(stamp)) return;
        static string Append(string value, string addition) => string.IsNullOrEmpty(value) ? addition : value + addition;
        switch (field)
        {
            case "Title": SelectedItem.Title = Append(SelectedItem.Title, stamp); break;
            case "Author": SelectedItem.Author = Append(SelectedItem.Author, stamp); break;
            case "KeywordA": SelectedItem.KeywordA = Append(SelectedItem.KeywordA, stamp); break;
            case "KeywordB": SelectedItem.KeywordB = Append(SelectedItem.KeywordB, stamp); break;
            case "Memo": SelectedItem.Memo = Append(SelectedItem.Memo, stamp); break;
            case "Genre": SelectedItem.Genre = Append(SelectedItem.Genre, stamp); break;
            case "Relation": SelectedItem.Relation = Append(SelectedItem.Relation, stamp); break;
        }
    }

    private static IEnumerable<string> ParseStamps(string? text) =>
        (text ?? string.Empty).Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Where(stamp => stamp.Length > 0);

    public System.Windows.Input.ICommand SyncCommand { get; }
    public System.Windows.Input.ICommand SyncThumbnailsCommand { get; }
    public System.Windows.Input.ICommand OpenSettingsCommand { get; }
    public System.Windows.Input.ICommand ClearFiltersCommand { get; }
    public System.Windows.Input.ICommand ToggleViewModeCommand { get; }
    public System.Windows.Input.ICommand SetSortKeyCommand { get; }
    public System.Windows.Input.ICommand ToggleSortDirectionCommand { get; }
    public System.Windows.Input.ICommand AddStandardShelfCommand { get; }
    public System.Windows.Input.ICommand AddSmartShelfCommand { get; }

    #region View & Sort Properties

    public bool IsGridView
    {
        get => _isGridView;
        set
        {
            if (_isGridView != value)
            {
                _isGridView = value;
                _settingsService.Current.MainViewIsGrid = value;
                _settingsService.Save();
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsListView));
                OnPropertyChanged(nameof(ListViewModeBackground));
                OnPropertyChanged(nameof(GridViewModeBackground));
            }
        }
    }

    public bool IsListView => !IsGridView;

    public Microsoft.UI.Xaml.Media.Brush ListViewModeBackground => IsListView
        ? (Microsoft.UI.Xaml.Application.Current.Resources["AccentFillColorDefaultBrush"] as Microsoft.UI.Xaml.Media.Brush ?? new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.DodgerBlue))
        : new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);

    public Microsoft.UI.Xaml.Media.Brush GridViewModeBackground => IsGridView
        ? (Microsoft.UI.Xaml.Application.Current.Resources["AccentFillColorDefaultBrush"] as Microsoft.UI.Xaml.Media.Brush ?? new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.DodgerBlue))
        : new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);

    public string SortKey
    {
        get => _sortKey;
        set
        {
            if (_sortKey != value)
            {
                _sortKey = value;
                _settingsService.Current.MainSortKey = value;
                _settingsService.Save();
                OnPropertyChanged();
                OnPropertyChanged(nameof(SortKeyLabel));
                NotifySortHeaderProperties();
                ApplyFilterAndSort();
            }
        }
    }

    public bool SortAscending
    {
        get => _sortAscending;
        set
        {
            if (_sortAscending != value)
            {
                _sortAscending = value;
                _settingsService.Current.MainSortAscending = value;
                _settingsService.Save();
                OnPropertyChanged();
                OnPropertyChanged(nameof(SortDirectionGlyph));
                NotifySortHeaderProperties();
                ApplyFilterAndSort();
            }
        }
    }

    public string SortDirectionGlyph => SortAscending ? "\uE70E" : "\uE70D"; // ChevronUp / ChevronDown

    public string SortKeyLabel => SortKey switch
    {
        "BookType" => "種別",
        "Rating" => "レート",
        "Author" => Settings.EffectiveAuthorLabel,
        "Genre" => Settings.EffectiveGenreLabel,
        "AddedDate" => "登録日",
        "Pages" => "ページ数",
        _ => "タイトル"
    };

    public string BookTypeSortHeader => SortHeader("種別", "BookType");
    public string TitleSortHeader => SortHeader("タイトル", "Title");
    public string RatingSortHeader => SortHeader("レート", "Rating");
    public string AuthorSortHeader => SortHeader(Settings.EffectiveAuthorLabel, "Author");
    public string GenreSortHeader => SortHeader(Settings.EffectiveGenreLabel, "Genre");
    public string AddedDateSortHeader => SortHeader("登録日", "AddedDate");

    private string SortHeader(string label, string key) =>
        SortKey == key ? $"{label} {(SortAscending ? "▲" : "▼")}" : label;

    private void NotifySortHeaderProperties()
    {
        OnPropertyChanged(nameof(BookTypeSortHeader));
        OnPropertyChanged(nameof(TitleSortHeader));
        OnPropertyChanged(nameof(RatingSortHeader));
        OnPropertyChanged(nameof(AuthorSortHeader));
        OnPropertyChanged(nameof(GenreSortHeader));
        OnPropertyChanged(nameof(AddedDateSortHeader));
    }

    public void SetViewMode(bool isGrid)
    {
        IsGridView = isGrid;
    }

    public void SetSortKey(string? key)
    {
        if (!string.IsNullOrEmpty(key))
        {
            if (SortKey == key)
            {
                SortAscending = !SortAscending;
            }
            else
            {
                SortKey = key;
                SortAscending = true;
            }
        }
    }

    public void ToggleSortDirection()
    {
        SortAscending = !SortAscending;
    }

    #endregion

    #region Filters

    public bool IsUnreadOnlyFilter
    {
        get => _isUnreadOnlyFilter;
        set
        {
            if (_isUnreadOnlyFilter != value)
            {
                _isUnreadOnlyFilter = value;
                OnPropertyChanged();
                ApplyFilterAndSort();
            }
        }
    }

    public bool IsTypeFilterAll => _selectedTypeFilters.Count == 0;
    public bool IsType0Selected => _selectedTypeFilters.Contains(0);
    public bool IsType1Selected => _selectedTypeFilters.Contains(1);
    public bool IsType2Selected => _selectedTypeFilters.Contains(2);
    public bool IsType3Selected => _selectedTypeFilters.Contains(3);
    public bool IsType4Selected => _selectedTypeFilters.Contains(4);
    public bool IsType5Selected => _selectedTypeFilters.Contains(5);

    public void ToggleTypeFilter(int? typeIndex)
    {
        if (!typeIndex.HasValue)
        {
            _selectedTypeFilters.Clear();
        }
        else
        {
            if (_selectedTypeFilters.Contains(typeIndex.Value))
                _selectedTypeFilters.Remove(typeIndex.Value);
            else
                _selectedTypeFilters.Add(typeIndex.Value);
        }
        NotifyTypeFilterProperties();
        ApplyFilterAndSort();
    }

    public void ToggleRatingFilter(int rating)
    {
        if (_selectedRatingFilters.Contains(rating))
            _selectedRatingFilters.Remove(rating);
        else
            _selectedRatingFilters.Add(rating);

        NotifyRatingFilterProperties();
        ApplyFilterAndSort();
    }

    public bool IsRating1Selected => _selectedRatingFilters.Contains(1);
    public bool IsRating2Selected => _selectedRatingFilters.Contains(2);
    public bool IsRating3Selected => _selectedRatingFilters.Contains(3);
    public bool IsRating4Selected => _selectedRatingFilters.Contains(4);
    public bool IsRating5Selected => _selectedRatingFilters.Contains(5);

    private void NotifyTypeFilterProperties()
    {
        OnPropertyChanged(nameof(IsTypeFilterAll));
        OnPropertyChanged(nameof(IsType0Selected));
        OnPropertyChanged(nameof(IsType1Selected));
        OnPropertyChanged(nameof(IsType2Selected));
        OnPropertyChanged(nameof(IsType3Selected));
        OnPropertyChanged(nameof(IsType4Selected));
        OnPropertyChanged(nameof(IsType5Selected));
    }

    private void NotifyRatingFilterProperties()
    {
        OnPropertyChanged(nameof(IsRating1Selected));
        OnPropertyChanged(nameof(IsRating2Selected));
        OnPropertyChanged(nameof(IsRating3Selected));
        OnPropertyChanged(nameof(IsRating4Selected));
        OnPropertyChanged(nameof(IsRating5Selected));
    }

    public void ClearFilters()
    {
        _isUnreadOnlyFilter = false;
        _selectedTypeFilters.Clear();
        _selectedRatingFilters.Clear();
        _searchQuery = string.Empty;

        OnPropertyChanged(nameof(IsUnreadOnlyFilter));
        OnPropertyChanged(nameof(SearchQuery));
        NotifyTypeFilterProperties();
        NotifyRatingFilterProperties();
        ApplyFilterAndSort();
    }

    #endregion

    #region Collection & Item Selection

    public string CurrentCollectionTitle
    {
        get => _currentCollectionTitle;
        private set
        {
            if (_currentCollectionTitle != value)
            {
                _currentCollectionTitle = value;
                OnPropertyChanged();
            }
        }
    }

    public string ItemsCountText => $"{Books.Count:N0} 項目";
    /// <summary>
    /// The whole library, which is no longer the same as what is loaded: selecting a
    /// shelf loads only that shelf's books.
    /// </summary>
    private int _libraryCount;

    public string TotalBooksCountText => $"蔵書計: {_libraryCount:N0} 冊 (表示中: {Books.Count:N0} 冊)";

    public bool IsLoading
    {
        get => _isLoading;
        set { _isLoading = value; OnPropertyChanged(); }
    }

    public bool IsEmpty => Books.Count == 0;

    public string SearchQuery
    {
        get => _searchQuery;
        set
        {
            if (_searchQuery != value)
            {
                _searchQuery = value;
                OnPropertyChanged();
                ApplyFilterAndSort();
            }
        }
    }

    public Shelf? SelectedShelf
    {
        get => _selectedShelf;
        set
        {
            _selectedShelf = value;
            _isUnreadCollectionSelected = false;
            OnPropertyChanged();
            CurrentCollectionTitle = _selectedShelf?.Title ?? "すべての項目";

            // Shelf membership is not held on the loaded items, and a smart shelf has no
            // membership at all, so which books a shelf holds is a question only the
            // database can answer.
            _ = RefreshBooksAsync();
        }
    }

    public void SelectAllBooksCollection()
    {
        _selectedShelf = null;
        _isUnreadCollectionSelected = false;
        CurrentCollectionTitle = "すべての項目";
        OnPropertyChanged(nameof(SelectedShelf));
        _ = RefreshBooksAsync();
    }

    public void SelectUnreadCollection()
    {
        _selectedShelf = null;
        _isUnreadCollectionSelected = true;
        CurrentCollectionTitle = "未読";
        OnPropertyChanged(nameof(SelectedShelf));
        _ = RefreshBooksAsync();
    }

    public ItemViewModel? SelectedItem
    {
        get => _selectedItem;
        set
        {
            if (_selectedItem != value)
            {
                _selectedItem = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasSelectedItem));
                OnPropertyChanged(nameof(NoSelectedItem));
            }
        }
    }

    public bool HasSelectedItem => SelectedItem != null;
    public bool NoSelectedItem => SelectedItem == null;

    public string StatusMessage
    {
        get => _statusMessage;
        set { _statusMessage = value; OnPropertyChanged(); }
    }

    #endregion

    #region Data Loading & Filtering

    public async Task InitializeAsync()
    {
        IsLoading = true;
        StatusMessage = "ライブラリを読み込み中...";

        try
        {
            await _repository.InitializeAsync();
            await LoadShelvesAsync();
            await LoadVolumesAsync();
            await RefreshBooksAsync();
        }
        catch (Exception ex)
        {
            StatusMessage = $"初期化エラー: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    public async Task RefreshBooksAsync()
    {
        IsLoading = true;
        try
        {
            // Reading twenty thousand rows and building a view model for each is far too
            // much for the UI thread; doing it there is what left the window black.
            var items = await Task.Run(() => _repository.GetItemsAsync(0, int.MaxValue, _selectedShelf?.Id));

            _allItemModels.Clear();
            _allItemModels.AddRange(items);

            _libraryCount = await Task.Run(() => _repository.GetItemCountAsync());

            await Task.Run(ApplyFilterAndSort);
            StatusMessage = $"ライブラリ読み込み完了 ({_allItemModels.Count:N0} 冊)";
        }
        catch (Exception ex)
        {
            StatusMessage = $"読み込み失敗: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    public void ApplyFilterAndSort()
    {
        IEnumerable<Item> query = _allItemModels;

        // Collection filter
        if (_isUnreadCollectionSelected)
        {
            query = query.Where(i => i.IsUnread);
        }
        // The shelf itself is applied by the query that loaded these items, not here.

        // Search text
        if (!string.IsNullOrWhiteSpace(_searchQuery))
        {
            string q = _searchQuery.Trim();
            query = query.Where(i => KeywordEquivalenceMatcher.Matches(
                i, q, Settings.KeywordEquivalenceRules));
        }

        // Unread filter
        if (_isUnreadOnlyFilter)
        {
            query = query.Where(i => i.IsUnread);
        }

        // Type filter
        if (_selectedTypeFilters.Count > 0)
        {
            query = query.Where(i => _selectedTypeFilters.Contains(i.BookType));
        }

        // Rating filter
        if (_selectedRatingFilters.Count > 0)
        {
            query = query.Where(i => _selectedRatingFilters.Contains(i.Rating));
        }

        // Sorting
        query = SortKey switch
        {
            "BookType" => SortAscending ? query.OrderBy(i => i.BookType).ThenBy(i => i.Title) : query.OrderByDescending(i => i.BookType).ThenBy(i => i.Title),
            "Rating" => SortAscending ? query.OrderBy(i => i.Rating).ThenBy(i => i.Title) : query.OrderByDescending(i => i.Rating).ThenBy(i => i.Title),
            "Author" => SortAscending ? query.OrderBy(i => i.Author).ThenBy(i => i.Title) : query.OrderByDescending(i => i.Author).ThenBy(i => i.Title),
            "Genre" => SortAscending ? query.OrderBy(i => i.Genre).ThenBy(i => i.Title) : query.OrderByDescending(i => i.Genre).ThenBy(i => i.Title),
            "AddedDate" => SortAscending ? query.OrderBy(i => i.AddedDate) : query.OrderByDescending(i => i.AddedDate),
            "Pages" => SortAscending ? query.OrderBy(i => i.Pages) : query.OrderByDescending(i => i.Pages),
            _ => SortAscending ? query.OrderBy(i => i.Title) : query.OrderByDescending(i => i.Title)
        };

        var filteredList = query.Select(item => new ItemViewModel(
            item, _thumbnailManager, _imageLoader, OnItemModelChanged, _coverGenerator)).ToList();

        RunOnUI(() =>
        {
            Books = new ObservableCollection<ItemViewModel>(filteredList);
            OnPropertyChanged(nameof(Books));

            if (SelectedItem != null)
            {
                SelectedItem = Books.FirstOrDefault(b => b.Id == SelectedItem.Id);
            }
            if (SelectedItem == null && Books.Count > 0)
            {
                SelectedItem = Books[0];
            }

            OnPropertyChanged(nameof(ItemsCountText));
            OnPropertyChanged(nameof(TotalBooksCountText));
            OnPropertyChanged(nameof(IsEmpty));
        });
    }



    /// <summary>
    /// Opens a book in an external viewer and marks it read, as double-clicking does on
    /// the Mac. ShelfRow has no viewer of its own.
    /// </summary>
    public async Task OpenItemAsync(ItemViewModel? itemViewModel, CancellationToken cancellationToken = default)
    {
        if (itemViewModel == null) return;

        var item = itemViewModel.Model;
        var volume = item.VolumeId.HasValue
            ? await _repository.GetVolumeByIdAsync(item.VolumeId.Value, cancellationToken)
            : null;

        var result = await Task.Run(() => _bookLauncher.Open(item, volume), cancellationToken);

        if (!result.Opened)
        {
            StatusMessage = result.Error ?? "ファイルを開けませんでした。";
            return;
        }

        StatusMessage = $"開きました: {item.Title}";

        if (itemViewModel.IsUnread)
        {
            item.LastReadDate = DateTime.UtcNow;

            // The setter notifies and persists.
            itemViewModel.IsUnread = false;
        }
    }

    public async Task DeleteItemAsync(ItemViewModel itemViewModel, CancellationToken cancellationToken = default)
    {
        try
        {
            await _repository.DeleteItemAsync(itemViewModel.Id, cancellationToken);
            StatusMessage = $"ライブラリから削除しました: {itemViewModel.Title}";
            await RefreshBooksAsync();
        }
        catch (Exception ex)
        {
            StatusMessage = $"削除に失敗しました: {ex.Message}";
        }
    }

    private void OnItemModelChanged(Item item)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await _repository.UpsertItemAsync(item);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to persist item: {ex.Message}");
            }
        });
    }

    public void SearchKeyword(string keyword, bool inAllLibrary)
    {
        if (inAllLibrary)
        {
            _selectedShelf = null;
            _isUnreadCollectionSelected = false;
            CurrentCollectionTitle = "すべての項目";
            OnPropertyChanged(nameof(SelectedShelf));
        }
        SearchQuery = keyword;
    }

    #endregion

    #region Shelves Management

    public async Task LoadShelvesAsync()
    {
        try
        {
            var list = await _repository.GetShelvesAsync();
            RunOnUI(() =>
            {
                Shelves.Clear();
                StaticShelves.Clear();
                SmartShelves.Clear();
                foreach (var s in list)
                {
                    Shelves.Add(s);
                    if (s.IsSmart)
                        SmartShelves.Add(s);
                    else
                        StaticShelves.Add(s);
                }
            });
        }
        catch (Exception ex)
        {
            StatusMessage = $"シェルフ読み込み失敗: {ex.Message}";
        }
    }

    public async Task CreateStandardShelfAsync()
    {
        var newShelf = new Shelf
        {
            Title = "新規標準シェルフ",
            Type = 0,
            SortOrder = StaticShelves.Count
        };
        await _repository.UpsertShelfAsync(newShelf);
        await LoadShelvesAsync();
        SelectedShelf = newShelf;
    }

    public async Task CreateSmartShelfAsync()
    {
        var newShelf = new Shelf
        {
            Title = "新規スマートシェルフ",
            Type = 1,
            SortOrder = SmartShelves.Count
        };
        await _repository.UpsertShelfAsync(newShelf);
        await LoadShelvesAsync();
        SelectedShelf = newShelf;
    }

    public async Task SaveShelfAsync(Shelf shelf)
    {
        await _repository.UpsertShelfAsync(shelf);
        await LoadShelvesAsync();
        SelectedShelf = Shelves.FirstOrDefault(existing => existing.Id == shelf.Id);
    }

    public async Task DeleteShelfAsync(Shelf shelf)
    {
        await _repository.DeleteShelfAsync(shelf.Id);
        await LoadShelvesAsync();
        if (SelectedShelf?.Id == shelf.Id)
        {
            SelectAllBooksCollection();
        }
    }

    public async Task SetItemShelfMembershipAsync(ItemViewModel itemViewModel, Shelf shelf, bool isMember)
    {
        if (shelf.IsSmart) return;

        Item item = itemViewModel.Model;
        bool alreadyMember = item.ShelfIds.Contains(shelf.Id);
        if (alreadyMember == isMember) return;

        if (isMember)
            item.ShelfIds.Add(shelf.Id);
        else
            item.ShelfIds.Remove(shelf.Id);

        try
        {
            await _repository.UpsertItemAsync(item);
            StatusMessage = isMember
                ? $"「{item.Title}」を「{shelf.Title}」に追加しました"
                : $"「{item.Title}」を「{shelf.Title}」から外しました";

            if (_selectedShelf?.Id == shelf.Id && !isMember)
                await RefreshBooksAsync();
        }
        catch (Exception ex)
        {
            if (isMember)
                item.ShelfIds.Remove(shelf.Id);
            else
                item.ShelfIds.Add(shelf.Id);
            StatusMessage = $"シェルフの更新に失敗しました: {ex.Message}";
        }
    }

    #endregion

    #region Volumes & Thumbnails & XML Import

    public bool HasVolumes => Volumes.Count > 0;
    public bool HasNoVolumes => Volumes.Count == 0;

    public async Task LoadVolumesAsync()
    {
        try
        {
            var list = await _repository.GetVolumesAsync();
            string? distributionRoot = _settingsService.Current.ThumbnailDistributionRoot;
            if (string.IsNullOrWhiteSpace(distributionRoot) || !Directory.Exists(distributionRoot))
            {
                distributionRoot = list
                    .Select(volume => ThumbnailStorageManager.FindDistributionRoot(volume.WindowsMountPath))
                    .FirstOrDefault(root => root is not null);
            }
            _imageLoader?.SetDistributionRoot(distributionRoot);
            RunOnUI(() =>
            {
                Volumes.Clear();
                foreach (var v in list)
                {
                    Volumes.Add(new VolumeViewModel(v));
                }
            });
        }
        catch (Exception ex)
        {
            StatusMessage = $"ボリューム読み込み失敗: {ex.Message}";
        }
    }

    public async Task SaveVolumeAsync(VolumeViewModel volumeVm)
    {
        try
        {
            // This dialog only edits the Windows mount path, which belongs to this
            // machine and never travels through iCloud, so saving it must not queue the
            // volume for upload.
            await _repository.UpsertVolumeAsync(volumeVm.Model, markPendingUpload: false);
            StatusMessage = $"ボリューム「{volumeVm.Name}」の設定を保存しました";
        }
        catch (Exception ex)
        {
            StatusMessage = $"ボリューム保存失敗: {ex.Message}";
        }
    }

    public async Task AddVolumeAsync(string posixPath, string windowsPath)
    {
        var volume = new Volume
        {
            Name = Path.GetFileName(posixPath.TrimEnd('/')),
            LastKnownPath = posixPath,
            WindowsMountPath = windowsPath
        };
        await _repository.UpsertVolumeAsync(volume);
        await LoadVolumesAsync();
    }

    public async Task SyncWithCloudKitAsync(CancellationToken cancellationToken = default)
    {
        if (IsLoading)
        {
            App.Log("Sync: ignored, another operation is already running");
            return;
        }

        IsLoading = true;
        StatusMessage = "iCloudと同期中...";
        try
        {
            await _cloudKitAccount.LoadAsync(cancellationToken);
            App.Log($"Sync: starting. hasApiToken={_cloudKitAccount.HasApiToken} signedIn={_cloudKitAccount.IsSignedIn} env={_cloudKitAccount.Environment}");

            if (!_cloudKitAccount.HasApiToken)
            {
                StatusMessage = "iCloud: APIトークンが未設定です。設定 > iCloud で入力してください。";
                App.Log("Sync: aborted, no API token");
                return;
            }

            // Send local edits first, so the download that follows brings back the
            // server's view of them rather than overwriting them with a stale copy.
            var uploadProgress = new Progress<int>(n => StatusMessage = $"iCloudへ送信中... {n:N0} 件");
            var progress = new Progress<int>(n => StatusMessage = $"iCloudと同期中... {n:N0} 件");

            // A first sync is tens of thousands of records to parse and write. On the UI
            // thread that freezes the window for minutes and Windows paints it black, so
            // the whole exchange runs off it. Progress reports come back here on their
            // own, because Progress<T> captures this thread when it is created.
            var upload = await Task.Run(() => _cloudKitAccount.ExecuteAsync(
                ct => _syncEngine.SyncUpAsync(uploadProgress, ct),
                cancellationToken), cancellationToken);

            var result = await Task.Run(() => _cloudKitAccount.ExecuteAsync(
                ct => _syncEngine.SyncDownAsync(progress, ct),
                cancellationToken), cancellationToken);

            await RefreshBooksAsync();
            await LoadShelvesAsync();
            await LoadVolumesAsync();

            string downText = result.Items == 0 && result.Shelves == 0 && result.Volumes == 0 && result.Deletions == 0
                ? "受信なし"
                : $"受信 本 {result.Items:N0} 件 / 本棚 {result.Shelves} / ボリューム {result.Volumes}";

            string upText = upload.Uploaded == 0 ? "送信なし" : $"送信 {upload.Uploaded:N0} 件";
            string problems = upload.Conflicted > 0 || upload.Failed > 0
                ? $" (競合 {upload.Conflicted} / 失敗 {upload.Failed})"
                : string.Empty;

            StatusMessage = $"iCloud同期完了: {upText}, {downText}{problems}";
        }
        catch (CloudKitException ex) when (ex.IsAuthenticationRequired)
        {
            StatusMessage = ex.RedirectUrl is null
                ? $"iCloud: {ex.ServerErrorCode}。選択した環境のAPIトークンを確認してください。"
                : "iCloud: サインインが完了していません。";
            App.Log($"Sync: authentication not completed. code={ex.ServerErrorCode} hasRedirect={ex.RedirectUrl != null}");
        }
        catch (CloudKitException ex)
        {
            StatusMessage = $"iCloud同期エラー: {ex.ServerErrorCode} - {ex.Reason}";
            App.Log($"Sync: CloudKit error {ex.ServerErrorCode} - {ex.Reason}");
        }
        catch (Exception ex)
        {
            StatusMessage = $"iCloud同期エラー: {ex.Message}";
            App.Log($"Sync: failed {ex}");
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// Queues every row for upload and syncs. For repairing a library that iCloud has
    /// fallen behind on.
    /// </summary>
    public async Task ResendEverythingToCloudAsync(CancellationToken cancellationToken = default)
    {
        await _repository.MarkAllPendingUploadAsync(cancellationToken);
        await SyncWithCloudKitAsync(cancellationToken);
    }

    public async Task PurgeCloudDataAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            StatusMessage = "iCloudのデータを削除しています...";
            await _cloudKitAccount.LoadAsync(cancellationToken);
            await _cloudKitAccount.DeleteCoreDataZoneAsync(cancellationToken);
            await _repository.DeleteSyncMetadataAsync(CloudKitSyncEngine.SyncTokenKey, cancellationToken);
            await _cloudKitAccount.SignOutAsync(cancellationToken);
            StatusMessage = "iCloudのデータを削除し、サインアウトしました。端末内の蔵書は残っています。";
        }
        catch (Exception ex)
        {
            StatusMessage = $"iCloudデータの削除に失敗しました: {ex.Message}";
        }
    }

    private bool _isSyncingThumbnails;
    public bool IsSyncingThumbnails
    {
        get => _isSyncingThumbnails;
        set
        {
            _isSyncingThumbnails = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanSyncThumbnails));
            OnPropertyChanged(nameof(ThumbnailSyncProgressVisibility));
        }
    }

    public bool CanSyncThumbnails => !IsSyncingThumbnails;
    public Microsoft.UI.Xaml.Visibility ThumbnailSyncProgressVisibility => IsSyncingThumbnails ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;

    private string _thumbnailSyncProgressText = string.Empty;
    public string ThumbnailSyncProgressText
    {
        get => _thumbnailSyncProgressText;
        set { _thumbnailSyncProgressText = value; OnPropertyChanged(); }
    }

    public async Task SyncAllThumbnailsAsync(string? specificNasPath = null, CancellationToken cancellationToken = default)
    {
        if (IsSyncingThumbnails) return;
        IsSyncingThumbnails = true;
        ThumbnailSyncProgressText = "NAS サムネイル共有フォルダを探索中...";
        StatusMessage = "NAS サムネイル同期中...";

        var aggregateResult = new ThumbnailSyncResult();
        try
        {
            var roots = new List<string>();
            if (!string.IsNullOrWhiteSpace(specificNasPath) && Directory.Exists(specificNasPath))
            {
                roots.Add(specificNasPath);
            }
            else if (!string.IsNullOrWhiteSpace(_settingsService.Current.ThumbnailDistributionRoot) && Directory.Exists(_settingsService.Current.ThumbnailDistributionRoot))
            {
                roots.Add(_settingsService.Current.ThumbnailDistributionRoot);
            }
            else
            {
                foreach (var vol in Volumes)
                {
                    string? root = ThumbnailStorageManager.FindDistributionRoot(vol.WindowsMountPath);
                    if (root != null && !roots.Contains(root))
                    {
                        roots.Add(root);
                    }
                }
            }

            if (roots.Count == 0)
            {
                ThumbnailSyncProgressText = "NAS サムネイル配布フォルダ (ShelfRowThumbnails) が見つかりませんでした。";
                StatusMessage = "NAS サムネイルフォルダが見つかりません";
                return;
            }

            var progress = new Progress<ThumbnailSyncProgress>(p =>
            {
                ThumbnailSyncProgressText = $"サムネイルを取得中: {p.Processed} / {p.Total} 件";
            });

            var libraryItemIds = await _repository.GetAllItemIdsAsync(cancellationToken);
            var localStates = (await _repository.GetLocalCoverStatesAsync(cancellationToken))
                .ToDictionary(state => state.ItemId);

            foreach (var root in roots)
            {
                if (_coverGenerator is not null)
                    await _coverGenerator.PublishPendingAsync(root, cancellationToken);
                var res = await _thumbnailManager.SyncAllThumbnailsFromNasAsync(
                    root,
                    libraryItemIds,
                    localStates,
                    progress,
                    maxConcurrency: Math.Clamp(Settings.ThumbnailConcurrency, 1, 32),
                    cancellationToken);
                aggregateResult.TotalFoundInNas += res.TotalFoundInNas;
                aggregateResult.Fetched += res.Fetched;
                aggregateResult.AlreadyCached += res.AlreadyCached;
                aggregateResult.Failed += res.Failed;
                aggregateResult.SuppressedAfterFailures += res.SuppressedAfterFailures;
                aggregateResult.ManifestMissing |= res.ManifestMissing;
                if (res.StateUpdates.Count > 0)
                {
                    await _repository.UpsertLocalCoverStatesAsync(res.StateUpdates, cancellationToken);
                    foreach (var state in res.StateUpdates)
                        localStates[state.ItemId] = state;
                }
            }

            string msg = aggregateResult.ManifestMissing && aggregateResult.TotalFoundInNas == 0
                ? "サムネイル同期を中止しました: 配布マニフェストがありません"
                : $"サムネイル同期完了: {aggregateResult.Fetched} 件取得、" +
                  $"{aggregateResult.AlreadyCached} 件キャッシュ済み、" +
                  $"{aggregateResult.Failed} 件失敗、{aggregateResult.SuppressedAfterFailures} 件再試行停止";
            ThumbnailSyncProgressText = msg;
            StatusMessage = msg;

            if (aggregateResult.Fetched > 0)
            {
                _imageLoader?.ClearMemoryCache();
                _imageLoader?.InvalidateManifest();
                RunOnUI(() =>
                {
                    foreach (var book in Books)
                    {
                        book.ReloadThumbnail();
                    }
                });
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"サムネイル同期エラー: {ex.Message}";
            ThumbnailSyncProgressText = $"エラー: {ex.Message}";
        }
        finally
        {
            IsSyncingThumbnails = false;
        }
    }

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

    public async Task ImportXmlFileAsync(string xmlFilePath, CancellationToken cancellationToken = default)
    {
        if (IsImporting) return;
        IsImporting = true;
        StatusMessage = "Stackroom XML をインポート中...";

        try
        {
            var importer = new StackroomXmlImporter();
            var progress = new Progress<ImportProgress>(p =>
            {
                ImportProgressText = $"XML解析中: 書籍 {p.ProcessedBooks} / {p.TotalBooks} 冊, プレイリスト {p.ProcessedShelves} / {p.TotalShelves} 件";
            });

            await using var stream = File.OpenRead(xmlFilePath);
            string inferredLegacyAssets = Path.Combine(
                Path.GetDirectoryName(xmlFilePath) ?? string.Empty,
                Path.GetFileNameWithoutExtension(xmlFilePath));
            var mergeContext = new StackroomImportMergeContext(
                await _repository.GetItemsForImportMergeAsync(cancellationToken),
                await _repository.GetShelvesAsync(cancellationToken),
                await _repository.GetVolumesAsync(cancellationToken),
                Directory.Exists(inferredLegacyAssets) ? inferredLegacyAssets : null,
                _thumbnailManager.LocalCacheDirectory);
            var result = await importer.ImportAsync(stream, mergeContext, progress, cancellationToken);
            StatusMessage = "インポートデータをデータベースへ保存中...";

            foreach (var vol in result.DiscoveredVolumes)
            {
                await _repository.UpsertVolumeAsync(vol, cancellationToken: cancellationToken);
            }

            foreach (var shelf in result.ImportedShelves)
            {
                await _repository.UpsertShelfAsync(shelf, cancellationToken: cancellationToken);
            }

            var itemsToSave = result.ImportedBooks
                .Concat(result.UpdatedExistingBooks)
                .ToList();
            if (itemsToSave.Count > 0)
            {
                await _repository.UpsertItemsBatchAsync(itemsToSave, cancellationToken: cancellationToken);
            }

            await RefreshBooksAsync();
            await LoadShelvesAsync();
            await LoadVolumesAsync();

            string completionMessage = $"XMLインポートが完了しました。\n" +
                $"書籍: {result.ImportedBooks.Count:N0} 冊（既存 {result.SkippedBooks:N0} 冊）\n" +
                $"シェルフ: {result.ImportedShelves.Count:N0} 件（既存 {result.SkippedShelves:N0} 件）";
            StatusMessage = completionMessage;
            ImportCompletedNotification?.Invoke(completionMessage);
        }
        catch (Exception ex)
        {
            string err = $"XMLインポート失敗: {ex.Message}";
            StatusMessage = err;
            ImportCompletedNotification?.Invoke(err);
        }
        finally
        {
            IsImporting = false;
            ImportProgressText = string.Empty;
        }
    }

    #endregion

    private void RunOnUI(Action action)
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
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
