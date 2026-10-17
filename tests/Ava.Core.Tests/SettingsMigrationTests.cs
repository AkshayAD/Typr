using System.Text.Json;
using Ava.Core;
using Xunit;

namespace Ava.Core.Tests;

public sealed class SettingsMigrationTests
{
    [Fact] public void EmptyObjectGetsLocalFirstDefaults()
    {
        var settings = SettingsMigrator.Load("{}");
        Assert.Equal(AppSettings.CurrentVersion, settings.SettingsVersion);
        Assert.True(settings.PolishEnabled);
        Assert.Equal(.85, settings.CaptionOpacity);
        Assert.Equal(2, settings.CaptionMaxLines);
        Assert.Equal(2, settings.CpuThreads);
        Assert.Equal(0, settings.SavedAudioLimit);
        Assert.False(settings.SaveHistory);
        Assert.Equal("System", settings.Theme);
        Assert.Equal("Iris", settings.Accent);
        Assert.Empty(settings.Vocabulary);
        Assert.Empty(settings.Snippets);
    }
    [Fact] public void VersionZeroRunsBothOneStepMigrations()
    {
        var settings = SettingsMigrator.Load("""{"Polish":false,"CaptionOpacityPercent":35,"CaptionLines":3}""");
        Assert.Equal(2, settings.SettingsVersion);
        Assert.False(settings.PolishEnabled);
        Assert.Equal(.35, settings.CaptionOpacity);
        Assert.Equal(3, settings.CaptionMaxLines);
    }
    [Fact] public void VersionOneOnlyRunsSecondMigration()
    {
        var settings = SettingsMigrator.Load("""{"SettingsVersion":1,"CaptionOpacity":0.4,"CaptionLines":1}""");
        Assert.Equal(.4, settings.CaptionOpacity);
        Assert.Equal(1, settings.CaptionMaxLines);
    }
    [Fact] public void MigrationNeverOverwritesNewerExplicitProperty()
    {
        var settings = SettingsMigrator.Load("""{"Polish":false,"PolishEnabled":true,"CaptionLines":1,"CaptionMaxLines":3}""");
        Assert.True(settings.PolishEnabled);
        Assert.Equal(3, settings.CaptionMaxLines);
    }
    [Fact] public void MigrationAcceptsCaseInsensitiveNames()
    {
        var settings = SettingsMigrator.Load("""{"settingsVersion":0,"polish":false,"captionLines":1}""");
        Assert.False(settings.PolishEnabled);
        Assert.Equal(1, settings.CaptionMaxLines);
    }
    [Fact] public void DuplicateCaseVariantKeysAreRejected()
        => Assert.Throws<JsonException>(() => SettingsMigrator.Load("""{"CaptionLines":1,"captionLines":2}"""));
    [Theory]
    [InlineData(-1)] [InlineData(99)]
    public void UnknownVersionIsNotSilentlyDowngraded(int version)
        => Assert.Throws<NotSupportedException>(() => SettingsMigrator.Load($$"""{"SettingsVersion":{{version}}}"""));
    [Theory]
    [InlineData("null")] [InlineData("[]")] [InlineData("broken")]
    public void MalformedSettingsThrowInsteadOfOverwritingUserFile(string json)
        => Assert.ThrowsAny<JsonException>(() => SettingsMigrator.Load(json));
    [Fact] public void UnsafeVisualAndResourceValuesAreClamped()
    {
        var settings = SettingsMigrator.Load("""{"SettingsVersion":2,"CaptionOpacity":4,"CaptionMaxLines":12,"CpuThreads":99,"SavedAudioLimit":500}""");
        Assert.Equal(1, settings.CaptionOpacity);
        Assert.Equal(3, settings.CaptionMaxLines);
        Assert.Equal(4, settings.CpuThreads);
        Assert.Equal(200, settings.SavedAudioLimit);
    }
    [Fact] public void CurrentSettingsRoundTrip()
    {
        var original = new AppSettings { PolishEnabled = false, SpeechModelPath = @"C:\Users\me\models", CaptionOpacity = .3 };
        Assert.Equal(original, SettingsMigrator.Load(SettingsMigrator.Save(original)));
    }

    [Theory]
    [InlineData("{\"SettingsVersion\":2}", .85)]
    [InlineData("{\"SettingsVersion\":2,\"CaptionOpacity\":0.15}", .15)]
    [InlineData("{\"CaptionOpacityPercent\":15}", .15)]
    [InlineData("{\"SettingsVersion\":2,\"CaptionOpacity\":-2}", 0)]
    [InlineData("{\"SettingsVersion\":2,\"CaptionOpacity\":2}", 1)]
    public void ReadableCaptionDefaultPreservesExplicitPreferencesAndClamping(string json, double expected)
        => Assert.Equal(expected, SettingsMigrator.Load(json).CaptionOpacity);

