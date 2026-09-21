using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
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
            }

            _viewModel = value;
            if (_viewModel != null)
            {
                _viewModel.OpenSettingsRequested += ViewModel_OpenSettingsRequested;
                _viewModel.ImportCompletedNotification += ViewModel_ImportCompletedNotification;
                ApplyStartupLock();
            }
        }
    }

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
        if (_viewModel != null)
        {
            await _viewModel.CreateStandardShelfAsync();
        }
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
        if (sender is MenuFlyoutItem item && item.Tag is Shelf shelf && _viewModel != null)
        {
            await _viewModel.DeleteShelfAsync(shelf);
        }
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
