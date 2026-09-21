using System;
using Microsoft.UI;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace ShelfRow.App.Models;

public static class BookTypeVisuals
{
    public const int Count = 6;

    public static string GetGlyph(int bookType) => bookType switch
    {
        4 => "\uE8A5", // Document / Text
        5 => "\uE8B2", // Film / Movie
        _ => "\uE82D"  // Book
    };

    public static SolidColorBrush GetBrush(int bookType) => bookType switch
    {
        0 => new SolidColorBrush(Color.FromArgb(255, 234, 179, 8)),  // Yellow
        1 => new SolidColorBrush(Color.FromArgb(255, 59, 130, 246)),  // Blue
        2 => new SolidColorBrush(Color.FromArgb(255, 34, 197, 94)),  // Green
        3 => new SolidColorBrush(Color.FromArgb(255, 236, 72, 153)), // Pink
        4 => new SolidColorBrush(Color.FromArgb(255, 156, 163, 175)),// Gray
        5 => new SolidColorBrush(Color.FromArgb(255, 168, 85, 247)), // Purple
        _ => new SolidColorBrush(Color.FromArgb(255, 107, 114, 128)) // Secondary
    };

    public static string GetDefaultName(int bookType) => bookType switch
    {
        0 => "厚い本",
        1 => "薄い本",
        2 => "本の一部",
        3 => "画像セット",
        4 => "テキスト",
        5 => "ムービー",
        _ => "その他"
    };
}

