using System;
using Microsoft.UI;
using Microsoft.UI.Xaml;

namespace ShelfRow.App.Services;

/// <summary>Applies the effective XAML theme to the native Windows title bar.</summary>
internal static class WindowTitleBarTheme
{
    public static void Apply(Window window, FrameworkElement themedRoot)
    {
        try
        {
            bool dark = themedRoot.ActualTheme == ElementTheme.Dark;
            var titleBar = window.AppWindow.TitleBar;

            if (dark)
            {
                var background = ColorHelper.FromArgb(255, 32, 32, 32);
                titleBar.BackgroundColor = background;
                titleBar.ForegroundColor = Colors.White;
                titleBar.InactiveBackgroundColor = background;
                titleBar.InactiveForegroundColor = ColorHelper.FromArgb(255, 170, 170, 170);
                titleBar.ButtonBackgroundColor = background;
                titleBar.ButtonForegroundColor = Colors.White;
                titleBar.ButtonInactiveBackgroundColor = background;
                titleBar.ButtonInactiveForegroundColor = ColorHelper.FromArgb(255, 170, 170, 170);
                titleBar.ButtonHoverBackgroundColor = ColorHelper.FromArgb(255, 55, 55, 55);
                titleBar.ButtonHoverForegroundColor = Colors.White;
                titleBar.ButtonPressedBackgroundColor = ColorHelper.FromArgb(255, 72, 72, 72);
                titleBar.ButtonPressedForegroundColor = Colors.White;
            }
            else
            {
                var background = ColorHelper.FromArgb(255, 243, 243, 243);
                titleBar.BackgroundColor = background;
                titleBar.ForegroundColor = Colors.Black;
                titleBar.InactiveBackgroundColor = background;
                titleBar.InactiveForegroundColor = ColorHelper.FromArgb(255, 96, 96, 96);
                titleBar.ButtonBackgroundColor = background;
                titleBar.ButtonForegroundColor = Colors.Black;
                titleBar.ButtonInactiveBackgroundColor = background;
                titleBar.ButtonInactiveForegroundColor = ColorHelper.FromArgb(255, 96, 96, 96);
                titleBar.ButtonHoverBackgroundColor = ColorHelper.FromArgb(255, 225, 225, 225);
                titleBar.ButtonHoverForegroundColor = Colors.Black;
                titleBar.ButtonPressedBackgroundColor = ColorHelper.FromArgb(255, 210, 210, 210);
                titleBar.ButtonPressedForegroundColor = Colors.Black;
            }
        }
        catch (Exception ex)
        {
            // Older Windows builds can reject title-bar customization. The content
            // theme should still be usable even when native chrome cannot be changed.
            App.Log($"Title bar theme could not be applied: {ex.Message}");
        }
    }
}
