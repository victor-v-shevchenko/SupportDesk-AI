namespace SupportDesk.UnitTests.AI;

/// <summary>
/// Every test class that runs a call on <see cref="TrackedFakeTime"/> through <see cref="FakeClock"/> or <c>ReplyRig.RunAsync</c>
/// is in this collection. Collections with parallelization disabled run alone, so the thread-pool counters that
/// <see cref="FakeClock"/> reads to decide the call is quiet reflect only the call under test.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class FakeClockGroup
{
    public const string Name = "Fake clock";
}
