using System.Reflection;
using AgentSwarm.Messaging.Abstractions;

namespace AgentSwarm.Messaging.Abstractions.Tests;

public class HumanDecisionEventImmutabilityTests
{
    /// <summary>
    /// Reflection-based check that <see cref="HumanDecisionEvent.QuestionId"/>
    /// (and all other settable properties) use init-only setters. An init-only
    /// setter at the IL level carries a <c>System.Runtime.CompilerServices.IsExternalInit</c>
    /// required custom modifier on the setter's return parameter — the exact marker
    /// the C# compiler uses to refuse mutation outside of construction / <c>with</c>
    /// expressions. If a property setter were public mutable, this marker would be
    /// absent and the assertion would fail, mirroring the compile-time guarantee.
    /// </summary>
    [Fact]
    public void QuestionId_Setter_Is_InitOnly()
    {
        var prop = typeof(HumanDecisionEvent).GetProperty(nameof(HumanDecisionEvent.QuestionId));
        Assert.NotNull(prop);

        var setter = prop!.SetMethod;
        Assert.NotNull(setter);

        var modReqs = setter!.ReturnParameter.GetRequiredCustomModifiers();
        Assert.Contains(modReqs, t => t.FullName == "System.Runtime.CompilerServices.IsExternalInit");
    }

    [Fact]
    public void All_HumanDecisionEvent_Properties_Are_InitOnly()
    {
        var props = typeof(HumanDecisionEvent).GetProperties(BindingFlags.Public | BindingFlags.Instance);
        Assert.NotEmpty(props);
        foreach (var prop in props)
        {
            if (prop.SetMethod is null)
            {
                continue;
            }

            var modifiers = prop.SetMethod.ReturnParameter.GetRequiredCustomModifiers();
            Assert.Contains(modifiers, m => m.FullName == "System.Runtime.CompilerServices.IsExternalInit");
        }
    }

    [Fact]
    public void Records_Support_Value_Equality()
    {
        var a = new HumanDecisionEvent
        {
            QuestionId = "q1",
            ActionValue = "approve",
            Comment = null,
            Messenger = "Teams",
            ExternalUserId = "u1",
            ExternalMessageId = "m1",
            ReceivedAt = new DateTimeOffset(2026, 5, 13, 12, 0, 0, TimeSpan.Zero),
            CorrelationId = "c1"
        };
        var b = a with { }; // structural copy

        Assert.Equal(a, b);
        Assert.False(ReferenceEquals(a, b));
    }

    [Fact]
    public void With_Expression_Produces_New_Instance_Without_Mutating_Original()
    {
        var original = new HumanDecisionEvent { QuestionId = "q1" };
        var modified = original with { QuestionId = "q2" };

        Assert.Equal("q1", original.QuestionId); // original is unchanged
        Assert.Equal("q2", modified.QuestionId);
    }
}
