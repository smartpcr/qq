using System.Collections.ObjectModel;
using AgentSwarm.Messaging.Abstractions;
using FluentAssertions;

namespace AgentSwarm.Messaging.Tests.Models;

/// <summary>
/// Regression tests for the "shared contract cannot be mutated after construction"
/// promise the shared DTOs make. The previous defensive-copy implementation stored
/// the snapshot as a raw array / Dictionary and exposed it via
/// <see cref="IReadOnlyList{T}"/> / <see cref="IReadOnlyDictionary{K,V}"/>, so a
/// caller could downcast the exposed value back to <c>T[]</c> or
/// <see cref="Dictionary{TKey,TValue}"/> and mutate the model. These tests pin the
/// fix in place: exposed collections are wrapped in <see cref="ReadOnlyCollection{T}"/>
/// / <see cref="ReadOnlyDictionary{TKey,TValue}"/>, so downcasts to mutable types
/// either return <see langword="null"/> (the <c>as</c> form) or throw on mutation
/// when the caller downcasts to a writable interface.
/// </summary>
public class ImmutabilityRegressionTests
{
    private static AgentQuestion BuildQuestion(params HumanAction[] actions) =>
        new(
            QuestionId: "Q-imm",
            AgentId: "agent",
            TaskId: "task",
            Title: "t",
            Body: "b",
            Severity: MessageSeverity.Normal,
            AllowedActions: actions,
            ExpiresAt: DateTimeOffset.UnixEpoch,
            CorrelationId: "trace");

    private static GuildBinding BuildBinding(
        IReadOnlyList<ulong> roles,
        IReadOnlyDictionary<string, IReadOnlyList<ulong>>? restrictions) =>
        new(
            Id: Guid.NewGuid(),
            GuildId: 1UL,
            ChannelId: 2UL,
            ChannelPurpose: ChannelPurpose.Control,
            TenantId: "t",
            WorkspaceId: "w",
            AllowedRoleIds: roles,
            CommandRestrictions: restrictions,
            RegisteredAt: DateTimeOffset.UnixEpoch,
            IsActive: true);

    // ---- AgentQuestion.AllowedActions ----

    [Fact]
    public void AgentQuestion_AllowedActions_DowncastToArray_ReturnsNull()
    {
        var question = BuildQuestion(new HumanAction("a", "L", "v", false));

        var asArray = question.AllowedActions as HumanAction[];

        asArray.Should().BeNull("storage is wrapped in ReadOnlyCollection<T>, not a HumanAction[]");
    }

    [Fact]
    public void AgentQuestion_AllowedActions_DowncastToList_ReturnsNull()
    {
        var question = BuildQuestion(new HumanAction("a", "L", "v", false));

        var asList = question.AllowedActions as List<HumanAction>;

        asList.Should().BeNull("storage is wrapped in ReadOnlyCollection<T>, not a List<T>");
    }

    [Fact]
    public void AgentQuestion_AllowedActions_AsIList_IsReadOnlyAndThrowsOnMutation()
    {
        var question = BuildQuestion(new HumanAction("a", "L", "v", false));

        var asMutableView = question.AllowedActions as IList<HumanAction>;

        asMutableView.Should().NotBeNull("ReadOnlyCollection<T> implements IList<T>");
        asMutableView!.IsReadOnly.Should().BeTrue();

        var add = () => asMutableView.Add(new HumanAction("evil", "X", "y", false));
        add.Should().Throw<NotSupportedException>();

        var assign = () => asMutableView[0] = new HumanAction("hacked", "X", "y", true);
        assign.Should().Throw<NotSupportedException>();

        var clear = () => asMutableView.Clear();
        clear.Should().Throw<NotSupportedException>();
    }

    // ---- GuildBinding.AllowedRoleIds ----

    [Fact]
    public void GuildBinding_AllowedRoleIds_DowncastToArray_ReturnsNull()
    {
        var binding = BuildBinding(new ulong[] { 1UL, 2UL }, null);

        var asArray = binding.AllowedRoleIds as ulong[];

        asArray.Should().BeNull();
    }

    [Fact]
    public void GuildBinding_AllowedRoleIds_DowncastToList_ReturnsNull()
    {
        var binding = BuildBinding(new List<ulong> { 1UL, 2UL }, null);

        var asList = binding.AllowedRoleIds as List<ulong>;

        asList.Should().BeNull();
    }

    [Fact]
    public void GuildBinding_AllowedRoleIds_AsIList_ThrowsOnMutation()
    {
        var binding = BuildBinding(new ulong[] { 1UL, 2UL }, null);

        var asMutableView = binding.AllowedRoleIds as IList<ulong>;

        asMutableView.Should().NotBeNull();
        asMutableView!.IsReadOnly.Should().BeTrue();

        var add = () => asMutableView.Add(999UL);
        add.Should().Throw<NotSupportedException>();
    }

