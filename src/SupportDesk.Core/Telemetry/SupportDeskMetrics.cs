using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;
using SupportDesk.Core.AI;
using SupportDesk.Core.AI.Usage;
using SupportDesk.Core.Replies;

namespace SupportDesk.Core.Telemetry;

/// <summary>
/// The app's counters (PLAN.md 14, metrics): usage, cost, failures, injection-guard hits, tool calls and proposals, so a
/// dashboard shows them over time without querying <c>ModelCalls</c>. A singleton built from <see cref="IMeterFactory"/>, so
/// tests get a meter of their own (no static state). Every tag value comes from a fixed set or from configuration (the model
/// name); there is never an ID, a ticket number or text. Methods are called after the outcome is known and only add to counters.
/// None of them can throw: a measurement listener that throws (counters call listeners synchronously) is caught and logged at
/// debug level, so recording can never change a call's outcome.
/// </summary>
public sealed partial class SupportDeskMetrics
{
    /// <summary>The name of the app's meter, which the API subscribes to.</summary>
    public const string MeterName = "SupportDesk";

    public const string ModelCallsName = "supportdesk.model_calls";
    public const string TokensName = "supportdesk.model_calls.tokens";
    public const string CostName = "supportdesk.model_calls.cost";
    public const string DroppedName = "supportdesk.model_calls.dropped";
    public const string RepliesName = "supportdesk.replies";
    public const string ToolCallsName = "supportdesk.tool_calls";
    public const string GuardHitsName = "supportdesk.injection_guard.hits";
    public const string ClassificationsName = "supportdesk.classifications";
    public const string FollowUpsName = "supportdesk.follow_ups";

    private readonly Counter<long> _modelCalls;
    private readonly Counter<long> _tokens;
    private readonly Counter<double> _cost;
    private readonly Counter<long> _dropped;
    private readonly Counter<long> _replies;
    private readonly Counter<long> _toolCalls;
    private readonly Counter<long> _guardHits;
    private readonly Counter<long> _classifications;
    private readonly Counter<long> _followUps;

    private readonly ILogger<SupportDeskMetrics> _logger;

    public SupportDeskMetrics(IMeterFactory meterFactory, ILogger<SupportDeskMetrics> logger)
    {
        ArgumentNullException.ThrowIfNull(meterFactory);
        ArgumentNullException.ThrowIfNull(logger);

        _logger = logger;

        // The factory owns the meter and disposes it with the container
        var meter = meterFactory.Create(MeterName);
        _modelCalls = meter.CreateCounter<long>(ModelCallsName, "{call}", "Model calls, one per ModelCalls row.");
        _tokens = meter.CreateCounter<long>(TokensName, "{token}", "Tokens of model calls, as written to the ModelCalls row.");
        _cost = meter.CreateCounter<double>(CostName, "USD", "Cost of model calls with a price, as written to the ModelCalls row.");
        _dropped = meter.CreateCounter<long>(DroppedName, "{row}", "ModelCalls rows that were not saved.");
        _replies = meter.CreateCounter<long>(RepliesName, "{reply}", "Reply streams that ended.");
        _toolCalls = meter.CreateCounter<long>(ToolCallsName, "{call}", "Tool calls a reply made.");
        _guardHits = meter.CreateCounter<long>(GuardHitsName, "{ticket}", "Tickets with injection signals, one count per signal.");
        _classifications = meter.CreateCounter<long>(ClassificationsName, "{classification}", "Classifications that were saved.");
        _followUps = meter.CreateCounter<long>(FollowUpsName, "{proposal}", "Follow-up proposal events.");
    }

    /// <summary>One <c>ModelCalls</c> row: the call, its tokens, and its cost when the model has a price.</summary>
    public void RecordModelCall(ModelCall row, Exception? error)
    {
        ArgumentNullException.ThrowIfNull(row);

        TagList calls;
        try
        {
            calls = new TagList
            {
                { "feature", row.Feature.ToString() },
                { "provider", row.Provider },
                { "model", row.Model },
                { "outcome", OutcomeName(row.Outcome) },
            };
            if (error is not null)
            {
                calls.Add("error.type", ReplyErrors.From(error).Code);
            }
        }
        catch (ArgumentOutOfRangeException exception)
        {
            // An outcome this code doesn't know is a programming error; it must not reach the call, but the counters now
            // miss a row the table has, so it is a warning, not a debug line
            LogUnknownOutcome(exception);
            return;
        }

        var feature = row.Feature.ToString();
        Add(_modelCalls, 1, calls);
        var estimated = row.IsEstimated;
        Add(_tokens, row.InputTokens, new TagList { { "feature", feature }, { "model", row.Model }, { "direction", "input" }, { "estimated", estimated } });
        Add(_tokens, row.OutputTokens, new TagList { { "feature", feature }, { "model", row.Model }, { "direction", "output" }, { "estimated", estimated } });
        if (row.CostUsd is { } cost)
        {
            Add(_cost, (double)cost, new TagList { { "feature", feature }, { "model", row.Model } });
        }
    }

