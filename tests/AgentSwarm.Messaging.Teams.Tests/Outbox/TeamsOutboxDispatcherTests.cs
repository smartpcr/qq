using System.Net;
using System.Net.Http.Headers;
using AgentSwarm.Messaging.Abstractions;
using AgentSwarm.Messaging.Core;
using AgentSwarm.Messaging.Teams.Cards;
using AgentSwarm.Messaging.Teams.Outbox;
using Microsoft.Bot.Builder;
using Microsoft.Bot.Builder.Integration.AspNet.Core;
using Microsoft.Bot.Schema;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Rest;
using Newtonsoft.Json;

namespace AgentSwarm.Messaging.Teams.Tests.Outbox;

/// <summary>
/// Regression coverage for the iter-3 evaluator critiques that drove the Stage 6.1
/// re-implementation of <see cref="TeamsOutboxDispatcher"/>:
/// <list type="bullet">
/// <item>Critique #2 — whitelist transient/permanent HTTP classification (only 408, 425,
/// 429, and 5xx are transient).</item>
/// <item>Critique #3 — two-layer idempotency (outbox-row <c>ActivityId</c> + card-state
/// row), with post-send <see cref="IMessageOutbox.RecordSendReceiptAsync"/> persisted
/// before card-state save.</item>
/// </list>
/// </summary>
public sealed class TeamsOutboxDispatcherTests
{
    // -----------------------------------------------------------------------------------
    // Critique #2 — IsTransientStatusCode (pure-function whitelist).
    // -----------------------------------------------------------------------------------

    [Theory]
    [InlineData(408)] // Request Timeout
    [InlineData(425)] // Too Early
    [InlineData(429)] // Too Many Requests
    [InlineData(500)]
    [InlineData(502)]
    [InlineData(503)]
    [InlineData(504)]
    [InlineData(599)] // any 5xx
    public void IsTransientStatusCode_TrueForWhitelistedCodes(int status)
    {
        Assert.True(TeamsOutboxDispatcher.IsTransientStatusCode(status),
            $"HTTP {status} must be classified as transient.");
    }

    [Theory]
    [InlineData(400)] // Bad Request — malformed payload
    [InlineData(401)] // Unauthorized
    [InlineData(403)] // Forbidden — app not installed
    [InlineData(404)] // Not Found — conversation removed
    [InlineData(409)] // Conflict
    [InlineData(410)] // Gone
    [InlineData(413)] // Payload Too Large
    [InlineData(415)] // Unsupported Media Type
    [InlineData(422)] // Unprocessable Entity
    [InlineData(426)] // Upgrade Required
    [InlineData(200)] // 2xx (not an error, should never be passed but defensively non-transient)
    [InlineData(301)] // Redirect codes are non-transient by this whitelist
    public void IsTransientStatusCode_FalseForEverythingElse(int status)
    {
        Assert.False(TeamsOutboxDispatcher.IsTransientStatusCode(status),
            $"HTTP {status} must NOT be classified as transient (whitelist).");
    }

    // -----------------------------------------------------------------------------------
    // Critique #2 — ClassifyTransportFailure for ErrorResponseException / HttpRequestException.
    // -----------------------------------------------------------------------------------

    [Theory]
    [InlineData(400)]
    [InlineData(413)]
    [InlineData(415)]
    [InlineData(422)]
    public void ClassifyTransportFailure_ErrorResponseException_PermanentForNonWhitelisted(int status)
    {
        var dispatcher = NewDispatcherForClassificationOnly();
        var entry = MinimalEntry("e1");
        var ex = BuildErrorResponseException((HttpStatusCode)status);

        var result = dispatcher.ClassifyTransportFailure(entry, ex);

        Assert.Equal(OutboxDispatchOutcome.Permanent, result.Outcome);
        Assert.Contains(status.ToString(), result.Error);
    }

