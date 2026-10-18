using System.Text.Json;
using Typr.Core;
using Xunit;

namespace Typr.Core.Tests;

public sealed class BarDockEdgeSettingsTests
{
    [Fact] public void LegacyCoordinatesRemainAvailableForEdgeInference()
    {
        var settings = SettingsMigrator.Load("""{"SettingsVersion":2,"BarLeft":18,"BarTop":18,"BarScale":0.85,"SavedAudioLimit":0}""");
        Assert.Null(settings.BarDockEdge);
        Assert.Equal(18, settings.BarLeft); Assert.Equal(18, settings.BarTop);
        Assert.Equal(.85, settings.BarScale); Assert.Equal(0, settings.SavedAudioLimit);
    }

    [Theory]
    [InlineData("left", "Left")] [InlineData(" Right ", "Right")]
    [InlineData("TOP", "Top")] [InlineData(" bottom ", "Bottom")]
    public void ExplicitEdgeSurvivesRoundTripAtAmbiguousCoordinates(string edge, string expected)
    {
        var settings = SettingsMigrator.Load(JsonSerializer.Serialize(new { BarDockEdge = edge, BarLeft = 18, BarTop = 18 }));
        var restored = SettingsMigrator.Load(SettingsMigrator.Save(settings));
        Assert.Equal(expected, restored.BarDockEdge);
        Assert.Equal(settings.BarLeft, restored.BarLeft); Assert.Equal(settings.BarTop, restored.BarTop);
    }

    [Fact] public void UnknownEdgesUseLegacyInferenceAndAmbiguousKeysAreRejected()
    {
        Assert.Null(SettingsMigrator.Load("""{"BarDockEdge":"Corner"}""").BarDockEdge);
        Assert.Equal("Left", SettingsMigrator.Load("""{"bardockedge":"left"}""").BarDockEdge);
        Assert.Throws<JsonException>(() => SettingsMigrator.Load("""{"BarDockEdge":"Left","bardockedge":"Top"}"""));
    }
}
