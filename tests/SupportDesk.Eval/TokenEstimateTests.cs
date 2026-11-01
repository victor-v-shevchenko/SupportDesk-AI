using Anthropic;
using Anthropic.Core;
using Anthropic.Models.Messages;
using Microsoft.Extensions.Configuration;
using SupportDesk.Core.AI;
using SupportDesk.Core.AI.Prompts;
using SupportDesk.Core.Tickets;
using SupportDesk.Data.Seeding;

namespace SupportDesk.Eval;

/// <summary>
/// The budget's token estimate (PLAN.md 6.4) must never be lower than the real count, or a call the guard lets through
/// could be over the limit. Counts the three prompts with seeded tickets through Anthropic's token-counting endpoint,
/// which is free but needs an API key; skipped without one.
/// </summary>
[Trait("Category", "Eval")]
public class TokenEstimateTests
{
    private static readonly string BaseDirectory = AppContext.BaseDirectory;

    public static TheoryData<string, string> Cases()
    {
        // The shortest ticket, the longest, and the one that tries to close the ticket tag
        var tickets = SeedFiles.ReadTicketsAsync(Path.Combine(BaseDirectory, "seed"), CancellationToken.None).GetAwaiter().GetResult();
        var byLength = tickets.OrderBy(t => t.Subject.Length + t.Body.Length).ToList();
        string[] chosen = [byLength[0].Number, byLength[^1].Number, tickets.Single(t => t.Body.Contains("</ticket>", StringComparison.Ordinal)).Number];

        var data = new TheoryData<string, string>();
        foreach (var prompt in AiOptionsValidator.RequiredPrompts)
        {
            foreach (var ticket in chosen.Distinct(StringComparer.Ordinal))
            {
                data.Add(prompt, ticket);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task The_estimate_is_not_lower_than_Anthropics_count(string promptName, string ticketNumber)
    {
        var ct = TestContext.Current.CancellationToken;
        var configuration = new ConfigurationBuilder().AddUserSecrets<TokenEstimateTests>().AddEnvironmentVariables().Build();
        var apiKey = configuration["AI:Anthropic:ApiKey"];
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            Assert.Skip("Set AI:Anthropic:ApiKey (user secrets or AI__Anthropic__ApiKey) to run this check.");
        }

        // The version the app is configured with, so a new prompt version is the one counted
        var version = new ConfigurationBuilder().AddJsonFile(Path.Combine(BaseDirectory, "appsettings.json")).Build()[$"AI:Prompts:{promptName}"]!;
        var prompt = PromptStore.Load(Path.Combine(BaseDirectory, "prompts"), promptName, version);
        var messages = prompt.ToMessages(await ValuesAsync(prompt, ticketNumber, ct));
        var model = configuration["AI:Chat:Model"] is { Length: > 0 } configured ? configured : "claude-opus-5-5";

        var client = new AnthropicClient(new ClientOptions { ApiKey = apiKey });
        var count = await client.Messages.CountTokens(
            new MessageCountTokensParams
            {
                Model = model,
                System = messages[0].Text,
                Messages = [new UserMessageParam { Content = messages[1].Text }],
            },
            ct);

        var estimate = TokenEstimator.EstimateInput(messages, options: null);
        TestContext.Current.SendDiagnosticMessage($"{promptName} with {ticketNumber}: estimate {estimate}, Anthropic counted {count.InputTokens}");
        Assert.True(estimate >= count.InputTokens, $"{promptName} with {ticketNumber}: estimate {estimate}, Anthropic counted {count.InputTokens}.");
    }

    private static async Task<Dictionary<string, string>> ValuesAsync(PromptTemplate prompt, string ticketNumber, CancellationToken cancellationToken)
    {
        var seed = Path.Combine(BaseDirectory, "seed");
        var ticket = (await SeedFiles.ReadTicketsAsync(seed, cancellationToken)).Single(t => t.Number == ticketNumber);
        var values = new Dictionary<string, string>
        {
            ["ticket"] = TicketPromptFormatter.Format(new Ticket
            {
                Number = ticket.Number,
                Subject = ticket.Subject,
                Body = ticket.Body,
                CustomerName = ticket.CustomerName,
                OrderNumber = ticket.OrderNumber,
            }),
        };

        if (prompt.Placeholders.Contains("sources"))
        {
            // Five sources, as retrieval returns (k = 5): whole seeded articles stand in for chunks until step 4
            var articles = await SeedFiles.ReadArticlesAsync(seed, cancellationToken);
            values["sources"] = string.Join("\n\n", articles.Take(5).Select((a, i) => $"[{i + 1}] {a.Title}\n{a.BodyMarkdown}"));
        }

        return values;
    }
}
