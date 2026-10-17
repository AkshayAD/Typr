using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Typr.Infrastructure;

internal sealed class NativeProviderCli(string dataDirectory, string? codexOverride = null)
{
    private static readonly HttpClient Http = CreateHttpClient();
    internal string WorkingDirectory { get; } = Directory.CreateDirectory(Path.Combine(dataDirectory, "empty-workspace")).FullName;

    internal string? Resolve(PolishProvider provider)
    {
        if (provider == PolishProvider.ChatGpt && codexOverride is not null)
            return IsNativeExecutable(codexOverride) ? Path.GetFullPath(codexOverride) : null;
        string directory = Path.Combine(dataDirectory, provider.ToString(), "cli");
        string marker = Path.Combine(directory, "current.txt");
        if (File.Exists(marker))
        {
            string version = File.ReadAllText(marker).Trim();
            if (Regex.IsMatch(version, @"^\d+\.\d+\.\d+(?:[-.][A-Za-z0-9]+)*$"))
            {
                string managed = Path.Combine(directory, version, provider == PolishProvider.ChatGpt ? "codex.exe" : "claude.exe");
                if (IsNativeExecutable(managed)) return managed;
            }
        }
        string npm = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm", "node_modules");
        string architecture = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "arm64" : "x64";
        string triple = architecture == "arm64" ? "aarch64-pc-windows-msvc" : "x86_64-pc-windows-msvc";
        var candidates = provider == PolishProvider.ChatGpt
            ? new[] { Path.Combine(npm, "@openai", "codex", "node_modules", "@openai", "codex-win32-" + architecture, "vendor", triple, "bin", "codex.exe") }
            : new[] { Path.Combine(npm, "@anthropic-ai", "claude-code", "bin", "claude.exe"), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin", "claude.exe") };
        foreach (string candidate in candidates) if (IsNativeExecutable(candidate)) return candidate;
        foreach (string path in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(path)) continue;
            try
            {
                string directoryPath = path.Trim('"');
                if (!Path.IsPathFullyQualified(directoryPath)) continue;
                string candidate = Path.Combine(directoryPath, provider == PolishProvider.ChatGpt ? "codex.exe" : "claude.exe");
                if (IsNativeExecutable(candidate)) return Path.GetFullPath(candidate);
            }
            catch (ArgumentException) { }
        }
        return null;
    }

    internal static bool IsNativeExecutable(string path) => Path.IsPathFullyQualified(path) && Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(path);

    internal async Task<string?> VersionAsync(PolishProvider provider, CancellationToken cancellationToken)
    {
        string? path = Resolve(provider);
        if (path is null) return null;
        var result = await RunAsync(path, ["--version"], null, WorkingDirectory, cancellationToken).ConfigureAwait(false);
        return Regex.Match(result.Output, @"\d+\.\d+\.\d+(?:[-.][A-Za-z0-9]+)*").Value is { Length: > 0 } version ? version : null;
    }

    internal async Task<CliUpdateInfo> CheckUpdateAsync(PolishProvider provider, CancellationToken cancellationToken)
    {
        string? installed = await VersionAsync(provider, cancellationToken).ConfigureAwait(false);
        var release = await GetReleaseAsync(provider, cancellationToken).ConfigureAwait(false);
        return new(installed, release.Version, installed is null || IsNewer(release.Version, installed));
    }

