using AgentSwarm.Messaging.Abstractions;
using FluentAssertions;

namespace AgentSwarm.Messaging.Tests.Models;

public class MessageSeverityOrderingTests
{
    [Fact]
    public void AscendingSort_PutsCriticalFirstAndLowLast()
    {
        var shuffled = new[]
        {
            MessageSeverity.Low,
            MessageSeverity.Critical,
            MessageSeverity.Normal,
            MessageSeverity.High,
        };

        var sorted = shuffled.OrderBy(s => (int)s).ToArray();

        sorted.Should().Equal(
            MessageSeverity.Critical,
            MessageSeverity.High,
            MessageSeverity.Normal,
            MessageSeverity.Low);
    }

    [Fact]
    public void IntegerValues_AreContiguousFromZero()
    {
        ((int)MessageSeverity.Critical).Should().Be(0);
        ((int)MessageSeverity.High).Should().Be(1);
        ((int)MessageSeverity.Normal).Should().Be(2);
        ((int)MessageSeverity.Low).Should().Be(3);
    }

    [Fact]
    public void CriticalCompareTo_IsLessThanEverythingElse()
    {
        // Sanity-check the priority order ("Critical > High > Normal > Low" per
        // architecture.md Section 3.1) is realised through ascending integer sort.
        ((int)MessageSeverity.Critical).Should().BeLessThan((int)MessageSeverity.High);
        ((int)MessageSeverity.High).Should().BeLessThan((int)MessageSeverity.Normal);
        ((int)MessageSeverity.Normal).Should().BeLessThan((int)MessageSeverity.Low);
    }
}
