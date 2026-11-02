namespace Pilot.Node;

/// <summary>One model and effort a session can run on, as <c>sonnet-high</c>; a null effort is Claude Code's own default (written as the bare model).</summary>
public readonly record struct ModelPair(string Model, string? Effort)
{
    public override string ToString() => Effort is null ? Model : Model + "-" + Effort;

    /// <summary>Where the pair sits on <see cref="RoleRange.Line"/> (the bare model first for its model); a unit of order, not a count.</summary>
    internal int Order => Array.IndexOf(RoleRange.Models, Model) * 8 + (Effort is null ? 0 : Array.IndexOf(RoleRange.Efforts, Effort) + 1);
}

/// <summary>
/// A role's range (#599): the pair it starts on and the highest pair it may step up to (a deep review, a redone leg, the worker
/// ladder). Written <c>sonnet-high..opus-medium</c>. Effort max is never part of a range, on any model.
/// </summary>
public sealed record RoleRange(ModelPair Default, ModelPair Max)
{
    /// <summary>The models a range is drawn over, weakest first.</summary>
    public static string[] Models { get; } = ["sonnet", "opus"];

    /// <summary>The efforts a range allows, lowest first: effort max is left out.</summary>
    public static string[] Efforts { get; } = ["low", "medium", "high", "xhigh"];

    /// <summary>Every pair in order (sonnet-low ... sonnet-xhigh, opus-low ... opus-xhigh): the line a range selector draws.</summary>
    public static IReadOnlyList<string> Line { get; } = [.. Models.SelectMany(m => Efforts.Select(e => m + "-" + e))];

    public override string ToString() => $"{Default}..{Max}";

    public static bool TryParsePair(string? text, out ModelPair pair)
    {
        pair = default;
        var value = text?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }
        foreach (var model in Models)
        {
            if (value == model)
            {
                pair = new ModelPair(model, null);
                return true;
            }
            if (value.StartsWith(model + "-", StringComparison.Ordinal) && Efforts.Contains(value[(model.Length + 1)..]))
            {
                pair = new ModelPair(model, value[(model.Length + 1)..]);
                return true;
            }
        }
        return false;
    }

    /// <summary>Reads <c>default..max</c>, or <c>default max</c> as two values, or one pair that is both; why not in <paramref name="error"/>.</summary>
    public static bool TryParse(IReadOnlyList<string> values, out RoleRange? range, out string? error)
    {
        range = null;
        var given = values.Select(v => v.Trim()).Where(v => v.Length > 0).ToArray();
        if (given.Length == 1 && given[0].Contains("..", StringComparison.Ordinal))
        {
            given = given[0].Split("..", 2);
        }
        if (given.Length is < 1 or > 2)
        {
            error = "takes a default and a max pair, as sonnet-high..opus-medium";
            return false;
        }
        foreach (var g in given)
        {
            if (g.Equals("max", StringComparison.OrdinalIgnoreCase) || g.EndsWith("-max", StringComparison.OrdinalIgnoreCase))
            {
                error = "never takes effort max";
                return false;
            }
        }
        if (!TryParsePair(given[0], out var def) || !TryParsePair(given[^1], out var max))
        {
            error = $"takes pairs of {string.Join(", ", Models)} with an effort of {string.Join(", ", Efforts)} (or the bare model for Claude Code's own effort), as sonnet-high..opus-medium";
            return false;
        }
        if (max.Order < def.Order)
        {
            error = "takes a max not below its default";
            return false;
        }
        range = new RoleRange(def, max);
        error = null;
        return true;
    }

    /// <summary>The nearest pair to a model and effort an old setting named: Sonnet for anything weaker or other than Opus, effort max as xhigh.</summary>
    public static ModelPair Nearest(string? model, string? effort, ModelPair fallback)
    {
        var m = RoleModels.ModelRank(model) switch { 2 => "opus", 0 or 1 => "sonnet", _ => fallback.Model };
        var e = effort is null or RoleModels.NoneValue ? fallback.Effort : effort == "max" ? "xhigh" : Efforts.Contains(effort) ? effort : fallback.Effort;
        return new ModelPair(m, e);
    }
}
