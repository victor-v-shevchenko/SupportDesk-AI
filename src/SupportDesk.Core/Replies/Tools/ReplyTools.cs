using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.AI;
using SupportDesk.Core.FollowUps;
using SupportDesk.Core.Orders;
using SupportDesk.Core.Tickets;

namespace SupportDesk.Core.Replies.Tools;

/// <summary>Creates the tools of one reply run (PLAN.md 10). Scoped, like the repository the proposal tool saves through.</summary>
public sealed class ReplyToolFactory(IOrderStatusService orders, IFollowUpRepository followUps)
{
    public ReplyTools Create(Ticket ticket, Guid replyRunId)
    {
        ArgumentNullException.ThrowIfNull(ticket);

        return new ReplyTools(orders, followUps, ticket.Id, replyRunId);
    }
}

/// <summary>
/// The two tools a reply may call, bound to one ticket and reply run, and the <c>tool_call</c> events of the calls that ran.
/// <see cref="FunctionInvokingChatClient"/> runs a tool and passes its result on to the caller as a
/// <see cref="FunctionResultContent"/>; the reply looks up the event for that call with <see cref="Take"/>. A call that
/// failed validation, or named another tool, has no event.
/// </summary>
public sealed class ReplyTools
{
    public const string LookupOrderStatus = "lookup_order_status";
    public const string ProposeFollowUpTask = "propose_follow_up_task";

    private readonly ConcurrentDictionary<string, ReplyToolCall> _events = new(StringComparer.Ordinal);

    internal ReplyTools(IOrderStatusService orders, IFollowUpRepository followUps, int ticketId, Guid replyRunId)
    {
        Tools = [new LookupOrderStatusFunction(orders, this), new ProposeFollowUpTaskFunction(followUps, ticketId, replyRunId, this)];
    }

    /// <summary>Only these run: the tool loop answers a call to any other name with an error result.</summary>
    public IReadOnlyList<AITool> Tools { get; }

    /// <summary>The event for the call <paramref name="callId"/>, once; null when it has none.</summary>
    public ReplyToolCall? Take(string callId) => _events.TryRemove(callId, out var call) ? call : null;

    /// <summary>
    /// Keyed by the call the tool loop is running. Without one (the tool was invoked outside the loop) there's no call to
    /// match a result to, so there's no event.
    /// </summary>
    internal void Record(ReplyToolCall call)
    {
        if (FunctionInvokingChatClient.CurrentContext?.CallContent.CallId is { Length: > 0 } callId)
        {
            _events[callId] = call;
        }
    }
}

/// <summary>
/// A tool with a hand-written JSON schema. The arguments are checked before the tool runs; anything wrong goes back to
/// the model as an error result rather than an exception. Every result is wrapped by <see cref="ToolResult"/>.
/// </summary>
internal abstract class ReplyFunction(string name, string description, string schema) : AIFunction
{
    private readonly JsonElement _schema = JsonDocument.Parse(schema).RootElement.Clone();

    public override string Name => name;

    public override string Description => description;

    public override JsonElement JsonSchema => _schema;

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        var reader = new ToolArguments(arguments);
        var run = Prepare(reader);
        var unknown = reader.Unknown();
        if (unknown is not null)
        {
            reader.Errors.Add(unknown);
        }

        return reader.Errors.Count > 0 || run is null
            ? ToolResult.Error(reader.Errors)
            : ToolResult.Wrap(await run(cancellationToken));
    }

    /// <summary>Reads and checks the arguments; returns the tool's work, or null when <see cref="ToolArguments.Errors"/> has errors.</summary>
    protected abstract Func<CancellationToken, Task<object>>? Prepare(ToolArguments arguments);
}

internal sealed class LookupOrderStatusFunction(IOrderStatusService orders, ReplyTools owner) : ReplyFunction(
    ReplyTools.LookupOrderStatus,
    "Looks up the shipping status of a Kestrel Outfitters order: status, carrier, last update and last tracking event. Returns found: false when there's no such order.",
    """
    {
      "type": "object",
      "properties": {
        "orderNumber": { "type": "string", "pattern": "^KO-[0-9]{6}$", "description": "The order number: KO- followed by six digits, for example KO-204118." }
      },
      "required": ["orderNumber"],
      "additionalProperties": false
    }
    """)
{
    private const string OrderNumberArgument = "orderNumber";

    protected override Func<CancellationToken, Task<object>>? Prepare(ToolArguments arguments)
    {
        var orderNumber = arguments.RequiredString(OrderNumberArgument);
        if (orderNumber is not null && !OrderNumber.IsValid(orderNumber))
        {
            arguments.Errors.Add($"{OrderNumberArgument} must be KO- followed by six digits, for example KO-204118.");
            return null;
        }

        return orderNumber is null ? null : async cancellationToken =>
        {
            var order = await orders.FindAsync(orderNumber, cancellationToken);
            object result = order is null
                ? new OrderNotFound()
                : new OrderFound(order.OrderNumber, order.Status, order.Carrier, order.LastUpdate, order.LastEvent);
            owner.Record(new ReplyOrderLookup(new OrderLookupArguments(orderNumber), result));
            return result;
        };
    }
}

