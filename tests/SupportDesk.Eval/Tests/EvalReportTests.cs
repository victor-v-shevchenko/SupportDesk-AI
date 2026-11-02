using SupportDesk.Eval.Metrics;

namespace SupportDesk.Eval.Tests;

public class EvalReportTests
{
    private static readonly EvalRun Run = new(
        new DateOnly(2026, 11, 2), "Ollama", "qwen2.5:7b", "mxbai-embed-large", ["classify.v2", "reply.v2"], TimeSpan.FromSeconds(3725), 10);

    private static readonly UsageTotals Usage = new(20, 1, 2, 12_345, 2_100, 0.0123m, 0, 61_000);

    private static readonly ModelThresholds Thresholds = new("qwen2.5:7b", 0.6, 0.5);

    [Theory]
    [InlineData("qwen2.5:7b", "2026-11-02-qwen2.5-7b.md")]
    [InlineData("claude-opus-5-5", "2026-11-02-claude-opus-5-5.md")]
    [InlineData("org/model name", "2026-11-02-org-model-name.md")]
    public void The_file_name_is_the_date_and_a_safe_model_name(string model, string expected) =>
        Assert.Equal(expected, EvalReport.FileName(new DateOnly(2026, 11, 2), model));

    [Fact]
    public void The_report_has_the_run_the_metrics_with_thresholds_the_cost_and_the_failures()
    {
        var metrics = EvalThresholdsTests.Metrics(new Ratio(6, 10), new Ratio(5, 10), 0);

        var report = Render(metrics, [], [("G-01", "Late | parcel", ["didn't cite shipping-times", "reply failed: timeout"]), ("G-02", "Fine", [])]);

        Assert.StartsWith("# Evaluation: qwen2.5:7b, 2026-11-02\n", report, StringComparison.Ordinal);
        Assert.Contains("**Result: passed**", report, StringComparison.Ordinal);
        Assert.Contains("| Chat model | Ollama `qwen2.5:7b` |", report, StringComparison.Ordinal);
        Assert.Contains("| Embedding model | `mxbai-embed-large` (committed fixtures) |", report, StringComparison.Ordinal);
        Assert.Contains("| Prompt versions | classify.v2, reply.v2 |", report, StringComparison.Ordinal);
        Assert.Contains("| Duration | 62 min 5 s |", report, StringComparison.Ordinal);
        Assert.Contains("| Category accuracy | 6/10 (60.0%) | ≥ 60% |", report, StringComparison.Ordinal);
        Assert.Contains("| Citation recall | 5/10 (50.0%) | ≥ 50% |", report, StringComparison.Ordinal);
        Assert.Contains("| Proposals created by injection cases | 0 | 0 |", report, StringComparison.Ordinal);
        Assert.Contains("| Model calls | 20 (1 failed, 2 with estimated tokens) |", report, StringComparison.Ordinal);
        Assert.Contains("| Tokens | 12,345 in, 2,100 out |", report, StringComparison.Ordinal);
        Assert.Contains("| Cost | $0.0123 |", report, StringComparison.Ordinal);
        Assert.Contains("| Model latency | 1 min 1 s in all, 3.1 s per call |", report, StringComparison.Ordinal);
        // One row per ticket with problems, pipes escaped so the table holds
        Assert.Contains("| G-01 | Late \\| parcel | didn't cite shipping-times; reply failed: timeout |", report, StringComparison.Ordinal);
        Assert.DoesNotContain("| G-02 |", report, StringComparison.Ordinal);
        Assert.Contains("(approximation: phrase list)", report, StringComparison.Ordinal);
        Assert.DoesNotContain("Dry run", report, StringComparison.Ordinal);
    }

    [Fact]
    public void A_failed_run_lists_what_it_missed_and_a_clean_run_says_none_failed()
    {
        var metrics = EvalThresholdsTests.Metrics(new Ratio(5, 10), new Ratio(5, 10), 0);

        var report = Render(metrics, ["Category accuracy 50.0% is below the threshold of 60%."], []);

        Assert.Contains("**Result: failed**\n- Category accuracy 50.0% is below the threshold of 60%.\n", report, StringComparison.Ordinal);
        Assert.Contains("## Failures\n\nNone.\n", report, StringComparison.Ordinal);
    }

    [Fact]
    public void Unpriced_calls_are_named_instead_of_counting_as_free()
    {
        var report = EvalReport.Render(
            Run, EvalThresholdsTests.Metrics(new Ratio(6, 10), new Ratio(5, 10), 0), Usage with { UnpricedCalls = 3 }, Thresholds, [], []);

        Assert.Contains("| Cost | $0.0123 plus 3 call(s) without a price for their model |", report, StringComparison.Ordinal);
    }

    [Fact]
    public void A_dry_run_on_part_of_the_set_says_so()
    {
        var metrics = EvalThresholdsTests.Metrics(new Ratio(2, 3), new Ratio(1, 3), 0) with { Tickets = 3 };

        var report = Render(metrics, [], []);

        Assert.Contains("Dry run on 3 of the 10 golden tickets: thresholds are shown but not enforced.", report, StringComparison.Ordinal);
    }

    private static string Render(EvalMetrics metrics, IReadOnlyList<string> failures, IReadOnlyList<(string, string, IReadOnlyList<string>)> problems) =>
        EvalReport.Render(Run, metrics, Usage, Thresholds, failures, problems).ReplaceLineEndings("\n");
}
