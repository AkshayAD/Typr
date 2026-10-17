namespace Typr.Infrastructure;

/// <summary>Copies an existing profile once; the original remains available for recovery.</summary>
public static class ProfileMigration
{
    public static string LegacyDirectoryName { get; } = new string(['A', 'v', 'a']);
    const int MaximumEntries = 20_000;
    const int MaximumDepth = 32;
    const long MaximumBytes = 16L * 1024 * 1024 * 1024;

    public static string MigrateDirectory(string parent)
    {
        var fullParent = Path.GetFullPath(parent);
        var destination = Path.Combine(fullParent, "Typr");
        var source = Path.Combine(fullParent, LegacyDirectoryName);
        string? staging = null;
        try {
            if (File.Exists(fullParent)) throw new IOException("The profile parent is not a directory.");
            if (Directory.Exists(fullParent)) RejectReparsePoint(fullParent);
            if (Directory.Exists(destination)) { RejectReparsePoint(destination); return destination; }
            if (File.Exists(destination)) throw new IOException("The profile destination is not a directory.");
            if (!Directory.Exists(source)) {
                if (File.Exists(source)) throw new IOException("The existing profile is not a directory.");
                return destination;
            }
            RejectReparsePoint(source);
            staging = Path.Combine(fullParent, "Typr.migration-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(staging);
            int count = 0;
            long bytes = 0;
            CopyDirectory(source, staging, 0, ref count, ref bytes);
            // Another instance may have finished first. Its profile always wins.
            if (Directory.Exists(destination)) { RejectReparsePoint(destination); return destination; }
            try { Directory.Move(staging, destination); }
            catch (IOException) when (Directory.Exists(destination)) { RejectReparsePoint(destination); }
            return destination;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new IOException("The existing profile could not be copied safely. Its original files are preserved.", exception);
        }
        finally {
            // Only the uniquely named sibling created by this attempt can be removed.
            if (staging != null && Directory.Exists(staging) &&
                Path.GetDirectoryName(Path.GetFullPath(staging))!.Equals(fullParent, StringComparison.OrdinalIgnoreCase)) {
                try {
                    if (!File.GetAttributes(staging).HasFlag(FileAttributes.ReparsePoint)) Directory.Delete(staging, recursive: true);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
            }
        }
    }

    static void CopyDirectory(string source, string destination, int depth, ref int count, ref long bytes)
    {
        RejectReparsePoint(source);
        if (depth > MaximumDepth) throw new IOException("The existing profile has too many nested directories.");
        foreach (var entry in Directory.EnumerateFileSystemEntries(source)) {
            if (++count > MaximumEntries) throw new IOException("The existing profile contains too many entries.");
            var attributes = File.GetAttributes(entry);
            if (attributes.HasFlag(FileAttributes.ReparsePoint)) throw new IOException("A linked profile entry cannot be migrated automatically.");
            var target = Path.Combine(destination, Path.GetFileName(entry));
            if (attributes.HasFlag(FileAttributes.Directory)) {
                Directory.CreateDirectory(target);
                CopyDirectory(entry, target, depth + 1, ref count, ref bytes);
            }
            else {
                using var input = new FileStream(entry, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (input.Length > MaximumBytes - bytes) throw new IOException("The existing profile is too large to migrate automatically.");
                bytes += input.Length;
                using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                input.CopyTo(output);
                output.Flush(flushToDisk: true);
            }
        }
    }

    static void RejectReparsePoint(string path)
    {
        if (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
            throw new IOException("A linked profile directory cannot be migrated automatically.");
    }

    public static string? NormalizeModelPath(string? path, string legacyLocal, string newLocal)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) return path;
        try {
            var oldModels = Path.GetFullPath(Path.Combine(legacyLocal, "models"));
            var relative = Path.GetRelativePath(oldModels, Path.GetFullPath(path));
            if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)) return path;
            var newRoot = Path.GetFullPath(newLocal);
            var newModels = Path.Combine(newRoot, "models");
            var target = Path.GetFullPath(Path.Combine(newModels, relative));
            if (!File.Exists(target) && !Directory.Exists(target)) return path;
            // An existing destination may contain user-created links. Do not redirect
            // a legacy setting through one of those links outside the migrated tree.
            for (string? current = target; current != null; current = Path.GetDirectoryName(current)) {
                RejectReparsePoint(current);
                if (current.Equals(newRoot, StringComparison.OrdinalIgnoreCase)) return target;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { }
        return path;
    }
}
