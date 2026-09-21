using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ShelfRow.Core.Models;

namespace ShelfRow.App.Views;

public sealed class SmartShelfEditorDialog
{
    public static async Task<bool> ShowAsync(XamlRoot xamlRoot, Shelf shelf, AppSettings settings)
    {
        var conditions = SmartConditionsCodec.Decode(shelf.SmartConditionsJson);
        var title = new TextBox { Header = "名前", Text = shelf.Title };
        var keywordEnabled = new CheckBox { Content = "キーワード条件を使う", IsChecked = conditions.Keyword is not null };
        var keywordField = Combo(new[] { "Title", "Author", "Genre", "Relation", "Keyword A", "Keyword B", "Neta" }, conditions.Keyword?.Field ?? "Title");
        var keywordMode = Combo(new[] { "含む", "含まない", "一致" }, (conditions.Keyword?.Mode ?? 0).ToString());
        keywordMode.SelectedIndex = conditions.Keyword?.Mode ?? 0;
        var keywordText = new TextBox { PlaceholderText = "検索文字列", Text = conditions.Keyword?.Text ?? string.Empty };
        var unread = new CheckBox { Content = "未読のみ", IsChecked = conditions.UnreadOnly };

        var typeChecks = Enumerable.Range(0, 6).Select(index => new CheckBox
        {
            Content = settings.GetEffectiveTypeName(index),
            IsChecked = conditions.Types?.Contains(index) == true,
            Tag = index
        }).ToArray();
        var ratingChecks = Enumerable.Range(1, 5).Select(rate => new CheckBox
        {
            Content = $"★{rate}", IsChecked = conditions.Rates?.Contains(rate) == true, Tag = rate
        }).ToArray();

        var dateEnabled = new CheckBox { Content = "日付条件を使う", IsChecked = conditions.Date is not null };
        var dateField = Combo(new[] { "登録日", "最終閲覧日" }, "0");
        dateField.SelectedIndex = conditions.Date?.Field ?? 0;
        var dateMode = Combo(new[] { "指定日数以内", "指定日数より前" }, "0");
        dateMode.SelectedIndex = conditions.Date?.Mode ?? 0;
        var days = new NumberBox { Minimum = 1, Maximum = 36500, Value = conditions.Date?.Days ?? 30, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline };

        var panel = new StackPanel { Spacing = 10, Width = 560 };
        panel.Children.Add(title);
        panel.Children.Add(keywordEnabled);
        panel.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { keywordField, keywordMode, keywordText } });
        panel.Children.Add(new TextBlock { Text = "種別（未選択ならすべて）" });
        var types = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        foreach (var check in typeChecks) types.Children.Add(check);
        panel.Children.Add(types);
        panel.Children.Add(new TextBlock { Text = "評価（未選択ならすべて）" });
        var ratings = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        foreach (var check in ratingChecks) ratings.Children.Add(check);
        panel.Children.Add(ratings);
        panel.Children.Add(unread);
        panel.Children.Add(dateEnabled);
        panel.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { dateField, dateMode, days } });

        var dialog = new ContentDialog
        {
            Title = shelf.Title.Length == 0 ? "スマートシェルフを作成" : "スマートシェルフを編集",
            PrimaryButtonText = "保存",
            CloseButtonText = "キャンセル",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = xamlRoot,
            MinWidth = 640,
            MaxWidth = 720,
            Content = new ScrollViewer { Content = panel, MaxHeight = 600 }
        };
        bool valid = true;
        dialog.PrimaryButtonClick += (_, args) =>
        {
            valid = !string.IsNullOrWhiteSpace(title.Text);
            if (!valid) args.Cancel = true;
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary || !valid)
            return false;

        shelf.Title = title.Text.Trim();
        conditions.Keyword = keywordEnabled.IsChecked == true && !string.IsNullOrWhiteSpace(keywordText.Text)
            ? new SmartConditions.KeywordCondition { Field = SelectedText(keywordField), Text = keywordText.Text.Trim(), Mode = keywordMode.SelectedIndex }
            : null;
        conditions.Types = SelectedTags(typeChecks);
        conditions.Rates = SelectedTags(ratingChecks);
        conditions.UnreadOnly = unread.IsChecked == true;
        conditions.Date = dateEnabled.IsChecked == true
            ? new SmartConditions.DateCondition { Field = dateField.SelectedIndex, Mode = dateMode.SelectedIndex, Days = Math.Max(1, (int)days.Value) }
            : null;
        shelf.SmartConditionsJson = SmartConditionsCodec.Encode(conditions);
        return true;
    }

    private static ComboBox Combo(IEnumerable<string> values, string selected)
    {
        var combo = new ComboBox { MinWidth = 120 };
        foreach (string value in values) combo.Items.Add(new ComboBoxItem { Content = value });
        combo.SelectedIndex = Math.Max(0, values.ToList().FindIndex(value => value == selected));
        return combo;
    }

    private static string SelectedText(ComboBox combo) => (combo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? string.Empty;
    private static HashSet<int>? SelectedTags(IEnumerable<CheckBox> checks)
    {
        var selected = checks.Where(check => check.IsChecked == true).Select(check => (int)check.Tag).ToHashSet();
        return selected.Count == 0 ? null : selected;
    }
}
