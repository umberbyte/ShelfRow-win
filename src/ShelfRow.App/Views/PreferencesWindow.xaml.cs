using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ShelfRow.App.Services;
using ShelfRow.App.ViewModels;
using ShelfRow.Core.Models;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace ShelfRow.App.Views;

public sealed partial class PreferencesWindow : Window
{
    private readonly MainViewModel _mainViewModel;
    private readonly AppSettingsService _settingsService;
    private readonly AppSettings _settings;
    private bool _isInitializing = true;

    public ObservableCollection<HelperMapping> HelperMappings { get; } = new();
    public ObservableCollection<KeywordEquivalenceRule> KeywordRules { get; } = new();

    public PreferencesWindow(MainViewModel mainViewModel)
    {
        this.InitializeComponent();
        _mainViewModel = mainViewModel;
        _settingsService = new AppSettingsService();
        _settings = _settingsService.Current;

        Title = "設定";
        try
        {
            this.AppWindow.Resize(new Windows.Graphics.SizeInt32(860, 620));
        }
        catch { }

        LoadSettingsToUI();
        _isInitializing = false;
    }

    private void LoadSettingsToUI()
    {
        // General
        foreach (ComboBoxItem item in ComboAppearance.Items)
        {
            if (item.Tag?.ToString() == _settings.AppearanceMode)
            {
                ComboAppearance.SelectedItem = item;
                break;
            }
        }
        ToggleCompactDisplay.IsOn = _settings.CompactDisplay;
        ToggleCloseOnExit.IsOn = _settings.CloseOnExit;

        // Viewer
        TxtSlideshowHelper.Text = _settings.SlideshowHelperPath;
        TxtZipHelper.Text = _settings.ZipHelperPath;

        // Helper Mappings
        HelperMappings.Clear();
        foreach (var m in _settings.HelperMappings)
        {
            HelperMappings.Add(m);
        }
        ListHelperMappings.ItemsSource = HelperMappings;

        // Keywords
        KeywordRules.Clear();
        foreach (var r in _settings.KeywordEquivalenceRules)
        {
            KeywordRules.Add(r);
        }
        ListKeywordRules.ItemsSource = KeywordRules;

        // Customize
        TxtRenameFormat.Text = _settings.CustomRenameFormat;
        TxtType0.Text = _settings.TypeNameThickBook;
        TxtType1.Text = _settings.TypeNameThinBook;
        TxtType2.Text = _settings.TypeNamePartBook;
        TxtType3.Text = _settings.TypeNameImageSet;
        TxtType4.Text = _settings.TypeNameText;
        TxtType5.Text = _settings.TypeNameMovie;

        TxtFieldAuthor.Text = _settings.FieldNameAuthor;
        TxtFieldGenre.Text = _settings.FieldNameGenre;
        TxtFieldRelation.Text = _settings.FieldNameRelation;
        TxtFieldKeywordA.Text = _settings.FieldNameKeywordA;
        TxtFieldKeywordB.Text = _settings.FieldNameKeywordB;

        // Security
        TogglePasswordLock.IsOn = _settings.PasswordLockEnabled;
        TxtPasswordValue.Password = _settings.PasswordValue;
        TxtPasswordValue.IsEnabled = _settings.PasswordLockEnabled;

        // Maintenance
        TxtThumbnailRoot.Text = _settings.ThumbnailDistributionRoot;

        // iCloud: the token itself stays in the credential locker and is never shown back.
        CmbCloudEnvironment.SelectedIndex = App.CloudKitAccount?.Environment == "production" ? 1 : 0;
        _ = RefreshCloudAuthStatusAsync();
    }

    private void SaveSettingsFromUI()
    {
        if (_isInitializing) return;

        // General
        if (ComboAppearance.SelectedItem is ComboBoxItem selectedItem && selectedItem.Tag != null)
        {
            _settings.AppearanceMode = selectedItem.Tag.ToString()!;
        }
        _settings.CompactDisplay = ToggleCompactDisplay.IsOn;
        _settings.CloseOnExit = ToggleCloseOnExit.IsOn;

        // Viewer
        _settings.SlideshowHelperPath = TxtSlideshowHelper.Text;
        _settings.ZipHelperPath = TxtZipHelper.Text;

        // Helper
        _settings.HelperMappings = HelperMappings.ToList();

        // Keywords
        _settings.KeywordEquivalenceRules = KeywordRules.ToList();

        // Customize
        _settings.CustomRenameFormat = TxtRenameFormat.Text;
        _settings.TypeNameThickBook = TxtType0.Text;
        _settings.TypeNameThinBook = TxtType1.Text;
        _settings.TypeNamePartBook = TxtType2.Text;
        _settings.TypeNameImageSet = TxtType3.Text;
        _settings.TypeNameText = TxtType4.Text;
        _settings.TypeNameMovie = TxtType5.Text;

        _settings.FieldNameAuthor = TxtFieldAuthor.Text;
        _settings.FieldNameGenre = TxtFieldGenre.Text;
        _settings.FieldNameRelation = TxtFieldRelation.Text;
        _settings.FieldNameKeywordA = TxtFieldKeywordA.Text;
        _settings.FieldNameKeywordB = TxtFieldKeywordB.Text;

        // Security
        _settings.PasswordLockEnabled = TogglePasswordLock.IsOn;
        _settings.PasswordValue = TxtPasswordValue.Password;

        // Maintenance
        _settings.ThumbnailDistributionRoot = TxtThumbnailRoot.Text;

        _settingsService.Save(_settings);
    }

