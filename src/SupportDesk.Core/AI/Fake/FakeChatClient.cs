using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace SupportDesk.Core.AI.Fake;

/// <summary>
/// A provider that plays scripted scenarios (PLAN.md 6.8), picked by the ticket number in <see cref="AiCallContext"/>.
/// It behaves like a real adapter where tests depend on it: text arrives in chunks over time, tool calls come as
/// <see cref="FunctionCallContent"/>, usage comes on the last update, errors are <see cref="FakeProviderException"/>s
/// (an <see cref="HttpRequestException"/> with a status code and maybe <c>Retry-After</c>), a refusal is <see cref="ChatFinishReason.ContentFilter"/>, and every wait honors cancellation.
/// </summary>
public sealed class FakeChatClient(FakeScenarioSet scenarios, TimeProvider timeProvider) : IChatClient
{
    public const string ProviderName = "fake";
    public const string ModelId = "fake";

    private readonly ChatClientMetadata _metadata = new(ProviderName, defaultModelId: ModelId);

    public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        await GetStreamingResponseAsync(messages, options, cancellationToken).ToChatResponseAsync(cancellationToken);

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        // Checked here rather than in the iterator, so a null throws when called, not on the first MoveNextAsync
        ArgumentNullException.ThrowIfNull(messages);

        return PlayAsync(messages, options, cancellationToken);
    }

    private async IAsyncEnumerable<ChatResponseUpdate> PlayAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var context = AiCallContext.From(options);
        // Each tool round trip (or retry) adds the model's earlier answer to the history, which moves on to the next turn
        var turnIndex = messages.Count(m => m.Role == ChatRole.Assistant);
        var turn = scenarios.TurnFor(context?.TicketNumber, context?.Feature, turnIndex);

        var responseId = "fake-" + Guid.NewGuid().ToString("N");
        var messageId = "msg-" + Guid.NewGuid().ToString("N");
        var toolCalls = 0;
        ChatFinishReason? finishReason = null;
        UsageDetails? usage = null;

        foreach (var step in turn.Steps)
        {
            switch (step)
            {
                case FakeTextStep text:
                    for (var start = 0; start < text.Text.Length; start += text.ChunkSize)
                    {
                        await DelayAsync(TimeSpan.FromMilliseconds(text.DelayMs), cancellationToken);
                        var chunk = text.Text.Substring(start, Math.Min(text.ChunkSize, text.Text.Length - start));
                        yield return Update(responseId, messageId, new TextContent(chunk));
                    }

                    break;
                case FakeToolCallStep call:
                    toolCalls++;
                    var callId = string.Create(CultureInfo.InvariantCulture, $"call-{turnIndex}-{toolCalls}");
                    yield return Update(responseId, messageId, new FunctionCallContent(callId, call.Name, ToArguments(call.Arguments)));
                    break;
                case FakeUsageStep u:
                    usage = new UsageDetails { InputTokenCount = u.InputTokens, OutputTokenCount = u.OutputTokens, TotalTokenCount = u.InputTokens + u.OutputTokens };
                    break;
                case FakeErrorStep error:
                    throw new FakeProviderException(
                        error.Message ?? string.Create(CultureInfo.InvariantCulture, $"Fake provider error {error.StatusCode}."),
                        (HttpStatusCode)error.StatusCode,
                        error.RetryAfterSeconds is { } seconds ? TimeSpan.FromSeconds(seconds) : null);
                case FakeStallStep stall:
                    await DelayAsync(TimeSpan.FromSeconds(stall.Seconds), cancellationToken);
                    break;
                case FakeFinishStep finish:
                    finishReason = FakeScenarioSet.ToFinishReason(finish.Reason);
                    break;
            }
        }

        // Like the real adapters: the finish reason and usage come together on a last update without text
        var last = Update(responseId, messageId);
        last.FinishReason = finishReason ?? (toolCalls > 0 ? ChatFinishReason.ToolCalls : ChatFinishReason.Stop);
        if (usage is not null)
        {
            last.Contents.Add(new UsageContent(usage));
        }

        yield return last;
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);

        if (serviceKey is not null)
        {
            return null;
        }

        if (serviceType == typeof(ChatClientMetadata))
        {
            return _metadata;
        }

        return serviceType.IsInstanceOfType(this) ? this : null;
    }

    public void Dispose()
    {
        // Nothing to release
    }

    private Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) =>
        delay > TimeSpan.Zero ? Task.Delay(delay, timeProvider, cancellationToken) : Task.CompletedTask;

    private ChatResponseUpdate Update(string responseId, string messageId, params AIContent[] contents) => new(ChatRole.Assistant, [.. contents])
    {
        ResponseId = responseId,
        MessageId = messageId,
        ModelId = ModelId,
        CreatedAt = timeProvider.GetUtcNow(),
    };

    private static Dictionary<string, object?> ToArguments(JsonElement arguments) =>
        arguments.EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value.Clone(), StringComparer.Ordinal);
}
