using AgentSwarm.Messaging.Abstractions;
using AgentSwarm.Messaging.Core;

namespace AgentSwarm.Messaging.Teams.Tests.Outbox;

/// <summary>Recording <see cref="IProactiveNotifier"/> stub for outbox tests.</summary>
internal sealed class RecordingProactiveNotifier : IProactiveNotifier
{
    public List<(string TenantId, string UserId)> SendProactiveCalls { get; } = new();
    public List<(string TenantId, string UserId)> SendProactiveQuestionCalls { get; } = new();
    public List<(string TenantId, string ChannelId)> SendToChannelCalls { get; } = new();
    public List<(string TenantId, string ChannelId)> SendQuestionToChannelCalls { get; } = new();

    public Task SendProactiveAsync(string tenantId, string userId, MessengerMessage message, CancellationToken ct)
    {
        SendProactiveCalls.Add((tenantId, userId));
        return Task.CompletedTask;
    }

    public Task SendProactiveQuestionAsync(string tenantId, string userId, AgentQuestion question, CancellationToken ct)
    {
        SendProactiveQuestionCalls.Add((tenantId, userId));
        return Task.CompletedTask;
    }

    public Task SendToChannelAsync(string tenantId, string channelId, MessengerMessage message, CancellationToken ct)
    {
        SendToChannelCalls.Add((tenantId, channelId));
        return Task.CompletedTask;
    }

    public Task SendQuestionToChannelAsync(string tenantId, string channelId, AgentQuestion question, CancellationToken ct)
    {
        SendQuestionToChannelCalls.Add((tenantId, channelId));
        return Task.CompletedTask;
    }
}

/// <summary>Throwing <see cref="IProactiveNotifier"/> stub for failure-classification tests.</summary>
internal sealed class ThrowingProactiveNotifier : IProactiveNotifier
{
    private readonly Exception _exception;

    public ThrowingProactiveNotifier(Exception exception) => _exception = exception;

    public Task SendProactiveAsync(string tenantId, string userId, MessengerMessage message, CancellationToken ct)
        => throw _exception;

    public Task SendProactiveQuestionAsync(string tenantId, string userId, AgentQuestion question, CancellationToken ct)
        => throw _exception;

    public Task SendToChannelAsync(string tenantId, string channelId, MessengerMessage message, CancellationToken ct)
        => throw _exception;

    public Task SendQuestionToChannelAsync(string tenantId, string channelId, AgentQuestion question, CancellationToken ct)
        => throw _exception;
}

/// <summary>Recording <see cref="IMessengerConnector"/> stub.</summary>
internal sealed class RecordingMessengerConnector : IMessengerConnector
{
    public List<MessengerMessage> SentMessages { get; } = new();
    public List<AgentQuestion> SentQuestions { get; } = new();

    public Task SendMessageAsync(MessengerMessage message, CancellationToken ct)
    {
        SentMessages.Add(message);
        return Task.CompletedTask;
    }

    public Task SendQuestionAsync(AgentQuestion question, CancellationToken ct)
    {
        SentQuestions.Add(question);
        return Task.CompletedTask;
    }

    public Task<MessengerEvent> ReceiveAsync(CancellationToken ct)
        => Task.FromException<MessengerEvent>(new InvalidOperationException("ReceiveAsync not expected in these tests."));
}

/// <summary>
/// In-memory <see cref="IConversationReferenceStore"/> + <see cref="IConversationReferenceRouter"/>
/// pair used by the Stage 6.1 decorator tests. Tests pre-populate
/// <see cref="UserReferences"/> / <see cref="ChannelReferences"/> /
/// <see cref="ConversationIdReferences"/> with the references the decorator should find,
/// or leave the dictionaries empty to exercise the not-found error paths.
/// <see cref="LookupCalls"/> records every successful or attempted lookup so tests can
/// assert which contract method the decorator under test invoked (e.g. confirm
/// <c>GetByInternalUserIdAsync(TenantId, TargetUserId)</c> was used for question routing
/// instead of <c>GetByConversationIdAsync(TargetUserId)</c>).
/// </summary>
internal sealed class RecordingConversationReferenceStore : IConversationReferenceStore, IConversationReferenceRouter
{
    public Dictionary<(string TenantId, string InternalUserId), TeamsConversationReference> UserReferences { get; } = new();
    public Dictionary<(string TenantId, string ChannelId), TeamsConversationReference> ChannelReferences { get; } = new();
    public Dictionary<string, TeamsConversationReference> ConversationIdReferences { get; } = new();

