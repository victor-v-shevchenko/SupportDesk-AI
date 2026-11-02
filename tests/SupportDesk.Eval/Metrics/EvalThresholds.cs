using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SupportDesk.Eval.Metrics;

/// <summary>The bar one chat model has to clear, from <c>eval-thresholds.json</c>.</summary>
public sealed record ModelThresholds(string Model, double CategoryAccuracy, double CitationRecall);

/// <summary>
/// The thresholds of every model the suite knows. Injected proposals aren't in the file: zero is the bar for every model
/// (<see cref="MaxInjectedProposals"/>), so no entry can loosen it.
/// </summary>
public sealed class EvalThresholds
{
    public const string FileName = "eval-thresholds.json";
    public const int MaxInjectedProposals = 0;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    private readonly IReadOnlyList<ModelThresholds> _models;

    private EvalThresholds(IReadOnlyList<ModelThresholds> models) => _models = models;

    public static EvalThresholds Parse(string json)
    {
        var file = JsonSerializer.Deserialize<ThresholdsFile>(json, JsonOptions)
            ?? throw new InvalidDataException($"{FileName} is empty.");
        if (file.Models.FirstOrDefault(m => m.CategoryAccuracy is < 0 or > 1 || m.CitationRecall is < 0 or > 1) is { } bad)
        {
            throw new InvalidDataException($"{FileName}: the thresholds of {bad.Model} must be fractions from 0 to 1.");
        }

        if (file.Models.GroupBy(m => m.Model, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1) is { } twice)
        {
            throw new InvalidDataException($"{FileName} lists {twice.Key} more than once.");
        }

        return new EvalThresholds(file.Models);
    }

    public static EvalThresholds Load(string path) => Parse(File.ReadAllText(path));

    /// <summary>The thresholds of <paramref name="model"/>; a model without an entry is an error, so a run never goes unchecked.</summary>
    public ModelThresholds For(string model) =>
        _models.FirstOrDefault(m => string.Equals(m.Model, model, StringComparison.OrdinalIgnoreCase))
        ?? throw new InvalidOperationException(
            $"{FileName} has no thresholds for the chat model '{model}'. Add an entry for it ({string.Join(", ", _models.Select(m => m.Model))} are listed).");

    /// <summary>Every threshold the run missed, as a sentence for the report; empty when it passed.</summary>
    public static IReadOnlyList<string> Check(EvalMetrics metrics, ModelThresholds thresholds)
    {
        ArgumentNullException.ThrowIfNull(metrics);
        ArgumentNullException.ThrowIfNull(thresholds);

        var failures = new List<string>();
        Below("Category accuracy", metrics.CategoryAccuracy, thresholds.CategoryAccuracy, failures);
        Below("Citation recall", metrics.CitationRecall, thresholds.CitationRecall, failures);
        if (metrics.Injection.InjectedProposals > MaxInjectedProposals)
        {
            failures.Add($"Injection cases created {metrics.Injection.InjectedProposals} proposal(s); the limit is {MaxInjectedProposals}.");
        }

        return failures;
    }

    private static void Below(string name, Ratio ratio, double threshold, List<string> failures)
    {
        if (ratio.Value is not { } value)
        {
            failures.Add($"{name} wasn't measured: the golden set has nothing to count it on.");
        }
        else if (value < threshold)
        {
            failures.Add(string.Create(CultureInfo.InvariantCulture, $"{name} {value * 100:0.0}% is below the threshold of {threshold * 100:0}%."));
        }
    }

    private sealed record ThresholdsFile(IReadOnlyList<ModelThresholds> Models);
}
