using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SupportDesk.Core.AI;
using SupportDesk.Core.AI.Prompts;
using SupportDesk.Core.AI.Usage;
using SupportDesk.Core.Replies.Tools;
using SupportDesk.Core.Retrieval;
using SupportDesk.Core.Telemetry;
using SupportDesk.Core.Tickets;

namespace SupportDesk.Core.Replies;

public interface IReplyService
{
    /// <summary>
    /// Streams a suggested reply for <paramref name="ticket"/> (PLAN.md 7): <see cref="ReplyToken"/>s and
    /// <see cref="ReplyToolCall"/>s, then <see cref="ReplyCitations"/>, <see cref="ReplyUsage"/> and <see cref="ReplyDone"/>,
    /// or one <see cref="ReplyError"/> at the end. The caller's cancellation isn't an error: the stream ends with <see cref="OperationCanceledException"/>.
    /// </summary>
    IAsyncEnumerable<ReplyEvent> StreamAsync(Ticket ticket, CancellationToken cancellationToken);
}

/// <summary>The two settings the reply reads, from <see cref="AiOptions"/> and <see cref="RetrievalOptions"/>.</summary>
public sealed class ReplySettings(IOptions<AiOptions> aiOptions, IOptions<RetrievalOptions> retrievalOptions)
{
    public int ReplyChunks => retrievalOptions.Value.ReplyChunks;

    public int MaxInputTokensPerCall => aiOptions.Value.Budget.MaxInputTokensPerCall;
}

