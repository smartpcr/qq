using System.Linq;
using System.Reflection;
using AgentSwarm.Messaging.Abstractions;
using FluentAssertions;
using Xunit;

namespace AgentSwarm.Messaging.Slack.Tests;

/// <summary>
/// Stage 1.2 contract tests. The Abstractions project hosts compile-target
/// stubs for shared types; these tests pin their public surface to the
/// field contracts spelled out in
/// <c>docs/stories/qq-SLACK-MESSENGER-SUPP/architecture.md</c> section 3.6,
/// section 4.1, and the uploaded reference doc
/// <c>.forge-attachments/agent_swarm_messenger_user_stories.md</c>
/// (Shared Data Model + FR-001) so that the upstream canonical types can
/// replace the stubs without silently shifting a field name, type, or
/// nullability annotation.
/// </summary>
public sealed class AbstractionsStubContractTests
{
    [Fact]
    public void IMessengerConnector_exposes_the_three_canonical_methods()
    {
        Type connector = typeof(IMessengerConnector);

        connector.IsInterface.Should().BeTrue();

        string[] methodNames = connector.GetMethods()
            .Select(m => m.Name)
            .OrderBy(n => n)
            .ToArray();

        methodNames.Should().BeEquivalentTo(new[]
        {
            nameof(IMessengerConnector.ReceiveAsync),
            nameof(IMessengerConnector.SendMessageAsync),
            nameof(IMessengerConnector.SendQuestionAsync),
        });
    }

    [Fact]
    public void IMessengerConnector_ReceiveAsync_returns_polymorphic_event_list()
    {
        MethodInfo receive = typeof(IMessengerConnector).GetMethod(nameof(IMessengerConnector.ReceiveAsync))!;

        receive.ReturnType.Should().Be(typeof(Task<IReadOnlyList<MessengerEvent>>));
    }

    [Fact]
    public void IMessengerConnector_SendMessageAsync_signature_matches_canonical_shape()
    {
        MethodInfo m = typeof(IMessengerConnector).GetMethod(nameof(IMessengerConnector.SendMessageAsync))!;

        m.ReturnType.Should().Be(typeof(Task));

        ParameterInfo[] p = m.GetParameters();
        p.Should().HaveCount(2);
        AssertParameter(p[0], expectedName: "message", expectedType: typeof(MessengerMessage));
        AssertParameter(p[1], expectedName: "ct", expectedType: typeof(CancellationToken));
    }

    [Fact]
    public void IMessengerConnector_SendQuestionAsync_signature_matches_canonical_shape()
    {
        MethodInfo m = typeof(IMessengerConnector).GetMethod(nameof(IMessengerConnector.SendQuestionAsync))!;

        m.ReturnType.Should().Be(typeof(Task));

        ParameterInfo[] p = m.GetParameters();
        p.Should().HaveCount(2);
        AssertParameter(p[0], expectedName: "question", expectedType: typeof(AgentQuestion));
        AssertParameter(p[1], expectedName: "ct", expectedType: typeof(CancellationToken));
    }

    [Fact]
    public void IMessengerConnector_ReceiveAsync_signature_matches_canonical_shape()
    {
        MethodInfo m = typeof(IMessengerConnector).GetMethod(nameof(IMessengerConnector.ReceiveAsync))!;

        ParameterInfo[] p = m.GetParameters();
        p.Should().HaveCount(1);
        AssertParameter(p[0], expectedName: "ct", expectedType: typeof(CancellationToken));
    }

    [Fact]
    public void AgentQuestion_exposes_all_section_3_6_1_fields_with_canonical_types()
    {
        // Field contract from uploaded reference doc lines 831-840 and
        // architecture.md section 3.6.1. Property names AND types are pinned
        // so the canonical Abstractions story can swap in its sealed record
        // without silently flipping nullability (notably ExpiresAt).
        AssertProperty(typeof(AgentQuestion), "QuestionId", typeof(string), expectNullable: false);
        AssertProperty(typeof(AgentQuestion), "AgentId", typeof(string), expectNullable: false);
        AssertProperty(typeof(AgentQuestion), "TaskId", typeof(string), expectNullable: false);
        AssertProperty(typeof(AgentQuestion), "Title", typeof(string), expectNullable: false);
        AssertProperty(typeof(AgentQuestion), "Body", typeof(string), expectNullable: false);
        AssertProperty(typeof(AgentQuestion), "Severity", typeof(string), expectNullable: false);
        AssertProperty(typeof(AgentQuestion), "AllowedActions", typeof(IReadOnlyList<HumanAction>), expectNullable: false);
        AssertProperty(typeof(AgentQuestion), "ExpiresAt", typeof(DateTimeOffset), expectNullable: false);
        AssertProperty(typeof(AgentQuestion), "CorrelationId", typeof(string), expectNullable: false);
    }

