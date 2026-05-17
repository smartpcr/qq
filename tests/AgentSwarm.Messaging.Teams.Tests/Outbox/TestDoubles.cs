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
/// </summary>
internal sealed class RecordingConversationReferenceStore : IConversationReferenceStore, IConversationReferenceRouter
{
    public Dictionary<(string TenantId, string InternalUserId), TeamsConversationReference> UserReferences { get; } = new();
    public Dictionary<(string TenantId, string ChannelId), TeamsConversationReference> ChannelReferences { get; } = new();
    public Dictionary<string, TeamsConversationReference> ConversationIdReferences { get; } = new();

    public Task SaveOrUpdateAsync(TeamsConversationReference reference, CancellationToken ct) => Task.CompletedTask;

    public Task<TeamsConversationReference?> GetAsync(string tenantId, string aadObjectId, CancellationToken ct)
        => Task.FromResult<TeamsConversationReference?>(null);

    public Task<TeamsConversationReference?> GetByAadObjectIdAsync(string tenantId, string aadObjectId, CancellationToken ct)
        => Task.FromResult<TeamsConversationReference?>(null);

    public Task<TeamsConversationReference?> GetByInternalUserIdAsync(string tenantId, string internalUserId, CancellationToken ct)
    {
        UserReferences.TryGetValue((tenantId, internalUserId), out var r);
        return Task.FromResult<TeamsConversationReference?>(r);
    }

    public Task<TeamsConversationReference?> GetByChannelIdAsync(string tenantId, string channelId, CancellationToken ct)
    {
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
