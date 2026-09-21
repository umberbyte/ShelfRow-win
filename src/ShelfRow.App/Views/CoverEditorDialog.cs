using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using ShelfRow.App.Services;
using ShelfRow.Core.Models;
using Windows.Storage.Streams;

namespace ShelfRow.App.Views;

public static class CoverEditorDialog
{
    public static async Task<bool?> ShowAsync(
        XamlRoot xamlRoot,
        Item item,
        CoverGenerationService generator,
        CancellationToken cancellationToken = default)
    {
        var candidates = await generator.GetCandidatesAsync(item, cancellationToken);
        if (candidates is null) return null;

        var image = new Image { Width = 360, Height = 440, Stretch = Microsoft.UI.Xaml.Media.Stretch.Uniform };
        var name = new TextBlock { TextAlignment = TextAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        var slider = new Slider { Minimum = 0, Maximum = candidates.Entries.Count - 1, StepFrequency = 1, IsThumbToolTipEnabled = true };
        var panel = new StackPanel { Width = 440, Spacing = 8, Children = { image, name, slider } };
        int loadVersion = 0;

        async Task LoadPreviewAsync(int index)
        {
            int version = ++loadVersion;
            string entry = candidates.Entries[index];
            name.Text = $"{index + 1} / {candidates.Entries.Count}  {entry}";
            byte[]? bytes = await generator.ReadCandidateAsync(candidates, entry, cancellationToken);
            if (bytes is null || version != loadVersion) return;
            var bitmap = new BitmapImage { DecodePixelWidth = 480 };
            using var stream = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(stream))
            {
                writer.WriteBytes(bytes);
                await writer.StoreAsync();
            }
            stream.Seek(0);
            await bitmap.SetSourceAsync(stream);
            if (version == loadVersion) image.Source = bitmap;
        }

        slider.ValueChanged += async (_, args) => await LoadPreviewAsync((int)Math.Round(args.NewValue));
        await LoadPreviewAsync(0);
        var dialog = new ContentDialog
        {
            Title = "表紙を編集",
            PrimaryButtonText = "この画像を表紙にする",
            CloseButtonText = "キャンセル",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = xamlRoot,
            Content = panel
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return false;
        return await generator.ReplaceAsync(
            item, candidates, candidates.Entries[(int)Math.Round(slider.Value)], cancellationToken);
    }
}
