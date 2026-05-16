using AgentSwarm.Messaging.Abstractions;
using FluentAssertions;

namespace AgentSwarm.Messaging.Tests.Contracts;

/// <summary>
/// Stage 1.3 required scenario: "Given a mock implementation of
/// IMessengerConnector, When SendQuestionAsync is called with an
/// AgentQuestionEnvelope, Then the call compiles and completes without error".
/// Implements an in-process recording fake (rather than reaching for Moq) so
/// the test doubles as a contract-shape check — every interface method has to
/// be implementable with the exact signature shipped in Abstractions for this
/// file to compile.
/// </summary>
public class MessengerConnectorContractTests
{
    private sealed class RecordingMessengerConnector : IMessengerConnector
    {
        public List<MessengerMessage> SentMessages { get; } = new();
        public List<AgentQuestionEnvelope> SentQuestions { get; } = new();
        public Queue<IReadOnlyList<MessengerEvent>> ReceiveQueue { get; } = new();

        public Task SendMessageAsync(MessengerMessage message, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            SentMessages.Add(message);
            return Task.CompletedTask;
        }

        public Task SendQuestionAsync(AgentQuestionEnvelope envelope, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            SentQuestions.Add(envelope);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<MessengerEvent>> ReceiveAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var batch = ReceiveQueue.Count > 0 ? ReceiveQueue.Dequeue() : Array.Empty<MessengerEvent>();
            return Task.FromResult(batch);
        }
    }

    private static AgentQuestionEnvelope BuildEnvelope() =>
        new(
            Question: new AgentQuestion(
                QuestionId: "Q-42",
                AgentId: "build-agent-3",
                TaskId: "task-1",
                Title: "Cache strategy?",
                Body: "Pick a cache strategy for the update-service.",
                Severity: MessageSeverity.High,
                AllowedActions: new[]
                {
                    new HumanAction("approve", "Approve", "approve", false),
                    new HumanAction("reject", "Reject", "reject", true),
                },
                ExpiresAt: DateTimeOffset.UnixEpoch.AddHours(1),
                CorrelationId: "trace-1"),
            ProposedDefaultActionId: "approve",
            RoutingMetadata: new Dictionary<string, string>
            {
                ["DiscordChannelId"] = "1122334455",
            });

    [Fact]
    public async Task SendQuestionAsync_OnMockImplementation_CompletesWithoutError()
    {
        IMessengerConnector connector = new RecordingMessengerConnector();
        var envelope = BuildEnvelope();

        var act = async () => await connector.SendQuestionAsync(envelope, CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task SendQuestionAsync_RecordsTheEnvelope()
    {
        var connector = new RecordingMessengerConnector();
        var envelope = BuildEnvelope();

        await ((IMessengerConnector)connector).SendQuestionAsync(envelope, CancellationToken.None);

        connector.SentQuestions.Should().HaveCount(1);
        connector.SentQuestions[0].Should().BeSameAs(envelope);
    }

    [Fact]
    public async Task SendMessageAsync_RecordsTheMessage()
    {
        var connector = new RecordingMessengerConnector();
        var message = new MessengerMessage(
            Messenger: "Discord",
            ChannelId: "1",
            Body: "hello",
            Severity: MessageSeverity.Normal,
            CorrelationId: "trace",
            Timestamp: DateTimeOffset.UnixEpoch);

        await ((IMessengerConnector)connector).SendMessageAsync(message, CancellationToken.None);

        connector.SentMessages.Should().HaveCount(1);
        connector.SentMessages[0].Should().BeSameAs(message);
    }

    [Fact]
    public async Task ReceiveAsync_EmptyBuffer_ReturnsEmptyList()
    {
        IMessengerConnector connector = new RecordingMessengerConnector();

        var events = await connector.ReceiveAsync(CancellationToken.None);

        events.Should().NotBeNull().And.BeEmpty();
    }

    [Fact]
    public async Task ReceiveAsync_ReturnsQueuedBatch()
    {
        var connector = new RecordingMessengerConnector();
        var ev = new MessengerEvent(
            Messenger: "Discord",
            EventType: MessengerEventType.ButtonClick,
            ExternalUserId: "u",
            ExternalChannelId: "c",
            ExternalMessageId: "m",
            Payload: null,
            CorrelationId: "trace",
            Timestamp: DateTimeOffset.UnixEpoch);
        connector.ReceiveQueue.Enqueue(new[] { ev });

        var events = await ((IMessengerConnector)connector).ReceiveAsync(CancellationToken.None);

        events.Should().HaveCount(1);
        events[0].Should().BeSameAs(ev);
    }

    [Fact]
    public async Task SendQuestionAsync_CancelledToken_Throws()
    {
        IMessengerConnector connector = new RecordingMessengerConnector();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = async () => await connector.SendQuestionAsync(BuildEnvelope(), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}