    [Theory]
    [InlineData(408)]
    [InlineData(425)]
    [InlineData(500)]
    [InlineData(503)]
    public void ClassifyTransportFailure_ErrorResponseException_TransientForWhitelisted(int status)
    {
        var dispatcher = NewDispatcherForClassificationOnly();
        var entry = MinimalEntry("e1");
        var ex = BuildErrorResponseException((HttpStatusCode)status);

        var result = dispatcher.ClassifyTransportFailure(entry, ex);

        Assert.Equal(OutboxDispatchOutcome.Transient, result.Outcome);
    }

    [Fact]
    public void ClassifyTransportFailure_429WithRetryAfterHeader_ParsesIntoResult()
    {
        var dispatcher = NewDispatcherForClassificationOnly();
        var entry = MinimalEntry("e1");
        var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        response.Headers.Add("Retry-After", "12");
        var ex = new ErrorResponseException("HTTP 429")
        {
            Response = new HttpResponseMessageWrapper(response, string.Empty),
        };

        var result = dispatcher.ClassifyTransportFailure(entry, ex);

        Assert.Equal(OutboxDispatchOutcome.Transient, result.Outcome);
        Assert.NotNull(result.RetryAfter);
        Assert.Equal(TimeSpan.FromSeconds(12), result.RetryAfter);
    }

    [Fact]
    public void ClassifyTransportFailure_HttpRequestException4xx_Permanent()
    {
        var dispatcher = NewDispatcherForClassificationOnly();
        var entry = MinimalEntry("e1");
        var ex = new HttpRequestException("bad request", inner: null, statusCode: HttpStatusCode.BadRequest);

        var result = dispatcher.ClassifyTransportFailure(entry, ex);

        Assert.Equal(OutboxDispatchOutcome.Permanent, result.Outcome);
    }

    [Fact]
    public void ClassifyTransportFailure_HttpRequestException5xx_Transient()
    {
        var dispatcher = NewDispatcherForClassificationOnly();
        var entry = MinimalEntry("e1");
        var ex = new HttpRequestException("upstream", inner: null, statusCode: HttpStatusCode.BadGateway);

        var result = dispatcher.ClassifyTransportFailure(entry, ex);

        Assert.Equal(OutboxDispatchOutcome.Transient, result.Outcome);
    }

    [Fact]
    public void ClassifyTransportFailure_TaskCanceledException_Transient()
    {
        var dispatcher = NewDispatcherForClassificationOnly();
        var entry = MinimalEntry("e1");
        var ex = new TaskCanceledException("timeout");

        var result = dispatcher.ClassifyTransportFailure(entry, ex);

        Assert.Equal(OutboxDispatchOutcome.Transient, result.Outcome);
    }

    [Fact]
    public void ClassifyTransportFailure_ConversationReferenceNotFound_Permanent()
    {
        var dispatcher = NewDispatcherForClassificationOnly();
        var entry = MinimalEntry("e1");
        var ex = ConversationReferenceNotFoundException.ForUser("tenant-1", "user-1");

        var result = dispatcher.ClassifyTransportFailure(entry, ex);

        Assert.Equal(OutboxDispatchOutcome.Permanent, result.Outcome);
    }

    [Fact]
    public void ClassifyTransportFailure_UnknownExceptionType_Permanent()
    {
        var dispatcher = NewDispatcherForClassificationOnly();
        var entry = MinimalEntry("e1");
        var ex = new InvalidOperationException("contract violation");

        var result = dispatcher.ClassifyTransportFailure(entry, ex);

        Assert.Equal(OutboxDispatchOutcome.Permanent, result.Outcome);
    }

    // -----------------------------------------------------------------------------------
    // Critique #3 — Layer-2 idempotency: pre-existing card-state row short-circuits send.
    // -----------------------------------------------------------------------------------

