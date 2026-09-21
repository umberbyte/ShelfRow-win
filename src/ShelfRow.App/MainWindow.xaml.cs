using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ShelfRow.App.ViewModels;
using ShelfRow.App.Views;
using ShelfRow.Core.Models;

namespace ShelfRow.App;

public sealed partial class MainWindow : Window
{
    private MainViewModel? _viewModel;
    private PreferencesWindow? _preferencesWindow;

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
            }
        }
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
        if (_viewModel != null)
        {
            await _viewModel.CreateSmartShelfAsync();
        }
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