    [Fact]
    public void HumanAction_exposes_all_section_3_6_2_fields_with_canonical_types()
    {
        AssertProperty(typeof(HumanAction), "ActionId", typeof(string), expectNullable: false);
        AssertProperty(typeof(HumanAction), "Label", typeof(string), expectNullable: false);
        AssertProperty(typeof(HumanAction), "Value", typeof(string), expectNullable: false);
        AssertProperty(typeof(HumanAction), "RequiresComment", typeof(bool), expectNullable: false);
    }

    [Fact]
    public void HumanDecisionEvent_exposes_all_section_3_6_3_fields_with_canonical_types()
    {
        // Field contract from uploaded reference doc lines 860-868 and
        // architecture.md section 3.6.3. Comment is the only nullable field
        // (string?), matching the canonical sealed record exactly.
        AssertProperty(typeof(HumanDecisionEvent), "QuestionId", typeof(string), expectNullable: false);
        AssertProperty(typeof(HumanDecisionEvent), "ActionValue", typeof(string), expectNullable: false);
        AssertProperty(typeof(HumanDecisionEvent), "Comment", typeof(string), expectNullable: true);
        AssertProperty(typeof(HumanDecisionEvent), "Messenger", typeof(string), expectNullable: false);
        AssertProperty(typeof(HumanDecisionEvent), "ExternalUserId", typeof(string), expectNullable: false);
        AssertProperty(typeof(HumanDecisionEvent), "ExternalMessageId", typeof(string), expectNullable: false);
        AssertProperty(typeof(HumanDecisionEvent), "ReceivedAt", typeof(DateTimeOffset), expectNullable: false);
        AssertProperty(typeof(HumanDecisionEvent), "CorrelationId", typeof(string), expectNullable: false);
    }

    [Fact]
    public void HumanDecisionEvent_derives_from_MessengerEvent()
    {
        typeof(MessengerEvent).IsAssignableFrom(typeof(HumanDecisionEvent)).Should().BeTrue();
        typeof(MessengerEvent).IsAbstract.Should().BeTrue();
    }

    [Fact]
    public void MessengerMessage_exposes_all_section_3_6_4_fields_with_canonical_types()
    {
        AssertProperty(typeof(MessengerMessage), "MessageId", typeof(string), expectNullable: false);
        AssertProperty(typeof(MessengerMessage), "AgentId", typeof(string), expectNullable: false);
        AssertProperty(typeof(MessengerMessage), "TaskId", typeof(string), expectNullable: false);
        AssertProperty(typeof(MessengerMessage), "Content", typeof(string), expectNullable: false);
        AssertProperty(typeof(MessengerMessage), "MessageType", typeof(MessageType), expectNullable: false);
        AssertProperty(typeof(MessengerMessage), "CorrelationId", typeof(string), expectNullable: false);
        AssertProperty(typeof(MessengerMessage), "Timestamp", typeof(DateTimeOffset), expectNullable: false);
    }

    [Fact]
    public void MessageType_enum_covers_renderer_styles()
    {
        string[] names = Enum.GetNames(typeof(MessageType));

        names.Should().Contain(new[] { "StatusUpdate", "Completion", "Error" });
    }

    private static void AssertParameter(ParameterInfo parameter, string expectedName, Type expectedType)
    {
        parameter.Name.Should().Be(expectedName,
            because: $"the uploaded reference doc (FR-001 / lines 46, 50, 53) and architecture.md section 4.1 spell the parameter as '{expectedName}'; renaming it would break named-argument call sites against the canonical interface");
        parameter.ParameterType.Should().Be(expectedType,
            because: $"parameter '{expectedName}' must be {expectedType.FullName}");
    }

    private static void AssertProperty(Type recordType, string name, Type expectedType, bool expectNullable)
    {
        PropertyInfo? prop = recordType.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
        prop.Should().NotBeNull(
            because: $"architecture.md section 3.6 / uploaded reference doc requires {recordType.Name}.{name}");

        Type actualType = prop!.PropertyType;

        if (expectedType.IsValueType)
        {
            Type wanted = expectNullable
                ? typeof(Nullable<>).MakeGenericType(expectedType)
                : expectedType;
            actualType.Should().Be(wanted,
                because: $"{recordType.Name}.{name} should be {(expectNullable ? expectedType.Name + "?" : expectedType.Name)} per the canonical contract");
        }
        else
        {
            actualType.Should().Be(expectedType,
                because: $"{recordType.Name}.{name} should be {expectedType.Name}");

            NullabilityInfoContext nullabilityContext = new();
            NullabilityInfo info = nullabilityContext.Create(prop);
            bool actualNullable = info.ReadState == NullabilityState.Nullable;
            actualNullable.Should().Be(expectNullable,
                because: $"{recordType.Name}.{name} nullability must be {(expectNullable ? "nullable (string?)" : "non-nullable (string)")} per the canonical contract");
        }
    }
}