    [Fact] public void MigrationIsIdempotent()
    {
        var once = SettingsMigrator.Load("""{"CaptionOpacityPercent":50,"CaptionLines":1}""");
        Assert.Equal(once, SettingsMigrator.Load(SettingsMigrator.Save(once)));
    }

    [Fact] public void ExistingVersionTwoRetentionPreferenceIsPreserved()
    {
        var settings = SettingsMigrator.Load("""{"SettingsVersion":2,"SavedAudioLimit":75}""");
        Assert.Equal(75, settings.SavedAudioLimit);
        Assert.False(settings.SaveHistory);
    }

    [Theory]
    [InlineData(" dark ", "forest", "Dark", "Forest")]
    [InlineData("LIGHT", "Ocean", "Light", "Ocean")]
    [InlineData("unknown", "purple", "System", "Iris")]
    [InlineData(null, null, "System", "Iris")]
    public void AppearanceChoicesAreCanonicalAndBounded(string? theme, string? accent, string expectedTheme, string expectedAccent)
    {
        var json = JsonSerializer.Serialize(new { Theme = theme, Accent = accent });
        var settings = SettingsMigrator.Load(json);
        Assert.Equal(expectedTheme, settings.Theme);
        Assert.Equal(expectedAccent, settings.Accent);
    }

    [Fact] public void NewPropertiesAcceptCaseInsensitiveNamesAndRejectDuplicateAliases()
    {
        var settings = SettingsMigrator.Load("""{"theme":"dark","accent":"ocean","savehistory":true,"vocabulary":[{"phrase":"type er","replacement":"Typr"}]}""");
        Assert.Equal("Dark", settings.Theme);
        Assert.Equal("Ocean", settings.Accent);
        Assert.True(settings.SaveHistory);
        Assert.Equal(new TextReplacement("type er", "Typr"), Assert.Single(settings.Vocabulary));
        Assert.Throws<JsonException>(() => SettingsMigrator.Load("""{"Theme":"Light","theme":"Dark"}"""));
    }

    [Fact] public void InvalidReplacementEntriesAreDiscardedWithoutLosingValidRules()
    {
        var settings = SettingsMigrator.Load("""
            {"Vocabulary":[null,{}, {"Phrase":null,"Replacement":"x"},
              {"Phrase":"empty","Replacement":null}, {"Phrase":" ","Replacement":"x"},
              {"Phrase":"blank","Replacement":" "},
              {"Phrase":" type er ","Replacement":" Typr "},
              {"Phrase":"TYPE ER","Replacement":"ignored"}],"Snippets":null}
            """);
        Assert.Equal(new TextReplacement("type er", "Typr"), Assert.Single(settings.Vocabulary));
        Assert.Empty(settings.Snippets);
    }

    [Fact] public void ReplacementCountAndSizeAreBoundedPerList()
    {
        var rules = new[] {
            new TextReplacement(new string('p', OutputPersonalizer.MaxPhraseLength + 1), "ignored"),
            new TextReplacement("too long", new string('r', OutputPersonalizer.MaxReplacementLength + 1))
        }.Concat(Enumerable.Range(0, 205).Select(index => new TextReplacement($"phrase {index}", $"output {index}"))).ToArray();
        var settings = SettingsMigrator.Load(JsonSerializer.Serialize(new { Vocabulary = rules, Snippets = rules }));
        Assert.Equal(OutputPersonalizer.MaxRules, settings.Vocabulary.Length);
        Assert.Equal(OutputPersonalizer.MaxRules, settings.Snippets.Length);
        Assert.Equal("phrase 0", settings.Vocabulary[0].Phrase);
        Assert.Equal("phrase 199", settings.Vocabulary[^1].Phrase);
    }

    [Fact] public void PersonalizationAndAppearanceRoundTrip()
    {
        var original = new AppSettings {
            Theme = "Dark", Accent = "Ocean", SaveHistory = true, SavedAudioLimit = 20,
            Vocabulary = [new("type er", "Typr")],
            Snippets = [new("my signature", "Best regards,\nExample")]
        };
        var loaded = SettingsMigrator.Load(SettingsMigrator.Save(original));
        Assert.Equal(original.Theme, loaded.Theme);
        Assert.Equal(original.Accent, loaded.Accent);
        Assert.Equal(original.SaveHistory, loaded.SaveHistory);
        Assert.Equal(original.SavedAudioLimit, loaded.SavedAudioLimit);
        Assert.Equal(original.Vocabulary, loaded.Vocabulary);
        Assert.Equal(original.Snippets, loaded.Snippets);
    }

    [Theory]
    [InlineData("{\"Vocabulary\":\"not an array\"}")]
    [InlineData("{\"Snippets\":[{\"Phrase\":42,\"Replacement\":\"text\"}]}")]
    public void MalformedReplacementTypesRejectTheSettingsFile(string json)
        => Assert.Throws<JsonException>(() => SettingsMigrator.Load(json));
}
