using System;
using System.IO;
using System.Text.Json;
using ShelfRow.Core.Models;

namespace ShelfRow.App.Services;

public class AppSettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly string _settingsFilePath;
    private AppSettings _currentSettings;

    public AppSettingsService(string? customPath = null)
    {
        if (!string.IsNullOrEmpty(customPath))
        {
            _settingsFilePath = customPath;
        }
        else
        {
            string appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ShelfRow");
            Directory.CreateDirectory(appData);
            _settingsFilePath = Path.Combine(appData, "settings.json");
        }

        _currentSettings = Load();
    }

    public AppSettings Current => _currentSettings;

    public AppSettings Load()
    {
        try
        {
            if (File.Exists(_settingsFilePath))
            {
                string json = File.ReadAllText(_settingsFilePath);
                var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
                if (settings != null)
                {
                    _currentSettings = settings;
                    return _currentSettings;
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to load settings: {ex.Message}");
        }

        _currentSettings = new AppSettings();
        return _currentSettings;
    }

    public void Save(AppSettings? settings = null)
    {
        if (settings != null)
        {
            _currentSettings = settings;
        }

        try
        {
            string json = JsonSerializer.Serialize(_currentSettings, JsonOptions);
            File.WriteAllText(_settingsFilePath, json);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to save settings: {ex.Message}");
        }
    }
}

