using System;
using System.Collections.Generic;

namespace ShelfRow.Core.Models;

/// <summary>
/// Keyword equivalence rule for grouping synonymous terms.
/// Matches mac version KeywordEquivalenceRule.
/// </summary>
public class KeywordEquivalenceRule
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Field { get; set; } = "keywordA"; // author, genre, relation, keywordA, keywordB, memo
    public List<string> Terms { get; set; } = new();

    public string TermsDisplay
    {
        get => string.Join(", ", Terms);
        set => Terms = new List<string>(value.Split(new[] { ',', '\n', '、' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }
}

/// <summary>
/// Helper extension mapping (e.g. mov, avi, mpg -> VLC.exe).
/// Matches mac version HelperSettingsView.
/// </summary>
public class HelperMapping
{
    public string Extensions { get; set; } = string.Empty;
    public string ApplicationPath { get; set; } = string.Empty;
}

/// <summary>
/// User settings matching mac PreferencesView.swift.
/// </summary>
public class AppSettings
{
    // General
    public string AppearanceMode { get; set; } = "System"; // System, Light, Dark
    public bool CompactDisplay { get; set; } = false;
    public bool CloseOnExit { get; set; } = true;

    // Viewer
    public string SlideshowHelperPath { get; set; } = string.Empty;
    public string ZipHelperPath { get; set; } = string.Empty;

    // Helper
    public List<HelperMapping> HelperMappings { get; set; } = new()
    {
        new HelperMapping { Extensions = "mov, avi, mpg", ApplicationPath = "" },
        new HelperMapping { Extensions = "rar, zip, 7z", ApplicationPath = "" }
    };

    // Keywords
    public List<KeywordEquivalenceRule> KeywordEquivalenceRules { get; set; } = new();

    // Customize - Rename format
    public string CustomRenameFormat { get; set; } = "[@author] @title";
    public string StampsList { get; set; } = string.Empty;

    // Customize - Type Names (empty = default: 厚い本, 薄い本, 本の一部, 画像セット, テキスト, ムービー)
    public string TypeNameThickBook { get; set; } = "";
    public string TypeNameThinBook { get; set; } = "";
    public string TypeNamePartBook { get; set; } = "";
    public string TypeNameImageSet { get; set; } = "";
    public string TypeNameText { get; set; } = "";
    public string TypeNameMovie { get; set; } = "";

    // Customize - Field Names (empty = default: 作者, ジャンル, 関連, キーワードA, キーワードB)
    public string FieldNameAuthor { get; set; } = "";
    public string FieldNameGenre { get; set; } = "";
    public string FieldNameRelation { get; set; } = "";
    public string FieldNameKeywordA { get; set; } = "";
    public string FieldNameKeywordB { get; set; } = "";

    // Security
    public bool PasswordLockEnabled { get; set; } = false;
    public string PasswordValue { get; set; } = string.Empty;

    // Maintenance - Thumbnail Distribution
    public string ThumbnailDistributionRoot { get; set; } = string.Empty;

    /// <summary>
    /// Which CloudKit environment the library lives in, "development" or "production".
    /// A build run from Xcode writes to development, so that is where an unreleased
    /// Mac app's data is.
    /// </summary>
    public string CloudKitEnvironment { get; set; } = "development";
    public bool ThumbnailAutoFetchEnabled { get; set; } = true;
    public int ThumbnailConcurrency { get; set; } = 8;

    // Maintenance - Backup
    public bool BackupEnabled { get; set; } = false;
    public string BackupFolderPath { get; set; } = string.Empty;

    // UI View Settings
    public bool MainViewIsGrid { get; set; } = true;
    public string MainSortKey { get; set; } = "Title"; // Any list column, plus Pages
    public bool MainSortAscending { get; set; } = true;
    public string ListVisibleColumns { get; set; } = "unread,bookType,rating,author,genre,addedDate";
    public bool ListColumnOrderAppliesGlobally { get; set; } = true;
    public string ListColumnOrderGlobal { get; set; } = string.Empty;
    public Dictionary<string, string> ListColumnOrdersByCollection { get; set; } = new();
    public bool ListColumnWidthAppliesGlobally { get; set; } = true;
    public Dictionary<string, double> ListColumnWidthsGlobal { get; set; } = new();
    public Dictionary<string, Dictionary<string, double>> ListColumnWidthsByCollection { get; set; } = new();
    public int MainWindowWidth { get; set; } = 1280;
    public int MainWindowHeight { get; set; } = 800;

    // Helper methods to get effective names
    public string GetEffectiveTypeName(int index) => index switch
    {
        0 => string.IsNullOrWhiteSpace(TypeNameThickBook) ? "厚い本" : TypeNameThickBook,
        1 => string.IsNullOrWhiteSpace(TypeNameThinBook) ? "薄い本" : TypeNameThinBook,
        2 => string.IsNullOrWhiteSpace(TypeNamePartBook) ? "本の一部" : TypeNamePartBook,
        3 => string.IsNullOrWhiteSpace(TypeNameImageSet) ? "画像セット" : TypeNameImageSet,
        4 => string.IsNullOrWhiteSpace(TypeNameText) ? "テキスト" : TypeNameText,
        5 => string.IsNullOrWhiteSpace(TypeNameMovie) ? "ムービー" : TypeNameMovie,
        _ => "その他"
    };

    public string EffectiveAuthorLabel => string.IsNullOrWhiteSpace(FieldNameAuthor) ? "作者" : FieldNameAuthor;
    public string EffectiveGenreLabel => string.IsNullOrWhiteSpace(FieldNameGenre) ? "ジャンル" : FieldNameGenre;
    public string EffectiveRelationLabel => string.IsNullOrWhiteSpace(FieldNameRelation) ? "関連" : FieldNameRelation;
    public string EffectiveKeywordALabel => string.IsNullOrWhiteSpace(FieldNameKeywordA) ? "キーワードA" : FieldNameKeywordA;
    public string EffectiveKeywordBLabel => string.IsNullOrWhiteSpace(FieldNameKeywordB) ? "キーワードB" : FieldNameKeywordB;
}
