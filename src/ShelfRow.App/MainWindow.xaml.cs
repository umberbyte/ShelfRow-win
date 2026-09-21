using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ShelfRow.App.ViewModels;
using ShelfRow.Core.Models;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace ShelfRow.App;

public sealed partial class MainWindow : Window
{
    private MainViewModel? _viewModel;

    public MainWindow()
    {
        this.InitializeComponent();
        Title = "ShelfRow";
        try
        {
            this.AppWindow.Resize(new Windows.Graphics.SizeInt32(1150, 780));
        }
        catch { }
    }

    public MainViewModel? ViewModel
    {
        get => _viewModel;
        set
        {
            if (_viewModel != null)
            {
                _viewModel.PropertyChanged -= ViewModel_PropertyChanged;
                _viewModel.ImportCompletedNotification -= ViewModel_ImportCompletedNotification;
            }

            _viewModel = value;
            if (_viewModel != null)
            {
                _viewModel.PropertyChanged += ViewModel_PropertyChanged;
                _viewModel.ImportCompletedNotification += ViewModel_ImportCompletedNotification;
                PopulateShelvesInNav();
            }
        }
    }

    private void ViewModel_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.Shelves))
        {
            DispatcherQueue.TryEnqueue(PopulateShelvesInNav);
        }
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

    private async void ImportXmlButton_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel == null || _viewModel.IsImporting) return;

        try
        {
            var picker = new FileOpenPicker();
            var hwnd = WindowNative.GetWindowHandle(this);
            InitializeWithWindow.Initialize(picker, hwnd);

            picker.ViewMode = PickerViewMode.List;
            picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
            picker.FileTypeFilter.Add(".xml");

            var file = await picker.PickSingleFileAsync();
            if (file != null)
            {
                await _viewModel.ImportXmlFileAsync(file.Path);
            }
        }
        catch (Exception ex)
        {
            if (this.Content?.XamlRoot != null)
            {
                var dialog = new ContentDialog
                {
                    Title = "ファイル選択エラー",
                    Content = ex.Message,
                    CloseButtonText = "OK",
                    XamlRoot = this.Content.XamlRoot
                };
                await dialog.ShowAsync();
            }
        }
    }

    private void PopulateShelvesInNav()
    {
        if (_viewModel == null) return;

        // Keep first items (All books + Header)
        while (NavView.MenuItems.Count > 2)
        {
            NavView.MenuItems.RemoveAt(2);
        }

        foreach (var shelf in _viewModel.Shelves)
        {
            var navItem = new NavigationViewItem
            {
                Content = shelf.Title,
                Tag = shelf,
                Icon = new FontIcon { Glyph = shelf.IsSmart ? "\uE734" : "\uE8F1" }
            };
            NavView.MenuItems.Add(navItem);
        }
    }

    private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is NavigationViewItem item)
        {
            if (item.Tag is string tag && tag == "all_books")
            {
                if (_viewModel != null) _viewModel.SelectedShelf = null;
            }
            else if (item.Tag is Shelf shelf)
            {
                if (_viewModel != null) _viewModel.SelectedShelf = shelf;
            }
        }
    }

    private void GridView_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is ItemViewModel book)
        {
            // Open detail or log selection
            Debug.WriteLine($"Selected book: {book.Title}");
        }
    }
}