    [Fact]
    public async Task DispatchAsync_AgentQuestion_CardStateExists_ReturnsSuccessWithoutCallingAdapter()
    {
        var question = SampleQuestion("q-1");
        var adapter = new ThrowingCloudAdapter();
        var cardStore = new RecordingCardStateStore();
        cardStore.Preload[question.QuestionId] = new TeamsCardState
        {
            QuestionId = question.QuestionId,
            ActivityId = "act-pre-existing",
            ConversationId = "conv-pre-existing",
            ConversationReferenceJson = "{}",
            CreatedAt = DateTimeOffset.UnixEpoch,
            UpdatedAt = DateTimeOffset.UnixEpoch,
        };

        var outbox = new RecordingOutbox();
        var dispatcher = NewDispatcher(adapter, outbox, cardStore, new RecordingAgentQuestionStore(), new StubCardRenderer());

        var entry = NewQuestionEntry("e-q-1", question);
        var result = await dispatcher.DispatchAsync(entry, CancellationToken.None);

        Assert.Equal(OutboxDispatchOutcome.Success, result.Outcome);
        Assert.NotNull(result.Receipt);
        Assert.Equal("act-pre-existing", result.Receipt!.Value.ActivityId);
        Assert.Equal("conv-pre-existing", result.Receipt!.Value.ConversationId);
        Assert.Empty(adapter.ContinueCalls);
        Assert.Empty(outbox.SendReceiptCalls);
    }

    // -----------------------------------------------------------------------------------
    // Critique #3 — Layer-1 idempotency: outbox-row ActivityId short-circuits send.
    // -----------------------------------------------------------------------------------

    [Fact]
    public async Task DispatchAsync_AgentQuestion_EntryHasActivityIdButNoCardState_ReplaysCardStateSaveOnly()
    {
        var question = SampleQuestion("q-2");
        var adapter = new ThrowingCloudAdapter();
        var cardStore = new RecordingCardStateStore();
        var qStore = new RecordingAgentQuestionStore();
        var outbox = new RecordingOutbox();
        var dispatcher = NewDispatcher(adapter, outbox, cardStore, qStore, new StubCardRenderer());

        var entry = NewQuestionEntry("e-q-2", question) with
        {
            ActivityId = "act-from-prior-attempt",
            ConversationId = "conv-from-prior-attempt",
        };

        var result = await dispatcher.DispatchAsync(entry, CancellationToken.None);

        Assert.Equal(OutboxDispatchOutcome.Success, result.Outcome);
        Assert.NotNull(result.Receipt);
        Assert.Equal("act-from-prior-attempt", result.Receipt!.Value.ActivityId);
        Assert.Equal("conv-from-prior-attempt", result.Receipt!.Value.ConversationId);
        // Adapter must NOT be called — Layer-1 idempotency short-circuits the BF send.
        Assert.Empty(adapter.ContinueCalls);
        // Card-state save IS retried (the prior failure path), using the row's ids.
        Assert.Single(cardStore.Saved);
        Assert.Equal("act-from-prior-attempt", cardStore.Saved[0].ActivityId);
        // No RecordSendReceiptAsync call — receipt already persisted from the prior attempt.
        Assert.Empty(outbox.SendReceiptCalls);
    }

    [Fact]
    public async Task DispatchAsync_MessengerMessage_EntryHasActivityId_ReturnsSuccessWithoutCallingAdapter()
    {
        var adapter = new ThrowingCloudAdapter();
        var outbox = new RecordingOutbox();
        var dispatcher = NewDispatcher(adapter, outbox, new RecordingCardStateStore(),
            new RecordingAgentQuestionStore(), new StubCardRenderer());

        var entry = NewMessageEntry("e-m-1", SampleMessage("m-1")) with
        {
            ActivityId = "act-replay",
            ConversationId = "conv-replay",
        };

        var result = await dispatcher.DispatchAsync(entry, CancellationToken.None);

        Assert.Equal(OutboxDispatchOutcome.Success, result.Outcome);
        Assert.NotNull(result.Receipt);
        Assert.Equal("act-replay", result.Receipt!.Value.ActivityId);
        Assert.Empty(adapter.ContinueCalls);
    }

    // -----------------------------------------------------------------------------------
    // Critique #3 — Post-send durability ordering: receipt persisted before card-state save.
    // -----------------------------------------------------------------------------------

