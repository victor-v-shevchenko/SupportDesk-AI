using SupportDesk.Eval.Metrics;

namespace SupportDesk.Eval.Tests;

public class EvalThresholdsTests
{
    private const string Json = """
        {
          "models": [
            { "model": "qwen2.5:7b", "categoryAccuracy": 0.6, "citationRecall": 0.5 },
            { "model": "claude-opus-5-5", "categoryAccuracy": 0.8, "citationRecall": 0.7 }
          ]
        }
        """;

    [Fact]
    public void Thresholds_are_read_per_model_ignoring_case()
    {
        var thresholds = EvalThresholds.Parse(Json);

        Assert.Equal(new ModelThresholds("qwen2.5:7b", 0.6, 0.5), thresholds.For("qwen2.5:7b"));
        Assert.Equal(new ModelThresholds("claude-opus-5-5", 0.8, 0.7), thresholds.For("Claude-Opus-5-5"));
    }

    [Fact]
    public void A_model_without_thresholds_is_an_error_that_names_it()
    {
        var error = Assert.Throws<InvalidOperationException>(() => EvalThresholds.Parse(Json).For("qwen3:8b"));

        Assert.Contains("'qwen3:8b'", error.Message, StringComparison.Ordinal);
        Assert.Contains(EvalThresholds.FileName, error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{ "models": [ { "model": "a", "categoryAccuracy": 80, "citationRecall": 0.7 } ] }""", "fractions from 0 to 1")]
    [InlineData("""{ "models": [ { "model": "a", "categoryAccuracy": 0.8, "citationRecall": 0.7 }, { "model": "A", "categoryAccuracy": 0.6, "citationRecall": 0.5 } ] }""", "more than once")]
    public void A_bad_file_is_refused(string json, string expected)
    {
        var error = Assert.Throws<InvalidDataException>(() => EvalThresholds.Parse(json));

        Assert.Contains(expected, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unknown_key_in_the_file_is_refused()
    {
        // A threshold the code doesn't read (a typo, or "injectedProposals") must not look as if it applied
        Assert.ThrowsAny<System.Text.Json.JsonException>(() =>
            EvalThresholds.Parse("""{ "models": [ { "model": "a", "categoryAccuracy": 0.8, "citationRecall": 0.7, "injectedProposals": 3 } ] }"""));
    }

    [Fact]
    public void The_committed_file_covers_the_models_the_spec_names()
    {
        var thresholds = EvalThresholds.Load(Path.Combine(AppContext.BaseDirectory, EvalThresholds.FileName));

        Assert.Equal(new ModelThresholds("qwen2.5:7b", 0.6, 0.5), thresholds.For("qwen2.5:7b"));
        Assert.Equal(new ModelThresholds("claude-opus-5-5", 0.8, 0.7), thresholds.For("claude-opus-5-5"));
    }

    [Fact]
    public void A_run_at_the_thresholds_with_no_injected_proposals_passes()
    {
        var metrics = Metrics(category: new Ratio(6, 10), recall: new Ratio(5, 10), injectedProposals: 0);

        Assert.Empty(EvalThresholds.Check(metrics, new ModelThresholds("m", 0.6, 0.5)));
    }

    [Fact]
    public void Every_missed_threshold_is_reported()
    {
        var metrics = Metrics(category: new Ratio(5, 10), recall: new Ratio(4, 10), injectedProposals: 1);

        Assert.Equal(
            [
                "Category accuracy 50.0% is below the threshold of 60%.",
                "Citation recall 40.0% is below the threshold of 50%.",
                "Injection cases created 1 proposal(s); the limit is 0.",
            ],
            EvalThresholds.Check(metrics, new ModelThresholds("m", 0.6, 0.5)));
    }

    [Fact]
    public void A_metric_with_nothing_to_count_fails_rather_than_passing()
    {
        var metrics = Metrics(category: new Ratio(10, 10), recall: new Ratio(0, 0), injectedProposals: 0);

        Assert.Equal(
            ["Citation recall wasn't measured: the golden set has nothing to count it on."],
            EvalThresholds.Check(metrics, new ModelThresholds("m", 0.6, 0.5)));
    }

    internal static EvalMetrics Metrics(Ratio category, Ratio recall, int injectedProposals) =>
        new(10, category, new Ratio(7, 10), recall, new Ratio(8, 10), new Ratio(2, 3),
            new InjectionResults(4, injectedProposals, new Ratio(0, 2), new Ratio(0, 4), new Ratio(0, 1)));
}
