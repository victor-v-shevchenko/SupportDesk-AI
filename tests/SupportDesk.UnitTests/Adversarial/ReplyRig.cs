using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using SupportDesk.Core.AI;
using SupportDesk.Core.AI.Fake;
using SupportDesk.Core.AI.Prompts;
using SupportDesk.Core.AI.Usage;
using SupportDesk.Core.Replies;
using SupportDesk.Core.Replies.Tools;
using SupportDesk.Core.Retrieval;
using SupportDesk.Core.Tickets;
using SupportDesk.UnitTests.AI;
using SupportDesk.UnitTests.Replies;

namespace SupportDesk.UnitTests.Adversarial;

/// <summary>
/// The real <see cref="ReplyService"/> behind the real pipeline, around the fake provider playing scenarios written in the
/// test (PLAN.md 6.8), on a fake clock. The spy sees what the provider is sent, after the tool loop and the pipeline.
/// </summary>
internal sealed class ReplyRig : IDisposable
{
    /// <summary>The ticket number every scenario written with <see cref="Scenario"/> is keyed to.</summary>
    public const string TicketNumber = "T-9001";

    private readonly CapturingLoggerProvider _logs = new();
    private readonly ILoggerFactory _loggerFactory;
    private readonly TestPipeline _pipeline;

    private ReplyRig(FakeTimeProvider time, SpyChatClient spy, IRetrievalService retrieval, StubOrders orders, RecordingFollowUps followUps, Action<AiOptions>? configure)
    {
        Time = time;
        Spy = spy;
        Orders = orders;
        FollowUps = followUps;
        _loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(_logs));

        void Configure(AiOptions options)
        {
            options.Prompts["reply"] = "reply.v2";
            // Backoff stays short, so a retry never needs more than a few steps of the fake clock
            options.Retry.BaseDelayMs = 100;
            options.Retry.MaxDelayMs = 5000;
            configure?.Invoke(options);
        }

        _pipeline = TestPipeline.Create(spy, time, new FakeErrorClassifier(), Configure);
        var settings = Options.Create(TestPipeline.Settings(Configure));
        Service = new ReplyService(
            _pipeline.Client,
            retrieval,
            new ReplyToolFactory(orders, followUps),
            new PromptStore(settings),
            new PriceTable(settings, NullLogger<PriceTable>.Instance),
            settings,
            Options.Create(new RetrievalOptions { ReplyChunks = 10 }),
            _loggerFactory.CreateLogger<ReplyService>());
    }

    public FakeTimeProvider Time { get; }

    public SpyChatClient Spy { get; }

    public StubOrders Orders { get; }

    public RecordingFollowUps FollowUps { get; }

    public ReplyService Service { get; }

    /// <summary>One <see cref="ModelCall"/> row per call that went through the pipeline's usage logging.</summary>
    public IReadOnlyList<ModelCall> Rows => _pipeline.Rows.All;

    public IReadOnlyList<string> Logs => _logs.Messages;

    /// <summary>Builds a rig whose provider plays <paramref name="turns"/> for <see cref="TicketNumber"/>'s reply.</summary>
    public static ReplyRig Create(
        string turns, IRetrievalService? retrieval = null, Action<AiOptions>? configure = null, StubOrders? orders = null, RecordingFollowUps? followUps = null)
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 1, 9, 0, 0, TimeSpan.Zero));
        var fake = new FakeChatClient(FakeScenarioSet.Parse(Scenario(turns)), time);
        return new ReplyRig(time, new SpyChatClient(fake, time), retrieval ?? new StubRetrieval(), orders ?? new StubOrders(), followUps ?? new RecordingFollowUps(), configure);
    }

    /// <summary>
    /// A scenarios file: a default scenario (so the file is valid) and one for <see cref="TicketNumber"/> whose reply plays
    /// <paramref name="turns"/>, a comma-separated list of turns made with <see cref="Turn"/>.
    /// </summary>
    public static string Scenario(string turns) => $$"""
        {
          "scenarios": [
            { "name": "default", "ticketNumber": null, "turns": { "*": [ { "steps": [ { "type": "text", "text": "Default.", "chunkSize": 20, "delayMs": 0 } ] } ] } },
            { "name": "adversarial", "ticketNumber": "{{TicketNumber}}", "turns": { "Reply": [ {{turns}} ] } }
          ]
        }
        """;

    public static string Turns(params string[] turns) => string.Join(", ", turns);

    public static string Turn(params string[] steps) => $$"""{ "steps": [ {{string.Join(", ", steps)}} ] }""";

    public static string Text(string text, int delayMs = 0) =>
        $$"""{ "type": "text", "text": {{JsonSerializer.Serialize(text)}}, "chunkSize": 12, "delayMs": {{delayMs}} }""";

    public static string Usage(int input = 500, int output = 40) => $$"""{ "type": "usage", "inputTokens": {{input}}, "outputTokens": {{output}} }""";

    public static string Error(int status, double? retryAfterSeconds = null) => retryAfterSeconds is { } seconds
        ? $$"""{ "type": "error", "statusCode": {{status}}, "retryAfterSeconds": {{seconds.ToString(CultureInfo.InvariantCulture)}} }"""
        : $$"""{ "type": "error", "statusCode": {{status}} }""";

    public static string Stall(double seconds) => $$"""{ "type": "stall", "seconds": {{seconds.ToString(CultureInfo.InvariantCulture)}} }""";

    public static string Reset() => """{ "type": "reset" }""";

    public static string Finish(string reason) => $$"""{ "type": "finish", "reason": "{{reason}}" }""";

    /// <summary>A call to <paramref name="name"/>; <paramref name="arguments"/> is a JSON object.</summary>
    public static string ToolCall(string name, string arguments) => $$"""{ "type": "tool_call", "name": {{JsonSerializer.Serialize(name)}}, "arguments": {{arguments}} }""";

    public static string Propose(string title, string notes = "", int? dueInDays = null) =>
        ToolCall(ReplyTools.ProposeFollowUpTask, JsonSerializer.Serialize(dueInDays is { } days ? new { title, notes, dueInDays = days } : (object)new { title, notes }));

    public static string Lookup(string orderNumber) =>
        ToolCall(ReplyTools.LookupOrderStatus, JsonSerializer.Serialize(new { orderNumber }));

    public static Ticket Ticket(string subject = "Where is my parcel?", string body = "It has not arrived yet.", int id = 7) =>
        ReplyTestData.Ticket(TicketNumber, subject, body, id);

    /// <summary>Reads the stream to its end, moving the fake clock on while the stream waits.</summary>
    public async Task<List<ReplyEvent>> RunAsync(Ticket ticket, TimeSpan? step = null, CancellationToken cancellationToken = default)
    {
        var events = new List<ReplyEvent>();
        await Time.RunAsync(ReadAsync(ticket, events, cancellationToken), step ?? TimeSpan.FromMilliseconds(250));
        return events;
    }

    public async Task ReadAsync(Ticket ticket, List<ReplyEvent> into, CancellationToken cancellationToken = default)
    {
        await foreach (var reply in Service.StreamAsync(ticket, cancellationToken))
        {
            lock (into)
            {
                into.Add(reply);
            }
        }
    }

    public void Dispose()
    {
        _pipeline.Dispose();
        _loggerFactory.Dispose();
        _logs.Dispose();
    }
}

