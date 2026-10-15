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
        Assert.Equal(.15, settings.CaptionOpacity);
        Assert.Equal(2, settings.CaptionMaxLines);
        Assert.Equal(2, settings.CpuThreads);
        Assert.Equal(200, settings.SavedAudioLimit);
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
    [Fact] public void MigrationIsIdempotent()
    {
        var once = SettingsMigrator.Load("""{"CaptionOpacityPercent":50,"CaptionLines":1}""");
        Assert.Equal(once, SettingsMigrator.Load(SettingsMigrator.Save(once)));
    }
}