    [Fact]
    public async Task DispatchAsync_AgentQuestion_CardStateSaveFails_ReturnsTransientAndPersistsReceiptFirst()
    {
        var question = SampleQuestion("q-3");
        var adapter = new SendingCloudAdapter
        {
            FixedActivityId = "act-fresh",
            FixedConversationId = "conv-fresh",
        };
        var cardStore = new FailingCardStateStore(); // SaveAsync throws.
        var qStore = new RecordingAgentQuestionStore();
        var outbox = new RecordingOutbox();
        var dispatcher = NewDispatcher(adapter, outbox, cardStore, qStore, new StubCardRenderer());

        var entry = NewQuestionEntry("e-q-3", question);
        var result = await dispatcher.DispatchAsync(entry, CancellationToken.None);

        // Transient because card-state save failed, NOT permanent — retry will replay
        // only the post-send persistence via Layer-1 idempotency.
        Assert.Equal(OutboxDispatchOutcome.Transient, result.Outcome);
        // CRITICAL — RecordSendReceiptAsync was called BEFORE the cardstate save attempt,
        // so the next retry can short-circuit the BF send.
        Assert.Single(outbox.SendReceiptCalls);
        Assert.Equal("e-q-3", outbox.SendReceiptCalls[0].EntryId);
        Assert.Equal("act-fresh", outbox.SendReceiptCalls[0].Receipt.ActivityId);
        Assert.Equal("conv-fresh", outbox.SendReceiptCalls[0].Receipt.ConversationId);
        // Adapter WAS called once (initial send).
        Assert.Single(adapter.ContinueCalls);
    }

    [Fact]
    public async Task DispatchAsync_AgentQuestion_HappyPath_RecordSendReceiptCalledBeforeCardStateSave()
    {
        var question = SampleQuestion("q-4");
        var adapter = new SendingCloudAdapter
        {
            FixedActivityId = "act-fresh",
            FixedConversationId = "conv-fresh",
        };
        var orderedSink = new OrderedCallSink();
        var cardStore = new OrderedRecordingCardStateStore(orderedSink);
        var qStore = new RecordingAgentQuestionStore();
        var outbox = new OrderedRecordingOutbox(orderedSink);
        var dispatcher = NewDispatcher(adapter, outbox, cardStore, qStore, new StubCardRenderer());

        var entry = NewQuestionEntry("e-q-4", question);
        var result = await dispatcher.DispatchAsync(entry, CancellationToken.None);

        Assert.Equal(OutboxDispatchOutcome.Success, result.Outcome);
        Assert.Equal(new[] { "RecordSendReceiptAsync", "CardStateStore.SaveAsync" }, orderedSink.Calls);
    }

    // -----------------------------------------------------------------------------------
    // Critique #3 — card-state lookup failure is transient (not silent success).
    // -----------------------------------------------------------------------------------

    [Fact]
    public async Task DispatchAsync_AgentQuestion_CardStateLookupThrows_ReturnsTransient()
    {
        var question = SampleQuestion("q-5");
        var adapter = new ThrowingCloudAdapter();
        var cardStore = new ThrowingLookupCardStateStore();
        var outbox = new RecordingOutbox();
        var dispatcher = NewDispatcher(adapter, outbox, cardStore, new RecordingAgentQuestionStore(), new StubCardRenderer());

        var entry = NewQuestionEntry("e-q-5", question);
        var result = await dispatcher.DispatchAsync(entry, CancellationToken.None);

        Assert.Equal(OutboxDispatchOutcome.Transient, result.Outcome);
        // Adapter must NOT be called — we never know whether a prior attempt succeeded,
        // so we cannot risk a duplicate send.
        Assert.Empty(adapter.ContinueCalls);
    }

    // -----------------------------------------------------------------------------------
    // Misc — guard paths.
    // -----------------------------------------------------------------------------------