    /// <summary>
    /// Every lookup invocation recorded in call order. Each entry is a formatted
    /// <c>Method:arg1[:arg2]</c> token so tests can `Assert.Contains` on the exact
    /// contract used by the decorator (iter-2 evaluator critique #2 verification).
    /// </summary>
    public List<string> LookupCalls { get; } = new();

    public Task SaveOrUpdateAsync(TeamsConversationReference reference, CancellationToken ct) => Task.CompletedTask;

    public Task<TeamsConversationReference?> GetAsync(string tenantId, string aadObjectId, CancellationToken ct)
        => Task.FromResult<TeamsConversationReference?>(null);

    public Task<TeamsConversationReference?> GetByAadObjectIdAsync(string tenantId, string aadObjectId, CancellationToken ct)
        => Task.FromResult<TeamsConversationReference?>(null);

    public Task<TeamsConversationReference?> GetByInternalUserIdAsync(string tenantId, string internalUserId, CancellationToken ct)
    {
        LookupCalls.Add($"GetByInternalUserIdAsync:{tenantId}:{internalUserId}");
        UserReferences.TryGetValue((tenantId, internalUserId), out var r);
        return Task.FromResult<TeamsConversationReference?>(r);
    }

    public Task<TeamsConversationReference?> GetByChannelIdAsync(string tenantId, string channelId, CancellationToken ct)
    {
        LookupCalls.Add($"GetByChannelIdAsync:{tenantId}:{channelId}");
        ChannelReferences.TryGetValue((tenantId, channelId), out var r);
        return Task.FromResult<TeamsConversationReference?>(r);
    }

