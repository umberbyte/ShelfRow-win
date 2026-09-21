using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Windowing;
using Windows.System;
using ShelfRow.App.ViewModels;
using ShelfRow.App.Views;
using ShelfRow.Core.Models;

namespace ShelfRow.App;

public sealed partial class MainWindow : Window
{
    private MainViewModel? _viewModel;
    private PreferencesWindow? _preferencesWindow;
    private string _stampTarget = "KeywordA";
    private int _lastRestoredWidth = 1280;
    private int _lastRestoredHeight = 800;

    public MainWindow()
    {
        this.InitializeComponent();
        Title = "ShelfRow";
        try
        {
            this.AppWindow.Resize(new Windows.Graphics.SizeInt32(1280, 800));
        }
        catch { }

        // A list marks Enter handled for its own selection behaviour, so an ordinary
        // KeyDown attached in markup never sees it. Registering for handled events too
        // is the only way to act on it.
        var enterHandler = new Microsoft.UI.Xaml.Input.KeyEventHandler(BookList_KeyDown);
        BookGridView.AddHandler(UIElement.KeyDownEvent, enterHandler, handledEventsToo: true);
        BookListView.AddHandler(UIElement.KeyDownEvent, enterHandler, handledEventsToo: true);
        AppWindow.Changed += MainWindow_Changed;
        AppWindow.Closing += MainWindow_Closing;
    }

