using System.Globalization;
using MineClaw.Agent.Llm;

namespace MineClaw.Bot.Blueprints;

/// <summary>A model the Design Studio can talk to: one of the bot's tiers, or a configured profile (Ollama/DeepSeek, …).</summary>
/// <param name="Id">"main" / "worker" / "cheap" (the tiers) or "profile:&lt;name&gt;".</param>
public sealed record DesignerModel(string Id, string Label, string Provider, string Model, ModelTier Tier, string? Profile, string? CostHint);

/// <summary>
/// The models the bot is configured to use (Agent:Llm): each tier's model and every usable profile, de-duplicated by
/// (provider, model). A Claude model outside the 5.5 generation is never offered (startup refuses such a config anyway).
/// </summary>
public static class DesignerModels
{
    public static List<DesignerModel> List(LlmOptions o)
    {
        var list = new List<DesignerModel>();
        void Add(string id, string label, string provider, string model, ModelTier tier, string? profile, string? hint)
        {
            provider = LlmProviders.Normalize(provider);
            if (string.IsNullOrWhiteSpace(model) || !o.ProviderConfigured(provider)) return;
            if (provider == LlmProviders.Anthropic && !ModelProfiles.IsCurrentGeneration(model)) return;
            if (list.Any(m => m.Provider == provider && m.Model.Equals(model, StringComparison.OrdinalIgnoreCase))) return;
            list.Add(new DesignerModel(id, label, provider, model.Trim(), tier, profile, hint));
        }
        Add("main", $"{Pretty(o.MainModel)} (brain)", o.ProviderFor(ModelTier.Main), o.MainModel, ModelTier.Main, null, "$$$");
        Add("worker", $"{Pretty(o.WorkerModel)} (worker)", o.ProviderFor(ModelTier.Worker), o.WorkerModel, ModelTier.Worker, null, "$$");
        Add("cheap", $"{Pretty(o.CheapModel)} (memory)", o.ProviderFor(ModelTier.Cheap), o.CheapModel, ModelTier.Cheap, null, "$");
        foreach (var name in o.UsableProfiles())
            if (o.Profile(name) is { } p)
                Add("profile:" + name, $"{Pretty(p.Model)} ({name})", p.Provider, p.Model, ModelTier.Main, name, p.CostHint);
        return list;
    }

    /// <summary>The default: the brain tier when usable, else the first.</summary>
    public static DesignerModel? Default(LlmOptions o) => List(o).FirstOrDefault();

    /// <summary>An id from <see cref="List"/>, or a model id one of them names; null = not available.</summary>
    public static DesignerModel? Resolve(LlmOptions o, string? id)
    {
        var all = List(o);
        if (string.IsNullOrWhiteSpace(id)) return all.FirstOrDefault();
        id = id.Trim();
        return all.FirstOrDefault(m => m.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
               ?? all.FirstOrDefault(m => m.Model.Equals(id, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>"claude-opus-5-5" → "Opus 5.5"; other providers' names stay as they are.</summary>
    public static string Pretty(string model)
    {
        var m = model.Trim();
        if (!m.StartsWith("claude-", StringComparison.OrdinalIgnoreCase)) return m;
        var parts = m["claude-".Length..].Split('-');
        if (parts.Length < 3) return m;
        return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(parts[0]) + " " + parts[1] + "." + parts[2];
    }
}
