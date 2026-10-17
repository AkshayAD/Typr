using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Typr.Core;

namespace Typr.Infrastructure;

internal sealed class ClaudeCodeProvider(NativeProviderCli cli) : IAsyncDisposable
{
    private readonly Dictionary<string, (Process Process, Task<ProviderConnection> Completion)> _logins = new();

    internal async Task<ProviderConnection> GetConnectionAsync(CancellationToken cancellationToken)
    {
        string? path = cli.Resolve(PolishProvider.Claude);
        if (path is null) return new(PolishProvider.Claude, ProviderConnectionState.CliMissing, "Install the official Claude Code CLI in Settings, then sign in with your own Claude subscription.");
        var result = await NativeProviderCli.RunAsync(path, ["auth", "status", "--json"], null, cli.WorkingDirectory, cancellationToken).ConfigureAwait(false);
        using var json = JsonDocument.Parse(result.Output);
        var root = json.RootElement;
        bool loggedIn = root.TryGetProperty("loggedIn", out var logged) && logged.GetBoolean();
        string method = root.TryGetProperty("authMethod", out var auth) ? auth.GetString() ?? "" : "";
        bool firstParty = root.TryGetProperty("apiProvider", out var provider) && provider.GetString() == "firstParty";
        if (!loggedIn || !firstParty || method.Contains("api", StringComparison.OrdinalIgnoreCase))
            return new(PolishProvider.Claude, ProviderConnectionState.SignInRequired, "Connect local Claude Code with your own Claude subscription. API credits are not used automatically.");
        string? label = root.TryGetProperty("email", out var email) ? email.GetString() : null;
        return new(PolishProvider.Claude, ProviderConnectionState.Connected, "Connected through your unmodified local Claude Code CLI and its own sign-in.", label);
    }

    internal async Task<ProviderSignIn> StartSignInAsync(CancellationToken cancellationToken)
    {
        string path = RequireExecutable();
        var process = Process.Start(NativeProviderCli.StartInfo(path, ["auth", "login", "--claudeai"], cli.WorkingDirectory))
            ?? throw new IOException("Could not start Claude Code sign-in.");
        string id = "claude-" + Guid.NewGuid().ToString("N");
        var browser = new TaskCompletionSource<Uri?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var stdout = ReadLoginOutputAsync(process.StandardOutput, browser);
        var stderr = ReadLoginOutputAsync(process.StandardError, browser);
        var completed = CompleteLoginAsync(process, stdout, stderr);
        lock (_logins) _logins.Add(id, (process, completed));
        // Claude Code owns its OAuth callback and opens the browser. Never collect a token or code.
        try
        {
            await Task.WhenAny(browser.Task, completed, Task.Delay(TimeSpan.FromSeconds(2), cancellationToken)).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException) { await CancelLoginAsync(id).ConfigureAwait(false); throw; }
        Uri? uri = browser.Task.IsCompletedSuccessfully ? browser.Task.Result : null;
        return new(uri, "Complete sign-in in the browser opened by the official Claude Code CLI, then return to Typr. Your credentials stay with Claude Code.", id);
    }

