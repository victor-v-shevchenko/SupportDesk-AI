namespace SupportDesk.Core.AI;

/// <summary>The feature a model call belongs to; also the <c>Feature</c> of a <c>ModelCall</c> row (PLAN.md 4).</summary>
public enum AiFeature
{
    Reply,
    Summary,
    Classification,
}
