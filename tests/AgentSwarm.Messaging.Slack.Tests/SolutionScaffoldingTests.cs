using AgentSwarm.Messaging.Slack;
using FluentAssertions;
using Xunit;

namespace AgentSwarm.Messaging.Slack.Tests;

/// <summary>
/// Stage 1.1 smoke tests. They prove that the test project links against
/// the Slack project, the xUnit runner initialises, and FluentAssertions /
/// Moq dependencies resolve. Concrete behaviour tests are added by later
/// stages.
/// </summary>
public sealed class SolutionScaffoldingTests
{
    [Fact]
    public void Slack_assembly_is_referenced()
    {
        System.Reflection.Assembly slack = typeof(AssemblyMarker).Assembly;
        slack.GetName().Name.Should().Be("AgentSwarm.Messaging.Slack");
    }

    [Fact]
    public void Test_runner_executes_facts()
    {
        // If this fact runs at all, the xUnit + Microsoft.NET.Test.Sdk wiring
        // is healthy. Keep an explicit assertion so a misconfigured runner
        // that swallows the fact still fails CI.
        (2 + 2).Should().Be(4);
    }
}