    public MainViewModel? ViewModel
    {
        get => _viewModel;
        set
        {
            if (_viewModel != null)
            {
                _viewModel.OpenSettingsRequested -= ViewModel_OpenSettingsRequested;
                _viewModel.ImportCompletedNotification -= ViewModel_ImportCompletedNotification;
                _viewModel.PropertyChanged -= ViewModel_PropertyChanged;
            }

            _viewModel = value;
            if (_viewModel != null)
            {
                _viewModel.OpenSettingsRequested += ViewModel_OpenSettingsRequested;
                _viewModel.ImportCompletedNotification += ViewModel_ImportCompletedNotification;
                _viewModel.PropertyChanged += ViewModel_PropertyChanged;
                ApplySavedWindowSize();
                ApplyDisplaySettings();
                ApplyStartupLock();
            }
        }
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.Settings))
            ApplyDisplaySettings();
    }

    private void ApplyDisplaySettings()
    {
        if (_viewModel is null) return;

        RootGrid.RequestedTheme = _viewModel.Settings.AppearanceMode.ToLowerInvariant() switch
        {
            "light" => ElementTheme.Light,
            "dark" => ElementTheme.Dark,
            _ => ElementTheme.Default
        };

        bool compact = _viewModel.Settings.CompactDisplay;
        SidebarColumn.Width = new GridLength(compact ? 195 : 260);
        InspectorColumn.Width = new GridLength(compact ? 240 : 320);
        SidebarHeader.Padding = compact ? new Thickness(12, 10, 12, 8) : new Thickness(18, 16, 18, 12);
        SidebarScroll.Padding = compact ? new Thickness(4, 0, 4, 4) : new Thickness(8, 0, 8, 8);
        SidebarFooter.Padding = compact ? new Thickness(8, 5, 8, 5) : new Thickness(12, 8, 12, 8);
        TopHeader.Padding = compact ? new Thickness(15, 10, 15, 7) : new Thickness(20, 14, 20, 10);
        FilterPanel.Padding = compact ? new Thickness(15, 0, 15, 8) : new Thickness(20, 0, 20, 12);
        BookGridView.Padding = compact ? new Thickness(15, 12, 15, 12) : new Thickness(20, 16, 20, 16);
        BookListView.Padding = compact ? new Thickness(9, 6, 9, 6) : new Thickness(12, 8, 12, 8);
        InspectorScroll.Padding = compact ? new Thickness(12, 15, 12, 18) : new Thickness(16, 20, 16, 24);
    }

    private void MainWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        SaveWindowSize();

        if (_viewModel?.Settings.CloseOnExit != false)
        {
            _preferencesWindow?.Close();
            return;
        }

        // macOS keeps the app available in the Dock. The Windows equivalent keeps
        // the taskbar button alive by minimizing rather than leaving a hidden,
        // unreachable process behind.
        args.Cancel = true;
        if (sender.Presenter is OverlappedPresenter presenter)
            presenter.Minimize();
    }

    private void ApplySavedWindowSize()
    {
        if (_viewModel is null) return;

        int maxWidth = 10000;
        int maxHeight = 10000;
        try
        {
            var display = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
            maxWidth = Math.Max(640, display.WorkArea.Width);
            maxHeight = Math.Max(480, display.WorkArea.Height);
        }
        catch
        {
        }

        int minWidth = Math.Min(960, maxWidth);
        int minHeight = Math.Min(600, maxHeight);
        _lastRestoredWidth = Math.Clamp(_viewModel.Settings.MainWindowWidth, minWidth, maxWidth);
        _lastRestoredHeight = Math.Clamp(_viewModel.Settings.MainWindowHeight, minHeight, maxHeight);
        AppWindow.Resize(new Windows.Graphics.SizeInt32(_lastRestoredWidth, _lastRestoredHeight));
    }

    private void MainWindow_Changed(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (!args.DidSizeChange)
            return;

        if (sender.Presenter is OverlappedPresenter presenter
            && presenter.State != OverlappedPresenterState.Restored)
            return;

        _lastRestoredWidth = sender.Size.Width;
        _lastRestoredHeight = sender.Size.Height;
    }

    private void SaveWindowSize() =>
        _viewModel?.SaveMainWindowSize(_lastRestoredWidth, _lastRestoredHeight);

    private void ApplyStartupLock()
    {
        bool shouldLock = _viewModel?.Settings.PasswordLockEnabled == true
                          && !string.IsNullOrEmpty(_viewModel.Settings.PasswordValue);
        LockOverlay.Visibility = shouldLock ? Visibility.Visible : Visibility.Collapsed;
        if (shouldLock)
            LockPasswordBox.Focus(FocusState.Programmatic);
    }

    private void Unlock_Click(object sender, RoutedEventArgs e) => TryUnlock();

    private void LockPasswordBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            e.Handled = true;
            TryUnlock();
        }
    }

    private void LockPasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        LockErrorText.Visibility = Visibility.Collapsed;
    }

    private void TryUnlock()
    {
        if (_viewModel is not null && LockPasswordBox.Password == _viewModel.Settings.PasswordValue)
        {
            LockPasswordBox.Password = string.Empty;
            LockErrorText.Visibility = Visibility.Collapsed;
            LockOverlay.Visibility = Visibility.Collapsed;
        }
        else
        {
            LockPasswordBox.Password = string.Empty;
            LockErrorText.Visibility = Visibility.Visible;
            LockPasswordBox.Focus(FocusState.Programmatic);
        }
    }

    private async void EditCover_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel?.SelectedItem is not { } selected
            || _viewModel.CoverGenerator is not { } generator
            || Content?.XamlRoot is not { } root)
            return;

        try
        {
            bool? changed = await CoverEditorDialog.ShowAsync(root, selected.Model, generator);
            if (changed == true)
            {
                App.ImageLoader?.ClearMemoryCache();
                App.ImageLoader?.InvalidateManifest();
                selected.ReloadThumbnail();
            }
            else if (changed is null)
            {
                await new ContentDialog
                {
                    Title = "表紙を編集できません",
                    Content = "この書籍は画像を含むZIP/CBZファイルとして開けませんでした。",
                    CloseButtonText = "OK",
                    XamlRoot = root
                }.ShowAsync();
            }
        }
        catch (Exception ex)
        {
            await new ContentDialog
            {
                Title = "表紙の編集に失敗しました",
                Content = ex.Message,
                CloseButtonText = "OK",
                XamlRoot = root
            }.ShowAsync();
        }
    }

    private void InspectorTextBox_GotFocus(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox { Tag: string field }) _stampTarget = field;
    }

    private void Stamp_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string stamp }) _viewModel?.ApplyStamp(_stampTarget, stamp);
    }

    private async void EditStamps_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null || Content?.XamlRoot is not { } root) return;
        var editor = new TextBox
        {
            Text = string.Join(Environment.NewLine, _viewModel.Stamps),
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinWidth = 360,
            MinHeight = 220,
            PlaceholderText = "1行に1つ入力してください"
        };
        var dialog = new ContentDialog
        {
            Title = "スタンプを編集",
            Content = editor,
            PrimaryButtonText = "保存",
            CloseButtonText = "キャンセル",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = root
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            _viewModel.SaveStamps(editor.Text);
    }

    private void ViewModel_OpenSettingsRequested(object? sender, EventArgs e)
    {
        OpenPreferencesWindow();
    }

    private void ViewModel_ImportCompletedNotification(string message)
    {
        DispatcherQueue.TryEnqueue(async () =>
        {
            if (this.Content?.XamlRoot == null) return;
            var dialog = new ContentDialog
            {
                Title = "インポート結果",
                Content = message,
                CloseButtonText = "OK",
                XamlRoot = this.Content.XamlRoot
            };
            await dialog.ShowAsync();
        });
    }

    #region Sidebar Navigation

    private void AllBooks_Click(object sender, RoutedEventArgs e)
    {
        _viewModel?.SelectAllBooksCollection();
    }

    private void UnreadBooks_Click(object sender, RoutedEventArgs e)
    {
        _viewModel?.SelectUnreadCollection();
    }

    private void Shelf_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ListView lv && lv.SelectedItem is Shelf shelf)
        {
            if (_viewModel != null)
            {
                _viewModel.SelectedShelf = shelf;
            }
        }
    }

    private async void NewStandardShelf_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null || Content?.XamlRoot is not { } root) return;

        var shelf = new Shelf { Type = 0, SortOrder = _viewModel.StaticShelves.Count };
        if (await EditStandardShelfTitleAsync(root, shelf, "新規標準シェルフ"))
            await _viewModel.SaveShelfAsync(shelf);
    }

    private async void EditStandardShelf_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem { Tag: Shelf shelf }
            && _viewModel is not null
            && Content?.XamlRoot is { } root
            && await EditStandardShelfTitleAsync(root, shelf, "シェルフ名を変更"))
        {
            await _viewModel.SaveShelfAsync(shelf);
        }
    }

    private static async Task<bool> EditStandardShelfTitleAsync(XamlRoot root, Shelf shelf, string title)
    {
        var editor = new TextBox
        {
            Text = shelf.Title,
            PlaceholderText = "シェルフ名",
            MinWidth = 320,
            SelectionStart = 0,
            SelectionLength = shelf.Title.Length
        };
        var dialog = new ContentDialog
        {
            Title = title,
            Content = editor,
            PrimaryButtonText = "保存",
            CloseButtonText = "キャンセル",
            DefaultButton = ContentDialogButton.Primary,
            IsPrimaryButtonEnabled = !string.IsNullOrWhiteSpace(editor.Text),
            XamlRoot = root
        };
        editor.TextChanged += (_, _) => dialog.IsPrimaryButtonEnabled = !string.IsNullOrWhiteSpace(editor.Text);
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return false;
        shelf.Title = editor.Text.Trim();
        return true;
    }

    private async void NewSmartShelf_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel != null && Content?.XamlRoot is { } root)
        {
            var shelf = new Shelf { Title = string.Empty, Type = 1, SortOrder = _viewModel.SmartShelves.Count };
            if (await SmartShelfEditorDialog.ShowAsync(root, shelf, _viewModel.Settings))
                await _viewModel.SaveShelfAsync(shelf);
        }
    }

    private async void EditSmartShelf_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem { Tag: Shelf shelf } && _viewModel is not null && Content?.XamlRoot is { } root
            && await SmartShelfEditorDialog.ShowAsync(root, shelf, _viewModel.Settings))
            await _viewModel.SaveShelfAsync(shelf);
    }

    private async void DeleteShelf_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuFlyoutItem { Tag: Shelf shelf }
            || _viewModel is null
            || Content?.XamlRoot is not { } root)
            return;

        var dialog = new ContentDialog
        {
            Title = "シェルフを削除",
            Content = $"「{shelf.Title}」を削除します。\nシェルフ内の本と実体ファイルは削除されません。",
            PrimaryButtonText = "削除",
            CloseButtonText = "キャンセル",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = root
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            await _viewModel.DeleteShelfAsync(shelf);
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        OpenPreferencesWindow();
    }

    private void OpenPreferencesWindow()
    {
        if (_viewModel == null) return;

        if (_preferencesWindow == null)
        {
            _preferencesWindow = new PreferencesWindow(_viewModel);
            _preferencesWindow.Closed += (s, args) => _preferencesWindow = null;
        }

        _preferencesWindow.Activate();
    }

    #endregion

    #region Book Context Menu

    private void BookContextFlyout_Opening(object sender, object e)
    {
        if (sender is not MenuFlyout flyout
            || flyout.Target is not FrameworkElement { DataContext: ItemViewModel book }
            || _viewModel is null)
            return;

        flyout.Items.Clear();
        var open = new MenuFlyoutItem { Text = "開く", Icon = new FontIcon { Glyph = "\uE8A7" } };
        open.Click += async (_, _) => await _viewModel.OpenItemAsync(book);
        flyout.Items.Add(open);
        flyout.Items.Add(new MenuFlyoutSeparator());

        var unread = new ToggleMenuFlyoutItem { Text = "未読", IsChecked = book.IsUnread };
        unread.Click += (_, _) => book.IsUnread = unread.IsChecked;
        flyout.Items.Add(unread);

        var ratings = new MenuFlyoutSubItem { Text = "レート" };
        for (int rating = 0; rating <= 5; rating++)
        {
            int selectedRating = rating;
            var ratingItem = new ToggleMenuFlyoutItem
            {
                Text = rating == 0 ? "なし" : new string('★', rating),
                IsChecked = book.Rating == rating
            };
            ratingItem.Click += (_, _) => book.Rating = selectedRating;
            ratings.Items.Add(ratingItem);
        }
        flyout.Items.Add(ratings);

        var types = new MenuFlyoutSubItem { Text = "種類" };
        for (int type = 0; type < 6; type++)
        {
            int selectedType = type;
            var typeItem = new ToggleMenuFlyoutItem
            {
                Text = _viewModel.Settings.GetEffectiveTypeName(type),
                IsChecked = book.BookType == type
            };
            typeItem.Click += (_, _) => book.BookType = selectedType;
            types.Items.Add(typeItem);
        }
        flyout.Items.Add(types);

        var shelves = new MenuFlyoutSubItem { Text = "標準シェルフ" };
        foreach (Shelf shelf in _viewModel.StaticShelves)
        {
            var membership = new ToggleMenuFlyoutItem
            {
                Text = shelf.Title,
                IsChecked = book.Model.ShelfIds.Contains(shelf.Id)
            };
            membership.Click += async (_, _) =>
                await _viewModel.SetItemShelfMembershipAsync(book, shelf, membership.IsChecked);
            shelves.Items.Add(membership);
        }
        if (shelves.Items.Count == 0)
            shelves.Items.Add(new MenuFlyoutItem { Text = "標準シェルフがありません", IsEnabled = false });
        flyout.Items.Add(shelves);
        flyout.Items.Add(new MenuFlyoutSeparator());

        var delete = new MenuFlyoutItem { Text = "ライブラリから削除..." };
        delete.Click += async (_, _) => await ConfirmDeleteItemAsync(book);
        flyout.Items.Add(delete);
    }

    private async Task ConfirmDeleteItemAsync(ItemViewModel book)
    {
        if (_viewModel is null || Content?.XamlRoot is not { } root) return;
        var dialog = new ContentDialog
        {
            Title = "ライブラリから削除",
            Content = $"「{book.Title}」をShelfRowのライブラリから削除します。\n実体ファイルは削除されません。",
            PrimaryButtonText = "削除",
            CloseButtonText = "キャンセル",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = root
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            await _viewModel.DeleteItemAsync(book);
    }

    #endregion

    #region View Mode & Sort Controls

    private void ViewModeList_Click(object sender, RoutedEventArgs e)
    {
        _viewModel?.SetViewMode(false);
    }

    private void ViewModeGrid_Click(object sender, RoutedEventArgs e)
    {
        _viewModel?.SetViewMode(true);
    }

    private void SortByTitle_Click(object sender, RoutedEventArgs e) => _viewModel?.SetSortKey("Title");
    private void SortByRating_Click(object sender, RoutedEventArgs e) => _viewModel?.SetSortKey("Rating");
    private void SortByAuthor_Click(object sender, RoutedEventArgs e) => _viewModel?.SetSortKey("Author");
    private void SortByAddedDate_Click(object sender, RoutedEventArgs e) => _viewModel?.SetSortKey("AddedDate");
    private void SortByPages_Click(object sender, RoutedEventArgs e) => _viewModel?.SetSortKey("Pages");

    private void ToggleSortDirection_Click(object sender, RoutedEventArgs e)
    {
        _viewModel?.ToggleSortDirection();
    }

    #endregion

    #region Live Filters

    private void FilterTypeAll_Click(object sender, RoutedEventArgs e) => _viewModel?.ToggleTypeFilter(null);
    private void FilterType0_Click(object sender, RoutedEventArgs e) => _viewModel?.ToggleTypeFilter(0);
    private void FilterType1_Click(object sender, RoutedEventArgs e) => _viewModel?.ToggleTypeFilter(1);
    private void FilterType2_Click(object sender, RoutedEventArgs e) => _viewModel?.ToggleTypeFilter(2);
    private void FilterType3_Click(object sender, RoutedEventArgs e) => _viewModel?.ToggleTypeFilter(3);
    private void FilterType4_Click(object sender, RoutedEventArgs e) => _viewModel?.ToggleTypeFilter(4);
    private void FilterType5_Click(object sender, RoutedEventArgs e) => _viewModel?.ToggleTypeFilter(5);

    private void FilterRating1_Click(object sender, RoutedEventArgs e) => _viewModel?.ToggleRatingFilter(1);
    private void FilterRating2_Click(object sender, RoutedEventArgs e) => _viewModel?.ToggleRatingFilter(2);
    private void FilterRating3_Click(object sender, RoutedEventArgs e) => _viewModel?.ToggleRatingFilter(3);
    private void FilterRating4_Click(object sender, RoutedEventArgs e) => _viewModel?.ToggleRatingFilter(4);
    private void FilterRating5_Click(object sender, RoutedEventArgs e) => _viewModel?.ToggleRatingFilter(5);

    private void ClearFilters_Click(object sender, RoutedEventArgs e)
    {
        _viewModel?.ClearFilters();
    }

    #endregion

    #region Inspector Keyword Searches

    private void SearchAuthorKeyword_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel?.SelectedItem != null && !string.IsNullOrWhiteSpace(_viewModel.SelectedItem.Author))
        {
            _viewModel.SearchKeyword(_viewModel.SelectedItem.Author, inAllLibrary: true);
        }
    }

    private void SearchKeywordA_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel?.SelectedItem != null && !string.IsNullOrWhiteSpace(_viewModel.SelectedItem.KeywordA))
        {
            _viewModel.SearchKeyword(_viewModel.SelectedItem.KeywordA, inAllLibrary: true);
        }
    }

    private void SearchKeywordB_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel?.SelectedItem != null && !string.IsNullOrWhiteSpace(_viewModel.SelectedItem.KeywordB))
        {
            _viewModel.SearchKeyword(_viewModel.SelectedItem.KeywordB, inAllLibrary: true);
        }
    }

    private void SearchGenreKeyword_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel?.SelectedItem != null && !string.IsNullOrWhiteSpace(_viewModel.SelectedItem.Genre))
        {
            _viewModel.SearchKeyword(_viewModel.SelectedItem.Genre, inAllLibrary: true);
        }
    }

    private void SearchRelationKeyword_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel?.SelectedItem != null && !string.IsNullOrWhiteSpace(_viewModel.SelectedItem.Relation))
        {
            _viewModel.SearchKeyword(_viewModel.SelectedItem.Relation, inAllLibrary: true);
        }
    }

    private async void BookList_DoubleTapped(object sender, Microsoft.UI.Xaml.Input.DoubleTappedRoutedEventArgs e)
    {
        if (_viewModel != null)
            await _viewModel.OpenItemAsync(_viewModel.SelectedItem);
    }

    private async void BookList_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Enter || _viewModel == null)
            return;

        e.Handled = true;
        await _viewModel.OpenItemAsync(_viewModel.SelectedItem);
    }

    #endregion
}