    [Fact]
    public async Task DispatchAsync_MissingConversationReferenceJson_ReturnsPermanent()
    {
        var dispatcher = NewDispatcher(new ThrowingCloudAdapter(), new RecordingOutbox(),
            new RecordingCardStateStore(), new RecordingAgentQuestionStore(), new StubCardRenderer());

        var entry = MinimalEntry("e1") with
        {
            ConversationReferenceJson = null,
        };

        var result = await dispatcher.DispatchAsync(entry, CancellationToken.None);

        Assert.Equal(OutboxDispatchOutcome.Permanent, result.Outcome);
    }

    [Fact]
    public async Task DispatchAsync_MalformedConversationReferenceJson_ReturnsPermanent()
    {
        var dispatcher = NewDispatcher(new ThrowingCloudAdapter(), new RecordingOutbox(),
            new RecordingCardStateStore(), new RecordingAgentQuestionStore(), new StubCardRenderer());

        var entry = MinimalEntry("e1") with
        {
            ConversationReferenceJson = "{not-valid-json",
        };

        var result = await dispatcher.DispatchAsync(entry, CancellationToken.None);

        Assert.Equal(OutboxDispatchOutcome.Permanent, result.Outcome);
    }

    [Fact]
    public async Task DispatchAsync_UnknownPayloadType_ReturnsPermanent()
    {
        var dispatcher = NewDispatcher(new ThrowingCloudAdapter(), new RecordingOutbox(),
            new RecordingCardStateStore(), new RecordingAgentQuestionStore(), new StubCardRenderer());

        var entry = MinimalEntry("e1") with
        {
            PayloadType = "UnknownType",
            PayloadJson = "{}",
            ConversationReferenceJson = SampleReferenceJson(),
        };

        var result = await dispatcher.DispatchAsync(entry, CancellationToken.None);

        Assert.Equal(OutboxDispatchOutcome.Permanent, result.Outcome);
    }

    // -----------------------------------------------------------------------------------
    // Helpers.
    // -----------------------------------------------------------------------------------

    private static TeamsOutboxDispatcher NewDispatcherForClassificationOnly()
    {
        // Classification methods are pure-ish; constructor takes the full graph but
        // ClassifyTransportFailure / IsTransientStatusCode never touch the collaborators.
        return new TeamsOutboxDispatcher(
            adapter: new ThrowingCloudAdapter(),
            options: new TeamsMessagingOptions { MicrosoftAppId = "test-app" },
            outbox: new RecordingOutbox(),
            cardStateStore: new RecordingCardStateStore(),
            agentQuestionStore: new RecordingAgentQuestionStore(),
            cardRenderer: new StubCardRenderer(),
            logger: NullLogger<TeamsOutboxDispatcher>.Instance);
    }

    private static TeamsOutboxDispatcher NewDispatcher(
        CloudAdapter adapter,
        IMessageOutbox outbox,
        ICardStateStore cardStore,
        IAgentQuestionStore qStore,
        IAdaptiveCardRenderer renderer)
    {
        return new TeamsOutboxDispatcher(
            adapter: adapter,
            options: new TeamsMessagingOptions { MicrosoftAppId = "test-app" },
            outbox: outbox,
            cardStateStore: cardStore,
            agentQuestionStore: qStore,
            cardRenderer: renderer,
            logger: NullLogger<TeamsOutboxDispatcher>.Instance);
    }

    private static OutboxEntry MinimalEntry(string id) => new()
    {
        OutboxEntryId = id,
        CorrelationId = $"corr-{id}",
        Destination = "teams://tenant-1/user/user-1",
        PayloadType = OutboxPayloadTypes.MessengerMessage,
        PayloadJson = "{}",
        ConversationReferenceJson = SampleReferenceJson(),
        CreatedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
    };

    private static OutboxEntry NewMessageEntry(string id, MessengerMessage message) => new()
    {
        OutboxEntryId = id,
        CorrelationId = $"corr-{id}",
        Destination = "teams://tenant-1/user/user-1",
        PayloadType = OutboxPayloadTypes.MessengerMessage,
        PayloadJson = System.Text.Json.JsonSerializer.Serialize(
            new TeamsOutboxPayloadEnvelope { Message = message },
            TeamsOutboxPayloadEnvelope.JsonOptions),
        ConversationReferenceJson = SampleReferenceJson(),
        CreatedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
    };