    /// <summary>Rows that never reached the table.</summary>
    public void RecordDropped(string reason, int count)
    {
        if (count > 0)
        {
            Add(_dropped, count, new TagList { { "reason", reason } });
        }
    }

    /// <summary>A reply stream ended: <paramref name="outcome"/> is <c>done</c>, an error code or <c>cancelled</c>.</summary>
    public void RecordReply(string outcome, bool guarded) =>
        Add(_replies, 1, new TagList { { "outcome", outcome }, { "guarded", guarded } });

    public void RecordToolCall(string tool, string result) =>
        Add(_toolCalls, 1, new TagList { { "tool", tool }, { "result", result } });

    /// <summary>One count per signal name found in a ticket.</summary>
    public void RecordGuardHits(string scope, IEnumerable<string> signals)
    {
        ArgumentNullException.ThrowIfNull(signals);

        foreach (var signal in signals)
        {
            Add(_guardHits, 1, new TagList { { "scope", scope }, { "signal", signal } });
        }
    }

    /// <summary>A saved classification; <paramref name="reason"/> only for <see cref="MetricValues.NeedsManual"/>.</summary>
    public void RecordClassification(string status, string? reason)
    {
        var tags = new TagList { { "status", status } };
        if (reason is not null)
        {
            tags.Add("reason", reason);
        }

        Add(_classifications, 1, tags);
    }

    public void RecordFollowUp(string followUpEvent, int count = 1)
    {
        if (count > 0)
        {
            Add(_followUps, count, new TagList { { "event", followUpEvent } });
        }
    }

    /// <summary>One guarded add: a listener that throws is logged and the next measurement still goes in.</summary>
    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Measurement listeners run inside Counter.Add; whatever one throws must never change the outcome of the call being counted.")]
    private void Add<T>(Counter<T> counter, T value, TagList tags)
        where T : struct
    {
        try
        {
            counter.Add(value, tags);
        }
        catch (Exception exception)
        {
            LogFailed(exception);
        }
    }

    private static string OutcomeName(ModelCallOutcome outcome) => outcome switch
    {
        ModelCallOutcome.Success => "success",
        ModelCallOutcome.Error => "error",
        ModelCallOutcome.Cancelled => "cancelled",
        _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "Unknown model call outcome."),
    };

    // Debug level: a listener's failure is outside the app and never worth a warning (an unknown outcome is, below)
    [LoggerMessage(Level = LogLevel.Debug, Message = "Recording a metric failed")]
    private partial void LogFailed(Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "A model call wasn't counted, so the counters miss a row the table has")]
    private partial void LogUnknownOutcome(Exception exception);
}

/// <summary>The fixed tag values of <see cref="SupportDeskMetrics"/>; the tests check every recorded value against these.</summary>
public static class MetricValues
{
    public const string Done = "done";
    public const string Cancelled = "cancelled";

    public const string ScopeReply = "reply";
    public const string ScopeClassification = "classification";

    public const string ToolRan = "ran";
    public const string ToolInvalidArguments = "invalid_arguments";
    public const string ToolOverLimit = "over_limit";

    public const string NeedsManual = "needs_manual";
    public const string Held = "held";
    public const string ReasonInvalidOutput = "invalid_output";
    public const string ReasonOutputLimit = "output_limit";

    public const string DropBufferFull = "buffer_full";
    public const string DropShutdown = "shutdown";
    public const string DropSaveFailed = "save_failed";
    public const string DropDrainDeadline = "drain_deadline";

    public const string Proposed = "proposed";
    public const string Updated = "updated";
    public const string Confirmed = "confirmed";
    public const string Expired = "expired";
}
