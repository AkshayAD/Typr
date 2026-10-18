using System.Text.Json;

namespace Typr.Infrastructure;

/// <summary>Per-user model locations shared by installed versions and development builds.</summary>
public sealed class ModelLocations
{
    private static readonly object CatalogGate = new();
    public static string DefaultCatalogPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Typr", "model-locations.json");
    private readonly string catalogPath;
    private sealed record Catalog(string? CpuDirectory = null, string? GpuDirectory = null);

    public ModelLocations(string? catalogPath = null) => this.catalogPath = catalogPath ?? DefaultCatalogPath;

    public string ResolveCpu(string? configuredDirectory) => ResolveDirectory(configuredDirectory, ReadCatalog()?.CpuDirectory,
        ModelDownload.DefaultDirectory, ModelDownload.IsReady);
    public string ResolveGpu(string? configuredDirectory) => ResolveDirectory(configuredDirectory, ReadCatalog()?.GpuDirectory,
        NeMoSpeechDownload.DefaultDirectory, NeMoSpeechDownload.IsReady);

    internal static string ResolveDirectory(string? configured, string? registered, string defaultDirectory, Func<string, bool> ready)
    {
        string? custom = AbsolutePath(configured), shared = AbsolutePath(registered);
        foreach (var candidate in new[] { custom, shared, defaultDirectory })
            if (candidate != null && ready(candidate)) return candidate;
        // Keep a chosen download folder when its files are incomplete or temporarily unavailable.
        return custom ?? shared ?? defaultDirectory;
    }

    // Call only after the pinned SHA256 checks have succeeded, never from a size-only readiness check.
    internal void RememberVerifiedCpu(string directory) => Remember(directory, gpu: false);
    internal void RememberVerifiedGpu(string directory) => Remember(directory, gpu: true);

    private void Remember(string directory, bool gpu)
    {
        string? absolute = AbsolutePath(directory);
        if (absolute == null) return;
        lock (CatalogGate)
        {
            var catalog = ReadCatalog();
            // An unreadable catalog is preserved just like unreadable settings/history.
            if (catalog == null) return;
            var updated = gpu ? catalog with { GpuDirectory = absolute } : catalog with { CpuDirectory = absolute };
            if (updated == catalog) return;
            string temporary = catalogPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(catalogPath))!);
                File.WriteAllText(temporary, JsonSerializer.Serialize(updated, new JsonSerializerOptions { WriteIndented = true }));
                File.Move(temporary, catalogPath, overwrite: true);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            finally
            {
                try { File.Delete(temporary); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    private Catalog? ReadCatalog()
    {
        try { return File.Exists(catalogPath) ? JsonSerializer.Deserialize<Catalog>(File.ReadAllText(catalogPath)) : new Catalog(); }
        catch (JsonException) { return null; }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private static string? AbsolutePath(string? path)
    {
        try { return !string.IsNullOrWhiteSpace(path) && Path.IsPathFullyQualified(path) ? Path.GetFullPath(path) : null; }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }
}
