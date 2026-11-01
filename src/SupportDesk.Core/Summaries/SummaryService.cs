using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SupportDesk.Core.AI;
using SupportDesk.Core.AI.Prompts;
using SupportDesk.Core.Tickets;

namespace SupportDesk.Core.Summaries;

public interface ISummaryService
{
    /// <summary>
    /// Generates a summary of the ticket, saves it in place of any earlier one and returns it; null when there's no such
    /// ticket. A model error (<see cref="ModelUnavailableException"/>, <see cref="ModelRefusedException"/>,
    /// <see cref="BudgetExceededException"/>) or output that fails the check (<see cref="ModelOutputException"/>) is thrown
    /// before anything is saved, so the earlier summary stays as it was.
    /// </summary>
    Task<TicketSummary?> GenerateAsync(int ticketId, CancellationToken cancellationToken);
}

/// <summary>The ticket summary (PLAN.md 9): one non-streaming call through the shared pipeline, checked, then saved.</summary>
public sealed partial class SummaryService(
    IChatClient chat,
    IPromptStore prompts,
    ITicketRepository tickets,
    IOptions<AiOptions> options,
    ILogger<SummaryService> logger) : ISummaryService
{
    public const string PromptName = "summary";

    public async Task<TicketSummary?> GenerateAsync(int ticketId, CancellationToken cancellationToken)
    {
        var ticket = await tickets.FindAsync(ticketId, cancellationToken);
        if (ticket is null)
        {
            return null;
        }

        var prompt = prompts.GetPrompt(PromptName);
        var messages = BuildMessages(prompt, ticket);
        var chatOptions = new ChatOptions();
        if (options.Value.Budget.MaxOutputTokens.TryGetValue(AiFeature.Summary, out var maxOutput))
        {
            chatOptions.MaxOutputTokens = maxOutput;
        }

        new AiCallContext(AiFeature.Summary, prompt.Version, ticket.Id, ticket.Number).ApplyTo(chatOptions);

        var response = await chat.GetResponseAsync(messages, chatOptions, cancellationToken);
        var text = SummaryOutput.Check(response.Text);
        if (text is null)
        {
            LogEmptySummary(ticket.Id);
            throw new ModelOutputException("The model returned an empty summary.");
        }

        return await tickets.SaveSummaryAsync(ticket.Id, text, prompt.Version, cancellationToken);
    }

    /// <summary>The system prompt as written, and the ticket inside <c>&lt;ticket&gt;</c> in the user message (PLAN.md 6.3).</summary>
    public static IReadOnlyList<ChatMessage> BuildMessages(PromptTemplate prompt, Ticket ticket)
    {
        ArgumentNullException.ThrowIfNull(prompt);

        return prompt.ToMessages(new Dictionary<string, string>(StringComparer.Ordinal) { ["ticket"] = TicketPromptFormatter.Format(ticket) });
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The summary for ticket {TicketId} was empty; the saved summary is unchanged")]
    private partial void LogEmptySummary(int ticketId);
}