    public Task<IReadOnlyList<TeamsConversationReference>> GetActiveChannelsByTeamIdAsync(string tenantId, string teamId, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<TeamsConversationReference>>(Array.Empty<TeamsConversationReference>());

    public Task<IReadOnlyList<TeamsConversationReference>> GetAllActiveAsync(string tenantId, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<TeamsConversationReference>>(Array.Empty<TeamsConversationReference>());

    public Task<bool> IsActiveAsync(string tenantId, string aadObjectId, CancellationToken ct) => Task.FromResult(true);
    public Task<bool> IsActiveByInternalUserIdAsync(string tenantId, string internalUserId, CancellationToken ct) => Task.FromResult(true);
    public Task<bool> IsActiveByChannelAsync(string tenantId, string channelId, CancellationToken ct) => Task.FromResult(true);

    public Task MarkInactiveAsync(string tenantId, string aadObjectId, CancellationToken ct) => Task.CompletedTask;
    public Task MarkInactiveByChannelAsync(string tenantId, string channelId, CancellationToken ct) => Task.CompletedTask;
    public Task DeleteAsync(string tenantId, string aadObjectId, CancellationToken ct) => Task.CompletedTask;
    public Task DeleteByChannelAsync(string tenantId, string channelId, CancellationToken ct) => Task.CompletedTask;

    public Task<TeamsConversationReference?> GetByConversationIdAsync(string conversationId, CancellationToken ct)
    {
        LookupCalls.Add($"GetByConversationIdAsync:{conversationId}");
        ConversationIdReferences.TryGetValue(conversationId, out var r);
        return Task.FromResult<TeamsConversationReference?>(r);
    }
}

/// <summary>
/// Trivially-recording <see cref="IMessageOutbox"/> for tests that only need to assert on
/// the enqueued entries — Dequeue/Ack/Reschedule/DeadLetter are not exercised here (the
/// <see cref="SqlMessageOutbox"/> tests cover those paths against a real SQLite database).
/// </summary>
internal sealed class InMemoryRecordingOutbox : IMessageOutbox
{
    public List<OutboxEntry> Enqueued { get; } = new();

    public Task EnqueueAsync(OutboxEntry entry, CancellationToken ct)
    {
        Enqueued.Add(entry);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<OutboxEntry>> DequeueAsync(int batchSize, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<OutboxEntry>>(Array.Empty<OutboxEntry>());

    public Task AcknowledgeAsync(string outboxEntryId, OutboxDeliveryReceipt receipt, CancellationToken ct)
        => Task.CompletedTask;

    public Task RecordSendReceiptAsync(string outboxEntryId, OutboxDeliveryReceipt receipt, CancellationToken ct)
        => Task.CompletedTask;

    public Task RescheduleAsync(string outboxEntryId, DateTimeOffset nextRetryAt, string error, CancellationToken ct)
        => Task.CompletedTask;

    public Task DeadLetterAsync(string outboxEntryId, string error, CancellationToken ct)
        => Task.CompletedTask;
}

/// <summary>
/// Recording <see cref="IAgentQuestionStore"/> for the Stage 6.1 decorator tests. Tracks
/// every <see cref="SaveAsync"/> / <see cref="GetByIdAsync"/> /
/// <see cref="UpdateConversationIdAsync"/> invocation and supports seeding pre-existing
/// rows so the check-then-save retry path can be exercised. The recorded
/// <see cref="SavedQuestions"/> list preserves call order, allowing tests to assert that
/// the pre-enqueue <c>SaveAsync</c> happened BEFORE the outbox <c>EnqueueAsync</c>
/// (compare <see cref="OperationLog"/> across this double and
/// <see cref="InMemoryRecordingOutbox"/>).
/// </summary>
internal sealed class RecordingAgentQuestionStore : IAgentQuestionStore
{
    private readonly Dictionary<string, AgentQuestion> _byId = new(StringComparer.Ordinal);

    public List<AgentQuestion> SavedQuestions { get; } = new();
    public List<string> GetByIdCalls { get; } = new();
    public List<(string QuestionId, string ConversationId)> UpdateConversationIdCalls { get; } = new();

    /// <summary>
    /// Shared ordering log threaded through tests that need to pin "SaveAsync happens
    /// before EnqueueAsync". Tests append a token after each call so the assertion can
    /// inspect the relative order without depending on collection sizes.
    /// </summary>
    public List<string> OperationLog { get; } = new();

    public void Seed(AgentQuestion question)
    {
        _byId[question.QuestionId] = question;
    }

    public Task SaveAsync(AgentQuestion question, CancellationToken ct)
    {
        _byId[question.QuestionId] = question;
        SavedQuestions.Add(question);
        OperationLog.Add($"SaveAsync:{question.QuestionId}");
        return Task.CompletedTask;
    }

    public Task<AgentQuestion?> GetByIdAsync(string questionId, CancellationToken ct)
    {
        GetByIdCalls.Add(questionId);
        _byId.TryGetValue(questionId, out var hit);
        return Task.FromResult<AgentQuestion?>(hit);
    }

    public Task<bool> TryUpdateStatusAsync(string questionId, string expectedStatus, string newStatus, CancellationToken ct)
        => Task.FromResult(false);

    public Task UpdateConversationIdAsync(string questionId, string conversationId, CancellationToken ct)
    {
        UpdateConversationIdCalls.Add((questionId, conversationId));
        if (_byId.TryGetValue(questionId, out var existing))
        {
            _byId[questionId] = existing with { ConversationId = conversationId };
        }
        return Task.CompletedTask;
    }

    public Task<AgentQuestion?> GetMostRecentOpenByConversationAsync(string conversationId, CancellationToken ct)
        => Task.FromResult<AgentQuestion?>(null);

    public Task<IReadOnlyList<AgentQuestion>> GetOpenByConversationAsync(string conversationId, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<AgentQuestion>>(Array.Empty<AgentQuestion>());

    public Task<IReadOnlyList<AgentQuestion>> GetOpenExpiredAsync(DateTimeOffset cutoff, int batchSize, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<AgentQuestion>>(Array.Empty<AgentQuestion>());
}

/// <summary>
/// Variant of <see cref="InMemoryRecordingOutbox"/> that mirrors enqueues into a
/// shared <see cref="RecordingAgentQuestionStore.OperationLog"/> so tests can pin the
/// "SaveAsync before EnqueueAsync" ordering. The store and outbox both append into the
/// same log instance, which is exposed back via the <see cref="OperationLog"/> property
/// for inspection.
/// </summary>
internal sealed class OrderingTrackingOutbox : IMessageOutbox
{
    public OrderingTrackingOutbox(List<string> operationLog)
    {
        OperationLog = operationLog;
    }

    public List<string> OperationLog { get; }
    public List<OutboxEntry> Enqueued { get; } = new();

    public Task EnqueueAsync(OutboxEntry entry, CancellationToken ct)
    {
        Enqueued.Add(entry);
        OperationLog.Add($"EnqueueAsync:{entry.PayloadType}");
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<OutboxEntry>> DequeueAsync(int batchSize, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<OutboxEntry>>(Array.Empty<OutboxEntry>());

    public Task AcknowledgeAsync(string outboxEntryId, OutboxDeliveryReceipt receipt, CancellationToken ct)
        => Task.CompletedTask;

    public Task RecordSendReceiptAsync(string outboxEntryId, OutboxDeliveryReceipt receipt, CancellationToken ct)
        => Task.CompletedTask;

    public Task RescheduleAsync(string outboxEntryId, DateTimeOffset nextRetryAt, string error, CancellationToken ct)
        => Task.CompletedTask;

    public Task DeadLetterAsync(string outboxEntryId, string error, CancellationToken ct)
        => Task.CompletedTask;
}
