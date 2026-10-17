using System.Collections.Concurrent;
using System.Text.Json;
using Typr.Core;

namespace Typr.Infrastructure;

public sealed class AiPolishingService : IAsyncDisposable
{
    private readonly NativeProviderCli _cli;
    private readonly ClaudeCodeProvider _claude;
    private readonly Func<CancellationToken, Task<ICodexConnection>> _connect;
    private readonly Func<PolishModel, CancellationToken, Task<ICodexConnection>> _connectTextOnly;
    private readonly SemaphoreSlim _connectionGate = new(1, 1);
    private readonly SemaphoreSlim _polishGate = new(1, 1);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<bool>> _logins = new();
    private ICodexConnection? _connection;
    private bool _disposed;

    public AiPolishingService(string? dataDirectory = null, string? cliPath = null)
    {
        dataDirectory ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Typr", "providers");
        _cli = new(dataDirectory, cliPath);
        _claude = new(_cli);
        _connect = async cancellationToken => await CodexRpcClient.StartAsync(RequireExecutable(), _cli.WorkingDirectory, cancellationToken).ConfigureAwait(false);
        _connectTextOnly = async (model, cancellationToken) => await CodexRpcClient.StartAsync(RequireExecutable(), _cli.WorkingDirectory, cancellationToken, model).ConfigureAwait(false);
    }

    internal AiPolishingService(string dataDirectory, Func<CancellationToken, Task<ICodexConnection>> connect) : this(dataDirectory)
    {
        _connect = connect;
        _connectTextOnly = (_, cancellationToken) => connect(cancellationToken);
    }

