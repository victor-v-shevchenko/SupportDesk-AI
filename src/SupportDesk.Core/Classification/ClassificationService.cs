using System.Diagnostics;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using SupportDesk.Core.AI;
using SupportDesk.Core.AI.Prompts;
using SupportDesk.Core.Replies.Tools;
using SupportDesk.Core.Telemetry;
using SupportDesk.Core.Tickets;

namespace SupportDesk.Core.Classification;

public interface IClassificationService
{
    /// <summary>
    /// Classifies the ticket and saves the result, replacing any earlier one; null when there's no such ticket. An answer
    /// that fails validation twice, one that stopped at the output limit, and a ticket with injection signals (which never
    /// reaches the model) are saved as <see cref="ClassificationStatus.NeedsManual"/>. Model errors are thrown
    /// (PLAN.md 6.5) and leave the saved classification as it was.
    /// </summary>
    Task<TicketClassification?> ClassifyAsync(int ticketId, CancellationToken cancellationToken);
}

/// <summary>
/// Classification with structured output (PLAN.md 9): asks for <see cref="ClassificationResult"/> with its JSON schema,
/// validates the answer itself, and on a rejected answer tries once more with the validation error added to the prompt.
/// </summary>
public sealed partial class ClassificationService(
    IChatClient chat,
    IPromptStore prompts,
    ITicketRepository tickets,
    SupportDeskMetrics metrics,
    TimeProvider time,
    ILogger<ClassificationService> logger) : IClassificationService
{
    /// <summary>The first answer and one retry; never more (PLAN.md 9).</summary>
    public const int MaxAttempts = 2;

    /// <summary>How the reason of a classification held back by the injection guard starts; the evaluation tells it from a model failure by this.</summary>
    public const string InjectionReasonPrefix = "The ticket text has injection signals";

    /// <summary>
    /// The reason of an answer that stopped at the output limit before it was valid (issue #49): a budget problem, not the
    /// model's answer, so it isn't retried with the same limit.
    /// </summary>
    public const string OutputLimitReason = "The model's answer stopped at the output limit before it was complete, so a person should classify it.";

    /// <summary>The reason a held classification carries: the strong signal names, never ticket text; at most <see cref="ClassificationResult.ReasonMaxLength"/> characters.</summary>
    public static string InjectionReason(IEnumerable<string> signals) =>
        $"{InjectionReasonPrefix} ({string.Join(", ", signals)}), so a person should classify it.";

    public async Task<TicketClassification?> ClassifyAsync(int ticketId, CancellationToken cancellationToken)
    {
        using var activity = SupportDeskTelemetry.Source.StartActivity("classification");
        activity?.SetTag(SupportDeskTelemetry.TicketId, ticketId);
        try
        {
            var saved = await ClassifyCoreAsync(ticketId, activity, cancellationToken);
            activity?.SetTag(SupportDeskTelemetry.Outcome, saved?.Status.ToString());
            return saved;
        }
        catch (Exception exception) when (activity.RecordedFailure(exception, cancellationToken))
        {
            throw;
        }
    }

    private async Task<TicketClassification?> ClassifyCoreAsync(int ticketId, Activity? activity, CancellationToken cancellationToken)
    {
        var ticket = await tickets.FindAsync(ticketId, cancellationToken);
        if (ticket is null)
        {
            return null;
        }

        var prompt = prompts.GetPrompt(PromptNames.Classify);

        // A ticket that tries to steer the model gets no model call: there's nothing for an injected category to steer
        // (PLAN.md 9), and a person checks it. Only the strong signals hold it: the role turn has documented false positives
        var signals = InjectionSignals.FindStrong(ticket);
        if (signals.Count > 0)
        {
            LogInjectionSignals(ticket.Id, string.Join(", ", signals));
            // Held without a model call: the strong signal names, never the matched text
            activity?.SetTag("supportdesk.held", true);
            activity?.SetTag("supportdesk.injection_signals", signals.ToArray());
            var held = new TicketClassification
            {
                Status = ClassificationStatus.NeedsManual,
                Reason = InjectionReason(signals),
                PromptVersion = prompt.Version,
                ClassifiedAt = time.GetUtcNow(),
            };
            var savedHeld = await tickets.SaveClassificationAsync(ticket.Id, held, cancellationToken);
            // Only a result that was saved: a null means no row matched
            if (savedHeld is not null)
            {
                metrics.RecordGuardHits(MetricValues.ScopeClassification, signals);
                metrics.RecordClassification(MetricValues.Held, null);
            }

            return savedHeld;
        }

        var options = new AiCallContext(AiFeature.Classification, prompt.Version, ticket.Id, ticket.Number).ApplyTo(new ChatOptions());
        var messages = prompt.ToMessages(new Dictionary<string, string> { ["ticket"] = TicketPromptFormatter.Format(ticket) }).ToList();

        var classification = await ClassifyAsync(ticket.Id, prompt, messages, options, cancellationToken);
        classification.PromptVersion = prompt.Version;
        classification.ClassifiedAt = time.GetUtcNow();

        var saved = await tickets.SaveClassificationAsync(ticket.Id, classification, cancellationToken);
        // When no row matched, nothing was saved and nothing is counted
        if (saved is not null)
        {
            if (classification.Status == ClassificationStatus.Done)
            {
                metrics.RecordClassification(MetricValues.Done, null);
            }
            else
            {
                metrics.RecordClassification(
                    MetricValues.NeedsManual, classification.Reason == OutputLimitReason ? MetricValues.ReasonOutputLimit : MetricValues.ReasonInvalidOutput);
            }
        }

        return saved;
    }

    private async Task<TicketClassification> ClassifyAsync(
        int ticketId, PromptTemplate prompt, List<ChatMessage> messages, ChatOptions options, CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            // Sets the JSON schema as the response format; the typed result isn't read, because it doesn't enforce the schema
            var response = await chat.GetResponseAsync<ClassificationResult>(
                messages, ClassificationJson.Options, options, useJsonSchemaResponseFormat: true, cancellationToken);
            var validation = ClassificationValidator.Validate(response.Text);
            if (validation.Result is { } result)
            {
                return new TicketClassification { Status = ClassificationStatus.Done, Category = result.Category, Priority = result.Priority, Reason = result.Reason };
            }

            LogRejected(ticketId, attempt, validation.ErrorText, response.Text);

            // A retry with the same limit would stop there again, typically while a thinking model is still thinking
            if (response.FinishReason == ChatFinishReason.Length)
            {
                LogStoppedAtOutputLimit(ticketId, attempt, response.Usage?.OutputTokenCount);
                return new TicketClassification { Status = ClassificationStatus.NeedsManual, Reason = OutputLimitReason };
            }

            // The rejected answer goes back as the model's turn (an empty one can't be sent), then the error as the next user turn
            if (!string.IsNullOrWhiteSpace(response.Text))
            {
                messages.Add(new ChatMessage(ChatRole.Assistant, response.Text));
            }

            messages.Add(new ChatMessage(ChatRole.User, prompt.RenderRetry(new Dictionary<string, string> { ["error"] = validation.ErrorText })));
        }

        LogNeedsManual(ticketId, MaxAttempts);
        return new TicketClassification { Status = ClassificationStatus.NeedsManual };
    }

    // The raw output can repeat the ticket, so it's only logged at debug level (CLAUDE.md, LLM rules), and its event ID is in
    // ContentLogEvents, so the OpenTelemetry export drops it (PLAN.md 14)
    [LoggerMessage(EventId = ContentLogEvents.ClassificationRejected, Level = LogLevel.Debug, Message = "Classification answer {Attempt} for ticket {TicketId} was rejected: {Errors} Raw output: {Output}")]
    private partial void LogRejected(int ticketId, int attempt, string errors, string output);

    // The signal names only: the matched text is ticket text
    [LoggerMessage(Level = LogLevel.Warning, Message = "Ticket {TicketId} isn't classified by the model: the ticket has injection signals ({Signals})")]
    private partial void LogInjectionSignals(int ticketId, string signals);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Classification answer {Attempt} for ticket {TicketId} stopped at the output limit after {OutputTokens} tokens; raise AI:Budget:MaxOutputTokens:Classification or turn the model's thinking off (AI:Ollama:Think)")]
    private partial void LogStoppedAtOutputLimit(int ticketId, int attempt, long? outputTokens);

    [LoggerMessage(Level = LogLevel.Information, Message = "Ticket {TicketId} needs manual classification after {Attempts} rejected answers")]
    private partial void LogNeedsManual(int ticketId, int attempts);
}
