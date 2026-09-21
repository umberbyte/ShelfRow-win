using System;
using System.ComponentModel;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ShelfRow.App.ViewModels;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace ShelfRow.App.Views;

public sealed partial class VolumeSettingsDialog : ContentDialog
{
    private readonly MainViewModel _mainViewModel;
    private readonly IntPtr _windowHandle;

    public VolumeSettingsDialog(MainViewModel mainViewModel, IntPtr windowHandle)
    {
        _mainViewModel = mainViewModel ?? throw new ArgumentNullException(nameof(mainViewModel));
        _windowHandle = windowHandle;
        this.InitializeComponent();

        VolumeItemsRepeater.ItemsSource = _mainViewModel.Volumes;
        _mainViewModel.Volumes.CollectionChanged += (s, e) => UpdateEmptyState();
        _mainViewModel.PropertyChanged += ViewModel_PropertyChanged;

        UpdateEmptyState();
        UpdateSyncProgress();

        PrimaryButtonClick += VolumeSettingsDialog_PrimaryButtonClick;
    }

    private void UpdateEmptyState()
    {
        bool hasVolumes = _mainViewModel.Volumes.Count > 0;
        EmptyStatePanel.Visibility = hasVolumes ? Visibility.Collapsed : Visibility.Visible;
        VolumeListScrollViewer.Visibility = hasVolumes ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateSyncProgress()
    {
        SyncThumbnailsBtn.IsEnabled = !_mainViewModel.IsSyncingThumbnails;
        SyncProgressPanel.Visibility = _mainViewModel.IsSyncingThumbnails ? Visibility.Visible : Visibility.Collapsed;
        SyncProgressStatusText.Text = _mainViewModel.ThumbnailSyncProgressText;
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.IsSyncingThumbnails) ||
            e.PropertyName == nameof(MainViewModel.ThumbnailSyncProgressText))
        {
            DispatcherQueue.TryEnqueue(UpdateSyncProgress);
        }
    }

    private async void SyncThumbnails_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            foreach (var vol in _mainViewModel.Volumes)
            {
                await _mainViewModel.SaveVolumeAsync(vol);
            }
            await _mainViewModel.SyncAllThumbnailsAsync();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"SyncThumbnails error: {ex.Message}");
        }
    }

    private async void VolumeSettingsDialog_PrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        var deferral = args.GetDeferral();
        try
        {
            foreach (var vol in _mainViewModel.Volumes)
            {
                await _mainViewModel.SaveVolumeAsync(vol);
            }
        }
        finally
        {
            deferral.Complete();
        }
    }

    private void AutoDetect_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is VolumeViewModel vm)
        {
            vm.SuggestDefaultMapping();
        }
    }

    private async void BrowseFolder_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not VolumeViewModel vm) return;

        try
        {
            var picker = new FolderPicker();
            InitializeWithWindow.Initialize(picker, _windowHandle);
            picker.SuggestedStartLocation = PickerLocationId.ComputerFolder;
            picker.FileTypeFilter.Add("*");

            var folder = await picker.PickSingleFolderAsync();
            if (folder != null)
            {
                vm.WindowsMountPath = folder.Path;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"FolderPicker error: {ex.Message}");
        }
    }
}
