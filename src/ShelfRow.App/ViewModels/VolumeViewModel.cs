using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using Microsoft.UI;
using Microsoft.UI.Xaml.Media;
using ShelfRow.Core.Models;
using ShelfRow.Storage;

namespace ShelfRow.App.ViewModels;

public class VolumeViewModel : INotifyPropertyChanged
{
    private readonly Volume _volume;
    private bool _isAccessible;

    public VolumeViewModel(Volume volume)
    {
        _volume = volume ?? throw new ArgumentNullException(nameof(volume));
        CheckAccessibility();
    }

    public Volume Model => _volume;

    public Guid Id => _volume.Id;

    public string Name
    {
        get => _volume.Name;
        set
        {
            if (_volume.Name != value)
            {
                _volume.Name = value;
                OnPropertyChanged();
            }
        }
    }

    public string LastKnownPath
    {
        get => _volume.LastKnownPath;
        set
        {
            if (_volume.LastKnownPath != value)
            {
                _volume.LastKnownPath = value;
                OnPropertyChanged();
                CheckAccessibility();
            }
        }
    }

    public string WindowsMountPath
    {
        get => _volume.WindowsMountPath ?? string.Empty;
        set
        {
            var trimmed = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            if (_volume.WindowsMountPath != trimmed)
            {
                _volume.WindowsMountPath = trimmed;
                OnPropertyChanged();
                CheckAccessibility();
            }
        }
    }

    public bool IsAccessible
    {
        get => _isAccessible;
        private set
        {
            if (_isAccessible != value)
            {
                _isAccessible = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(StatusText));
                OnPropertyChanged(nameof(StatusBrush));
            }
        }
    }

    public string StatusText => IsAccessible ? "接続済み（アクセス可能）" : "未接続（フォルダー未設定またはアクセス不可）";

    public SolidColorBrush StatusBrush => IsAccessible
        ? new SolidColorBrush(Windows.UI.Color.FromArgb(255, 16, 185, 129)) // Emerald Green
        : new SolidColorBrush(Windows.UI.Color.FromArgb(255, 239, 68, 68));  // Red

    public void CheckAccessibility()
    {
        bool accessible = false;

        if (!string.IsNullOrWhiteSpace(_volume.WindowsMountPath))
        {
            try
            {
                accessible = Directory.Exists(_volume.WindowsMountPath);
            }
            catch
            {
                accessible = false;
            }
        }
        else if (!string.IsNullOrWhiteSpace(_volume.LastKnownPath))
        {
            // Auto heuristic check
            string converted = VolumePathResolver.ConvertPosixToWindows(_volume.LastKnownPath);
            if (!string.IsNullOrWhiteSpace(converted))
            {
                try
                {
                    accessible = Directory.Exists(converted);
                }
                catch
                {
                    accessible = false;
                }
            }
        }

        IsAccessible = accessible;
    }

    public void SuggestDefaultMapping()
    {
        if (string.IsNullOrWhiteSpace(_volume.WindowsMountPath) && !string.IsNullOrWhiteSpace(_volume.LastKnownPath))
        {
            WindowsMountPath = VolumePathResolver.ConvertPosixToWindows(_volume.LastKnownPath);
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