internal sealed class ProposeFollowUpTaskFunction(IFollowUpRepository followUps, int ticketId, Guid replyRunId, ReplyTools owner) : ReplyFunction(
    ReplyTools.ProposeFollowUpTask,
    "Proposes a follow-up task for a support agent, such as a carrier trace or a callback. It doesn't create the task: an agent confirms it later. Calling it again in the same reply replaces the earlier proposal.",
    $$"""
    {
      "type": "object",
      "properties": {
        "title": { "type": "string", "minLength": 1, "maxLength": {{FollowUpProposal.TitleMaxLength}}, "description": "What the agent should do, in a few words." },
        "notes": { "type": "string", "maxLength": {{FollowUpProposal.NotesMaxLength}}, "description": "Why, and anything the agent needs to know." },
        "dueInDays": { "type": "integer", "minimum": {{FollowUpRules.MinDueInDays}}, "maximum": {{FollowUpRules.MaxDueInDays}}, "description": "In how many days the task is due." }
      },
      "required": ["title"],
      "additionalProperties": false
    }
    """)
{
    public const string AwaitingConfirmation = "awaiting_user_confirmation";

    protected override Func<CancellationToken, Task<object>>? Prepare(ToolArguments arguments)
    {
        var title = arguments.RequiredString("title", FollowUpProposal.TitleMaxLength);
        var notes = arguments.OptionalString("notes", FollowUpProposal.NotesMaxLength) ?? "";
        var dueInDays = arguments.OptionalInteger("dueInDays", FollowUpRules.MinDueInDays, FollowUpRules.MaxDueInDays);

        return title is null || arguments.Errors.Count > 0 ? null : async cancellationToken =>
        {
            var proposal = await followUps.UpsertProposalAsync(new NewProposal(ticketId, replyRunId, title, notes, dueInDays), cancellationToken);
            owner.Record(new ReplyProposal(new ProposalPayload(proposal.Id, proposal.Title, proposal.Notes, proposal.DueDate, proposal.Status)));
            return new ProposalResult(StatusText(proposal.Status), proposal.Id);
        };
    }

    /// <summary>Pending is what the model sees almost always; a proposal confirmed or expired before a retry in the same run says so.</summary>
    private static string StatusText(ProposalStatus status) => status switch
    {
        ProposalStatus.Pending => AwaitingConfirmation,
        ProposalStatus.Confirmed => "already_confirmed",
        _ => "expired",
    };
}

/// <summary>
/// Reads tool arguments as the adapters pass them: <see cref="JsonElement"/>s mostly, or plain .NET values. Collects one
/// error per bad argument. The errors name the argument and the rule but never repeat the value, which the model may
/// have copied from the ticket.
/// </summary>
internal sealed class ToolArguments(AIFunctionArguments arguments)
{
    private readonly HashSet<string> _read = new(StringComparer.Ordinal);

    public List<string> Errors { get; } = [];

    public string? RequiredString(string name, int? maxLength = null)
    {
        var value = OptionalString(name, maxLength, required: true);
        if (value is { Length: 0 })
        {
            Errors.Add($"{name} can't be empty.");
            return null;
        }

        return value;
    }

    /// <summary>Trimmed; null when it's missing, JSON null, or wrong.</summary>
    public string? OptionalString(string name, int? maxLength = null) => OptionalString(name, maxLength, required: false);

    public int? OptionalInteger(string name, int min, int max)
    {
        if (!TryGet(name, out var value))
        {
            return null;
        }

        long? number = value switch
        {
            JsonElement { ValueKind: JsonValueKind.Number } e when e.TryGetInt64(out var n) => n,
            int n => n,
            long n => n,
            _ => null,
        };
        if (number is null || number < min || number > max)
        {
            Errors.Add($"{name} must be a whole number from {min} to {max}.");
            return null;
        }

        return (int)number;
    }

    /// <summary>The first argument the tool doesn't take, as an error; the name isn't repeated, since the model made it up.</summary>
    public string? Unknown() =>
        arguments.Keys.Any(k => !_read.Contains(k)) ? $"Only these arguments are allowed: {string.Join(", ", _read.Order(StringComparer.Ordinal))}." : null;

    private string? OptionalString(string name, int? maxLength, bool required)
    {
        if (!TryGet(name, out var value))
        {
            if (required)
            {
                Errors.Add($"{name} is required.");
            }

            return null;
        }

        var text = value switch
        {
            JsonElement { ValueKind: JsonValueKind.String } e => e.GetString(),
            string s => s,
            _ => null,
        };
        if (text is null)
        {
            Errors.Add($"{name} must be a string.");
            return null;
        }

        text = text.Trim();
        if (maxLength is { } max && text.Length > max)
        {
            Errors.Add($"{name} must be at most {max} characters.");
            return null;
        }

        return text;
    }

    /// <summary>A missing argument and an explicit JSON null are the same: not given.</summary>
    private bool TryGet(string name, out object? value)
    {
        _read.Add(name);
        if (arguments.TryGetValue(name, out value) && value is not null and not JsonElement { ValueKind: JsonValueKind.Null })
        {
            return true;
        }

        value = null;
        return false;
    }
}

/// <summary>
/// How a tool result reaches the model: JSON inside <c>&lt;tool_result&gt;…&lt;/tool_result&gt;</c>, which the reply prompt
/// says is data from the store's systems, not instructions. The serializer's default encoder writes <c>&lt;</c> and
/// <c>&gt;</c> as <c><</c> and <c>></c>, so nothing in a value can close the tag.
/// </summary>
public static class ToolResult
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static string Wrap(object payload) => $"<tool_result>\n{JsonSerializer.Serialize(payload, JsonOptions)}\n</tool_result>";

    public static string Error(IReadOnlyList<string> errors) =>
        Wrap(new ToolError(errors.Count > 0 ? string.Join(" ", errors) : "The arguments are invalid."));

    private sealed record ToolError(string Error);
}

/// <summary>What <c>propose_follow_up_task</c> tells the model.</summary>
public sealed record ProposalResult(string Status, Guid ProposalId);
