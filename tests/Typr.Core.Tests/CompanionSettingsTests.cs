using System.Text.Json;
using Typr.Core;
using Xunit;

namespace Typr.Core.Tests;

public sealed class CompanionSettingsTests
{
    [Fact] public void NewProfileHasExplicitProviderAndCommandDefaultsWithoutRetainingPersonalData()
    {
        var settings = SettingsMigrator.Load("{}");
        Assert.True(settings.AutoPasteEnabled);
        Assert.True(settings.AiPolishEnabled);
        Assert.Equal("ChatGPT only", settings.ProviderPriority);
        Assert.Equal("gpt-6-luna", settings.ChatGptModel);
        Assert.Equal("claude-haiku-5-5", settings.ClaudeModel);
        Assert.Equal("low", settings.PolishReasoning);
        Assert.True(settings.CommandShortcutsEnabled);
        Assert.Equal("Ctrl+Shift+M", settings.MicrophoneShortcut);
        Assert.Equal("Ctrl+Shift+Space", settings.HandsFreeShortcut);
        Assert.Equal("Ctrl+Shift+V", settings.PasteShortcut);
        Assert.Equal("whisper-small", settings.RetranscriptionModel);
        Assert.Null(settings.BarLeft); Assert.Null(settings.BarTop);
        Assert.False(settings.SaveHistory); Assert.Equal(0, settings.SavedAudioLimit);
    }

    [Fact] public void EveryCompanionPreferenceRoundTripsIncludingIndependentNonemptyActionLists()
    {
        var original = new AppSettings {
            AutoPasteEnabled = false, AiPolishEnabled = false, CommandShortcutsEnabled = false,
            ProviderPriority = "Claude first", ChatGptModel = "gpt-fixture", ClaudeModel = "claude-fixture",
            PolishReasoning = "none", PolishTimeoutSeconds = 90, BarLeft = -1400.25, BarTop = -85.5,
            BarHoverActions = ["HoldToTalk", "Cancel"], BarContextActions = ["HideBar", "OpenWorkspace"],
            MicrophoneShortcut = "F9", HandsFreeShortcut = "Ctrl+Shift+R", PasteShortcut = "F10",
            RetranscriptionModel = "whisper-large-v3", Vocabulary = [new("type er", "Typr")]
        };
        var saved = SettingsMigrator.Save(original);
        var loaded = SettingsMigrator.Load(saved);
        Assert.Equal(saved, SettingsMigrator.Save(loaded));
        Assert.Equal(original.BarHoverActions, loaded.BarHoverActions);
        Assert.Equal(original.BarContextActions, loaded.BarContextActions);
        Assert.NotSame(loaded.BarHoverActions, loaded.BarContextActions);
    }

    [Fact] public void CanonicalChoicesRejectUnsupportedValuesAndPreserveSupportedCaseInsensitiveChoices()
    {
        var settings = SettingsMigrator.Load("""
            {"ProviderPriority":" claude FIRST ","PolishReasoning":"NONE",
             "MicrophoneShortcut":" f9 ","HandsFreeShortcut":"ctrl+shift+r","PasteShortcut":"f10",
             "RetranscriptionModel":" WHISPER-LARGE-V3 ","PolishTimeoutSeconds":500}
            """);
        Assert.Equal("Claude first", settings.ProviderPriority);
        Assert.Equal("none", settings.PolishReasoning);
        Assert.Equal("F9", settings.MicrophoneShortcut);
        Assert.Equal("Ctrl+Shift+R", settings.HandsFreeShortcut);
        Assert.Equal("F10", settings.PasteShortcut);
        Assert.Equal("whisper-large-v3", settings.RetranscriptionModel);
        Assert.Equal(120, settings.PolishTimeoutSeconds);
        var fallback = SettingsMigrator.Load("""
            {"ProviderPriority":"unsupported","PolishReasoning":"ultra","MicrophoneShortcut":"Alt+F4",
             "HandsFreeShortcut":null,"PasteShortcut":"Ctrl+C","RetranscriptionModel":"unknown","PolishTimeoutSeconds":-10}
            """);
        Assert.Equal("ChatGPT only", fallback.ProviderPriority);
        Assert.Equal("low", fallback.PolishReasoning);
        Assert.Equal("Ctrl+Shift+M", fallback.MicrophoneShortcut);
        Assert.Equal("Ctrl+Shift+Space", fallback.HandsFreeShortcut);
        Assert.Equal("Ctrl+Shift+V", fallback.PasteShortcut);
        Assert.Equal("whisper-small", fallback.RetranscriptionModel);
        Assert.Equal(5, fallback.PolishTimeoutSeconds);
    }

    [Theory]
    [InlineData(null)] [InlineData("")] [InlineData("model with spaces")]
    [InlineData("../model")] [InlineData("model;command")] [InlineData("model/path")]
    public void InvalidModelIdentifiersFallBackWithoutBecomingCommandArguments(string? value)
    {
        var settings = SettingsMigrator.Load(JsonSerializer.Serialize(new { ChatGptModel = value, ClaudeModel = value }));
        Assert.Equal("gpt-6-luna", settings.ChatGptModel);
        Assert.Equal("claude-haiku-5-5", settings.ClaudeModel);
    }

    [Fact] public void OverlongModelAndNullOrUnknownActionsAreNormalizedWithoutChangingTheOtherList()
    {
        var settings = SettingsMigrator.Load(JsonSerializer.Serialize(new {
            ChatGptModel = new string('x', 101),
            BarHoverActions = new string?[] { null, "unknown", "HoldToTalk", "HoldToTalk", "Cancel", "holdtotalk" },
            BarContextActions = new[] { "OpenWorkspace", "HideBar" }
        }));
        Assert.Equal("gpt-6-luna", settings.ChatGptModel);
        Assert.Equal(new[] { "HoldToTalk", "Cancel" }, settings.BarHoverActions);
        Assert.Equal(new[] { "OpenWorkspace", "HideBar" }, settings.BarContextActions);
        var cleared = SettingsMigrator.Load("""{"BarHoverActions":null,"BarContextActions":[]}""");
        Assert.Empty(cleared.BarHoverActions); Assert.Empty(cleared.BarContextActions);
    }

    [Theory]
    [InlineData("ProviderPriority")] [InlineData("BarHoverActions")] [InlineData("MicrophoneShortcut")]
    public void DuplicateCaseVariantNewPropertiesRejectAmbiguousFiles(string name)
    {
        var json = "{\"" + name + "\":null,\"" + name.ToLowerInvariant() + "\":null}";
        Assert.Throws<JsonException>(() => SettingsMigrator.Load(json));
    }

    [Fact] public void NonfinitePositionsAreDiscardedWhileNegativeMonitorCoordinatesRemainValid()
    {
        var invalid = SettingsMigrator.Load("""{"BarLeft":1e400,"BarTop":-1e400}""");
        Assert.Null(invalid.BarLeft); Assert.Null(invalid.BarTop);
        var valid = SettingsMigrator.Load("""{"BarLeft":-1920.25,"BarTop":-120}""");
        Assert.Equal(-1920.25, valid.BarLeft); Assert.Equal(-120d, valid.BarTop);
    }
}
