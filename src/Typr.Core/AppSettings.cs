using System.Text.Json;
using System.Text.Json.Nodes;

namespace Typr.Core;

public sealed record TextReplacement(string Phrase, string Replacement);

public sealed record AppSettings
{
    public const int CurrentVersion = 2;
    public int SettingsVersion { get; init; } = CurrentVersion;
    public bool PolishEnabled { get; init; } = true;
    public bool LiveCaptionsEnabled { get; init; } = true;
    public bool BarVisible { get; init; } = true;
    public double CaptionOpacity { get; init; } = .85;
    public int CaptionMaxLines { get; init; } = 2;
    public int CaptionMaxCharacters { get; init; } = 60;
    public int CpuThreads { get; init; } = 2;
    public string SpeechResourceMode { get; init; } = "Automatic";
    public int SavedAudioLimit { get; init; }
    public string? SpeechModelPath { get; init; }
    public string Theme { get; init; } = "System";
    public string Accent { get; init; } = "Iris";
    public bool SaveHistory { get; init; }
    public TextReplacement[] Vocabulary { get; init; } = [];
    public TextReplacement[] Snippets { get; init; } = [];
    public bool AutoPasteEnabled { get; init; } = true;
    public bool AiPolishEnabled { get; init; } = true;
    public string ProviderPriority { get; init; } = "ChatGPT only";
    public string ChatGptModel { get; init; } = "gpt-6-luna";
    public string ClaudeModel { get; init; } = "claude-haiku-5-5";
    public string PolishReasoning { get; init; } = "low";
    public int PolishTimeoutSeconds { get; init; } = 30;
    public double? BarLeft { get; init; }
    public double? BarTop { get; init; }
    public double BarOpacity { get; init; } = .78;
    public double BarScale { get; init; } = 1;
    public int BarHoverDelay { get; init; } = 150;
    public bool AllowButtonRecording { get; init; }
    public string? InputDeviceId { get; init; }
    public string[] BarHoverActions { get; init; } = ["HoldToTalk", "PasteLast", "ToggleMicrophone", "ToggleCaptions"];
    public string[] BarContextActions { get; init; } = ["OpenWorkspace", "ToggleMicrophone", "PasteLast", "ToggleCaptions", "HideBar"];
    public bool CommandShortcutsEnabled { get; init; } = true;
    public string MicrophoneShortcut { get; init; } = "Ctrl+Shift+M";
    public string HandsFreeShortcut { get; init; } = "Ctrl+Shift+Space";
    public string PasteShortcut { get; init; } = "Ctrl+Shift+V";
    public string RetranscriptionModel { get; init; } = "whisper-small";
}