    public async Task<ProviderConnection> GetConnectionAsync(PolishProvider provider, CancellationToken cancellationToken = default)
    {
        try
        {
            if (provider == PolishProvider.Claude) return await _claude.GetConnectionAsync(cancellationToken).ConfigureAwait(false);
            var connection = await EnsureConnectionAsync(cancellationToken).ConfigureAwait(false);
            var response = await connection.RequestAsync("account/read", new { refreshToken = false }, cancellationToken).ConfigureAwait(false);
            if (!response.TryGetProperty("account", out var account) || account.ValueKind == JsonValueKind.Null ||
                !account.TryGetProperty("type", out var type) || type.GetString() != "chatgpt")
                return new(provider, ProviderConnectionState.SignInRequired, "Connect your ChatGPT subscription in Typr. Its private sign-in keeps personal Codex instructions and tools out of dictation.");
            string? label = account.TryGetProperty("email", out var email) ? email.GetString() : null;
            return new(provider, ProviderConnectionState.Connected, "Connected with your ChatGPT subscription through the official Codex CLI.", label);
        }
        catch (PolishProviderException ex) { return new(provider, ex.State, ex.Message); }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException or System.ComponentModel.Win32Exception)
        { return new(provider, ProviderConnectionState.Error, "Could not read the provider connection. Restart it or update its CLI in Settings."); }
    }

    public async Task<ProviderSignIn> StartSignInAsync(PolishProvider provider, CancellationToken cancellationToken = default)
    {
        if (provider == PolishProvider.Claude) return await _claude.StartSignInAsync(cancellationToken).ConfigureAwait(false);
        var connection = await EnsureConnectionAsync(cancellationToken).ConfigureAwait(false);
        var response = await connection.RequestAsync("account/login/start", new { type = "chatgpt" }, cancellationToken).ConfigureAwait(false);
        string id = response.GetProperty("loginId").GetString()!;
        _logins.GetOrAdd(id, _ => new(TaskCreationOptions.RunContinuationsAsynchronously));
        var uri = new Uri(response.GetProperty("authUrl").GetString()!);
        if (uri.Scheme != "https" || uri.Host != "auth.openai.com" && uri.Host != "chatgpt.com")
            throw new PolishProviderException(ProviderConnectionState.Error, "Codex returned an unexpected sign-in address. Update its CLI and try again.");
        return new(uri, "Finish the official ChatGPT sign-in in your browser, then return to Typr.", id);
    }

    public async Task<ProviderConnection> WaitForSignInAsync(string loginId, CancellationToken cancellationToken = default)
    {
        if (loginId.StartsWith("claude-", StringComparison.Ordinal)) return await _claude.WaitForSignInAsync(loginId, cancellationToken).ConfigureAwait(false);
        var completion = _logins.GetOrAdd(loginId, _ => new(TaskCreationOptions.RunContinuationsAsynchronously));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(10));
        bool success;
        try { success = await completion.Task.WaitAsync(timeout.Token).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            try { if (_connection is not null) await _connection.RequestAsync("account/login/cancel", new { loginId }, cleanup.Token).ConfigureAwait(false); }
            catch (Exception ex) when (ex is PolishProviderException or OperationCanceledException or IOException) { }
            throw;
        }
        finally { _logins.TryRemove(loginId, out _); }
        return success ? await GetConnectionAsync(PolishProvider.ChatGpt, cancellationToken).ConfigureAwait(false) :
            new(PolishProvider.ChatGpt, ProviderConnectionState.SignInRequired, "ChatGPT sign-in did not finish. Start sign-in again.");
    }

    public async Task<IReadOnlyList<PolishModel>> GetModelsAsync(PolishProvider provider, CancellationToken cancellationToken = default)
    {
        if (provider == PolishProvider.Claude) return await _claude.GetModelsAsync(cancellationToken).ConfigureAwait(false);
        var connection = await EnsureConnectionAsync(cancellationToken).ConfigureAwait(false);
        var models = new List<PolishModel>();
        string? cursor = null;
        do
        {
            var response = await connection.RequestAsync("model/list", new { cursor, limit = 100, includeHidden = false }, cancellationToken).ConfigureAwait(false);
            foreach (var model in response.GetProperty("data").EnumerateArray())
            {
                string id = model.GetProperty("model").GetString()!;
                string name = model.GetProperty("displayName").GetString()!;
                var efforts = model.GetProperty("supportedReasoningEfforts").EnumerateArray().Select(item => item.GetProperty("reasoningEffort").GetString()!).ToArray();
                models.Add(new(id, name, efforts, model.GetProperty("defaultReasoningEffort").GetString()!));
            }
            cursor = response.TryGetProperty("nextCursor", out var next) && next.ValueKind == JsonValueKind.String ? next.GetString() : null;
        } while (cursor is not null);
        return models;
    }

    public async Task<string> PolishAsync(string text, PolishProvider provider, string model = "gpt-6-luna", string effort = "low", CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (string.IsNullOrWhiteSpace(text)) return "";
        if (string.IsNullOrWhiteSpace(model) || string.IsNullOrWhiteSpace(effort)) throw new ArgumentException("Choose a model and reasoning effort first.");
        await _polishGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (provider == PolishProvider.Claude) return await _claude.PolishAsync(text, model, effort, cancellationToken).ConfigureAwait(false);
            var status = await GetConnectionAsync(provider, cancellationToken).ConfigureAwait(false);
            if (status.State != ProviderConnectionState.Connected) throw new PolishProviderException(status.State, status.Message);
            var models = await GetModelsAsync(provider, cancellationToken).ConfigureAwait(false);
            var selected = models.FirstOrDefault(item => item.Id == model);
            if (selected is null) throw new PolishProviderException(ProviderConnectionState.Error, $"The selected model '{model}' is not in this account's current catalog. Refresh models or explicitly choose another model.");
            if (!selected.ReasoningEfforts.Contains(effort, StringComparer.Ordinal))
                throw new PolishProviderException(ProviderConnectionState.Error, "The selected model does not advertise that reasoning effort. Refresh models and choose one of its supported efforts.");
            return await PolishWithCodexAsync(text, selected, effort, cancellationToken).ConfigureAwait(false);
        }
        catch (FormatException) { throw new PolishProviderException(ProviderConnectionState.Error, "The provider returned text that failed Typr's output checks. Your original transcript is available; try polishing again."); }
        catch (JsonException) { throw new PolishProviderException(ProviderConnectionState.Error, "The provider returned an invalid response. Update its CLI or retry polishing."); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new PolishProviderException(ProviderConnectionState.Error, "Polishing timed out. Retry or choose a lower reasoning effort."); }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception)
        { throw new PolishProviderException(ProviderConnectionState.Error, "The provider CLI could not complete polishing. Restart it or update its CLI in Settings."); }
        finally { _polishGate.Release(); }
    }

    private async Task<string> PolishWithCodexAsync(string text, PolishModel selected, string effort, CancellationToken cancellationToken)
    {
        await using var connection = await _connectTextOnly(selected, cancellationToken).ConfigureAwait(false);
        var configuration = await connection.RequestAsync("config/read", new { includeLayers = false }, cancellationToken).ConfigureAwait(false);
        JsonElement? effectiveConfig = configuration.TryGetProperty("config", out var config) ? config : null;
        var start = await connection.RequestAsync("thread/start", CodexTextOnlyPolicy.ThreadParameters(selected, _cli.WorkingDirectory, effectiveConfig), cancellationToken).ConfigureAwait(false);
        CodexTextOnlyPolicy.ValidateThreadStart(start);
        string threadId = start.GetProperty("thread").GetProperty("id").GetString()!;
        var completed = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        string? finalText = null;
        string? turnId = null;
        void OnNotification(string method, JsonElement parameters)
        {
            if (method == "typr/disconnected")
            {
                completed.TrySetException(new PolishProviderException(ProviderConnectionState.Error, "The Codex provider stopped. Restart it in Settings or update its CLI."));
                return;
            }
            if (!parameters.TryGetProperty("threadId", out var thread) || thread.GetString() != threadId) return;
            if (method == "turn/started" && parameters.TryGetProperty("turn", out var started))
                turnId = started.GetProperty("id").GetString();
            if (method == "item/completed" && parameters.TryGetProperty("item", out var item) &&
                item.TryGetProperty("type", out var type) && type.GetString() == "agentMessage" &&
                (!item.TryGetProperty("phase", out var phase) || phase.ValueKind == JsonValueKind.Null || phase.GetString() == "final_answer"))
                finalText = item.GetProperty("text").GetString();
            if (method == "turn/completed") completed.TrySetResult(parameters.GetProperty("turn").Clone());
        }
        connection.Notification += OnNotification;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(2));
        try
        {
            var turn = await connection.RequestAsync("turn/start", new
            {
                threadId, input = new[] { new { type = "text", text = PolishPrompt.CreateInput(text) } }, model = selected.Id, effort,
                environments = Array.Empty<object>(), runtimeWorkspaceRoots = Array.Empty<string>(),
                summary = "none", sandboxPolicy = new { type = "readOnly", networkAccess = false },
                outputSchema = new { type = "object", properties = new { text = new { type = "string" } }, required = new[] { "text" }, additionalProperties = false }
            }, timeout.Token).ConfigureAwait(false);
            turnId = turn.GetProperty("turn").GetProperty("id").GetString();
            var result = await completed.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
            if (result.GetProperty("status").GetString() != "completed")
                throw result.TryGetProperty("error", out var error) && error.ValueKind != JsonValueKind.Null ? CodexRpcClient.MapError(error) :
                    new PolishProviderException(ProviderConnectionState.Error, "Polishing was interrupted. Your original transcript is still available.");
            if (finalText is null) throw new FormatException("No final edited text was returned.");
            return PolishPrompt.ReadResponse(finalText, text);
        }
        finally
        {
            connection.Notification -= OnNotification;
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            try
            {
                if (timeout.IsCancellationRequested && turnId is not null) await connection.RequestAsync("turn/interrupt", new { threadId, turnId }, cleanup.Token).ConfigureAwait(false);
                await connection.RequestAsync("thread/unsubscribe", new { threadId }, cleanup.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is PolishProviderException or OperationCanceledException or IOException) { }
        }
    }

    public Task<string?> GetCliVersionAsync(CancellationToken cancellationToken = default) => GetCliVersionAsync(PolishProvider.ChatGpt, cancellationToken);
    public Task<string?> GetCliVersionAsync(PolishProvider provider, CancellationToken cancellationToken = default) => _cli.VersionAsync(provider, cancellationToken);
    public Task<CliUpdateInfo> CheckCliUpdateAsync(CancellationToken cancellationToken = default) => CheckCliUpdateAsync(PolishProvider.ChatGpt, cancellationToken);
    public Task<CliUpdateInfo> CheckCliUpdateAsync(PolishProvider provider, CancellationToken cancellationToken = default) => _cli.CheckUpdateAsync(provider, cancellationToken);
    public Task InstallCliUpdateAsync(CancellationToken cancellationToken = default) => InstallCliUpdateAsync(PolishProvider.ChatGpt, cancellationToken);
    public async Task InstallCliUpdateAsync(PolishProvider provider, CancellationToken cancellationToken = default)
    {
        await _cli.InstallUpdateAsync(provider, cancellationToken).ConfigureAwait(false);
        if (provider == PolishProvider.ChatGpt) await RestartAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task RestartAsync(CancellationToken cancellationToken = default)
    {
        await _connectionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_connection is not null) await _connection.DisposeAsync().ConfigureAwait(false);
            _connection = null;
        }
        finally { _connectionGate.Release(); }
    }

    private async Task<ICodexConnection> EnsureConnectionAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _connectionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_connection is { IsRunning: true }) return _connection;
            if (_connection is not null) await _connection.DisposeAsync().ConfigureAwait(false);
            _connection = await _connect(cancellationToken).ConfigureAwait(false);
            _connection.Notification += (method, parameters) =>
            {
                if (method == "account/login/completed" && parameters.TryGetProperty("loginId", out var id) && id.ValueKind == JsonValueKind.String)
                    _logins.GetOrAdd(id.GetString()!, _ => new(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult(parameters.GetProperty("success").GetBoolean());
            };
            return _connection;
        }
        finally { _connectionGate.Release(); }
    }

    private string RequireExecutable() => _cli.Resolve(PolishProvider.ChatGpt) ??
        throw new PolishProviderException(ProviderConnectionState.CliMissing, "Install the official Codex CLI in Settings first.");

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await RestartAsync().ConfigureAwait(false);
        await _claude.DisposeAsync().ConfigureAwait(false);
    }
}
