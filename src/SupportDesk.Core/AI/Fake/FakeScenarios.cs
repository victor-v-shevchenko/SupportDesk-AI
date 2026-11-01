using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;

namespace SupportDesk.Core.AI.Fake;

/// <summary>
/// One scripted conversation. <see cref="Turns"/> maps a feature name (<c>Reply</c>, <c>Summary</c>, <c>Classification</c>,
/// or <c>*</c> for any) to one turn per model call: the first call plays turn 0, the call after a tool round trip (or a
/// retry that sends the earlier answer back) plays turn 1, and the last turn repeats once they run out.
/// </summary>
public sealed record FakeScenario(string Name, string? TicketNumber, IReadOnlyDictionary<string, IReadOnlyList<FakeTurn>> Turns);

public sealed record FakeTurn(IReadOnlyList<FakeStep> Steps);

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type", UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FailSerialization)]
[JsonDerivedType(typeof(FakeTextStep), "text")]
[JsonDerivedType(typeof(FakeToolCallStep), "tool_call")]
[JsonDerivedType(typeof(FakeUsageStep), "usage")]
[JsonDerivedType(typeof(FakeErrorStep), "error")]
[JsonDerivedType(typeof(FakeStallStep), "stall")]
[JsonDerivedType(typeof(FakeFinishStep), "finish")]
public abstract record FakeStep;

/// <summary>Streams <see cref="Text"/> in chunks of <see cref="ChunkSize"/> characters, waiting <see cref="DelayMs"/> before each.</summary>
public sealed record FakeTextStep(string Text, int ChunkSize = 6, int DelayMs = 30) : FakeStep;

/// <summary>A call to the tool <see cref="Name"/>; <see cref="Arguments"/> is a JSON object, passed on as the model would send it.</summary>
public sealed record FakeToolCallStep(string Name, JsonElement Arguments) : FakeStep;

public sealed record FakeUsageStep(long InputTokens, long OutputTokens) : FakeStep;

/// <summary>Fails the call with this HTTP status. Placed first it's an error before the first item; after a text step, after it.</summary>
public sealed record FakeErrorStep(int StatusCode, string? Message = null) : FakeStep;

/// <summary>Sends nothing for <see cref="Seconds"/> (on the provider's clock), then goes on with the next step.</summary>
public sealed record FakeStallStep(double Seconds) : FakeStep;

/// <summary>The finish reason on the last update: <c>stop</c>, <c>length</c>, <c>tool_calls</c> or <c>content_filter</c> (a refusal).</summary>
public sealed record FakeFinishStep(string Reason) : FakeStep;

/// <summary>The scenarios from one file, checked when loaded (PLAN.md 6.8).</summary>
public sealed class FakeScenarioSet
{
    public const string DefaultScenario = "default";
    public const string AnyFeature = "*";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        AllowOutOfOrderMetadataProperties = true,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    private static readonly HashSet<string> FinishReasons = new(StringComparer.Ordinal) { "stop", "length", "tool_calls", "content_filter" };

    private readonly FakeScenario _default;
    private readonly Dictionary<string, FakeScenario> _byTicketNumber;

    private FakeScenarioSet(IReadOnlyList<FakeScenario> scenarios)
    {
        Scenarios = scenarios;
        _default = scenarios.Single(s => s.Name == DefaultScenario);
        _byTicketNumber = scenarios.Where(s => s.TicketNumber is not null).ToDictionary(s => s.TicketNumber!, StringComparer.Ordinal);
    }

    public IReadOnlyList<FakeScenario> Scenarios { get; }

    public static FakeScenarioSet Load(string path)
    {
        var text = File.ReadAllText(path);
        return Parse(text, path);
    }

    public static FakeScenarioSet Parse(string json, string source = "fake scenarios")
    {
        var file = JsonSerializer.Deserialize<ScenarioFile>(json, JsonOptions) ?? throw new InvalidDataException($"{source} is empty.");
        Check(file.Scenarios, source);
        return new FakeScenarioSet(file.Scenarios);
    }

    /// <summary>The scenario for <paramref name="ticketNumber"/>, or the default one.</summary>
    public FakeScenario ScenarioFor(string? ticketNumber) =>
        ticketNumber is not null && _byTicketNumber.TryGetValue(ticketNumber, out var scenario) ? scenario : _default;

