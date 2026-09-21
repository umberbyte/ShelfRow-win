using System;
using System.Collections.ObjectModel;
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

        Volumes = _mainViewModel.Volumes;
        PrimaryButtonClick += VolumeSettingsDialog_PrimaryButtonClick;
    }

    public ObservableCollection<VolumeViewModel> Volumes { get; }

    public Visibility HasVolumes => Volumes.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility HasNoVolumes => Volumes.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    private async void VolumeSettingsDialog_PrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        var deferral = args.GetDeferral();
        try
        {
            foreach (var vol in Volumes)
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