    internal async Task InstallUpdateAsync(PolishProvider provider, CancellationToken cancellationToken)
    {
        var release = await GetReleaseAsync(provider, cancellationToken).ConfigureAwait(false);
        string directory = Path.Combine(dataDirectory, provider.ToString(), "cli");
        Directory.CreateDirectory(directory);
        string download = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".download");
        try
        {
            using (var response = await Http.GetAsync(release.Download, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                await using var file = new FileStream(download, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
                await response.Content.CopyToAsync(file, cancellationToken).ConfigureAwait(false);
            }
            await using (var file = File.OpenRead(download))
            {
                byte[] actual = release.IsArchive ? await SHA512.HashDataAsync(file, cancellationToken).ConfigureAwait(false) : await SHA256.HashDataAsync(file, cancellationToken).ConfigureAwait(false);
                if (!CryptographicOperations.FixedTimeEquals(actual, release.Digest))
                    throw new PolishProviderException(ProviderConnectionState.Error, "The CLI download failed its integrity check. Try updating again.");
            }
            string versionDirectory = Path.Combine(directory, release.Version);
            Directory.CreateDirectory(versionDirectory);
            string destination = Path.Combine(versionDirectory, provider == PolishProvider.ChatGpt ? "codex.exe" : "claude.exe");
            if (!File.Exists(destination))
            {
                if (!release.IsArchive) File.Move(download, destination);
                else
                {
                    string temporaryBinary = Path.Combine(versionDirectory, Guid.NewGuid().ToString("N") + ".exe");
                    try
                    {
                        await using var compressed = File.OpenRead(download);
                        await using var gzip = new GZipStream(compressed, CompressionMode.Decompress);
                        using var archive = new TarReader(gzip);
                        bool found = false;
                        while (await archive.GetNextEntryAsync(cancellationToken: cancellationToken).ConfigureAwait(false) is { } entry)
                        {
                            // Extract only the published native binary, never archive-controlled paths or scripts.
                            if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile) ||
                                !entry.Name.Replace('\\', '/').EndsWith("/claude.exe", StringComparison.Ordinal) || entry.DataStream is null) continue;
                            await using (var binary = new FileStream(temporaryBinary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
                                await entry.DataStream.CopyToAsync(binary, cancellationToken).ConfigureAwait(false);
                            found = true;
                            break;
                        }
                        if (!found) throw new PolishProviderException(ProviderConnectionState.Error, "The published package did not contain the expected native Claude Code binary.");
                        File.Move(temporaryBinary, destination);
                    }
                    finally { if (File.Exists(temporaryBinary)) File.Delete(temporaryBinary); }
                }
            }
            // A versioned installation does not replace or update the user's global CLI.
            await File.WriteAllTextAsync(Path.Combine(directory, "current.txt"), release.Version, cancellationToken).ConfigureAwait(false);
        }
        finally { if (File.Exists(download)) File.Delete(download); }
    }

    private sealed record Release(string Version, Uri Download, byte[] Digest, bool IsArchive);
    private static async Task<Release> GetReleaseAsync(PolishProvider provider, CancellationToken cancellationToken)
    {
        string architecture = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "arm64" : "x64";
        if (provider == PolishProvider.ChatGpt)
        {
            using var response = await Http.GetAsync("https://api.github.com/repos/openai/codex/releases/latest", cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
            string version = json.RootElement.GetProperty("tag_name").GetString()!.Replace("rust-v", "", StringComparison.Ordinal);
            string name = "codex-" + (architecture == "arm64" ? "aarch64" : "x86_64") + "-pc-windows-msvc.exe";
            var asset = json.RootElement.GetProperty("assets").EnumerateArray().Single(item => item.GetProperty("name").GetString() == name);
            string digest = asset.GetProperty("digest").GetString()!;
            if (!digest.StartsWith("sha256:", StringComparison.Ordinal)) throw new FormatException("No trusted release digest.");
            var uri = new Uri(asset.GetProperty("browser_download_url").GetString()!);
            if (uri.Scheme != "https" || uri.Host != "github.com" || !uri.AbsolutePath.StartsWith("/openai/codex/releases/download/", StringComparison.Ordinal)) throw new FormatException("Invalid release location.");
            ValidateVersion(version);
            return new(version, uri, Convert.FromHexString(digest[7..]), false);
        }
        using (var response = await Http.GetAsync("https://registry.npmjs.org/@anthropic-ai/claude-code-win32-" + architecture + "/latest", cancellationToken).ConfigureAwait(false))
        {
            response.EnsureSuccessStatusCode();
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
            string version = json.RootElement.GetProperty("version").GetString()!;
            var dist = json.RootElement.GetProperty("dist");
            string integrity = dist.GetProperty("integrity").GetString()!;
            var uri = new Uri(dist.GetProperty("tarball").GetString()!);
            if (!integrity.StartsWith("sha512-", StringComparison.Ordinal) || uri.Scheme != "https" || uri.Host != "registry.npmjs.org") throw new FormatException("Invalid package integrity metadata.");
            ValidateVersion(version);
            return new(version, uri, Convert.FromBase64String(integrity[7..]), true);
        }
    }

    private static void ValidateVersion(string version)
    {
        if (!Regex.IsMatch(version, @"^\d+\.\d+\.\d+$")) throw new FormatException("Invalid stable release version.");
    }

    internal static bool IsNewer(string latest, string installed)
    {
        var stable = Regex.Match(installed, @"^\d+\.\d+\.\d+").Value;
        return Version.TryParse(latest, out var target) && Version.TryParse(stable, out var current) &&
            (target > current || target == current && installed != stable);
    }

    internal sealed record ProcessResult(int ExitCode, string Output);
    internal static ProcessStartInfo StartInfo(string executable, IEnumerable<string> arguments, string workingDirectory)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = workingDirectory };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        foreach (string variable in new[] { "ANTHROPIC_API_KEY", "ANTHROPIC_AUTH_TOKEN", "ANTHROPIC_BASE_URL", "CLAUDE_CODE_OAUTH_TOKEN", "CLAUDE_CODE_USE_BEDROCK", "CLAUDE_CODE_USE_VERTEX", "CLAUDE_CODE_USE_FOUNDRY", "ANTHROPIC_DEFAULT_OPUS_MODEL", "ANTHROPIC_DEFAULT_SONNET_MODEL", "ANTHROPIC_DEFAULT_HAIKU_MODEL", "ANTHROPIC_DEFAULT_FABLE_MODEL", "CLAUDE_CODE_EFFORT_LEVEL" })
            start.Environment.Remove(variable);
        // Safe mode skips user settings, but shell-level telemetry settings are
        // still inherited. Dictation must not reach an unrelated telemetry collector.
        foreach (string variable in start.Environment.Keys.Where(name =>
            name.StartsWith("OTEL_", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("CLAUDE_CODE_OTEL_", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("BETA_TRACING_ENDPOINT", StringComparison.OrdinalIgnoreCase)).ToArray())
            start.Environment.Remove(variable);
        foreach (string variable in new[] { "CLAUDE_CODE_ENABLE_TELEMETRY", "CLAUDE_CODE_ENHANCED_TELEMETRY_BETA", "ENABLE_ENHANCED_TELEMETRY_BETA",
            "OTEL_LOG_USER_PROMPTS", "OTEL_LOG_ASSISTANT_RESPONSES", "OTEL_LOG_TOOL_CONTENT", "OTEL_LOG_TOOL_DETAILS", "OTEL_LOG_RAW_API_BODIES" })
            start.Environment[variable] = "0";
        foreach (string variable in new[] { "OTEL_METRICS_EXPORTER", "OTEL_LOGS_EXPORTER", "OTEL_TRACES_EXPORTER" })
            start.Environment[variable] = "none";
        start.Environment["CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC"] = "1";
        return start;
    }

    internal static async Task<ProcessResult> RunAsync(string executable, IEnumerable<string> arguments, string? input, string workingDirectory, CancellationToken cancellationToken)
    {
        using var process = Process.Start(StartInfo(executable, arguments, workingDirectory)) ?? throw new IOException("Could not start the provider CLI.");
        using var registration = cancellationToken.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { } });
        var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errors = process.StandardError.ReadToEndAsync(cancellationToken);
        if (input is not null) await process.StandardInput.WriteAsync(input.AsMemory(), cancellationToken).ConfigureAwait(false);
        process.StandardInput.Close();
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        string text = await output.ConfigureAwait(false);
        await errors.ConfigureAwait(false); // Never expose stderr, which can contain credentials or dictated text.
        return new(process.ExitCode, text);
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Typr/1.0");
        return client;
    }
}
