using System.Text.Json;
using Typr.Infrastructure;
using Xunit;

namespace Typr.Infrastructure.Tests;

public sealed class ModelLocationsTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "typr-model-locations-" + Guid.NewGuid().ToString("N"));
    private string CatalogPath => Path.Combine(root, "shared", "model-locations.json");

    [Fact] public void ReadyConfiguredFolderWinsAndMissingConfiguredFolderReusesTheCatalog()
    {
        string custom = Path.Combine(root, "custom"), shared = Path.Combine(root, "central"), fallback = Path.Combine(root, "default");
        Assert.Equal(custom, ModelLocations.ResolveDirectory(custom, shared, fallback, _ => true));
        Assert.Equal(shared, ModelLocations.ResolveDirectory(custom, shared, fallback, path => path == shared));
        Assert.Equal(fallback, ModelLocations.ResolveDirectory(custom, shared, fallback, path => path == fallback));
        Assert.Equal(custom, ModelLocations.ResolveDirectory(custom, shared, fallback, _ => false));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("models/relative")]
    [InlineData("invalid\0path")]
    public void MissingOrMalformedConfiguredPathsFallBackToAnAbsoluteSharedFolder(string? configured)
    {
        string shared = Path.Combine(root, "central"), fallback = Path.Combine(root, "default");
        Assert.Equal(shared, ModelLocations.ResolveDirectory(configured, shared, fallback, path => path == shared));
        Assert.Equal(fallback, ModelLocations.ResolveDirectory(configured, "relative-catalog-path", fallback, _ => false));
    }

    [Fact] public void VerifiedLocationsSurviveANewInstanceAndRetainTheOtherBackend()
    {
        var locations = new ModelLocations(CatalogPath);
        string cpu = Path.Combine(root, "cpu"), gpu = Path.Combine(root, "gpu");
        locations.RememberVerifiedCpu(cpu);
        new ModelLocations(CatalogPath).RememberVerifiedGpu(gpu);
        using var catalog = JsonDocument.Parse(File.ReadAllText(CatalogPath));
        Assert.Equal(cpu, catalog.RootElement.GetProperty("CpuDirectory").GetString());
        Assert.Equal(gpu, catalog.RootElement.GetProperty("GpuDirectory").GetString());
        Assert.Equal(cpu, new ModelLocations(CatalogPath).ResolveCpu(null));
        Assert.Equal(gpu, new ModelLocations(CatalogPath).ResolveGpu(null));
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(CatalogPath)!));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("null")]
    [InlineData("{\"CpuDirectory\": 17}")]
    public void CorruptCatalogIsIgnoredAndPreservedDuringRegistration(string contents)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(CatalogPath)!);
        File.WriteAllText(CatalogPath, contents);
        var locations = new ModelLocations(CatalogPath);
        string custom = Path.Combine(root, "custom");
        Assert.Equal(custom, locations.ResolveCpu(custom));
        Assert.Equal(NeMoSpeechDownload.DefaultDirectory, locations.ResolveGpu(null));
        locations.RememberVerifiedCpu(custom);
        Assert.Equal(contents, File.ReadAllText(CatalogPath));
    }

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