/// <summary>What the provider was sent in one call, and when (on the fake clock).</summary>
internal sealed record SpyCall(DateTimeOffset At, IReadOnlyList<ChatMessage> Messages, IReadOnlyList<string> ToolNames)
{
    public ChatMessage System => Messages.Single(m => m.Role == ChatRole.System);

    public ChatMessage User => Messages.Single(m => m.Role == ChatRole.User);
}

/// <summary>Records every call the provider is sent, and whether the call's token was cancelled when its stream ended.</summary>
internal sealed class SpyChatClient(IChatClient inner, TimeProvider time) : DelegatingChatClient(inner)
{
    private readonly List<SpyCall> _calls = [];
    private int _cancelled;

    public IReadOnlyList<SpyCall> Calls
    {
        get
        {
            lock (_calls)
            {
                return [.. _calls];
            }
        }
    }

    /// <summary>How many calls ended with their token cancelled.</summary>
    public int CancelledCalls => Volatile.Read(ref _cancelled);

    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        lock (_calls)
        {
            _calls.Add(new SpyCall(time.GetUtcNow(), [.. messages], [.. options?.Tools?.Select(t => t.Name) ?? []]));
        }

        return Watch(base.GetStreamingResponseAsync(messages, options, cancellationToken), cancellationToken);
    }

    private async IAsyncEnumerable<ChatResponseUpdate> Watch(IAsyncEnumerable<ChatResponseUpdate> updates, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var update in updates.WithCancellation(cancellationToken))
            {
                yield return update;
            }
        }
        finally
        {
            if (cancellationToken.IsCancellationRequested)
            {
                Interlocked.Increment(ref _cancelled);
            }
        }
    }
}

/// <summary>The checks every ending of a reply stream has to pass.</summary>
internal static class StreamAssert
{
    /// <summary>Exactly one terminal event, and it's last (PLAN.md 7): <see cref="ReplyDone"/> after citations and usage, or one <see cref="ReplyError"/> with neither.</summary>
    public static ReplyEvent EndsOnce(IReadOnlyList<ReplyEvent> events)
    {
        Assert.NotEmpty(events);
        var terminal = events.Where(e => e is ReplyError or ReplyDone).ToList();
        var last = Assert.Single(terminal);
        Assert.Same(events[^1], last);
        if (last is ReplyError)
        {
            Assert.DoesNotContain(events, e => e is ReplyCitations or ReplyUsage);
        }
        else
        {
            Assert.Equal([typeof(ReplyCitations), typeof(ReplyUsage), typeof(ReplyDone)], events.Skip(events.Count - 3).Select(e => e.GetType()));
        }

        return last;
    }

    public static ReplyError Error(IReadOnlyList<ReplyEvent> events, string code)
    {
        var error = Assert.IsType<ReplyError>(EndsOnce(events));
        Assert.Equal(code, error.Code);
        // Fixed text only: never the provider's message
        Assert.False(string.IsNullOrWhiteSpace(error.Message));
        return error;
    }
}