    // ---- GuildBinding.CommandRestrictions outer dict ----

    [Fact]
    public void GuildBinding_CommandRestrictions_DowncastToDictionary_ReturnsNull()
    {
        var binding = BuildBinding(
            Array.Empty<ulong>(),
            new Dictionary<string, IReadOnlyList<ulong>>
            {
                ["approve"] = new ulong[] { 1UL },
            });

        var asDict = binding.CommandRestrictions as Dictionary<string, IReadOnlyList<ulong>>;

        asDict.Should().BeNull();
    }

    [Fact]
    public void GuildBinding_CommandRestrictions_AsIDictionary_ThrowsOnMutation()
    {
        var binding = BuildBinding(
            Array.Empty<ulong>(),
            new Dictionary<string, IReadOnlyList<ulong>>
            {
                ["approve"] = new ulong[] { 1UL },
            });

        var asMutableMap = binding.CommandRestrictions
            as IDictionary<string, IReadOnlyList<ulong>>;

        asMutableMap.Should().NotBeNull();
        asMutableMap!.IsReadOnly.Should().BeTrue();

        var add = () => asMutableMap.Add("reject", new ulong[] { 999UL });
        add.Should().Throw<NotSupportedException>();

        var assign = () => asMutableMap["approve"] = new ulong[] { 999UL };
        assign.Should().Throw<NotSupportedException>();
    }

    // ---- GuildBinding.CommandRestrictions value lists ----

    [Fact]
    public void GuildBinding_CommandRestrictionsValue_DowncastToArray_ReturnsNull()
    {
        var binding = BuildBinding(
            Array.Empty<ulong>(),
            new Dictionary<string, IReadOnlyList<ulong>>
            {
                ["approve"] = new ulong[] { 1UL, 2UL },
            });

        var asArray = binding.CommandRestrictions!["approve"] as ulong[];

        asArray.Should().BeNull();
    }

    [Fact]
    public void GuildBinding_CommandRestrictionsValue_AsIList_ThrowsOnMutation()
    {
        var binding = BuildBinding(
            Array.Empty<ulong>(),
            new Dictionary<string, IReadOnlyList<ulong>>
            {
                ["approve"] = new ulong[] { 1UL, 2UL },
            });

        var asMutableView = binding.CommandRestrictions!["approve"] as IList<ulong>;

        asMutableView.Should().NotBeNull();
        asMutableView!.IsReadOnly.Should().BeTrue();

        var add = () => asMutableView.Add(999UL);
        add.Should().Throw<NotSupportedException>();
    }

    // ---- AgentQuestionEnvelope.RoutingMetadata ----

    [Fact]
    public void AgentQuestionEnvelope_RoutingMetadata_DowncastToDictionary_ReturnsNull()
    {
        var envelope = new AgentQuestionEnvelope(
            Question: BuildQuestion(new HumanAction("a", "L", "v", false)),
            ProposedDefaultActionId: null,
            RoutingMetadata: new Dictionary<string, string> { ["DiscordChannelId"] = "1" });

        var asDict = envelope.RoutingMetadata as Dictionary<string, string>;

        asDict.Should().BeNull();
    }

    [Fact]
    public void AgentQuestionEnvelope_RoutingMetadata_AsIDictionary_ThrowsOnMutation()
    {
        var envelope = new AgentQuestionEnvelope(
            Question: BuildQuestion(new HumanAction("a", "L", "v", false)),
            ProposedDefaultActionId: null,
            RoutingMetadata: new Dictionary<string, string> { ["DiscordChannelId"] = "1" });

        var asMutable = envelope.RoutingMetadata as IDictionary<string, string>;

        asMutable.Should().NotBeNull();
        asMutable!.IsReadOnly.Should().BeTrue();
        ((Action)(() => asMutable.Add("k", "v"))).Should().Throw<NotSupportedException>();
        ((Action)(() => asMutable["DiscordChannelId"] = "9")).Should().Throw<NotSupportedException>();
    }

    [Fact]
    public void AgentQuestionEnvelope_RoutingMetadata_IsDefensivelyCopied()
    {
        var source = new Dictionary<string, string> { ["DiscordChannelId"] = "1" };
        var envelope = new AgentQuestionEnvelope(
            Question: BuildQuestion(new HumanAction("a", "L", "v", false)),
            ProposedDefaultActionId: null,
            RoutingMetadata: source);

        source["DiscordChannelId"] = "9999";
        source["DiscordThreadId"] = "added";

        envelope.RoutingMetadata.Should().HaveCount(1);
        envelope.RoutingMetadata["DiscordChannelId"].Should().Be("1");
    }