public static class SettingsMigrator
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };

    public static AppSettings Load(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        var node = JsonNode.Parse(json) as JsonObject ?? throw new JsonException("Settings must be a JSON object.");
        // Normalize known keys so a case-insensitive input cannot leave stale aliases
        // alongside migrated properties.
        NormalizeKnownKeys(node);
        int version = node["SettingsVersion"]?.GetValue<int>() ?? 0;
        if (version < 0 || version > AppSettings.CurrentVersion)
            throw new NotSupportedException($"Unsupported settings version {version}.");
        while (version < AppSettings.CurrentVersion)
        {
            switch (version)
            {
                case 0: Migrate0To1(node); break;
                case 1: Migrate1To2(node); break;
                default: throw new NotSupportedException($"No migration from version {version}.");
            }
            node["SettingsVersion"] = ++version;
        }
        var settings = node.Deserialize<AppSettings>(Options) ?? throw new JsonException("Settings are empty.");
        return settings with
        {
            CaptionOpacity = Math.Clamp(settings.CaptionOpacity, 0, 1),
            CaptionMaxLines = Math.Clamp(settings.CaptionMaxLines, 1, 3),
            CaptionMaxCharacters = Math.Clamp(settings.CaptionMaxCharacters, 10, 120),
            CpuThreads = Math.Clamp(settings.CpuThreads, 2, 4),
            SpeechResourceMode = CanonicalChoice(settings.SpeechResourceMode, "Automatic", "Everyday", "Gaming"),
            SavedAudioLimit = Math.Clamp(settings.SavedAudioLimit, 0, 200),
            Theme = CanonicalChoice(settings.Theme, "System", "Light", "Dark"),
            Accent = CanonicalChoice(settings.Accent, "Iris", "Forest", "Ocean"),
            Vocabulary = OutputPersonalizer.NormalizeRules(settings.Vocabulary),
            Snippets = OutputPersonalizer.NormalizeRules(settings.Snippets),
            ProviderPriority = CanonicalChoice(settings.ProviderPriority, "ChatGPT only", "Claude only", "ChatGPT first", "Claude first"),
            ChatGptModel = SafeModel(settings.ChatGptModel, "gpt-6-luna"),
            ClaudeModel = SafeModel(settings.ClaudeModel, "claude-haiku-5-5"),
            PolishReasoning = CanonicalChoice(settings.PolishReasoning, "low", "none", "medium", "high", "xhigh", "max"),
            PolishTimeoutSeconds = Math.Clamp(settings.PolishTimeoutSeconds, 5, 120),
            BarLeft = settings.BarLeft is { } left && double.IsFinite(left) ? left : null,
            BarTop = settings.BarTop is { } top && double.IsFinite(top) ? top : null,
            BarOpacity = double.IsFinite(settings.BarOpacity) ? Math.Clamp(settings.BarOpacity, .2, 1) : .78,
            BarScale = settings.BarScale is .85 or 1 or 1.15 ? settings.BarScale : 1,
            BarHoverDelay = settings.BarHoverDelay is 0 or 150 or 300 or 500 ? settings.BarHoverDelay : 150,
            InputDeviceId = !string.IsNullOrWhiteSpace(settings.InputDeviceId) && settings.InputDeviceId.Length <= 512 ? settings.InputDeviceId.Trim() : null,
            BarHoverActions = NormalizeActions(settings.BarHoverActions),
            BarContextActions = NormalizeActions(settings.BarContextActions),
            MicrophoneShortcut = CanonicalChoice(settings.MicrophoneShortcut, "Ctrl+Shift+M", "Ctrl+Shift+F8", "F9"),
            HandsFreeShortcut = CanonicalChoice(settings.HandsFreeShortcut, "Ctrl+Shift+Space", "Ctrl+Shift+R", "F8"),
            PasteShortcut = CanonicalChoice(settings.PasteShortcut, "Ctrl+Shift+V", "Ctrl+Shift+F9", "F10"),
            RetranscriptionModel = CanonicalChoice(settings.RetranscriptionModel, "whisper-small", "whisper-large-v3")
        };
    }

    public static string Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.SettingsVersion != AppSettings.CurrentVersion)
            throw new NotSupportedException("Save requires current-version settings.");
        return JsonSerializer.Serialize(settings, Options);
    }

    private static void Migrate0To1(JsonObject node)
    {
        Rename(node, "Polish", "PolishEnabled");
        if (!node.ContainsKey("CaptionOpacity") && node["CaptionOpacityPercent"] is { } percent)
            node["CaptionOpacity"] = percent.GetValue<double>() / 100d;
        node.Remove("CaptionOpacityPercent");
    }
    private static void Migrate1To2(JsonObject node) => Rename(node, "CaptionLines", "CaptionMaxLines");
    private static string CanonicalChoice(string? value, params string[] choices)
        => choices.FirstOrDefault(choice => choice.Equals(value?.Trim(), StringComparison.OrdinalIgnoreCase)) ?? choices[0];
    private static string SafeModel(string? value, string fallback)
        => !string.IsNullOrWhiteSpace(value) && value.Length <= 100 && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.') ? value : fallback;
    private static string[] NormalizeActions(IEnumerable<string>? actions)
    {
        string[] known = ["ToggleRecording", "HoldToTalk", "Cancel", "PasteLast", "ToggleMicrophone", "ToggleCaptions", "OpenWorkspace", "HideBar"];
        return (actions ?? []).Where(a => known.Contains(a, StringComparer.Ordinal)).Distinct().Take(8).ToArray();
    }

    private static void Rename(JsonObject node, string from, string to)
    {
        if (!node.ContainsKey(to) && node[from] is { } value) node[to] = value.DeepClone();
        node.Remove(from);
    }
    private static void NormalizeKnownKeys(JsonObject node)
    {
        string[] names = [nameof(AppSettings.SettingsVersion), nameof(AppSettings.PolishEnabled),
            nameof(AppSettings.LiveCaptionsEnabled), nameof(AppSettings.BarVisible), nameof(AppSettings.CaptionOpacity),
            nameof(AppSettings.CaptionMaxLines), nameof(AppSettings.CaptionMaxCharacters), nameof(AppSettings.CpuThreads), nameof(AppSettings.SpeechResourceMode),
            nameof(AppSettings.SavedAudioLimit), nameof(AppSettings.SpeechModelPath), nameof(AppSettings.Theme),
            nameof(AppSettings.Accent), nameof(AppSettings.SaveHistory), nameof(AppSettings.Vocabulary),
            nameof(AppSettings.Snippets), nameof(AppSettings.AutoPasteEnabled), nameof(AppSettings.AiPolishEnabled),
            nameof(AppSettings.ProviderPriority), nameof(AppSettings.ChatGptModel), nameof(AppSettings.ClaudeModel),
            nameof(AppSettings.PolishReasoning), nameof(AppSettings.PolishTimeoutSeconds), nameof(AppSettings.BarLeft),
            nameof(AppSettings.BarTop), nameof(AppSettings.BarHoverActions), nameof(AppSettings.BarContextActions),
            nameof(AppSettings.BarOpacity), nameof(AppSettings.BarScale), nameof(AppSettings.BarHoverDelay),
            nameof(AppSettings.AllowButtonRecording), nameof(AppSettings.InputDeviceId),
            nameof(AppSettings.CommandShortcutsEnabled), nameof(AppSettings.MicrophoneShortcut),
            nameof(AppSettings.HandsFreeShortcut), nameof(AppSettings.PasteShortcut), nameof(AppSettings.RetranscriptionModel),
            "Polish", "CaptionOpacityPercent", "CaptionLines"];
        foreach (string key in node.Select(p => p.Key).ToArray())
        {
            string? canonical = names.FirstOrDefault(n => n.Equals(key, StringComparison.OrdinalIgnoreCase));
            if (canonical is null || canonical == key) continue;
            if (node.ContainsKey(canonical)) throw new JsonException($"Duplicate settings key: {key}.");
            node[canonical] = node[key]?.DeepClone();
            node.Remove(key);
        }
    }
}