    private static OutboxEntry NewQuestionEntry(string id, AgentQuestion question) => new()
    {
        OutboxEntryId = id,
        CorrelationId = $"corr-{id}",
        Destination = "teams://tenant-1/user/user-1",
        PayloadType = OutboxPayloadTypes.AgentQuestion,
        PayloadJson = System.Text.Json.JsonSerializer.Serialize(
            new TeamsOutboxPayloadEnvelope { Question = question },
            TeamsOutboxPayloadEnvelope.JsonOptions),
        ConversationReferenceJson = SampleReferenceJson(),
        CreatedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
    };

    private static string SampleReferenceJson()
    {
        var reference = new ConversationReference
        {
            ChannelId = "msteams",
            ServiceUrl = "https://smba.trafficmanager.net/test/",
            Conversation = new ConversationAccount(id: "conv-1"),
            User = new ChannelAccount(id: "user-1"),
            Bot = new ChannelAccount(id: "bot-1"),
        };
        return JsonConvert.SerializeObject(reference);
    }

    private static MessengerMessage SampleMessage(string id) => new(
        MessageId: id,
        CorrelationId: $"corr-{id}",
        AgentId: "agent-1",
        TaskId: "task-1",
        ConversationId: "conv-1",
        Body: "hello",
        Severity: MessageSeverities.Info,
        Timestamp: DateTimeOffset.UnixEpoch);

    private static AgentQuestion SampleQuestion(string id) => new()
    {
        QuestionId = id,
        AgentId = "agent-1",
        TaskId = "task-1",
        TenantId = "tenant-1",
        TargetUserId = "user-1",
        Title = "Approve?",
        Body = "Please approve the operation.",
        Severity = MessageSeverities.Info,
        AllowedActions = new[]
        {
            new HumanAction("approve", "Approve", "approve", RequiresComment: false),
            new HumanAction("reject", "Reject", "reject", RequiresComment: false),
        },
        ExpiresAt = DateTimeOffset.UnixEpoch.AddHours(1),
        CorrelationId = $"corr-{id}",
    };

    private static ErrorResponseException BuildErrorResponseException(HttpStatusCode statusCode)
    {
        return new ErrorResponseException($"HTTP {(int)statusCode}")
        {
            Response = new HttpResponseMessageWrapper(new HttpResponseMessage(statusCode), string.Empty),
        };
    }

    // ----- Test doubles --------------------------------------------------------------

    /// <summary>A <see cref="CloudAdapter"/> that throws if any send path is exercised —
    /// used to assert idempotency short-circuits the BF call entirely.</summary>
    private sealed class ThrowingCloudAdapter : CloudAdapter
    {
        public List<ConversationReference> ContinueCalls { get; } = new();

        public override Task ContinueConversationAsync(
            string botAppId,
            ConversationReference reference,
            BotCallbackHandler callback,
            CancellationToken cancellationToken)
        {
            ContinueCalls.Add(reference);
            throw new InvalidOperationException(
                "ThrowingCloudAdapter.ContinueConversationAsync was called; idempotency layer failed to short-circuit the send.");
        }
    }

    /// <summary>A <see cref="CloudAdapter"/> that records the call and synthesises a
    /// successful send with the configured identifiers.</summary>
    private sealed class SendingCloudAdapter : CloudAdapter
    {
        public string FixedActivityId { get; set; } = "act-default";
        public string FixedConversationId { get; set; } = "conv-default";
        public List<ConversationReference> ContinueCalls { get; } = new();