    [Fact]
    public void AgentQuestionEnvelope_RoutingMetadata_NullRequiredMap_Throws()
    {
        var act = () => new AgentQuestionEnvelope(
            Question: BuildQuestion(new HumanAction("a", "L", "v", false)),
            ProposedDefaultActionId: null,
            RoutingMetadata: null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void AgentQuestionEnvelope_RoutingMetadata_NullValueInsideMap_Throws()
    {
        var bad = new Dictionary<string, string> { ["DiscordChannelId"] = null! };

        var act = () => new AgentQuestionEnvelope(
            Question: BuildQuestion(new HumanAction("a", "L", "v", false)),
            ProposedDefaultActionId: null,
            RoutingMetadata: bad);

        act.Should().Throw<ArgumentException>()
            .WithMessage("*RoutingMetadata*DiscordChannelId*null*");
    }

    // ---- SwarmCommand.Arguments ----

    [Fact]
    public void SwarmCommand_Arguments_DowncastToDictionary_ReturnsNull()
    {
        var cmd = new SwarmCommand(
            CommandId: Guid.NewGuid(),
            CommandType: "approve",
            AgentTarget: "agent-1",
            Arguments: new Dictionary<string, string> { ["reason"] = "ship-it" },
            CorrelationId: "trace",
            Timestamp: DateTimeOffset.UnixEpoch);

        (cmd.Arguments as Dictionary<string, string>).Should().BeNull();
    }

    [Fact]
    public void SwarmCommand_Arguments_AsIDictionary_ThrowsOnMutation()
    {
        var cmd = new SwarmCommand(
            CommandId: Guid.NewGuid(),
            CommandType: "approve",
            AgentTarget: "agent-1",
            Arguments: new Dictionary<string, string> { ["reason"] = "ship-it" },
            CorrelationId: "trace",
            Timestamp: DateTimeOffset.UnixEpoch);

        var asMutable = cmd.Arguments as IDictionary<string, string>;

        asMutable.Should().NotBeNull();
        asMutable!.IsReadOnly.Should().BeTrue();
        ((Action)(() => asMutable.Add("k", "v"))).Should().Throw<NotSupportedException>();
    }

    [Fact]
    public void SwarmCommand_Arguments_IsDefensivelyCopied()
    {
        var source = new Dictionary<string, string> { ["reason"] = "ship-it" };
        var cmd = new SwarmCommand(
            CommandId: Guid.NewGuid(),
            CommandType: "approve",
            AgentTarget: "agent-1",
            Arguments: source,
            CorrelationId: "trace",
            Timestamp: DateTimeOffset.UnixEpoch);

        source["reason"] = "tampered";
        source["new-key"] = "hacked";

        cmd.Arguments.Should().HaveCount(1);
        cmd.Arguments["reason"].Should().Be("ship-it");
    }

    [Fact]
    public void SwarmCommand_Arguments_NullValueInsideMap_Throws()
    {
        var bad = new Dictionary<string, string> { ["reason"] = null! };

        var act = () => new SwarmCommand(
            CommandId: Guid.NewGuid(),
            CommandType: "approve",
            AgentTarget: "agent-1",
            Arguments: bad,
            CorrelationId: "trace",
            Timestamp: DateTimeOffset.UnixEpoch);

        act.Should().Throw<ArgumentException>()
            .WithMessage("*Arguments*reason*null*");
    }

    // ---- MessengerMessage.Metadata ----

    [Fact]
    public void MessengerMessage_Metadata_DowncastToDictionary_ReturnsNull()
    {
        var msg = new MessengerMessage(
            Messenger: "Discord",
            ChannelId: "1",
            Body: "hello",
            Severity: MessageSeverity.Normal,
            CorrelationId: "trace",
            Timestamp: DateTimeOffset.UnixEpoch,
            Metadata: new Dictionary<string, string> { ["ThreadId"] = "42" });

        (msg.Metadata as Dictionary<string, string>).Should().BeNull();
    }

    [Fact]
    public void MessengerMessage_Metadata_AsIDictionary_ThrowsOnMutation()
    {
        var msg = new MessengerMessage(
            Messenger: "Discord",
            ChannelId: "1",
            Body: "hello",
            Severity: MessageSeverity.Normal,
            CorrelationId: "trace",
            Timestamp: DateTimeOffset.UnixEpoch,
            Metadata: new Dictionary<string, string> { ["ThreadId"] = "42" });

        var asMutable = msg.Metadata as IDictionary<string, string>;

        asMutable.Should().NotBeNull();
        asMutable!.IsReadOnly.Should().BeTrue();
        ((Action)(() => asMutable.Add("k", "v"))).Should().Throw<NotSupportedException>();
    }

    [Fact]
    public void MessengerMessage_Metadata_IsDefensivelyCopied()
    {
        var source = new Dictionary<string, string> { ["ThreadId"] = "42" };
        var msg = new MessengerMessage(
            Messenger: "Discord",
            ChannelId: "1",
            Body: "hello",
            Severity: MessageSeverity.Normal,
            CorrelationId: "trace",
            Timestamp: DateTimeOffset.UnixEpoch,
            Metadata: source);

        source["ThreadId"] = "tampered";
        source["new-key"] = "hacked";

        msg.Metadata.Should().HaveCount(1);
        msg.Metadata!["ThreadId"].Should().Be("42");
    }

    [Fact]
    public void MessengerMessage_Metadata_NullValueInsideMap_Throws()
    {
        var bad = new Dictionary<string, string> { ["ThreadId"] = null! };

        var act = () => new MessengerMessage(
            Messenger: "Discord",
            ChannelId: "1",
            Body: "hello",
            Severity: MessageSeverity.Normal,
            CorrelationId: "trace",
            Timestamp: DateTimeOffset.UnixEpoch,
            Metadata: bad);

        act.Should().Throw<ArgumentException>()
            .WithMessage("*Metadata*ThreadId*null*");
    }

    [Fact]
    public void MessengerMessage_NullMetadata_IsAllowed()
    {
        // Optional dictionary -- null is the explicit "no metadata" signal.
        var msg = new MessengerMessage(
            Messenger: "Discord",
            ChannelId: "1",
            Body: "hello",
            Severity: MessageSeverity.Normal,
            CorrelationId: "trace",
            Timestamp: DateTimeOffset.UnixEpoch,
            Metadata: null);

        msg.Metadata.Should().BeNull();
    }

    // ---- MessengerEvent.Metadata ----

    [Fact]
    public void MessengerEvent_Metadata_DowncastToDictionary_ReturnsNull()
    {
        var evt = new MessengerEvent(
            Messenger: "Discord",
            EventType: MessengerEventType.ButtonClick,
            ExternalUserId: "u",
            ExternalChannelId: "c",
            ExternalMessageId: "m",
            Payload: null,
            CorrelationId: "trace",
            Timestamp: DateTimeOffset.UnixEpoch,
            Metadata: new Dictionary<string, string> { ["GuildId"] = "g" });

        (evt.Metadata as Dictionary<string, string>).Should().BeNull();
    }

    [Fact]
    public void MessengerEvent_Metadata_AsIDictionary_ThrowsOnMutation()
    {
        var evt = new MessengerEvent(
            Messenger: "Discord",
            EventType: MessengerEventType.ButtonClick,
            ExternalUserId: "u",
            ExternalChannelId: "c",
            ExternalMessageId: "m",
            Payload: null,
            CorrelationId: "trace",
            Timestamp: DateTimeOffset.UnixEpoch,
            Metadata: new Dictionary<string, string> { ["GuildId"] = "g" });

        var asMutable = evt.Metadata as IDictionary<string, string>;

        asMutable.Should().NotBeNull();
        asMutable!.IsReadOnly.Should().BeTrue();
        ((Action)(() => asMutable.Add("k", "v"))).Should().Throw<NotSupportedException>();
    }

    [Fact]
    public void MessengerEvent_Metadata_IsDefensivelyCopied()
    {
        var source = new Dictionary<string, string> { ["GuildId"] = "g" };
        var evt = new MessengerEvent(
            Messenger: "Discord",
            EventType: MessengerEventType.ButtonClick,
            ExternalUserId: "u",
            ExternalChannelId: "c",
            ExternalMessageId: "m",
            Payload: null,
            CorrelationId: "trace",
            Timestamp: DateTimeOffset.UnixEpoch,
            Metadata: source);

        source["GuildId"] = "tampered";
        source["new-key"] = "hacked";

        evt.Metadata.Should().HaveCount(1);
        evt.Metadata!["GuildId"].Should().Be("g");
    }

    [Fact]
    public void MessengerEvent_Metadata_NullValueInsideMap_Throws()
    {
        var bad = new Dictionary<string, string> { ["GuildId"] = null! };

        var act = () => new MessengerEvent(
            Messenger: "Discord",
            EventType: MessengerEventType.ButtonClick,
            ExternalUserId: "u",
            ExternalChannelId: "c",
            ExternalMessageId: "m",
            Payload: null,
            CorrelationId: "trace",
            Timestamp: DateTimeOffset.UnixEpoch,
            Metadata: bad);

        act.Should().Throw<ArgumentException>()
            .WithMessage("*Metadata*GuildId*null*");
    }
}