    private void SettingChanged(object sender, RoutedEventArgs e)
    {
        SaveSettingsFromUI();
    }

    private void SettingChanged(object sender, TextChangedEventArgs e)
    {
        SaveSettingsFromUI();
    }

    private void ComboAppearance_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        SaveSettingsFromUI();
    }

    private void TogglePasswordLock_Toggled(object sender, RoutedEventArgs e)
    {
        TxtPasswordValue.IsEnabled = TogglePasswordLock.IsOn;
        SaveSettingsFromUI();
    }

    private void PrefNavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is NavigationViewItem item && item.Tag is string tag)
        {
            ShowPane(tag);
        }
    }

    private void ShowPane(string pane)
    {
        PaneGeneral.Visibility = pane == "general" ? Visibility.Visible : Visibility.Collapsed;
        PaneViewer.Visibility = pane == "viewer" ? Visibility.Visible : Visibility.Collapsed;
        PaneHelper.Visibility = pane == "helper" ? Visibility.Visible : Visibility.Collapsed;
        PaneKeywords.Visibility = pane == "keywords" ? Visibility.Visible : Visibility.Collapsed;
        PaneCustomize.Visibility = pane == "customize" ? Visibility.Visible : Visibility.Collapsed;
        PaneICloud.Visibility = pane == "icloud" ? Visibility.Visible : Visibility.Collapsed;
        PaneSecurity.Visibility = pane == "security" ? Visibility.Visible : Visibility.Collapsed;
        PaneMaintenance.Visibility = pane == "maintenance" ? Visibility.Visible : Visibility.Collapsed;

        switch (pane)
        {
            case "general":
                PageTitle.Text = "一般";
                PageSubtitle.Text = "表示とアプリの基本動作を設定します。";
                break;
            case "viewer":
                PageTitle.Text = "ビューア";
                PageSubtitle.Text = "画像フォルダやアーカイブを開く外部ビューアを設定します。";
                break;
            case "helper":
                PageTitle.Text = "ヘルパー";
                PageSubtitle.Text = "拡張子ごとに起動するアプリケーションを設定します。";
                break;
            case "keywords":
                PageTitle.Text = "キーワード";
                PageSubtitle.Text = "作者名・ジャンル・キーワードなどをグループ化し、検索時に同じものとして扱います。";
                break;
            case "customize":
                PageTitle.Text = "カスタマイズ";
                PageSubtitle.Text = "ファイル名の解析ルールと、種類・項目名の表示を調整します。";
                break;
            case "icloud":
                PageTitle.Text = "iCloud";
                PageSubtitle.Text = "本・シェルフ・ボリュームの書誌情報を、同じiCloudアカウントの端末と同期します。";
                break;
            case "security":
                PageTitle.Text = "セキュリティ";
                PageSubtitle.Text = "起動後の簡易ロックとパスワードを設定します。";
                break;
            case "maintenance":
                PageTitle.Text = "保守";
                PageSubtitle.Text = "バックアップ、サムネイル配布、移行などのメンテナンス操作を行います。";
                break;
        }
    }

    #region Viewer & Helper Actions

    private async void BrowseSlideshowHelper_Click(object sender, RoutedEventArgs e)
    {
        var path = await PickExecutableFileAsync();
        if (!string.IsNullOrEmpty(path))
        {
            TxtSlideshowHelper.Text = path;
            SaveSettingsFromUI();
        }
    }

    private void ClearSlideshowHelper_Click(object sender, RoutedEventArgs e)
    {
        TxtSlideshowHelper.Text = string.Empty;
        SaveSettingsFromUI();
    }

    private async void BrowseZipHelper_Click(object sender, RoutedEventArgs e)
    {
        var path = await PickExecutableFileAsync();
        if (!string.IsNullOrEmpty(path))
        {
            TxtZipHelper.Text = path;
            SaveSettingsFromUI();
        }
    }

    private void ClearZipHelper_Click(object sender, RoutedEventArgs e)
    {
        TxtZipHelper.Text = string.Empty;
        SaveSettingsFromUI();
    }

    private async void BrowseHelperMapping_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is HelperMapping mapping)
        {
            var path = await PickExecutableFileAsync();
            if (!string.IsNullOrEmpty(path))
            {
                mapping.ApplicationPath = path;
                SaveSettingsFromUI();
                ListHelperMappings.ItemsSource = null;
                ListHelperMappings.ItemsSource = HelperMappings;
            }
        }
    }

    private void AddHelperMapping_Click(object sender, RoutedEventArgs e)
    {
        HelperMappings.Add(new HelperMapping { Extensions = "ext", ApplicationPath = "" });
        SaveSettingsFromUI();
    }

    private void RemoveHelperMapping_Click(object sender, RoutedEventArgs e)
    {
        if (ListHelperMappings.SelectedItem is HelperMapping selected)
        {
            HelperMappings.Remove(selected);
            SaveSettingsFromUI();
        }
    }

    private void AddKeywordRule_Click(object sender, RoutedEventArgs e)
    {
        KeywordRules.Add(new KeywordEquivalenceRule { Field = "keywordA" });
        SaveSettingsFromUI();
    }

    private void RemoveKeywordRule_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is KeywordEquivalenceRule rule)
        {
            KeywordRules.Remove(rule);
            SaveSettingsFromUI();
        }
    }

    private async Task<string?> PickExecutableFileAsync()
    {
        var picker = new FileOpenPicker();
        var hwnd = WindowNative.GetWindowHandle(this);
        InitializeWithWindow.Initialize(picker, hwnd);

        picker.ViewMode = PickerViewMode.List;
        picker.SuggestedStartLocation = PickerLocationId.ComputerFolder;
        picker.FileTypeFilter.Add(".exe");
        picker.FileTypeFilter.Add(".bat");
        picker.FileTypeFilter.Add(".cmd");

        var file = await picker.PickSingleFileAsync();
        return file?.Path;
    }

    #endregion

    #region Maintenance & Actions

    private async void BrowseThumbnailRoot_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker();
        var hwnd = WindowNative.GetWindowHandle(this);
        InitializeWithWindow.Initialize(picker, hwnd);

        picker.SuggestedStartLocation = PickerLocationId.ComputerFolder;
        picker.FileTypeFilter.Add("*");

        var folder = await picker.PickSingleFolderAsync();
        if (folder != null)
        {
            TxtThumbnailRoot.Text = folder.Path;
            SaveSettingsFromUI();
        }
    }

    private void ClearThumbnailRoot_Click(object sender, RoutedEventArgs e)
    {
        TxtThumbnailRoot.Text = string.Empty;
        SaveSettingsFromUI();
    }

    private async void SyncNow_Click(object sender, RoutedEventArgs e)
    {
        await _mainViewModel.SyncWithCloudKitAsync();
        await RefreshCloudAuthStatusAsync();
    }

    private async void CloudSignIn_Click(object sender, RoutedEventArgs e)
    {
        var account = App.CloudKitAccount;
        if (account == null) return;

        string token = TxtCloudApiToken.Password.Trim();
        if (token.Length > 0)
            await account.SetApiTokenAsync(token);
        else
            await account.LoadAsync();

        if (!account.HasApiToken)
        {
            TxtCloudAuthStatus.Text = "API トークンを入力してください。";
            return;
        }

        account.Environment = (CmbCloudEnvironment.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "development";
        _settings.CloudKitEnvironment = account.Environment;
        _settingsService.Save(_settings);

        // Signing in has no endpoint of its own: the sign-in page is reached by making a
        // real request and following the redirect the server answers with.
        TxtCloudAuthStatus.Text = "サインインしています...";
        await _mainViewModel.SyncWithCloudKitAsync();
        await RefreshCloudAuthStatusAsync();
    }

    private async void CloudSignOut_Click(object sender, RoutedEventArgs e)
    {
        if (App.CloudKitAccount is { } account)
            await account.SignOutAsync();

        await RefreshCloudAuthStatusAsync();
    }

    private async Task RefreshCloudAuthStatusAsync()
    {
        var account = App.CloudKitAccount;
        if (account == null) return;

        await account.LoadAsync();

        TxtCloudAuthStatus.Text = !account.HasApiToken
            ? "未設定: API トークンがありません。"
            : account.IsSignedIn
                ? $"サインイン済み ({account.Environment})"
                : "API トークンあり・未サインイン。";
    }

    private async void ResendToCloud_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            Title = "全件をiCloudへ再送信",
            Content = "この端末の蔵書をすべて送信待ちに入れます。実行しますか？",
            PrimaryButtonText = "再送信する",
            CloseButtonText = "キャンセル",
            XamlRoot = this.Content.XamlRoot
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            await _mainViewModel.ResendEverythingToCloudAsync();
        }
    }

    private async void PurgeCloudData_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            Title = "iCloudのデータを削除",
            Content = "このアプリがiCloudに保存している書誌情報を完全に削除します。よろしいですか？",
            PrimaryButtonText = "完全に削除",
            CloseButtonText = "キャンセル",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = this.Content.XamlRoot
        };
        await dialog.ShowAsync();
    }

    private async void SyncThumbnailsNow_Click(object sender, RoutedEventArgs e)
    {
        await _mainViewModel.SyncAllThumbnailsAsync();
    }

    private async void OpenVolumes_Click(object sender, RoutedEventArgs e)
    {
        var hwnd = WindowNative.GetWindowHandle(this);
        var dialog = new VolumeSettingsDialog(_mainViewModel, hwnd)
        {
            XamlRoot = this.Content.XamlRoot
        };
        await dialog.ShowAsync();
    }

    private async void ImportXml_Click(object sender, RoutedEventArgs e)
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
            await _mainViewModel.ImportXmlFileAsync(file.Path);
        }
    }

    #endregion
}

