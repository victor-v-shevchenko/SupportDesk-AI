using SupportDesk.Core.Telemetry;

namespace SupportDesk.Core.FollowUps;

public interface IFollowUpService
{
    /// <inheritdoc cref="IFollowUpRepository.ConfirmAsync"/>
    Task<ConfirmResult> ConfirmAsync(Guid proposalId, CancellationToken cancellationToken);
}

/// <summary>
/// Confirms proposals around the repository's transaction, with the <c>confirm_follow_up</c> span and the
/// <c>supportdesk.follow_ups</c> count (PLAN.md 14), so the repository only stores.
/// </summary>
public sealed class FollowUpService(IFollowUpRepository followUps, SupportDeskMetrics metrics) : IFollowUpService
{
    public const string ConfirmSpanName = "confirm_follow_up";

    public async Task<ConfirmResult> ConfirmAsync(Guid proposalId, CancellationToken cancellationToken)
    {
        // Around the whole call, so the database spans are its children
        using var activity = SupportDeskTelemetry.Source.StartActivity(ConfirmSpanName);
        activity?.SetTag("supportdesk.proposal_id", proposalId.ToString());
        try
        {
            var result = await followUps.ConfirmAsync(proposalId, cancellationToken);
            activity?.SetTag(SupportDeskTelemetry.RowsChanged, result.RowsChanged);
            activity?.SetTag(SupportDeskTelemetry.Outcome, result.Outcome.ToString());

            // After the commit, and only what this call changed: a repeat confirm returns the existing task, and an
            // already expired proposal was counted when it expired
            if (result is { Outcome: ConfirmOutcome.Confirmed, RowsChanged: > 0 })
            {
                metrics.RecordFollowUp(MetricValues.Confirmed);
            }
            else if (result is { Outcome: ConfirmOutcome.Expired, RowsChanged: > 0 })
            {
                metrics.RecordFollowUp(MetricValues.Expired, result.RowsChanged);
            }

            return result;
        }
        catch (Exception exception) when (activity.RecordedFailure(exception, cancellationToken))
        {
            throw;
        }
    }
}
