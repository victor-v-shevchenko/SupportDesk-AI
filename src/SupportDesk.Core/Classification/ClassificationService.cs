using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using SupportDesk.Core.AI;
using SupportDesk.Core.AI.Prompts;
using SupportDesk.Core.Replies.Tools;
using SupportDesk.Core.Tickets;

namespace SupportDesk.Core.Classification;

public interface IClassificationService
{
    /// <summary>
    /// Classifies the ticket and saves the result, replacing any earlier one; null when there's no such ticket. An answer
    /// that fails validation twice, and a ticket with injection signals (which never reaches the model), are saved as
    /// <see cref="ClassificationStatus.NeedsManual"/>. Model errors are thrown
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
    TimeProvider time,
    ILogger<ClassificationService> logger) : IClassificationService
{
    public const string PromptName = "classify";

    /// <summary>The first answer and one retry; never more (PLAN.md 9).</summary>
    public const int MaxAttempts = 2;

    /// <summary>How the reason of a classification held back by the injection guard starts; the evaluation tells it from a model failure by this.</summary>
    public const string InjectionReasonPrefix = "The ticket text has injection signals";

    public async Task<TicketClassification?> ClassifyAsync(int ticketId, CancellationToken cancellationToken)
    {
        var ticket = await tickets.FindAsync(ticketId, cancellationToken);
        if (ticket is null)
        {
            return null;
        }

        var prompt = prompts.GetPrompt(PromptName);

        // A ticket that tries to steer the model gets no model call: there's nothing for an injected category to steer
        // (PLAN.md 10, injection guard), and a person checks it
        var signals = InjectionSignals.Find(ticket);
        if (signals.Count > 0)
        {
            LogInjectionSignals(ticket.Id, string.Join(", ", signals));
            var held = new TicketClassification
            {
                Status = ClassificationStatus.NeedsManual,
                Reason = $"{InjectionReasonPrefix} ({string.Join(", ", signals)}), so a person should classify it.",
                PromptVersion = prompt.Version,
                ClassifiedAt = time.GetUtcNow(),
            };
            return await tickets.SaveClassificationAsync(ticket.Id, held, cancellationToken);
        }

        var options = new AiCallContext(AiFeature.Classification, prompt.Version, ticket.Id, ticket.Number).ApplyTo(new ChatOptions());
        var messages = prompt.ToMessages(new Dictionary<string, string> { ["ticket"] = TicketPromptFormatter.Format(ticket) }).ToList();

        var result = await ClassifyAsync(ticket.Id, prompt, messages, options, cancellationToken);
        var classification = result is null
            ? new TicketClassification { Status = ClassificationStatus.NeedsManual }
            : new TicketClassification { Status = ClassificationStatus.Done, Category = result.Category, Priority = result.Priority, Reason = result.Reason };
        classification.PromptVersion = prompt.Version;
        classification.ClassifiedAt = time.GetUtcNow();

        return await tickets.SaveClassificationAsync(ticket.Id, classification, cancellationToken);
    }

    private async Task<ClassificationResult?> ClassifyAsync(
        int ticketId, PromptTemplate prompt, List<ChatMessage> messages, ChatOptions options, CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            // Sets the JSON schema as the response format; the typed result isn't read, because it doesn't enforce the schema
            var response = await chat.GetResponseAsync<ClassificationResult>(
                messages, ClassificationJson.Options, options, useJsonSchemaResponseFormat: true, cancellationToken);
            var validation = ClassificationValidator.Validate(response.Text);
            if (validation.IsValid)
            {
                return validation.Result;
            }

            LogRejected(ticketId, attempt, validation.ErrorText, response.Text);

            // The rejected answer goes back as the model's turn (an empty one can't be sent), then the error as the next user turn
            if (!string.IsNullOrWhiteSpace(response.Text))
            {
                messages.Add(new ChatMessage(ChatRole.Assistant, response.Text));
            }

            messages.Add(new ChatMessage(ChatRole.User, prompt.RenderRetry(new Dictionary<string, string> { ["error"] = validation.ErrorText })));
        }

        LogNeedsManual(ticketId, MaxAttempts);
        return null;
    }

    // The raw output can repeat the ticket, so it's only logged at debug level (CLAUDE.md, LLM rules)
    [LoggerMessage(Level = LogLevel.Debug, Message = "Classification answer {Attempt} for ticket {TicketId} was rejected: {Errors} Raw output: {Output}")]
    private partial void LogRejected(int ticketId, int attempt, string errors, string output);

    // The signal names only: the matched text is ticket text
    [LoggerMessage(Level = LogLevel.Warning, Message = "Ticket {TicketId} isn't classified by the model: the ticket has injection signals ({Signals})")]
    private partial void LogInjectionSignals(int ticketId, string signals);

    [LoggerMessage(Level = LogLevel.Information, Message = "Ticket {TicketId} needs manual classification after {Attempts} rejected answers")]
    private partial void LogNeedsManual(int ticketId, int attempts);
}
