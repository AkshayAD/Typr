using System.Text.Json;
using System.Text.Json.Nodes;

namespace Ava.Core;

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
    public int SavedAudioLimit { get; init; }
    public string? SpeechModelPath { get; init; }
    public string Theme { get; init; } = "System";
    public string Accent { get; init; } = "Iris";
    public bool SaveHistory { get; init; }
    public TextReplacement[] Vocabulary { get; init; } = [];
    public TextReplacement[] Snippets { get; init; } = [];
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
            SavedAudioLimit = Math.Clamp(settings.SavedAudioLimit, 0, 200),
            Theme = CanonicalChoice(settings.Theme, "System", "Light", "Dark"),
            Accent = CanonicalChoice(settings.Accent, "Iris", "Forest", "Ocean"),
            Vocabulary = OutputPersonalizer.NormalizeRules(settings.Vocabulary),
            Snippets = OutputPersonalizer.NormalizeRules(settings.Snippets)
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

    private static void Rename(JsonObject node, string from, string to)
    {
        if (!node.ContainsKey(to) && node[from] is { } value) node[to] = value.DeepClone();
        node.Remove(from);
    }
    private static void NormalizeKnownKeys(JsonObject node)
    {
        string[] names = [nameof(AppSettings.SettingsVersion), nameof(AppSettings.PolishEnabled),
            nameof(AppSettings.LiveCaptionsEnabled), nameof(AppSettings.BarVisible), nameof(AppSettings.CaptionOpacity),
            nameof(AppSettings.CaptionMaxLines), nameof(AppSettings.CaptionMaxCharacters), nameof(AppSettings.CpuThreads),
            nameof(AppSettings.SavedAudioLimit), nameof(AppSettings.SpeechModelPath), nameof(AppSettings.Theme),
            nameof(AppSettings.Accent), nameof(AppSettings.SaveHistory), nameof(AppSettings.Vocabulary),
            nameof(AppSettings.Snippets), "Polish", "CaptionOpacityPercent", "CaptionLines"];
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
