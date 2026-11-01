using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using SupportDesk.Core.AI.Usage;

namespace SupportDesk.Core.AI;

/// <summary>
/// Writes one <see cref="ModelCall"/> per call (PLAN.md 6.6), above the retries, so a retried call is one row. The row
/// goes to <see cref="IModelCallSink"/>, which never waits, so logging can't slow or break a stream. A stream the caller
/// stops reading counts as cancelled. Without usage from the provider the tokens are estimated (<c>IsEstimated</c>).
/// Prompt and ticket text are never logged.
/// </summary>
public sealed partial class UsageLoggingMiddleware(
    IChatClient innerClient, IModelCallSink sink, PriceTable prices, TimeProvider timeProvider, ILogger<UsageLoggingMiddleware> logger)
    : DelegatingChatClient(innerClient)
{
    public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var call = Start(messages, options);
        try
        {
            var response = await base.GetResponseAsync(call.Messages, options, cancellationToken);
            call.Outcome = ModelCallOutcome.Success;
            call.Usage = response.Usage;
            call.OutputCharacters = TokenEstimator.Characters(response.Messages.SelectMany(m => m.Contents));
            return response;
        }
        catch (Exception exception) when (call.Failed(exception, cancellationToken))
        {
            // Failed() records the outcome and returns false, so the exception goes on unchanged
            throw;
        }
        finally
        {
            Write(call);
        }
    }

    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        LogStreamAsync(Start(messages, options), cancellationToken);

    private async IAsyncEnumerable<ChatResponseUpdate> LogStreamAsync(Call call, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var tokens = new StreamTokens();
        var updates = base.GetStreamingResponseAsync(call.Messages, call.Options, cancellationToken).GetAsyncEnumerator(cancellationToken);
        try
        {
            while (true)
            {
                try
                {
                    if (!await updates.MoveNextAsync())
                    {
                        call.Outcome = ModelCallOutcome.Success;
                        break;
                    }
                }
                catch (Exception exception) when (call.Failed(exception, cancellationToken))
                {
                    throw;
                }

                tokens.Observe(updates.Current);
                yield return updates.Current;
            }
        }
        finally
        {
            await updates.DisposeAsync();
            call.Usage ??= tokens.Usage;
            call.OutputCharacters = tokens.OutputCharacters;
            Write(call);
        }
    }

    private Call Start(IEnumerable<ChatMessage> messages, ChatOptions? options)
    {
        ArgumentNullException.ThrowIfNull(messages);

        var context = AiCallContext.From(options)
            ?? throw new InvalidOperationException("Every model call needs an AiCallContext (feature and prompt version); set it with AiCallContext.ApplyTo.");
        return new Call(context, messages as IReadOnlyList<ChatMessage> ?? [.. messages], options, timeProvider.GetUtcNow(), timeProvider.GetTimestamp());
    }

    private void Write(Call call)
    {
        var tokens = CallTokens.Resolve(call.Usage, call.Messages, call.Options, call.OutputCharacters);
        var metadata = InnerClient.GetService<ChatClientMetadata>();
        var model = call.Options?.ModelId ?? metadata?.DefaultModelId ?? "unknown";

        var row = new ModelCall
        {
            Timestamp = call.StartedAt,
            Feature = call.Context.Feature,
            Provider = Truncate(metadata?.ProviderName ?? "unknown", ModelCall.ProviderMaxLength),
            Model = Truncate(model, ModelCall.ModelMaxLength),
            PromptVersion = Truncate(call.Context.PromptVersion, ModelCall.PromptVersionMaxLength),
            InputTokens = tokens.Input,
            OutputTokens = tokens.Output,
            CostUsd = prices.Cost(model, tokens.Input, tokens.Output),
            LatencyMs = (long)timeProvider.GetElapsedTime(call.StartedTimestamp).TotalMilliseconds,
            IsEstimated = tokens.IsEstimated,
            Outcome = call.Outcome,
            TicketId = call.Context.TicketId,
            ReplyRunId = call.Context.ReplyRunId,
        };

        if (call.Error is { } error)
        {
            // The type and status only: an SDK's message can echo the request, which holds ticket text
            LogFailed(call.Context.Feature, call.Context.TicketId, error.GetType().Name, (error as ModelUnavailableException)?.StatusCode);
        }

        sink.Add(row);
    }

    private static string Truncate(string value, int maxLength) => value.Length <= maxLength ? value : value[..maxLength];

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Feature} model call for ticket {TicketId} failed: {ErrorType} (status {StatusCode})")]
    private partial void LogFailed(AiFeature feature, int? ticketId, string errorType, int? statusCode);

    private sealed class Call(AiCallContext context, IReadOnlyList<ChatMessage> messages, ChatOptions? options, DateTimeOffset startedAt, long startedTimestamp)
    {
        public AiCallContext Context { get; } = context;

        public IReadOnlyList<ChatMessage> Messages { get; } = messages;

        public ChatOptions? Options { get; } = options;

        public DateTimeOffset StartedAt { get; } = startedAt;

        public long StartedTimestamp { get; } = startedTimestamp;

        /// <summary>Until the call ends one way or the other, a stream that's dropped half-read counts as cancelled.</summary>
        public ModelCallOutcome Outcome { get; set; } = ModelCallOutcome.Cancelled;

        public UsageDetails? Usage { get; set; }

        public long OutputCharacters { get; set; }

        public Exception? Error { get; private set; }

        /// <summary>Records how the call failed; always false, for use in an exception filter.</summary>
        public bool Failed(Exception exception, CancellationToken cancellationToken)
        {
            if (exception is OperationCanceledException && cancellationToken.IsCancellationRequested)
            {
                Outcome = ModelCallOutcome.Cancelled;
                return false;
            }

            Outcome = ModelCallOutcome.Error;
            Error = exception;
            if (exception is ModelRefusedException { Usage: { } usage })
            {
                Usage = usage;
            }

            return false;
        }
    }
}
