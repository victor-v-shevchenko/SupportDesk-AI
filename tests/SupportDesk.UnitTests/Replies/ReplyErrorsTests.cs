using System.Net.ServerSentEvents;
using SupportDesk.Api.Endpoints;
using SupportDesk.Core.AI;
using SupportDesk.Core.Replies;

namespace SupportDesk.UnitTests.Replies;

public class ReplyErrorsTests
{
    public static TheoryData<Exception, string> Cases => new()
    {
        { new ModelUnavailableException("down", null, 503, isTransient: true), ReplyErrorCodes.ModelUnavailable },
        { new ModelUnavailableException("slow", null, null, isTransient: true) { IsTimeout = true }, ReplyErrorCodes.Timeout },
        { new BudgetExceededException("big"), ReplyErrorCodes.BudgetExceeded },
        { new ModelRefusedException("no"), ReplyErrorCodes.ModelRefused },
        { new InvalidOperationException("bug"), ReplyErrorCodes.InternalError },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void Each_typed_exception_maps_to_its_code_with_a_fixed_message(Exception exception, string code)
    {
        var error = ReplyErrors.From(exception);

        Assert.Equal(code, error.Code);
        Assert.False(string.IsNullOrWhiteSpace(error.Message));
        Assert.DoesNotContain(exception.Message, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Events_become_sse_items_named_as_in_the_plan()
    {
        ReplyEvent[] events =
        [
            new ReplyToken("Hi"),
            new ReplyCitations([], []),
            new ReplyUsage(1, 2, 0m, Estimated: false),
            new ReplyDone(Guid.NewGuid()),
            new ReplyError(ReplyErrorCodes.Timeout, "slow"),
        ];

        var items = new List<SseItem<object>>();
        await foreach (var item in ReplySse.ToSse(events.ToAsyncEnumerable(), TestContext.Current.CancellationToken))
        {
            items.Add(item);
        }

        Assert.Equal(["token", "citations", "usage", "done", "error"], items.Select(i => i.EventType));
        Assert.Equal(events, items.Select(i => i.Data));
    }
}
