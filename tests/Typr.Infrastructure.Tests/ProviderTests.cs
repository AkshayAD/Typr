using System.Text.Json;
using Typr.Infrastructure;
using Xunit;

namespace Typr.Infrastructure.Tests;

public class ProviderTests
{
    [Fact]
    public async Task PolishingUsesExactModelEphemeralThreadAndStructuredTextOnly()
    {
        var rpc = new FakeConnection();
        await using var service = CreateService(rpc);
        string output = await service.PolishAsync("kal four thirty pm ko milte hain", PolishProvider.ChatGpt);
        Assert.Equal("Kal 4:30 PM ko milte hain.", output);
        var thread = rpc.Requests.Single(request => request.Method == "thread/start").Parameters;
        Assert.Equal("gpt-6-luna", thread.GetProperty("model").GetString());
        Assert.True(thread.GetProperty("ephemeral").GetBoolean());
        Assert.Equal("read-only", thread.GetProperty("sandbox").GetString());
        Assert.Equal("never", thread.GetProperty("approvalPolicy").GetString());
        Assert.Empty(thread.GetProperty("environments").EnumerateArray());
        Assert.Empty(thread.GetProperty("dynamicTools").EnumerateArray());
        var turn = rpc.Requests.Single(request => request.Method == "turn/start").Parameters;
        Assert.Equal("low", turn.GetProperty("effort").GetString());
        Assert.False(turn.GetProperty("sandboxPolicy").GetProperty("networkAccess").GetBoolean());
        Assert.False(turn.GetProperty("outputSchema").GetProperty("additionalProperties").GetBoolean());
        Assert.Contains(rpc.Requests, request => request.Method == "thread/unsubscribe");
    }

    [Fact]
    public async Task MissingRequestedModelNeverInvokesDifferentModel()
    {
        var rpc = new FakeConnection();
        await using var service = CreateService(rpc);
        var exception = await Assert.ThrowsAsync<PolishProviderException>(() => service.PolishAsync("hello", PolishProvider.ChatGpt, "missing-model"));
        Assert.Contains("explicitly choose", exception.Message);
        Assert.DoesNotContain(rpc.Requests, request => request.Method == "turn/start");
    }

    [Fact]
    public async Task UnsupportedEffortNeverInvokesInference()
    {
        var rpc = new FakeConnection();
        await using var service = CreateService(rpc);
        await Assert.ThrowsAsync<PolishProviderException>(() => service.PolishAsync("hello", PolishProvider.ChatGpt, effort: "none"));
        Assert.DoesNotContain(rpc.Requests, request => request.Method == "turn/start");
    }

    [Fact]
    public async Task ApiKeyConnectionCannotSilentlyBillApiCredits()
    {
        var rpc = new FakeConnection { AccountType = "apiKey" };
        await using var service = CreateService(rpc);
        Assert.Equal(ProviderConnectionState.SignInRequired, (await service.GetConnectionAsync(PolishProvider.ChatGpt)).State);
        await Assert.ThrowsAsync<PolishProviderException>(() => service.PolishAsync("hello", PolishProvider.ChatGpt));
        Assert.DoesNotContain(rpc.Requests, request => request.Method == "turn/start");
    }

    [Fact]
    public async Task FailedTurnDoesNotReturnPartialText()
    {
        var rpc = new FakeConnection { TurnStatus = "failed" };
        await using var service = CreateService(rpc);
        await Assert.ThrowsAsync<PolishProviderException>(() => service.PolishAsync("hello", PolishProvider.ChatGpt));
    }

    [Fact]
    public async Task AddedCommentaryDoesNotEscapeStructuredOutputGuard()
    {
        var rpc = new FakeConnection { Text = "Here is the polished text:\nHello." };
        await using var service = CreateService(rpc);
        var exception = await Assert.ThrowsAsync<PolishProviderException>(() => service.PolishAsync("hello", PolishProvider.ChatGpt));
        Assert.Contains("output checks", exception.Message);
    }

