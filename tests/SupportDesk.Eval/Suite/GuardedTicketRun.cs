using System.Text.Json;
using SupportDesk.EmbedFixtures;
using SupportDesk.Eval.Metrics;

namespace SupportDesk.Eval.Suite;

/// <summary>
/// Runs one golden ticket so that a failure costs the report one ticket, not the run. A ticket that fails after it was
/// created still has its <c>ModelCalls</c> rows, so its ID is handed over as soon as it exists, and its cost stays in the totals.
/// </summary>
public static class GuardedTicketRun
{
    /// <summary>What one broken ticket may throw: a failed or cut-off response, a body that isn't what the API sends, or a stream that ends early.</summary>
    public static bool IsTicketFailure(Exception exception) =>
        exception is HttpRequestException or JsonException or InvalidOperationException or KeyNotFoundException or IOException;

    /// <param name="ticket">The golden ticket.</param>
    /// <param name="ticketIds">Every ticket the run created, to read their usage rows from afterwards.</param>
    /// <param name="run">The run itself; it calls its second argument with the new ticket's ID right after creating it.</param>
    public static async Task<TicketOutcome> RunAsync(GoldenTicket ticket, ICollection<int> ticketIds, Func<GoldenTicket, Action<int>, Task<TicketOutcome>> run)
    {
        ArgumentNullException.ThrowIfNull(ticketIds);
        ArgumentNullException.ThrowIfNull(run);

        try
        {
            return await run(ticket, ticketIds.Add);
        }
        catch (Exception exception) when (IsTicketFailure(exception))
        {
            // It counts as failed, and the report says so
            var error = $"not run ({exception.GetType().Name})";
            return new TicketOutcome(ticket, null, null, error, "", [], [], error, 0);
        }
    }
}
