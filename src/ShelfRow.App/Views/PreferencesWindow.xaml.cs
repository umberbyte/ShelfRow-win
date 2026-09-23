using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
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
    // About 1545 physical pixels at 150% scaling, matching the reference window.
    private const int PreferredWidth = 1030;
    private const int PreferredHeight = 800;

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr windowHandle);

    private readonly MainViewModel _mainViewModel;
    private readonly AppSettingsService _settingsService;
    private readonly AppSettings _settings;
    private bool _isInitializing = true;

    public ObservableCollection<HelperMapping> HelperMappings { get; } = new();
    public ObservableCollection<KeywordEquivalenceRule> KeywordRules { get; } = new();

    public PreferencesWindow(MainViewModel mainViewModel)
    {
        this.InitializeComponent();
        if (Content is FrameworkElement themedRoot)
            themedRoot.ActualThemeChanged += (_, _) => WindowTitleBarTheme.Apply(this, themedRoot);
        _mainViewModel = mainViewModel;
        _settingsService = new AppSettingsService();
        _settings = _settingsService.Current;

        Title = "設定";
        try
        {
            IntPtr windowHandle = WindowNative.GetWindowHandle(this);
            double scale = Math.Max(1, GetDpiForWindow(windowHandle) / 96.0);
            var workArea = Microsoft.UI.Windowing.DisplayArea
                .GetFromWindowId(AppWindow.Id, Microsoft.UI.Windowing.DisplayAreaFallback.Primary)
                .WorkArea;
            int width = Math.Min((int)Math.Round(PreferredWidth * scale), workArea.Width - 80);
            int height = Math.Min((int)Math.Round(PreferredHeight * scale), workArea.Height - 80);
            AppWindow.Resize(new Windows.Graphics.SizeInt32(width, height));
        }
        catch { }

        LoadSettingsToUI();
        _isInitializing = false;
        ApplyAppearance();
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
        ToggleGlobalColumnOrder.IsOn = _settings.ListColumnOrderAppliesGlobally;
        ToggleGlobalColumnWidth.IsOn = _settings.ListColumnWidthAppliesGlobally;
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
        ToggleBackupEnabled.IsOn = _settings.BackupEnabled;
        TxtBackupFolder.Text = _settings.BackupFolderPath;
        UpdateBackupControls();

        // iCloud: the token itself stays in the credential locker and is never shown back.
        CmbCloudEnvironment.SelectedIndex = (App.CloudKitAccount?.Environment ?? _settings.CloudKitEnvironment) == "development" ? 1 : 0;
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
        _settings.ListColumnOrderAppliesGlobally = ToggleGlobalColumnOrder.IsOn;
        _settings.ListColumnWidthAppliesGlobally = ToggleGlobalColumnWidth.IsOn;
        _settings.CloseOnExit = ToggleCloseOnExit.IsOn;

        // Viewer
        _settings.SlideshowHelperPath = TxtSlideshowHelper.Text;
        _settings.ZipHelperPath = TxtZipHelper.Text;

        // Helper
        _settings.HelperMappings = HelperMappings.ToList();

        // Keywords
        _settings.KeywordEquivalenceRules = KeywordRules.ToList();

        // Customize
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
        _settings.BackupEnabled = ToggleBackupEnabled.IsOn;
        _settings.BackupFolderPath = TxtBackupFolder.Text;

        _settingsService.Save(_settings);
        _mainViewModel.ReloadSettings();
        ApplyAppearance();
    }

    private void ApplyAppearance()
    {
        if (Content is FrameworkElement root)
        {
            root.RequestedTheme = _settings.AppearanceMode.ToLowerInvariant() switch
            {
                "light" => ElementTheme.Light,
                "dark" => ElementTheme.Dark,
                _ => ElementTheme.Default
            };
            WindowTitleBarTheme.Apply(this, root);
        }
    }

    private void SettingChanged(object sender, RoutedEventArgs e)
    {
        SaveSettingsFromUI();
    }

    private void SettingChanged(object sender, TextChangedEventArgs e)
    {
        SaveSettingsFromUI();
    }

    private void SettingChanged(object sender, SelectionChangedEventArgs e)
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

    private async void ToggleGlobalColumnOrder_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;
        if (!ToggleGlobalColumnOrder.IsOn)
        {
            SaveSettingsFromUI();
            return;
        }

        if (_settings.ListColumnOrderAppliesGlobally)
        {
            SaveSettingsFromUI();
            return;
        }

        bool confirmed = await ConfirmDiscardScopedSettingsAsync(
            "列の並び順を全体設定に戻しますか？",
            "シェルフごとに保存した列の並び順は削除され、元に戻せません。");
        if (confirmed)
        {
            _settings.ListColumnOrdersByCollection?.Clear();
            SaveSettingsFromUI();
        }
        else
        {
            _isInitializing = true;
            ToggleGlobalColumnOrder.IsOn = false;
            _isInitializing = false;
        }
    }

    private async void ToggleGlobalColumnWidth_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;
        if (!ToggleGlobalColumnWidth.IsOn)
        {
            SaveSettingsFromUI();
            return;
        }

        if (_settings.ListColumnWidthAppliesGlobally)
        {
            SaveSettingsFromUI();
            return;
        }

        bool confirmed = await ConfirmDiscardScopedSettingsAsync(
            "列幅を全体設定に戻しますか？",
            "シェルフごとに保存した列幅は削除され、元に戻せません。");
        if (confirmed)
        {
            _settings.ListColumnWidthsByCollection?.Clear();
            SaveSettingsFromUI();
        }
        else
        {
            _isInitializing = true;
            ToggleGlobalColumnWidth.IsOn = false;
            _isInitializing = false;
        }
    }

    private async Task<bool> ConfirmDiscardScopedSettingsAsync(string title, string message)
    {
        if (Content?.XamlRoot is not { } root)
            return false;

        var dialog = new ContentDialog
        {
            Title = title,
            Content = message,
            PrimaryButtonText = "OK",
            CloseButtonText = "キャンセル",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = root
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private void PrefNavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is NavigationViewItem item && item.Tag is string tag)
        {
            ShowPane(tag);
        }
    }

    private void PageScrollViewer_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        // Keep the lists bounded for virtualization while using the available height.
        // Reserve room for the page heading, card description, actions and padding.
        double listHeight = Math.Max(200, e.NewSize.Height - 260);
        if (ListHelperMappings != null) ListHelperMappings.MaxHeight = listHeight;
        if (ListKeywordRules != null) ListKeywordRules.MaxHeight = listHeight;
    }

    private void ShowPane(string pane)
    {
        PageScrollViewer.ChangeView(null, 0, null, disableAnimation: true);
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
                PageSubtitle.Text = "サムネイル配布や移行などのメンテナンス操作を行います。";
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

    private void BackupEnabled_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;
        SaveSettingsFromUI();
        UpdateBackupControls();
    }

    private void UpdateBackupControls(bool running = false)
    {
        bool enabled = ToggleBackupEnabled.IsOn && !running;
        BtnBrowseBackup.IsEnabled = enabled;
        bool hasFolder = !string.IsNullOrWhiteSpace(TxtBackupFolder.Text);
        BtnBackupNow.IsEnabled = enabled && hasFolder;
        BtnRestoreBackup.IsEnabled = enabled && hasFolder;
    }

    private async void BrowseBackupFolder_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker();
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        picker.SuggestedStartLocation = PickerLocationId.ComputerFolder;
        picker.FileTypeFilter.Add("*");
        var folder = await picker.PickSingleFolderAsync();
        if (folder == null) return;

        TxtBackupFolder.Text = folder.Path;
        TxtBackupStatus.Text = string.Empty;
        SaveSettingsFromUI();
        UpdateBackupControls();
    }

    private async void BackupNow_Click(object sender, RoutedEventArgs e)
    {
        if (App.BackupManager == null) return;
        UpdateBackupControls(running: true);
        TxtBackupStatus.Text = "バックアップを開始しています...";
        try
        {
            var summary = await App.BackupManager.BackUpAsync(TxtBackupFolder.Text);
            TxtBackupStatus.Text = $"バックアップ完了: コピー {summary.CopiedFiles} 件、スキップ {summary.SkippedFiles} 件、削除 {summary.RemovedFiles} 件。";
        }
        catch (Exception ex)
        {
            App.Log($"Backup failed: {ex}");
            TxtBackupStatus.Text = $"バックアップに失敗しました: {ex.Message}";
        }
        finally
        {
            UpdateBackupControls();
        }
    }

    private async void RestoreBackup_Click(object sender, RoutedEventArgs e)
    {
        if (App.BackupManager == null || Content?.XamlRoot is not { } root) return;
        var dialog = new ContentDialog
        {
            Title = "バックアップからリストアしますか？",
            Content = "現在のShelfRowデータはバックアップ時点の内容で上書きされます。復元内容は次回起動時に適用されます。",
            PrimaryButtonText = "リストア",
            CloseButtonText = "キャンセル",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = root
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        UpdateBackupControls(running: true);
        TxtBackupStatus.Text = "リストア内容を検証しています...";
        try
        {
            var summary = await App.BackupManager.StageRestoreAsync(TxtBackupFolder.Text);
            TxtBackupStatus.Text = $"リストア準備完了: {summary.CopiedFiles} 件。ShelfRowを再起動すると復元が適用されます。";
        }
        catch (Exception ex)
        {
            App.Log($"Restore staging failed: {ex}");
            TxtBackupStatus.Text = $"リストアに失敗しました: {ex.Message}";
        }
        finally
        {
            UpdateBackupControls();
        }
    }

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

    private bool _updatingCloudToggle;
    private bool _changingCloudSync;

    private async Task RefreshCloudSyncControlsAsync()
    {
        string? mode = await _mainViewModel.GetCloudSyncModeAsync();
        _updatingCloudToggle = true;
        ToggleCloudSync.IsOn = mode is "primary" or "replica";
        _updatingCloudToggle = false;
        TxtCloudSyncMode.Text = mode switch
        {
            "primary" => "1台目：この端末の蔵書をiCloudへ送信",
            "replica" => "2台目以降：iCloudの蔵書を受信して開始",
            _ => "同期はオフです。オンにすると、どちらの蔵書を残すか選択できます。"
        };
        CmbCloudEnvironment.IsEnabled = !ToggleCloudSync.IsOn;
        BtnResendCloud.IsEnabled = BtnPurgeCloud.IsEnabled = mode == "primary";
    }

    private async void ToggleCloudSync_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isInitializing || _updatingCloudToggle || _changingCloudSync) return;
        _changingCloudSync = true;
        ToggleCloudSync.IsEnabled = false;
        try
        {
            if (ToggleCloudSync.IsOn)
            {
                var choice = new RadioButtons();
                choice.Items.Add("この端末の蔵書をiCloudへ送る（1台目）");
                choice.Items.Add("iCloudの蔵書で置き換える（2台目以降）");
                var dialog = new ContentDialog
                {
                    Title = "どちらの蔵書を残しますか？",
                    Content = new StackPanel
                    {
                        Spacing = 16,
                        Children =
                        {
                            new TextBlock { Text = "切り替え前に蔵書データベースをバックアップします。\n\n1台目：この端末の蔵書を送信します。すでにiCloudに同じ本がある場合、二重に登録されることがあります。\n\n2台目以降：この端末の蔵書とボリュームのパスマッピングを削除し、iCloudの蔵書で置き換えます。書籍ファイルは削除しません。受信が終わるまでは蔵書が空または一部のみ表示されます。", TextWrapping = TextWrapping.Wrap },
                            choice
                        }
                    },
                    PrimaryButtonText = "同期をオンにする",
                    CloseButtonText = "キャンセル",
                    DefaultButton = ContentDialogButton.Close,
                    IsPrimaryButtonEnabled = false,
                    XamlRoot = Content.XamlRoot
                };
                choice.SelectionChanged += (_, _) => dialog.IsPrimaryButtonEnabled = choice.SelectedIndex >= 0;
                if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
                await ConfigureCloudAccountAsync();
                await App.CloudKitAccount!.SignInAsync();
                await _mainViewModel.SetCloudSyncEnabledAsync(true, choice.SelectedIndex == 1);
            }
            else
            {
                await _mainViewModel.SetCloudSyncEnabledAsync(false);
            }
            TxtCloudAuthStatus.Text = _mainViewModel.StatusMessage;
        }
        catch (Exception ex)
        {
            App.Log($"Cloud sync setup failed: {ex}");
            TxtCloudAuthStatus.Text = $"同期設定を変更できませんでした: {ex.Message}";
        }
        finally
        {
            await RefreshCloudSyncControlsAsync();
            ToggleCloudSync.IsEnabled = true;
            _changingCloudSync = false;
        }
    }

    private async Task ConfigureCloudAccountAsync()
    {
        var account = App.CloudKitAccount ?? throw new InvalidOperationException("iCloudが初期化されていません。");
        if (_mainViewModel.IsLoading) throw new InvalidOperationException("同期処理が終わるまでお待ちください。");
        account.Environment = (CmbCloudEnvironment.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "production";
        _settings.CloudKitEnvironment = account.Environment;
        _settingsService.Save(_settings);
        _mainViewModel.ReloadSettings();
        string token = TxtCloudApiToken.Password.Trim();
        if (token.Length > 0) await account.SetApiTokenAsync(token);
        else await account.LoadAsync();
    }

    private async void SyncNow_Click(object sender, RoutedEventArgs e)
    {
        // The main window's status bar is behind this window, so the result has to be
        // reported here or the click looks like it did nothing.
        TxtCloudAuthStatus.Text = "同期しています...";
        await _mainViewModel.SyncWithCloudKitAsync();
        TxtCloudAuthStatus.Text = _mainViewModel.StatusMessage;
    }

    private async void CloudSignIn_Click(object sender, RoutedEventArgs e)
    {
        if (_changingCloudSync) return;
        try
        {
            await ConfigureCloudAccountAsync();
            TxtCloudAuthStatus.Text = "サインインしています...";
            await App.CloudKitAccount!.SignInAsync();
            await RefreshCloudAuthStatusAsync();
        }
        catch (Exception ex)
        {
            App.Log($"Cloud sign-in failed: {ex}");
            TxtCloudAuthStatus.Text = $"サインインできませんでした: {ex.Message}";
        }
    }

    private async void CloudSignOut_Click(object sender, RoutedEventArgs e)
    {
        if (_changingCloudSync || _mainViewModel.IsLoading) return;
        try
        {
            await _mainViewModel.SetCloudSyncEnabledAsync(false);
            if (App.CloudKitAccount is { } account)
                await account.SignOutAsync();
            await RefreshCloudAuthStatusAsync();
        }
        catch (Exception ex)
        {
            App.Log($"Cloud sign-out failed: {ex}");
            TxtCloudAuthStatus.Text = $"サインアウトできませんでした: {ex.Message}";
        }
    }

    private async Task RefreshCloudAuthStatusAsync()
    {
        try
        {
        await RefreshCloudSyncControlsAsync();
        var account = App.CloudKitAccount;
        if (account == null) return;

        await account.LoadAsync();

        TxtCloudAuthStatus.Text = account.IsSignedIn
            ? $"サインイン済み ({account.Environment})"
            : "未サインインです。「サインイン」を押してください。";
        }
        catch (Exception ex)
        {
            App.Log($"Cloud settings load failed: {ex}");
            TxtCloudAuthStatus.Text = $"iCloud設定を読み込めませんでした: {ex.Message}";
        }
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
        var confirmation = new TextBox
        {
            PlaceholderText = "確認のため「削除」と入力してください",
            MinWidth = 380
        };
        var dialog = new ContentDialog
        {
            Title = "iCloudのデータを削除",
            Content = new StackPanel
            {
                Spacing = 12,
                Children =
                {
                    new TextBlock
                    {
                        Text = "このアプリがiCloudに保存している書誌情報を完全に削除します。端末内の蔵書は残りますが、この操作は取り消せません。先に他の端末でも同期を止めてください。",
                        TextWrapping = TextWrapping.Wrap,
                        MaxWidth = 460
                    },
                    confirmation
                }
            },
            PrimaryButtonText = "完全に削除",
            CloseButtonText = "キャンセル",
            DefaultButton = ContentDialogButton.Close,
            IsPrimaryButtonEnabled = false,
            XamlRoot = this.Content.XamlRoot
        };
        confirmation.TextChanged += (_, _) => dialog.IsPrimaryButtonEnabled = confirmation.Text == "削除";
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            TxtCloudAuthStatus.Text = "iCloudのデータを削除しています...";
            await _mainViewModel.PurgeCloudDataAsync();
            TxtCloudAuthStatus.Text = _mainViewModel.StatusMessage;
        }
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
