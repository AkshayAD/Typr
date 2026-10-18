using System.Text.Json;
using Typr.Core;
using Xunit;

namespace Typr.Core.Tests;

public sealed class GpuSpeechSettingsTests
{
    [Fact] public void ExistingProfilesGainAutomaticQualityWithoutChangingOtherChoices()
    {
        var settings = SettingsMigrator.Load("""{"SettingsVersion":2,"SavedAudioLimit":75,"SaveHistory":true,"AiPolishEnabled":false,"CpuThreads":4,"SpeechModelPath":"D:\\speech-cpu","SpeechResourceMode":"Gaming"}""");
        Assert.Equal("Automatic", settings.SpeechAcceleration);
        Assert.Equal("Quality", settings.GpuSpeechLatency);
        Assert.Null(settings.GpuSpeechModelPath);
        Assert.Equal(75, settings.SavedAudioLimit); Assert.True(settings.SaveHistory);
        Assert.False(settings.AiPolishEnabled); Assert.Equal(4, settings.CpuThreads);
        Assert.Equal(@"D:\speech-cpu", settings.SpeechModelPath);
        Assert.Equal("Gaming", settings.SpeechResourceMode);
    }

    [Theory]
    [InlineData("cpu", "fast", "CPU", "Fast")]
    [InlineData(" automatic ", " quality ", "Automatic", "Quality")]
    [InlineData("cuda", "unsafe", "Automatic", "Quality")]
    [InlineData(null, null, "Automatic", "Quality")]
    public void ChoicesAreCanonicalAndRoundTrip(string? acceleration, string? latency, string expectedAcceleration, string expectedLatency)
    {
        var settings = SettingsMigrator.Load(JsonSerializer.Serialize(new { SpeechAcceleration = acceleration, GpuSpeechLatency = latency, GpuSpeechModelPath = @" D:\Typr\gpu " }));
        Assert.Equal(expectedAcceleration, settings.SpeechAcceleration);
        Assert.Equal(expectedLatency, settings.GpuSpeechLatency);
        Assert.Equal(@"D:\Typr\gpu", settings.GpuSpeechModelPath);
        var restored = SettingsMigrator.Load(SettingsMigrator.Save(settings));
        Assert.Equal(settings.SpeechAcceleration, restored.SpeechAcceleration);
        Assert.Equal(settings.GpuSpeechLatency, restored.GpuSpeechLatency);
        Assert.Equal(settings.GpuSpeechModelPath, restored.GpuSpeechModelPath);
    }

    [Fact] public void GpuKeysAcceptCaseInsensitiveNamesAndRejectAmbiguousAliases()
    {
        var settings = SettingsMigrator.Load("""{"speechacceleration":"cpu","gpuspeechlatency":"fast","gpuspeechmodelpath":" "}""");
        Assert.Equal("CPU", settings.SpeechAcceleration);
        Assert.Equal("Fast", settings.GpuSpeechLatency);
        Assert.Null(settings.GpuSpeechModelPath);
        Assert.Throws<JsonException>(() => SettingsMigrator.Load("""{"SpeechAcceleration":"Automatic","speechacceleration":"CPU"}"""));
        Assert.Throws<JsonException>(() => SettingsMigrator.Load("""{"GpuSpeechLatency":"Quality","gpuspeechlatency":"Fast"}"""));
        Assert.Throws<JsonException>(() => SettingsMigrator.Load("""{"GpuSpeechModelPath":"D:\\one","gpuspeechmodelpath":"D:\\two"}"""));
    }
}