        public override Task ContinueConversationAsync(
            string botAppId,
            ConversationReference reference,
            BotCallbackHandler callback,
            CancellationToken cancellationToken)
        {
            ContinueCalls.Add(reference);
            var continuation = (Activity)reference.GetContinuationActivity();
            // Replace the conversation id with our fixed value so the dispatcher captures it.
            continuation.Conversation = new ConversationAccount(id: FixedConversationId);
            var turnContext = new TurnContext(this, continuation);
            return callback(turnContext, cancellationToken);
        }

        public override Task<ResourceResponse[]> SendActivitiesAsync(
            ITurnContext turnContext,
            Activity[] activities,
            CancellationToken cancellationToken)
        {
            var responses = activities.Select(_ => new ResourceResponse(FixedActivityId)).ToArray();
            return Task.FromResult(responses);
        }
    }

    private sealed class StubCardRenderer : IAdaptiveCardRenderer
    {
        private static Attachment NewCard()
            => new() { ContentType = "application/vnd.microsoft.card.adaptive", Content = new { } };

        public Attachment RenderQuestionCard(AgentQuestion question) => NewCard();
        public Attachment RenderStatusCard(AgentStatusSummary status) => NewCard();
        public Attachment RenderIncidentCard(IncidentSummary incident) => NewCard();
        public Attachment RenderReleaseGateCard(ReleaseGateRequest gate) => NewCard();
        public Attachment RenderDecisionConfirmationCard(HumanDecisionEvent decision) => NewCard();
        public Attachment RenderDecisionConfirmationCard(HumanDecisionEvent decision, string? actorDisplayName) => NewCard();
        public Attachment RenderExpiredNoticeCard(string questionId) => NewCard();
        public Attachment RenderCancelledNoticeCard(string questionId) => NewCard();
    }

    private sealed class RecordingOutbox : IMessageOutbox
    {
        public List<(string EntryId, OutboxDeliveryReceipt Receipt)> SendReceiptCalls { get; } = new();

        public Task EnqueueAsync(OutboxEntry entry, CancellationToken ct) => Task.CompletedTask;
        public Task<IReadOnlyList<OutboxEntry>> DequeueAsync(int batchSize, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<OutboxEntry>>(Array.Empty<OutboxEntry>());
        public Task AcknowledgeAsync(string outboxEntryId, OutboxDeliveryReceipt receipt, CancellationToken ct)
            => Task.CompletedTask;

        public Task RecordSendReceiptAsync(string outboxEntryId, OutboxDeliveryReceipt receipt, CancellationToken ct)
        {
            SendReceiptCalls.Add((outboxEntryId, receipt));
            return Task.CompletedTask;
        }

        public Task RescheduleAsync(string outboxEntryId, DateTimeOffset nextRetryAt, string error, CancellationToken ct)
            => Task.CompletedTask;
        public Task DeadLetterAsync(string outboxEntryId, string error, CancellationToken ct)
            => Task.CompletedTask;
    }

    private sealed class FailingCardStateStore : ICardStateStore
    {
        public Task SaveAsync(TeamsCardState state, CancellationToken ct)
            => throw new InvalidOperationException("simulated cardstate save failure");
        public Task<TeamsCardState?> GetByQuestionIdAsync(string questionId, CancellationToken ct)
            => Task.FromResult<TeamsCardState?>(null);
        public Task UpdateStatusAsync(string questionId, string newStatus, CancellationToken ct)
            => Task.CompletedTask;
    }

    private sealed class ThrowingLookupCardStateStore : ICardStateStore
    {
        public Task SaveAsync(TeamsCardState state, CancellationToken ct) => Task.CompletedTask;
        public Task<TeamsCardState?> GetByQuestionIdAsync(string questionId, CancellationToken ct)
            => throw new InvalidOperationException("simulated cardstate lookup failure");
        public Task UpdateStatusAsync(string questionId, string newStatus, CancellationToken ct)
            => Task.CompletedTask;
    }

    private sealed class OrderedCallSink
    {
        public List<string> Calls { get; } = new();
    }

    private sealed class OrderedRecordingOutbox : IMessageOutbox
    {
        private readonly OrderedCallSink _sink;
        public OrderedRecordingOutbox(OrderedCallSink sink) => _sink = sink;