    /// <summary>
    /// The turn to play. A scenario that doesn't script <paramref name="feature"/> (directly or with <c>*</c>) falls back to
    /// the default scenario, so a ticket set up for a reply error still gets a normal summary.
    /// </summary>
    public FakeTurn TurnFor(string? ticketNumber, AiFeature? feature, int turnIndex)
    {
        var turns = TurnsFor(ScenarioFor(ticketNumber), feature) ?? TurnsFor(_default, feature)
            ?? throw new InvalidOperationException($"The default fake scenario has no turns for {feature?.ToString() ?? "calls without a feature"}.");
        return turns[Math.Min(turnIndex, turns.Count - 1)];
    }

    private static IReadOnlyList<FakeTurn>? TurnsFor(FakeScenario scenario, AiFeature? feature) =>
        feature is { } f && scenario.Turns.TryGetValue(f.ToString(), out var turns) ? turns
        : scenario.Turns.TryGetValue(AnyFeature, out var any) ? any
        : null;

    private static void Check(IReadOnlyList<FakeScenario> scenarios, string source)
    {
        var errors = new List<string>();
        foreach (var duplicate in scenarios.GroupBy(s => s.Name, StringComparer.Ordinal).Where(g => g.Count() > 1))
        {
            errors.Add($"scenario '{duplicate.Key}' appears more than once");
        }

        foreach (var duplicate in scenarios.Where(s => s.TicketNumber is not null).GroupBy(s => s.TicketNumber, StringComparer.Ordinal).Where(g => g.Count() > 1))
        {
            errors.Add($"ticket {duplicate.Key} has more than one scenario");
        }

        var defaultScenario = scenarios.FirstOrDefault(s => s.Name == DefaultScenario);
        if (defaultScenario is null)
        {
            errors.Add($"there is no '{DefaultScenario}' scenario");
        }
        else if (defaultScenario.TicketNumber is not null)
        {
            errors.Add($"the '{DefaultScenario}' scenario can't have a ticket number");
        }
        else if (defaultScenario.Turns.ContainsKey(AnyFeature) is false
            && Enum.GetNames<AiFeature>().FirstOrDefault(f => !defaultScenario.Turns.ContainsKey(f)) is { } missing)
        {
            errors.Add($"the '{DefaultScenario}' scenario has no turns for {missing}");
        }

        foreach (var scenario in scenarios)
        {
            if (scenario.Name != DefaultScenario && scenario.TicketNumber is null)
            {
                errors.Add($"scenario '{scenario.Name}' has no ticket number");
            }

            foreach (var (feature, turns) in scenario.Turns)
            {
                var where = $"scenario '{scenario.Name}', {feature}";
                if (feature != AnyFeature && !Enum.TryParse<AiFeature>(feature, ignoreCase: false, out _))
                {
                    errors.Add($"{where}: unknown feature; use {string.Join(", ", Enum.GetNames<AiFeature>())} or {AnyFeature}");
                }

                if (turns.Count == 0)
                {
                    errors.Add($"{where}: no turns");
                }

                for (var i = 0; i < turns.Count; i++)
                {
                    errors.AddRange(turns[i].Steps.Select(StepError).OfType<string>().Select(e => $"{where}, turn {i}: {e}"));
                }
            }
        }

        if (errors.Count > 0)
        {
            throw new InvalidDataException($"{source}: {string.Join("; ", errors)}.");
        }
    }

    private static string? StepError(FakeStep step) => step switch
    {
        FakeTextStep { Text.Length: 0 } => "text is empty",
        FakeTextStep { ChunkSize: < 1 } => "chunkSize must be at least 1",
        FakeTextStep { DelayMs: < 0 } => "delayMs can't be negative",
        FakeToolCallStep { Name.Length: 0 } => "tool_call has no name",
        FakeToolCallStep { Arguments.ValueKind: not JsonValueKind.Object } => "tool_call arguments must be a JSON object",
        FakeUsageStep { InputTokens: < 0 } or FakeUsageStep { OutputTokens: < 0 } => "usage can't be negative",
        FakeErrorStep { StatusCode: < 400 or > 599 } => "error statusCode must be 400–599",
        FakeStallStep { Seconds: <= 0 } => "stall seconds must be positive",
        FakeFinishStep f when !FinishReasons.Contains(f.Reason) => $"finish reason must be one of {string.Join(", ", FinishReasons)}",
        _ => null,
    };

    internal static ChatFinishReason ToFinishReason(string reason) => reason switch
    {
        "length" => ChatFinishReason.Length,
        "tool_calls" => ChatFinishReason.ToolCalls,
        "content_filter" => ChatFinishReason.ContentFilter,
        _ => ChatFinishReason.Stop,
    };

    private sealed record ScenarioFile(IReadOnlyList<FakeScenario> Scenarios);
}
