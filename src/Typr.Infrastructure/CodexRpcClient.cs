using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Typr.Infrastructure;

internal interface ICodexConnection : IAsyncDisposable
{
    event Action<string, JsonElement>? Notification;
    bool IsRunning { get; }
    Task<JsonElement> RequestAsync(string method, object parameters, CancellationToken cancellationToken);
}

internal sealed class CodexRpcClient : ICodexConnection
{
    private readonly Process _process;
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly SemaphoreSlim _writer = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Task _reader;
    private readonly string? _catalogPath;
    private long _requestId;
    public event Action<string, JsonElement>? Notification;
    public bool IsRunning => !_lifetime.IsCancellationRequested && !_process.HasExited;

    private CodexRpcClient(Process process, string? catalogPath)
    {
        _process = process;
        _catalogPath = catalogPath;
        _reader = ReadAsync();
        // Drain diagnostic output without retaining it: it can contain provider or transcript details.
        _ = DrainErrorsAsync();
    }

    internal static async Task<CodexRpcClient> StartAsync(string executable, string workingDirectory, CancellationToken cancellationToken, PolishModel? textOnlyModel = null)
    {
        var start = CreateStartInfo(executable, workingDirectory);
        string? catalogPath = null;
        if (textOnlyModel is not null)
        {
            string catalogDirectory = Path.Combine(Path.GetDirectoryName(workingDirectory)!, "text-only-catalogs");
            Directory.CreateDirectory(catalogDirectory);
            catalogPath = Path.Combine(catalogDirectory, Guid.NewGuid().ToString("N") + ".json");
            await File.WriteAllTextAsync(catalogPath, CodexTextOnlyPolicy.CreateCatalog(textOnlyModel), cancellationToken).ConfigureAwait(false);
            start.ArgumentList.Add("-c");
            start.ArgumentList.Add("model_catalog_json=" + JsonSerializer.Serialize(catalogPath.Replace('\\', '/')));
        }
        Process process;
        try { process = Process.Start(start) ?? throw Unavailable(); }
        catch { if (catalogPath is not null && File.Exists(catalogPath)) File.Delete(catalogPath); throw; }
        var client = new CodexRpcClient(process, catalogPath);
        try
        {
            await client.RequestAsync("initialize", new
            {
                clientInfo = new { name = "typr", title = "Typr", version = "1.0.0" },
                capabilities = new { experimentalApi = textOnlyModel is not null }
            }, cancellationToken).ConfigureAwait(false);
            await client.WriteAsync(new { method = "initialized" }, cancellationToken).ConfigureAwait(false);
            return client;
        }
        catch { await client.DisposeAsync().ConfigureAwait(false); throw; }
    }

