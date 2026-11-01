using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using SupportDesk.Core.AI;
using SupportDesk.Core.AI.Prompts;
using SupportDesk.Core.Tickets;

namespace SupportDesk.Core.Classification;

public interface IClassificationService
{
    /// <summary>
    /// Classifies the ticket and saves the result, replacing any earlier one; null when there's no such ticket. An answer
    /// that fails validation twice is saved as <see cref="ClassificationStatus.NeedsManual"/>. Model errors are thrown
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

    public async Task<TicketClassification?> ClassifyAsync(int ticketId, CancellationToken cancellationToken)
    {
        var ticket = await tickets.FindAsync(ticketId, cancellationToken);
        if (ticket is null)
        {
            return null;
        }

        var prompt = prompts.GetPrompt(PromptName);
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

    [LoggerMessage(Level = LogLevel.Information, Message = "Ticket {TicketId} needs manual classification after {Attempts} rejected answers")]
    private partial void LogNeedsManual(int ticketId, int attempts);
}
