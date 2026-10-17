using System.Text.Json;
using Typr.Core;

namespace Typr.Infrastructure;

// Codex's startup model catalog controls tool construction. In particular, disabling the
// removed apply_patch_freeform feature does not remove the model's apply_patch tool.
internal static class CodexTextOnlyPolicy
{
    internal static string CreateCatalog(PolishModel model) => JsonSerializer.Serialize(new
    {
        models = new[] { new
        {
            slug = model.Id, display_name = model.DisplayName, description = "Typr text editing",
            default_reasoning_level = model.DefaultReasoningEffort,
            supported_reasoning_levels = model.ReasoningEfforts.Select(effort => new { effort, description = effort }),
            shell_type = "disabled", visibility = "list", supported_in_api = true, priority = 0,
            availability_nux = (object?)null, upgrade = (object?)null,
            base_instructions = PolishPrompt.Instructions,
            model_messages = new { instructions_template = PolishPrompt.Instructions },
            include_skills_usage_instructions = false, include_plugin_usage_instructions = false,
            include_apps_usage_instructions = false, support_verbosity = false, default_verbosity = (string?)null,
            apply_patch_tool_type = (string?)null, truncation_policy = new { mode = "tokens", limit = 4096 },
            experimental_supported_tools = Array.Empty<string>(), input_modalities = new[] { "text" },
            supports_search_tool = false, node_repl_disabled = true, tool_mode = "direct"
        } }
    });

    internal static object ThreadParameters(PolishModel model, string workingDirectory, JsonElement? configuration = null) => new
    {
        model = model.Id, modelProvider = "openai", cwd = workingDirectory, ephemeral = true,
        approvalPolicy = "never", sandbox = "read-only", baseInstructions = PolishPrompt.Instructions,
        developerInstructions = "Return only the schema-conforming edited transcript.",
        environments = Array.Empty<object>(), selectedCapabilityRoots = Array.Empty<object>(),
        runtimeWorkspaceRoots = Array.Empty<string>(), dynamicTools = Array.Empty<object>(),
        allowProviderModelFallback = false,
        config = ThreadOverrides(configuration)
    };

    internal static Dictionary<string, object> ThreadOverrides(JsonElement? configuration)
    {
        var result = new Dictionary<string, object> { ["project_doc_max_bytes"] = 0 };
        // Empty tables merge with lower config layers. Disable every configured MCP server by
        // name as well, including servers supplied by system configuration outside private home.
        if (configuration is { } config && config.TryGetProperty("mcp_servers", out var servers) && servers.ValueKind == JsonValueKind.Object)
            foreach (var server in servers.EnumerateObject())
                result["mcp_servers." + server.Name + ".enabled"] = false;
        return result;
    }

    internal static void ValidateThreadStart(JsonElement response)
    {
        if (!response.TryGetProperty("instructionSources", out var sources) || sources.ValueKind != JsonValueKind.Array || sources.GetArrayLength() != 0)
            throw new PolishProviderException(ProviderConnectionState.Error, "Codex loaded outside instructions. Typr stopped before sending the transcript; reconnect or update its private provider.");
    }
}