        public Task EnqueueAsync(OutboxEntry entry, CancellationToken ct) => Task.CompletedTask;
        public Task<IReadOnlyList<OutboxEntry>> DequeueAsync(int batchSize, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<OutboxEntry>>(Array.Empty<OutboxEntry>());
        public Task AcknowledgeAsync(string outboxEntryId, OutboxDeliveryReceipt receipt, CancellationToken ct)
            => Task.CompletedTask;

        public Task RecordSendReceiptAsync(string outboxEntryId, OutboxDeliveryReceipt receipt, CancellationToken ct)
        {
            _sink.Calls.Add("RecordSendReceiptAsync");
            return Task.CompletedTask;
        }

        public Task RescheduleAsync(string outboxEntryId, DateTimeOffset nextRetryAt, string error, CancellationToken ct)
            => Task.CompletedTask;
        public Task DeadLetterAsync(string outboxEntryId, string error, CancellationToken ct)
            => Task.CompletedTask;
    }

    private sealed class OrderedRecordingCardStateStore : ICardStateStore
    {
        private readonly OrderedCallSink _sink;
        public OrderedRecordingCardStateStore(OrderedCallSink sink) => _sink = sink;

        public Task SaveAsync(TeamsCardState state, CancellationToken ct)
        {
            _sink.Calls.Add("CardStateStore.SaveAsync");
            return Task.CompletedTask;
        }

        public Task<TeamsCardState?> GetByQuestionIdAsync(string questionId, CancellationToken ct)
            => Task.FromResult<TeamsCardState?>(null);
        public Task UpdateStatusAsync(string questionId, string newStatus, CancellationToken ct)
            => Task.CompletedTask;
    }

    private sealed class RecordingAgentQuestionStore : IAgentQuestionStore
    {
        public List<(string QuestionId, string ConversationId)> ConversationIdUpdates { get; } = new();

        public Task SaveAsync(AgentQuestion question, CancellationToken ct) => Task.CompletedTask;
        public Task<AgentQuestion?> GetByIdAsync(string questionId, CancellationToken ct) => Task.FromResult<AgentQuestion?>(null);
        public Task<bool> TryUpdateStatusAsync(string questionId, string expectedStatus, string newStatus, CancellationToken ct) => Task.FromResult(false);

        public Task UpdateConversationIdAsync(string questionId, string conversationId, CancellationToken ct)
        {
            ConversationIdUpdates.Add((questionId, conversationId));
            return Task.CompletedTask;
        }

        public Task<AgentQuestion?> GetMostRecentOpenByConversationAsync(string conversationId, CancellationToken ct) => Task.FromResult<AgentQuestion?>(null);
        public Task<IReadOnlyList<AgentQuestion>> GetOpenByConversationAsync(string conversationId, CancellationToken ct) => Task.FromResult<IReadOnlyList<AgentQuestion>>(Array.Empty<AgentQuestion>());
        public Task<IReadOnlyList<AgentQuestion>> GetOpenExpiredAsync(DateTimeOffset cutoff, int batchSize, CancellationToken ct) => Task.FromResult<IReadOnlyList<AgentQuestion>>(Array.Empty<AgentQuestion>());
    }

    private sealed class RecordingCardStateStore : ICardStateStore
    {
        public List<TeamsCardState> Saved { get; } = new();
        public Dictionary<string, TeamsCardState> Preload { get; } = new(StringComparer.Ordinal);

        public Task SaveAsync(TeamsCardState state, CancellationToken ct)
        {
            Saved.Add(state);
            return Task.CompletedTask;
        }

        public Task<TeamsCardState?> GetByQuestionIdAsync(string questionId, CancellationToken ct)
        {
            Preload.TryGetValue(questionId, out var hit);
            return Task.FromResult<TeamsCardState?>(hit);
        }

        public Task UpdateStatusAsync(string questionId, string newStatus, CancellationToken ct)
            => Task.CompletedTask;
    }
}