    internal static ProcessStartInfo CreateStartInfo(string executable, string workingDirectory)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = workingDirectory,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8
        };
        start.ArgumentList.Add("app-server");
        start.ArgumentList.Add("--listen");
        start.ArgumentList.Add("stdio://");
        foreach (var setting in RestrictedConfiguration) { start.ArgumentList.Add("-c"); start.ArgumentList.Add(setting); }
        string codexHome = PrivateHome(workingDirectory);
        Directory.CreateDirectory(codexHome);
        if (File.Exists(Path.Combine(codexHome, "AGENTS.md")) || File.Exists(Path.Combine(codexHome, "AGENTS.override.md")))
            throw new PolishProviderException(ProviderConnectionState.Error, "Typr's private provider folder contains custom instructions. Remove those custom AGENTS files before using text-only polishing.");
        start.Environment["CODEX_HOME"] = codexHome;
        // Only the official CLI reads and renews its own private credentials. An inherited API
        // key must never change subscription requests into separately billed API usage.
        foreach (string name in new[] { "OPENAI_API_KEY", "OPENAI_BASE_URL", "CODEX_API_KEY" }) start.Environment.Remove(name);
        return start;
    }

    internal static string PrivateHome(string workingDirectory) => Path.Combine(Path.GetDirectoryName(workingDirectory)!, "codex-home");

    internal static readonly string[] RestrictedConfiguration =
    [
        "model_provider=\"openai\"", "approval_policy=\"never\"", "sandbox_mode=\"read-only\"",
        "features.shell_tool=false", "features.multi_agent=false", "features.multi_agent_v2=false",
        "features.memories=false", "features.hooks=false", "features.apps=false", "features.plugins=false",
        "features.shell_snapshot=false", "features.view_image=false", "features.browser_use=false",
        "features.browser_use_external=false", "features.browser_use_full_cdp_access=false", "features.computer_use=false",
        "features.image_generation=false", "features.in_app_browser=false", "features.code_mode=false",
        "features.code_mode_only=false", "features.code_mode_host=false", "features.skill_search=false",
        "features.skip_host_skill_discovery=true", "features.skill_mcp_dependency_install=false",
        "features.tool_suggest=false", "features.remote_plugin=false", "features.request_permissions_tool=false",
        "features.send_message_to_user_async=false", "features.current_time_reminder=false", "features.sleep_tool=false",
        "features.deferred_executor=false", "features.token_budget=false", "features.goals=false",
        "agents.enabled=false", "web_search=\"disabled\"", "tools.update_plan.enabled=false", "tools.experimental_request_user_input.enabled=false",
        "mcp_servers={}", "plugins={}", "apps._default.enabled=false", "project_doc_max_bytes=0",
        "history.persistence=\"none\"", "analytics.enabled=false", "check_for_update_on_startup=false",
        "notify=[]", "otel.exporter=\"none\"", "otel.trace_exporter=\"none\"", "otel.metrics_exporter=\"none\"", "otel.log_user_prompt=false"
    ];

    public async Task<JsonElement> RequestAsync(string method, object parameters, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsRunning) throw Unavailable();
        long id = Interlocked.Increment(ref _requestId);
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = completion;
        try
        {
            await WriteAsync(new { id, method, @params = parameters }, cancellationToken).ConfigureAwait(false);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(45));
            try { return await completion.Task.WaitAsync(timeout.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            { throw new PolishProviderException(ProviderConnectionState.Error, "Codex did not respond. Restart the provider or update its CLI."); }
        }
        finally { _pending.TryRemove(id, out _); }
    }

    private async Task WriteAsync(object message, CancellationToken cancellationToken)
    {
        await _writer.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(message).AsMemory(), cancellationToken).ConfigureAwait(false);
            await _process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { _writer.Release(); }
    }

    private async Task ReadAsync()
    {
        try
        {
            while (await _process.StandardOutput.ReadLineAsync(_lifetime.Token).ConfigureAwait(false) is { } line)
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (root.TryGetProperty("id", out var id) && !root.TryGetProperty("method", out _))
                {
                    if (id.TryGetInt64(out var number) && _pending.TryGetValue(number, out var pending))
                    {
                        if (root.TryGetProperty("error", out var error)) pending.TrySetException(MapError(error));
                        else if (root.TryGetProperty("result", out var result)) pending.TrySetResult(result.Clone());
                    }
                }
                else if (root.TryGetProperty("method", out var method))
                {
                    if (root.TryGetProperty("id", out id))
                    {
                        // No tool, file, command, permission, token-refresh or user-input request is delegated to Typr.
                        await WriteAsync(new { id = id.Clone(), error = new { code = -32601, message = "Typr only edits supplied text; this operation is disabled." } }, _lifetime.Token).ConfigureAwait(false);
                    }
                    else if (root.TryGetProperty("params", out var parameters))
                        Notification?.Invoke(method.GetString()!, parameters.Clone());
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or OperationCanceledException or ObjectDisposedException) { }
        finally
        {
            foreach (var pending in _pending.Values) pending.TrySetException(Unavailable());
            Notification?.Invoke("typr/disconnected", JsonSerializer.SerializeToElement(new { }));
        }
    }

    private async Task DrainErrorsAsync()
    {
        try { while (await _process.StandardError.ReadLineAsync(_lifetime.Token).ConfigureAwait(false) is not null) { } }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException) { }
    }

    internal static PolishProviderException MapError(JsonElement error)
    {
        string message = error.ValueKind == JsonValueKind.String ? error.GetString()! : error.ToString();
        if (new[] { "unauthorized", "authentication", "401", "login", "sign in", "token expired", "refresh token" }.Any(part => message.Contains(part, StringComparison.OrdinalIgnoreCase)))
            return new(ProviderConnectionState.SignInRequired, "Your ChatGPT sign-in has expired. Sign in again in Settings.");
        if (message.Contains("model", StringComparison.OrdinalIgnoreCase) && new[] { "not found", "not supported", "unavailable", "access", "does not exist" }.Any(part => message.Contains(part, StringComparison.OrdinalIgnoreCase)))
            return new(ProviderConnectionState.Error, "The selected model is unavailable for this account. Refresh models or choose an available model; Typr has not substituted another model.");
        if (new[] { "rate limit", "usage limit", "quota", "429", "credits" }.Any(part => message.Contains(part, StringComparison.OrdinalIgnoreCase)))
            return new(ProviderConnectionState.Error, "The ChatGPT plan's current usage limit was reached. Try later or choose another explicitly configured provider.");
        return new(ProviderConnectionState.Error, "The provider could not complete polishing. Check the connection, sign in again or update the CLI.");
    }

    private static PolishProviderException Unavailable() => new(ProviderConnectionState.Error, "The Codex provider stopped. Restart it in Settings or update its CLI.");

    public async ValueTask DisposeAsync()
    {
        if (_lifetime.IsCancellationRequested) return;
        _lifetime.Cancel();
        try { if (!_process.HasExited) _process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        try { await _reader.ConfigureAwait(false); } catch (OperationCanceledException) { }
        _process.Dispose();
        if (_catalogPath is not null && File.Exists(_catalogPath)) File.Delete(_catalogPath);
        _lifetime.Dispose();
    }
}
