using Microsoft.Extensions.AI;
using SupportDesk.Core.AI;

namespace SupportDesk.UnitTests.AI;

public class AiCallContextTests
{
    [Fact]
    public void Context_round_trips_through_chat_options()
    {
        var context = new AiCallContext(AiFeature.Reply, "reply.v1", TicketId: 7, TicketNumber: "T-1007", ReplyRunId: Guid.NewGuid());

        var options = context.ApplyTo(new ChatOptions());

        Assert.Equal(context, AiCallContext.From(options));
    }

    [Fact]
    public void Optional_fields_stay_null_and_a_new_context_replaces_the_old_one()
    {
        var options = new AiCallContext(AiFeature.Reply, "reply.v1", 7, "T-1007", Guid.NewGuid()).ApplyTo(new ChatOptions());

        new AiCallContext(AiFeature.Summary, "summary.v1").ApplyTo(options);

        Assert.Equal(new AiCallContext(AiFeature.Summary, "summary.v1"), AiCallContext.From(options));
    }

    [Fact]
    public void Survives_cloning_as_the_tool_loop_does()
    {
        var context = new AiCallContext(AiFeature.Classification, "classify.v1", 3, "T-1003");

        var clone = context.ApplyTo(new ChatOptions()).Clone();

        Assert.Equal(context, AiCallContext.From(clone));
    }

    [Fact]
    public void Options_without_a_context_give_null()
    {
        Assert.Null(AiCallContext.From(null));
        Assert.Null(AiCallContext.From(new ChatOptions()));
        Assert.Null(AiCallContext.From(new ChatOptions { AdditionalProperties = new() { [AiCallContext.FeatureKey] = AiFeature.Reply } }));
    }
}
