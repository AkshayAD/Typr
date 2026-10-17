using System.Text.Json;
using Typr.Core;
using Xunit;

namespace Typr.Core.Tests;

public sealed class BarPresentationSettingsTests
{
    [Fact] public void DefaultsUseTranslucentStandardBarAndShortcutOnlyRecording()
    {
        var settings = SettingsMigrator.Load("{}");
        Assert.Equal(.78, settings.BarOpacity);
        Assert.Equal(1, settings.BarScale);
        Assert.Equal(150, settings.BarHoverDelay);
        Assert.False(settings.AllowButtonRecording);
        Assert.Null(settings.InputDeviceId);
    }

    [Fact] public void BarPresentationAndInputDeviceRoundTripIndependentlyOfCaptionsAndActions()
    {
        var original = new AppSettings {
            BarOpacity = .4, BarScale = 1.15, BarHoverDelay = 500, AllowButtonRecording = true,
            InputDeviceId = "{0.0.1.00000000}.{synthetic-device}", CaptionOpacity = .85,
            BarHoverActions = ["HoldToTalk"], BarContextActions = ["OpenWorkspace"]
        };
        string json = SettingsMigrator.Save(original);
        Assert.Equal(json, SettingsMigrator.Save(SettingsMigrator.Load(json)));
    }

    [Theory]
    [InlineData(-10, .2)] [InlineData(.2, .2)] [InlineData(.78, .78)] [InlineData(1, 1)] [InlineData(50, 1)]
    public void OpacityIsBoundedWithoutFadingOtherPreferences(double requested, double expected)
    {
        var settings = SettingsMigrator.Load(JsonSerializer.Serialize(new { BarOpacity = requested, CaptionOpacity = .42 }));
        Assert.Equal(expected, settings.BarOpacity);
        Assert.Equal(.42, settings.CaptionOpacity);
    }

    [Theory]
    [InlineData(.85, .85)] [InlineData(1, 1)] [InlineData(1.15, 1.15)] [InlineData(.1, 1)] [InlineData(3, 1)]
    public void OnlySupportedScalesAreAccepted(double value, double expected)
        => Assert.Equal(expected, SettingsMigrator.Load(JsonSerializer.Serialize(new { BarScale = value })).BarScale);

    [Theory]
    [InlineData(0, 0)] [InlineData(150, 150)] [InlineData(300, 300)] [InlineData(500, 500)] [InlineData(-1, 150)] [InlineData(8000, 150)]
    public void HoverDelayUsesOnlyBoundedChoices(int value, int expected)
        => Assert.Equal(expected, SettingsMigrator.Load(JsonSerializer.Serialize(new { BarHoverDelay = value })).BarHoverDelay);

    [Theory]
    [InlineData(null, null)] [InlineData("", null)] [InlineData("  ", null)] [InlineData(" device-id ", "device-id")]
    public void DeviceIdentifierIsOptionalAndTrimmed(string? value, string? expected)
        => Assert.Equal(expected, SettingsMigrator.Load(JsonSerializer.Serialize(new { InputDeviceId = value })).InputDeviceId);

    [Fact] public void OversizedAndNonfiniteSettingsCannotReachNativeConfiguration()
    {
        Assert.Null(SettingsMigrator.Load(JsonSerializer.Serialize(new { InputDeviceId = new string('x', 513) })).InputDeviceId);
        Assert.Equal(new string('x', 512), SettingsMigrator.Load(JsonSerializer.Serialize(new { InputDeviceId = new string('x', 512) })).InputDeviceId);
        var settings = SettingsMigrator.Load("""{"BarOpacity":1e400,"BarScale":-1e400}""");
        Assert.Equal(.78, settings.BarOpacity); Assert.Equal(1, settings.BarScale);
    }

    [Theory]
    [InlineData("BarOpacity")] [InlineData("BarScale")] [InlineData("BarHoverDelay")]
    [InlineData("AllowButtonRecording")] [InlineData("InputDeviceId")]
    public void DuplicateCaseVariantsOfNewKeysAreRejected(string name)
        => Assert.Throws<JsonException>(() => SettingsMigrator.Load("{\"" + name + "\":null,\"" + name.ToLowerInvariant() + "\":null}"));
}
