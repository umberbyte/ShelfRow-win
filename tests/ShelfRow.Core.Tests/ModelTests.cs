using System;
using ShelfRow.Core.Models;
using Xunit;

namespace ShelfRow.Core.Tests;

public class ModelTests
{
    [Fact]
    public void AppSettings_CloudKitEnvironment_DefaultsToProduction()
    {
        Assert.Equal("production", new AppSettings().CloudKitEnvironment);
        var settings = System.Text.Json.JsonSerializer.Deserialize<AppSettings>("{}");
        Assert.Equal("production", settings!.CloudKitEnvironment);
    }

    [Fact]
    public void AppSettings_CloudKitEnvironment_PreservesExplicitDevelopment()
    {
        var settings = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(
            "{\"CloudKitEnvironment\":\"development\"}");
        Assert.Equal("development", settings!.CloudKitEnvironment);
    }

    [Fact]
    public void Item_DefaultInitialization_SetsExpectedValues()
    {
        var item = new Item();
        Assert.NotEqual(Guid.Empty, item.Id);
        Assert.True(item.IsUnread);
        Assert.Equal(0, item.Rating);
        Assert.Equal(0, item.CoverVersion);
        Assert.Empty(item.ShelfIds);
    }

    [Fact]
    public void Shelf_SmartShelf_PropertyDetection()
    {
        var manualShelf = new Shelf { Type = 0 };
        var smartShelf = new Shelf { Type = 1, SmartConditionsJson = "{\"field\":\"rating\",\"op\":\">=\",\"value\":4}" };

        Assert.False(manualShelf.IsSmart);
        Assert.True(smartShelf.IsSmart);
        Assert.NotNull(smartShelf.SmartConditionsJson);
    }

    [Fact]
    public void Volume_InitializesCorrectly()
    {
        var vol = new Volume
        {
            Name = "NAS Books",
            LastKnownPath = "/Volumes/Books",
            WindowsMountPath = @"\\NAS\Books"
        };

        Assert.Equal("NAS Books", vol.Name);
        Assert.Equal("/Volumes/Books", vol.LastKnownPath);
        Assert.Equal(@"\\NAS\Books", vol.WindowsMountPath);
    }

    [Fact]
    public void AppSettings_DefaultValues_MatchMacSpecification()
    {
        var settings = new AppSettings();

        Assert.Equal("System", settings.AppearanceMode);
        Assert.False(settings.CompactDisplay);
        Assert.True(settings.CloseOnExit);
        Assert.Equal("厚い本", settings.GetEffectiveTypeName(0));
        Assert.Equal("薄い本", settings.GetEffectiveTypeName(1));
        Assert.Equal("本の一部", settings.GetEffectiveTypeName(2));
        Assert.Equal("画像セット", settings.GetEffectiveTypeName(3));
        Assert.Equal("テキスト", settings.GetEffectiveTypeName(4));
        Assert.Equal("ムービー", settings.GetEffectiveTypeName(5));
        Assert.Equal("作者", settings.EffectiveAuthorLabel);
        Assert.Equal("ジャンル", settings.EffectiveGenreLabel);
        Assert.Equal("関連", settings.EffectiveRelationLabel);
        Assert.Equal("キーワードA", settings.EffectiveKeywordALabel);
        Assert.Equal("キーワードB", settings.EffectiveKeywordBLabel);
        Assert.False(settings.PasswordLockEnabled);
    }

    [Fact]
    public void AppSettings_Serialization_RoundTripsSuccessfully()
    {
        var settings = new AppSettings
        {
            AppearanceMode = "Dark",
            CompactDisplay = true,
            CustomRenameFormat = "[{author}] {title}",
            HelperMappings = new System.Collections.Generic.List<HelperMapping>
            {
                new() { Extensions = "pdf", ApplicationPath = @"C:\Program Files\Acrobat.exe" }
            },
            KeywordEquivalenceRules = new System.Collections.Generic.List<KeywordEquivalenceRule>
            {
                new() { Field = "author", TermsDisplay = "吾峠呼世晴, 吾峠" }
            }
        };

        var json = System.Text.Json.JsonSerializer.Serialize(settings);
        var restored = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(json);

        Assert.NotNull(restored);
        Assert.Equal("Dark", restored.AppearanceMode);
        Assert.True(restored.CompactDisplay);
        Assert.Equal("[{author}] {title}", restored.CustomRenameFormat);
        Assert.Single(restored.HelperMappings);
        Assert.Equal("pdf", restored.HelperMappings[0].Extensions);
        Assert.Equal(@"C:\Program Files\Acrobat.exe", restored.HelperMappings[0].ApplicationPath);
        Assert.Single(restored.KeywordEquivalenceRules);
        Assert.Equal("吾峠呼世晴, 吾峠", restored.KeywordEquivalenceRules[0].TermsDisplay);
    }
}
