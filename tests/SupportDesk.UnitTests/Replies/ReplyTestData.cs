using Microsoft.Extensions.Options;
using SupportDesk.Core.AI;
using SupportDesk.Core.AI.Prompts;
using SupportDesk.Core.Retrieval;
using SupportDesk.Core.Tickets;

namespace SupportDesk.UnitTests.Replies;

internal static class ReplyTestData
{
    public static PromptTemplate ReplyTemplate { get; } = new PromptStore(Options.Create(new AiOptions
    {
        Prompts = new(StringComparer.Ordinal) { ["reply"] = "reply.v1" },
    })).GetPrompt("reply");

    public static Ticket Ticket(string number = "T-1001", string subject = "How long does standard shipping take?", string body = "I ordered a tent yesterday. When will it arrive?", int id = 1) => new()
    {
        Id = id,
        Number = number,
        Subject = subject,
        Body = body,
        CustomerName = "Maya Lindqvist",
        CreatedAt = new DateTimeOffset(2026, 10, 1, 8, 0, 0, TimeSpan.Zero),
    };

    public static RetrievedChunk Chunk(string slug, double similarity, string? heading = "Section", string? text = null, string? title = null) =>
        new(slug, title ?? "Title of " + slug, heading, text ?? "Text of " + slug + ".", similarity);
}

/// <summary>Returns fixed chunks and records what it was asked.</summary>
internal sealed class StubRetrieval(params RetrievedChunk[] chunks) : IRetrievalService
{
    public List<(string Query, int K)> Calls { get; } = [];

    public Exception? Throw { get; init; }

    public Task<IReadOnlyList<RetrievedChunk>> SearchAsync(string query, int k, CancellationToken cancellationToken)
    {
        Calls.Add((query, k));
        if (Throw is not null)
        {
            throw Throw;
        }

        return Task.FromResult<IReadOnlyList<RetrievedChunk>>([.. chunks.Take(k)]);
    }
}