    [Fact]
    public async Task CancellationInterruptsActiveTurnAndUnsubscribes()
    {
        var rpc = new FakeConnection { CompleteTurn = false };
        await using var service = CreateService(rpc);
        using var cancellation = new CancellationTokenSource();
        var polishing = service.PolishAsync("hello", PolishProvider.ChatGpt, cancellationToken: cancellation.Token);
        await rpc.TurnStarted.Task;
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => polishing);
        Assert.Contains(rpc.Requests, request => request.Method == "turn/interrupt");
        Assert.Contains(rpc.Requests, request => request.Method == "thread/unsubscribe");
    }

    [Fact]
    public async Task CancellationBeforeTurnStartResponseTerminatesItsDedicatedChild()
    {
        var rpc = new FakeConnection { HoldTurnStartResponse = true };
        await using var service = CreateService(rpc);
        using var cancellation = new CancellationTokenSource();
        var polishing = service.PolishAsync("hello", PolishProvider.ChatGpt, cancellationToken: cancellation.Token);
        await rpc.TurnStarted.Task;
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => polishing);
        Assert.Equal(1, rpc.DisposeCount);
    }

    [Fact]
    public async Task LoadedOutsideInstructionsStopBeforeAnyTranscriptIsSent()
    {
        var rpc = new FakeConnection { InstructionSources = ["C:/Users/synthetic/.codex/AGENTS.md"] };
        await using var service = CreateService(rpc);
        await Assert.ThrowsAsync<PolishProviderException>(() => service.PolishAsync("private dictated words", PolishProvider.ChatGpt));
        Assert.DoesNotContain(rpc.Requests, request => request.Method == "turn/start");
        Assert.Equal(1, rpc.DisposeCount);
    }

    [Fact]
    public void TextOnlyMetadataDisablesModelOwnedToolsAndPersonalInstructionSources()
    {
        using var json = JsonDocument.Parse(CodexTextOnlyPolicy.CreateCatalog(new("gpt-6-luna", "Luna", ["low"], "low")));
        var model = json.RootElement.GetProperty("models")[0];
        Assert.Equal("disabled", model.GetProperty("shell_type").GetString());
        Assert.Equal(JsonValueKind.Null, model.GetProperty("apply_patch_tool_type").ValueKind);
        Assert.Empty(model.GetProperty("experimental_supported_tools").EnumerateArray());
        Assert.False(model.GetProperty("include_skills_usage_instructions").GetBoolean());
        Assert.False(model.GetProperty("include_plugin_usage_instructions").GetBoolean());
    }

    [Fact]
    public void CodexUsesPrivateHomeAndDisablesNotificationAndTelemetryExports()
    {
        string work = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "Typr-provider-isolation-test", "workspace")).FullName;
        var start = CodexRpcClient.CreateStartInfo("synthetic-provider.exe", work);
        Assert.Equal(Path.Combine(Path.GetDirectoryName(work)!, "codex-home"), start.Environment["CODEX_HOME"]);
        Assert.Contains("notify=[]", start.ArgumentList);
        Assert.Contains("otel.exporter=\"none\"", start.ArgumentList);
        Assert.Contains("otel.trace_exporter=\"none\"", start.ArgumentList);
        Assert.Contains("otel.log_user_prompt=false", start.ArgumentList);
    }

    [Fact]
    public void McpEntriesFromLowerConfigurationLayersAreExplicitlyDisabled()
    {
        using var configuration = JsonDocument.Parse("{\"mcp_servers\":{\"synthetic_reader\":{\"enabled\":true},\"second\":{}}}");
        var overrides = CodexTextOnlyPolicy.ThreadOverrides(configuration.RootElement);
        Assert.Equal(false, overrides["mcp_servers.synthetic_reader.enabled"]);
        Assert.Equal(false, overrides["mcp_servers.second.enabled"]);
    }

    [Theory]
    [InlineData("401 Unauthorized: secret-token", ProviderConnectionState.SignInRequired)]
    [InlineData("selected model not supported", ProviderConnectionState.Error)]
    [InlineData("usage limit exceeded", ProviderConnectionState.Error)]
    public void ProviderErrorsAreTypedAndDoNotExposeRawSecrets(string raw, ProviderConnectionState state)
    {
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(new { message = raw }));
        var error = CodexRpcClient.MapError(json.RootElement);
        Assert.Equal(state, error.State);
        Assert.DoesNotContain("secret-token", error.Message);
    }

    [Fact]
    public void ClaudeUsesUnmodifiedCliSubscriptionWithNoToolsOrStoredSession()
    {
        var arguments = ClaudeCodeProvider.CreatePolishArguments("sonnet", "low");
        Assert.Equal("", arguments[Array.IndexOf(arguments, "--tools") + 1]);
        Assert.Contains("--safe-mode", arguments);
        Assert.Contains("--no-session-persistence", arguments);
        Assert.Contains("--strict-mcp-config", arguments);
        Assert.Contains("--json-schema", arguments);
        Assert.DoesNotContain("--bare", arguments);
        Assert.DoesNotContain("--dangerously-skip-permissions", arguments);
    }

    [Fact]
    public void ClaudeReturnsOnlySchemaValidatedText() =>
        Assert.Equal("Hello.", ClaudeCodeProvider.ReadPolishResult("{\"is_error\":false,\"result\":\"do not paste this\",\"structured_output\":{\"text\":\"Hello.\"}}", 0, "hello"));

    [Fact]
    public void ClaudeDoesNotUseUnstructuredResultFallback() =>
        Assert.Throws<FormatException>(() => ClaudeCodeProvider.ReadPolishResult("{\"is_error\":false,\"result\":\"Hello.\"}", 0, "hello"));

    [Theory]
    [InlineData("sonnet", "xhigh")]
    [InlineData("claude-opus-4-6", "xhigh")]
    [InlineData("claude-fable-5-1", "low")]
    [InlineData("claude-haiku-5-5", "none")]
    public void ClaudeRejectsUnverifiedAliasesModelsAndEffortsBeforeInference(string model, string effort) =>
        Assert.Throws<PolishProviderException>(() => ClaudeCodeProvider.ValidateSelection(ClaudeCodeProvider.ModelsForVersion("2.1.294"), model, effort));

    [Fact]
    public void ClaudePinnedModelsRequireCompatibleCliAndHaveDocumentedEfforts()
    {
        Assert.Throws<PolishProviderException>(() => ClaudeCodeProvider.ModelsForVersion("2.1.292"));
        var models = ClaudeCodeProvider.ModelsForVersion("2.1.294");
        Assert.Contains(models, model => model.Id == "claude-haiku-5-5");
        foreach (var model in models) Assert.Equal(new[] { "low", "medium", "high", "xhigh", "max" }, model.ReasoningEfforts);
    }

    [Fact]
    public void ClaudePossibleModelSubstitutionCannotBeReturned() =>
        Assert.Throws<PolishProviderException>(() => ClaudeCodeProvider.ReadPolishResult("{\"is_error\":false,\"modelUsage\":{\"claude-sonnet-4-6\":{}},\"structured_output\":{\"text\":\"Hello.\"}}", 0, "hello", "claude-haiku-5-5"));

    [Fact]
    public void ClaudeChildCannotInheritTelemetryCollectorsOrPromptLogging()
    {
        var inherited = new Dictionary<string, string> {
            ["CLAUDE_CODE_ENABLE_TELEMETRY"] = "1", ["CLAUDE_CODE_ENHANCED_TELEMETRY_BETA"] = "1",
            ["ENABLE_ENHANCED_TELEMETRY_BETA"] = "1", ["OTEL_LOG_USER_PROMPTS"] = "1",
            ["OTEL_LOG_ASSISTANT_RESPONSES"] = "1", ["OTEL_LOG_RAW_API_BODIES"] = "1",
            ["OTEL_METRICS_EXPORTER"] = "otlp", ["OTEL_LOGS_EXPORTER"] = "otlp", ["OTEL_TRACES_EXPORTER"] = "otlp",
            ["OTEL_EXPORTER_OTLP_ENDPOINT"] = "https://synthetic.invalid",
            ["OTEL_EXPORTER_OTLP_HEADERS"] = "synthetic-private-header",
            ["BETA_TRACING_ENDPOINT"] = "https://synthetic.invalid",
            ["CLAUDE_CODE_OTEL_CONTENT_MAX_LENGTH"] = "100000"
        };
        var previous = inherited.Keys.ToDictionary(name => name, Environment.GetEnvironmentVariable);
        try {
            foreach (var (name, value) in inherited) Environment.SetEnvironmentVariable(name, value);
            var start = NativeProviderCli.StartInfo("synthetic-claude.exe", ["--print"], Path.GetTempPath());
            foreach (string name in new[] { "CLAUDE_CODE_ENABLE_TELEMETRY", "CLAUDE_CODE_ENHANCED_TELEMETRY_BETA", "ENABLE_ENHANCED_TELEMETRY_BETA",
                "OTEL_LOG_USER_PROMPTS", "OTEL_LOG_ASSISTANT_RESPONSES", "OTEL_LOG_RAW_API_BODIES", "OTEL_LOG_TOOL_CONTENT", "OTEL_LOG_TOOL_DETAILS" })
                Assert.Equal("0", start.Environment[name]);
            foreach (string name in new[] { "OTEL_METRICS_EXPORTER", "OTEL_LOGS_EXPORTER", "OTEL_TRACES_EXPORTER" })
                Assert.Equal("none", start.Environment[name]);
            Assert.Equal("1", start.Environment["CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC"]);
            Assert.DoesNotContain(start.Environment.Keys, name => name.StartsWith("OTEL_EXPORTER_", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(start.Environment.Keys, name => name.StartsWith("CLAUDE_CODE_OTEL_", StringComparison.OrdinalIgnoreCase));
            Assert.False(start.Environment.ContainsKey("BETA_TRACING_ENDPOINT"));
            // The launcher policy is scoped to this child, preserving the user's CLI setup.
            foreach (var (name, value) in inherited) Assert.Equal(value, Environment.GetEnvironmentVariable(name));
        }
        finally { foreach (var (name, value) in previous) Environment.SetEnvironmentVariable(name, value); }
    }

    [Theory]
    [InlineData("0.163.0", "0.161.0", true)]
    [InlineData("0.163.0", "0.163.0-alpha.1", true)]
    [InlineData("0.163.0", "0.163.0", false)]
    [InlineData("0.163.0", "0.164.0", false)]
    public void UpdateComparisonDoesNotDowngradeStableVersion(string latest, string installed, bool expected) =>
        Assert.Equal(expected, NativeProviderCli.IsNewer(latest, installed));

    [Fact]
    public void CliResolverRejectsShellShims()
    {
        Assert.False(NativeProviderCli.IsNativeExecutable("codex.ps1"));
        Assert.False(NativeProviderCli.IsNativeExecutable("codex.cmd"));
    }

    private static AiPolishingService CreateService(FakeConnection rpc) => new(Path.Combine(Path.GetTempPath(), "Typr-provider-tests"), _ => Task.FromResult<ICodexConnection>(rpc));

    private sealed class FakeConnection : ICodexConnection
    {
        public event Action<string, JsonElement>? Notification;
        public bool IsRunning => true;
        internal List<(string Method, JsonElement Parameters)> Requests { get; } = [];
        internal string AccountType = "chatgpt";
        internal string TurnStatus = "completed";
        internal string Text = "Kal 4:30 PM ko milte hain.";
        internal bool CompleteTurn = true;
        internal bool HoldTurnStartResponse;
        internal int DisposeCount;
        internal string[] InstructionSources = [];
        internal TaskCompletionSource TurnStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<JsonElement> RequestAsync(string method, object parameters, CancellationToken cancellationToken)
        {
            Requests.Add((method, JsonSerializer.SerializeToElement(parameters)));
            object result = new { };
            if (method == "account/read") result = new { account = new { type = AccountType } };
            if (method == "model/list") result = new { data = new[] { new { model = "gpt-6-luna", displayName = "GPT-6 Luna", defaultReasoningEffort = "medium", supportedReasoningEfforts = new[] { new { reasoningEffort = "low" }, new { reasoningEffort = "medium" } } } }, nextCursor = (string?)null };
            if (method == "thread/start") result = new { thread = new { id = "thread-1" }, instructionSources = InstructionSources };
            if (method == "turn/start")
            {
                if (HoldTurnStartResponse)
                {
                    TurnStarted.TrySetResult();
                    return WaitForCancellationAsync(cancellationToken);
                }
                result = new { turn = new { id = "turn-1" } };
                if (CompleteTurn)
                {
                    Notification?.Invoke("item/completed", JsonSerializer.SerializeToElement(new { threadId = "thread-1", item = new { type = "agentMessage", phase = "final_answer", text = JsonSerializer.Serialize(new { text = Text }) } }));
                    Notification?.Invoke("turn/completed", JsonSerializer.SerializeToElement(new { threadId = "thread-1", turn = new { id = "turn-1", status = TurnStatus, error = (object?)null } }));
                }
                TurnStarted.TrySetResult();
            }
            return Task.FromResult(JsonSerializer.SerializeToElement(result));
        }
        private static async Task<JsonElement> WaitForCancellationAsync(CancellationToken cancellationToken)
        { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); return default; }
        public ValueTask DisposeAsync() { DisposeCount++; return ValueTask.CompletedTask; }
    }
}