    internal async Task<ProviderConnection> WaitForSignInAsync(string loginId, CancellationToken cancellationToken)
    {
        (Process Process, Task<ProviderConnection> Completion) login;
        lock (_logins)
            if (!_logins.TryGetValue(loginId, out login))
                throw new PolishProviderException(ProviderConnectionState.SignInRequired, "Start Claude Code sign-in again.");
        try { return await login.Completion.WaitAsync(cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { await CancelLoginAsync(loginId).ConfigureAwait(false); throw; }
        finally
        {
            if (login.Completion.IsCompleted)
            {
                lock (_logins) _logins.Remove(loginId);
                login.Process.Dispose();
            }
        }
    }

    private async Task CancelLoginAsync(string loginId)
    {
        (Process Process, Task<ProviderConnection> Completion) login;
        lock (_logins) { if (!_logins.Remove(loginId, out login)) return; }
        try { if (!login.Process.HasExited) login.Process.Kill(true); } catch (InvalidOperationException) { }
        try { await login.Completion.ConfigureAwait(false); }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or JsonException) { }
        login.Process.Dispose();
    }

    private async Task<ProviderConnection> CompleteLoginAsync(Process process, Task stdout, Task stderr)
    {
        await process.WaitForExitAsync().ConfigureAwait(false);
        await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
        if (process.ExitCode != 0) return new(PolishProvider.Claude, ProviderConnectionState.SignInRequired, "Claude Code sign-in did not finish. Start sign-in again.");
        return await GetConnectionAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private static async Task ReadLoginOutputAsync(StreamReader stream, TaskCompletionSource<Uri?> browser)
    {
        while (await stream.ReadLineAsync().ConfigureAwait(false) is { } line)
        {
            var match = Regex.Match(line, @"https://[^\s\x1b]+", RegexOptions.CultureInvariant);
            if (match.Success && Uri.TryCreate(match.Value.TrimEnd(')', '.', '"'), UriKind.Absolute, out var uri) &&
                (uri.Host == "claude.ai" || uri.Host == "platform.claude.com" || uri.Host == "console.anthropic.com"))
                browser.TrySetResult(uri);
        }
    }

    internal async Task<IReadOnlyList<PolishModel>> GetModelsAsync(CancellationToken cancellationToken)
    {
        RequireExecutable();
        string? version = await cli.VersionAsync(PolishProvider.Claude, cancellationToken).ConfigureAwait(false);
        return ModelsForVersion(version);
    }

    internal static IReadOnlyList<PolishModel> ModelsForVersion(string? version)
    {
        // The official model-config compatibility table requires 2.1.293 for all three 5.5
        // models. Pin IDs: older aliases can silently lower effort or select an older model.
        if (!Version.TryParse(version, out var installed) || installed < new Version(2, 1, 293))
            throw new PolishProviderException(ProviderConnectionState.Error, "Update Claude Code to 2.1.293 or later to use Typr's verified Claude model and reasoning options.");
        return new[] { "sonnet", "opus", "haiku" }.Select(family => new PolishModel(
            $"claude-{family}-5-5", $"Claude {char.ToUpperInvariant(family[0])}{family[1..]} 5.5 (catalog; access varies)",
            ["low", "medium", "high", "xhigh", "max"], "low")).ToArray();
    }

    internal static void ValidateSelection(IReadOnlyList<PolishModel> models, string model, string effort)
    {
        var selected = models.FirstOrDefault(item => item.Id == model);
        if (selected is null || !selected.ReasoningEfforts.Contains(effort, StringComparer.Ordinal))
            throw new PolishProviderException(ProviderConnectionState.Error, "Refresh Claude models and choose a listed model and supported reasoning effort. Typr has not substituted a model or lowered your effort.");
    }

    internal async Task<string> PolishAsync(string text, string model, string effort, CancellationToken cancellationToken)
    {
        ValidateSelection(await GetModelsAsync(cancellationToken).ConfigureAwait(false), model, effort);
        var connection = await GetConnectionAsync(cancellationToken).ConfigureAwait(false);
        if (connection.State != ProviderConnectionState.Connected) throw new PolishProviderException(connection.State, connection.Message);
        var arguments = CreatePolishArguments(model, effort);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(2));
        var result = await NativeProviderCli.RunAsync(RequireExecutable(), arguments, PolishPrompt.CreateInput(text), cli.WorkingDirectory, timeout.Token).ConfigureAwait(false);
        return ReadPolishResult(result.Output, result.ExitCode, text, model);
    }

    internal static string[] CreatePolishArguments(string model, string effort) =>
    [
        "--print", "--output-format", "json", "--model", model, "--effort", effort,
        "--tools", "", "--safe-mode", "--setting-sources", "", "--strict-mcp-config",
        "--mcp-config", "{\"mcpServers\":{}}", "--settings", JsonSerializer.Serialize(new { disableAllHooks = true, availableModels = new[] { model }, enforceAvailableModels = true }),
        "--disable-slash-commands", "--no-chrome", "--no-session-persistence", "--permission-prompts", "none",
        "--system-prompt", PolishPrompt.Instructions,
        "--json-schema", "{\"type\":\"object\",\"properties\":{\"text\":{\"type\":\"string\"}},\"required\":[\"text\"],\"additionalProperties\":false}"
    ];

    internal static string ReadPolishResult(string output, int exitCode, string original, string? expectedModel = null)
    {
        using var document = JsonDocument.Parse(output);
        var root = document.RootElement;
        if (exitCode != 0 || root.TryGetProperty("is_error", out var isError) && isError.GetBoolean())
        {
            var failure = CodexRpcClient.MapError(root);
            throw new PolishProviderException(failure.State, failure.State == ProviderConnectionState.SignInRequired
                ? "Your Claude Code sign-in expired. Connect local Claude Code again in Settings."
                : "Claude Code could not polish this text. Check the selected model, subscription usage and connection.");
        }
        if (expectedModel is not null && (!root.TryGetProperty("modelUsage", out var usage) || usage.ValueKind != JsonValueKind.Object ||
            !usage.EnumerateObject().Any() || usage.EnumerateObject().Any(item => item.Name != expectedModel)))
            throw new PolishProviderException(ProviderConnectionState.Error, "Claude Code did not confirm the requested model. Typr rejected a possible model substitution.");
        if (!root.TryGetProperty("structured_output", out var structured)) throw new FormatException("Claude Code did not return structured text.");
        return PolishPrompt.ReadResponse(structured.GetRawText(), original);
    }

    private string RequireExecutable() => cli.Resolve(PolishProvider.Claude) ??
        throw new PolishProviderException(ProviderConnectionState.CliMissing, "Install the official Claude Code CLI in Settings first.");

    public ValueTask DisposeAsync()
    {
        lock (_logins)
            foreach (var login in _logins.Values)
            {
                try { if (!login.Process.HasExited) login.Process.Kill(true); } catch (InvalidOperationException) { }
                login.Process.Dispose();
            }
        return ValueTask.CompletedTask;
    }
}