/// <summary>
/// A reply grounded in the knowledge base (PLAN.md 6.3, 6.4, 8), streamed through the shared pipeline, with the two tools
/// of PLAN.md 10. The pipeline's tool loop runs them; this service only offers them and reports the calls that ran.
/// </summary>
public sealed partial class ReplyService(
    IChatClient chat,
    IRetrievalService retrieval,
    ReplyToolFactory toolFactory,
    IPromptStore prompts,
    PriceTable prices,
    ReplySettings settings,
    ILogger<ReplyService> logger) : IReplyService
{
    public IAsyncEnumerable<ReplyEvent> StreamAsync(Ticket ticket, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ticket);

        return StreamEventsAsync(ticket, Guid.NewGuid(), cancellationToken);
    }

    /// <summary>
    /// Runs the reply and turns a failure into one <see cref="ReplyError"/> as the last event. Nothing can come after an
    /// error, and <see cref="ReplyDone"/> is always last otherwise, which is what keeps the event order on every path.
    /// </summary>
    private async IAsyncEnumerable<ReplyEvent> StreamEventsAsync(Ticket ticket, Guid replyRunId, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // Ends when the stream does: done, error, or the caller leaving (disposing the enumerator runs the finally below)
        using var activity = SupportDeskTelemetry.Source.StartActivity("reply");
        activity?.SetTag(SupportDeskTelemetry.TicketId, ticket.Id);
        activity?.SetTag(SupportDeskTelemetry.ReplyRunId, replyRunId.ToString());

        var finished = false;
        await using var events = RunAsync(ticket, replyRunId, activity, cancellationToken).GetAsyncEnumerator(cancellationToken);
        try
        {
            while (true)
            {
                ReplyEvent next;
                try
                {
                    // An iterator doesn't keep Activity.Current between calls, so the model calls and tools below would
                    // otherwise attach to the HTTP span instead of this one
                    if (activity is not null)
                    {
                        Activity.Current = activity;
                    }

                    if (!await events.MoveNextAsync())
                    {
                        finished = true;
                        yield break;
                    }

                    next = events.Current;
                }
                // Once the caller has cancelled, whatever the provider throws (it may wrap the cancellation) ends the stream as cancelled
                catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
                {
                    next = Fail(ticket, replyRunId, exception, activity);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    // The caller cancelled and the provider threw its own error (it may wrap the cancellation): end as cancelled
                    cancellationToken.ThrowIfCancellationRequested();
                    throw;
                }

                // Both are last, so a caller that stops reading right after one isn't logged as cancelling
                if (next is ReplyError or ReplyDone)
                {
                    finished = true;
                    activity?.SetTag(SupportDeskTelemetry.Outcome, next is ReplyDone ? "done" : "error");
                }

                yield return next;
                if (finished)
                {
                    yield break;
                }
            }
        }
        finally
        {
            // The caller cancelled or stopped reading; the model call's own row says Cancelled too (UsageLoggingMiddleware)
            if (!finished)
            {
                activity?.SetTag(SupportDeskTelemetry.Outcome, "cancelled");
                activity?.SetTag(SupportDeskTelemetry.Cancelled, true);
                LogCancelled(replyRunId, ticket.Id);
            }
        }
    }

    private async IAsyncEnumerable<ReplyEvent> RunAsync(Ticket ticket, Guid replyRunId, Activity? activity, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var template = prompts.GetPrompt(PromptNames.Reply);
        var tools = toolFactory.Create(ticket, replyRunId);
        activity?.SetTag("supportdesk.injection_guard_limited", tools.InjectionSignals.Count > 0);
        if (tools.InjectionSignals.Count > 0)
        {
            // Signal names only: the matched text is ticket text
            activity?.SetTag("supportdesk.injection_signals", tools.InjectionSignals.ToArray());
            LogInjectionSignals(replyRunId, ticket.Id, string.Join(", ", tools.InjectionSignals));
        }

        // Only the reply offers tools, so no other feature can run one
        var options = new AiCallContext(AiFeature.Reply, template.Version, ticket.Id, ticket.Number, replyRunId)
            .ApplyTo(new ChatOptions { Tools = [.. tools.Tools] });

        var chunks = await retrieval.SearchAsync(ReplyPromptBuilder.BuildQuery(ticket), settings.ReplyChunks, cancellationToken);
        activity?.SetTag("supportdesk.chunks_retrieved", chunks.Count);
        var prompt = ReplyPromptBuilder.Build(template, ticket, chunks, options, settings.MaxInputTokensPerCall);
        activity?.SetTag("supportdesk.chunks_kept", prompt.Sources.Count);
        if (prompt.Dropped > 0)
        {
            LogDropped(replyRunId, ticket.Id, prompt.Dropped, prompt.Sources.Count);
        }

        var tokens = new StreamTokens();
        var text = new StringBuilder();
        // The first model call, plus one more after each round of tool calls (the tool loop sends the results back)
        var calls = 1;
        await foreach (var update in chat.GetStreamingResponseAsync(prompt.Messages, options, cancellationToken).WithCancellation(cancellationToken))
        {
            tokens.Observe(update);
            // Text only: tool calls, thinking text and usage updates aren't part of the reply
            var piece = update.Text;
            if (piece.Length > 0)
            {
                text.Append(piece);
                yield return new ReplyToken(piece);
            }

            // The tool loop passes each result on after running the tool, so the event follows the tool, in call order
            if (update.Contents.OfType<FunctionResultContent>().Any())
            {
                calls++;
            }

            foreach (var result in update.Contents.OfType<FunctionResultContent>())
            {
                if (tools.Take(result.CallId) is { } call)
                {
                    yield return call;
                }
            }
        }

        var citations = CitationParser.Check(text.ToString(), prompt.Sources);
        if (citations.Removed.Count > 0)
        {
            LogRemoved(replyRunId, ticket.Id, string.Join(", ", citations.Removed), prompt.Sources.Count);
        }

        activity?.SetTag("supportdesk.citations", citations.Citations.Count);
        activity?.SetTag("supportdesk.citations_removed", citations.Removed.Count);
        yield return citations;

        var used = tokens.Resolve(prompt.Messages, options);
        var model = options.ModelId ?? chat.GetService<ChatClientMetadata>()?.DefaultModelId ?? "unknown";
        yield return new ReplyUsage(used.Input, used.Output, prices.Cost(model, used.Input, used.Output), used.IsEstimated, calls);
        yield return new ReplyDone(replyRunId);
    }

    private ReplyError Fail(Ticket ticket, Guid replyRunId, Exception exception, Activity? activity)
    {
        // Not cancelled: the caller of Fail has checked its token
        activity.RecordFailure(exception, CancellationToken.None);
        var error = ReplyErrors.From(exception);
        if (error.Code == ReplyErrorCodes.InternalError)
        {
            LogUnexpected(exception, replyRunId, ticket.Id);
        }
        else
        {
            // The type and status only: a provider's message can echo the request, which holds ticket text (PLAN.md 6.6)
            LogFailed(replyRunId, ticket.Id, error.Code, exception.GetType().Name, (exception as ModelUnavailableException)?.StatusCode);
        }

        return error;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Reply {ReplyRunId} for ticket {TicketId} dropped {Dropped} chunks over the token budget and kept {Kept}")]
    private partial void LogDropped(Guid replyRunId, int ticketId, int dropped, int kept);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Reply {ReplyRunId} for ticket {TicketId} cited markers outside 1…{SourceCount}: {Removed}")]
    private partial void LogRemoved(Guid replyRunId, int ticketId, string removed, int sourceCount);

    // The signal names only: the matched text is ticket text
    [LoggerMessage(Level = LogLevel.Warning, Message = "Reply {ReplyRunId} for ticket {TicketId} doesn't offer propose_follow_up_task: the ticket has injection signals ({Signals})")]
    private partial void LogInjectionSignals(Guid replyRunId, int ticketId, string signals);

    [LoggerMessage(Level = LogLevel.Information, Message = "Reply {ReplyRunId} for ticket {TicketId} was cancelled")]
    private partial void LogCancelled(Guid replyRunId, int ticketId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Reply {ReplyRunId} for ticket {TicketId} failed with {Code}: {ErrorType} (status {StatusCode})")]
    private partial void LogFailed(Guid replyRunId, int ticketId, string code, string errorType, int? statusCode);

    [LoggerMessage(Level = LogLevel.Error, Message = "Reply {ReplyRunId} for ticket {TicketId} failed unexpectedly")]
    private partial void LogUnexpected(Exception exception, Guid replyRunId, int ticketId);
}
