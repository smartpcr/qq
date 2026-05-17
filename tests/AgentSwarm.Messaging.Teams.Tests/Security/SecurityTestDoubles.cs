using System.Collections.Concurrent;
using System.Security.Claims;
using AgentSwarm.Messaging.Abstractions;
using AgentSwarm.Messaging.Core;
using AgentSwarm.Messaging.Teams;
using AgentSwarm.Messaging.Teams.Security;
using Microsoft.Bot.Connector;
using Microsoft.Bot.Connector.Authentication;

namespace AgentSwarm.Messaging.Teams.Tests.Security;

/// <summary>
/// Hand-rolled in-memory test doubles for the Stage 5.1 security surface. Follows the
/// convention used throughout this test project: no mocking library, recording fields on the
/// public surface so tests can assert behaviour directly.
/// </summary>
internal static class SecurityTestDoubles
{
    /// <summary>
    /// In-memory recording <see cref="IMessageOutbox"/> used by
    /// <c>InstallationStateGateTests</c>. Captures every <see cref="DeadLetterAsync"/> call
    /// so the gate's dead-letter contract can be verified without a SQL outbox.
    /// </summary>
    public sealed class RecordingMessageOutbox : IMessageOutbox
    {
        public List<(string OutboxEntryId, string Error)> DeadLettered { get; } = new();
        public List<OutboxEntry> Enqueued { get; } = new();
        public List<(string OutboxEntryId, OutboxDeliveryReceipt Receipt)> Acknowledged { get; } = new();
        public List<(string OutboxEntryId, OutboxDeliveryReceipt Receipt)> ReceiptsRecorded { get; } = new();
        public List<(string OutboxEntryId, DateTimeOffset NextRetryAt, string Error)> Rescheduled { get; } = new();
        public Exception? DeadLetterThrow { get; set; }

        public Task EnqueueAsync(OutboxEntry entry, CancellationToken ct)
        {
            Enqueued.Add(entry);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<OutboxEntry>> DequeueAsync(int batchSize, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<OutboxEntry>>(Array.Empty<OutboxEntry>());

        public Task AcknowledgeAsync(string outboxEntryId, OutboxDeliveryReceipt receipt, CancellationToken ct)
        {
            Acknowledged.Add((outboxEntryId, receipt));
            return Task.CompletedTask;
        }

        public Task RecordSendReceiptAsync(string outboxEntryId, OutboxDeliveryReceipt receipt, CancellationToken ct)
        {
            ReceiptsRecorded.Add((outboxEntryId, receipt));
            return Task.CompletedTask;
        }

        public Task RescheduleAsync(string outboxEntryId, DateTimeOffset nextRetryAt, string error, CancellationToken ct)
        {
            Rescheduled.Add((outboxEntryId, nextRetryAt, error));
            return Task.CompletedTask;
        }

        public Task DeadLetterAsync(string outboxEntryId, string error, CancellationToken ct)
        {
            DeadLettered.Add((outboxEntryId, error));
            if (DeadLetterThrow is not null)
            {
                throw DeadLetterThrow;
            }

            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// In-memory recording <see cref="AgentSwarm.Messaging.Persistence.IAuditLogger"/> used
    /// by Stage 5.1 security tests; captures every emitted <c>AuditEntry</c> in insertion
    /// order. Mirrors <c>TestDoubles.RecordingAuditLogger</c> but lives in the
    /// <c>Security</c> namespace so tests can resolve it without an outer-class import.
    /// </summary>
    public sealed class RecordingAuditLogger : AgentSwarm.Messaging.Persistence.IAuditLogger
    {
        public List<AgentSwarm.Messaging.Persistence.AuditEntry> Entries { get; } = new();
        public Exception? Throw { get; set; }

        public Task LogAsync(AgentSwarm.Messaging.Persistence.AuditEntry entry, CancellationToken cancellationToken)
        {
            Entries.Add(entry);
            if (Throw is not null)
            {
                throw Throw;
            }

            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// In-memory <see cref="IConversationReferenceStore"/> stub configurable per
    /// installation-gate scenario. Only the <c>IsActive*Async</c> probes are needed by
    /// <c>InstallationStateGate</c>; the remaining methods throw to surface accidental
    /// misuse.
    /// </summary>
    public sealed class StubConversationReferenceStore : IConversationReferenceStore
    {
        public Dictionary<(string TenantId, string InternalUserId), bool> UserActiveMap { get; }
            = new();

        public Dictionary<(string TenantId, string ChannelId), bool> ChannelActiveMap { get; }
            = new();

        public List<(string TenantId, string InternalUserId)> UserProbeCalls { get; } = new();
        public List<(string TenantId, string ChannelId)> ChannelProbeCalls { get; } = new();

        /// <summary>Optional list returned by <see cref="GetAllActiveAsync"/> — used by the health-check tests.</summary>
        public IReadOnlyList<TeamsConversationReference> ActiveSnapshot { get; set; }
            = Array.Empty<TeamsConversationReference>();

        /// <summary>If set, <see cref="GetAllActiveAsync"/> throws this exception — used to drive the unhealthy-store path.</summary>
        public Exception? GetAllActiveAsyncThrow { get; set; }

        /// <summary>
        /// Stage 6.3 — explicit result for <see cref="CountActiveAsync"/>; when null the
        /// stub mirrors <see cref="ActiveSnapshot"/>.Count (so existing happy-path tests
        /// that pre-load <see cref="ActiveSnapshot"/> continue to assert a real count
        /// without needing to wire a second field).
        /// </summary>
        public long? CountActiveResult { get; set; }

        /// <summary>
        /// Stage 6.3 — if set, <see cref="CountActiveAsync"/> throws this exception; used
        /// to drive the "database unreachable" health-check scenario directly through the
        /// new count probe.
        /// </summary>
        public Exception? CountActiveAsyncThrow { get; set; }

        public Task<bool> IsActiveByInternalUserIdAsync(string tenantId, string internalUserId, CancellationToken ct)
        {
            UserProbeCalls.Add((tenantId, internalUserId));
            UserActiveMap.TryGetValue((tenantId, internalUserId), out var active);
            return Task.FromResult(active);
        }

        public Task<bool> IsActiveByChannelAsync(string tenantId, string channelId, CancellationToken ct)
        {
            ChannelProbeCalls.Add((tenantId, channelId));
            ChannelActiveMap.TryGetValue((tenantId, channelId), out var active);
            return Task.FromResult(active);
        }

        public Task<IReadOnlyList<TeamsConversationReference>> GetAllActiveAsync(string tenantId, CancellationToken ct)
        {
            if (GetAllActiveAsyncThrow is not null)
            {
                throw GetAllActiveAsyncThrow;
            }

            return Task.FromResult(ActiveSnapshot);
        }

        public Task<long> CountActiveAsync(CancellationToken ct)
        {
            if (CountActiveAsyncThrow is not null)
            {
                throw CountActiveAsyncThrow;
            }

            return Task.FromResult(CountActiveResult ?? ActiveSnapshot.Count);
        }

        public Task SaveOrUpdateAsync(TeamsConversationReference reference, CancellationToken ct)
            => throw new NotSupportedException("StubConversationReferenceStore: SaveOrUpdateAsync is not used by Stage 5.1 tests.");

        public Task<TeamsConversationReference?> GetAsync(string tenantId, string aadObjectId, CancellationToken ct)
            => throw new NotSupportedException("StubConversationReferenceStore: GetAsync is not used by Stage 5.1 tests.");

        public Task<TeamsConversationReference?> GetByAadObjectIdAsync(string tenantId, string aadObjectId, CancellationToken ct)
            => throw new NotSupportedException("StubConversationReferenceStore: GetByAadObjectIdAsync is not used by Stage 5.1 tests.");

        public Task<TeamsConversationReference?> GetByInternalUserIdAsync(string tenantId, string internalUserId, CancellationToken ct)
            => throw new NotSupportedException("StubConversationReferenceStore: GetByInternalUserIdAsync is not used by Stage 5.1 tests.");

        public Task<TeamsConversationReference?> GetByChannelIdAsync(string tenantId, string channelId, CancellationToken ct)
            => throw new NotSupportedException("StubConversationReferenceStore: GetByChannelIdAsync is not used by Stage 5.1 tests.");

        public Task<IReadOnlyList<TeamsConversationReference>> GetActiveChannelsByTeamIdAsync(string tenantId, string teamId, CancellationToken ct)
            => throw new NotSupportedException("StubConversationReferenceStore: GetActiveChannelsByTeamIdAsync is not used by Stage 5.1 tests.");

        public Task<bool> IsActiveAsync(string tenantId, string aadObjectId, CancellationToken ct)
            => throw new NotSupportedException("StubConversationReferenceStore: IsActiveAsync is not used by Stage 5.1 tests.");

        public Task MarkInactiveAsync(string tenantId, string aadObjectId, CancellationToken ct)
            => throw new NotSupportedException("StubConversationReferenceStore: MarkInactiveAsync is not used by Stage 5.1 tests.");

        public Task MarkInactiveByChannelAsync(string tenantId, string channelId, CancellationToken ct)
            => throw new NotSupportedException("StubConversationReferenceStore: MarkInactiveByChannelAsync is not used by Stage 5.1 tests.");

        public Task DeleteAsync(string tenantId, string aadObjectId, CancellationToken ct)
            => throw new NotSupportedException("StubConversationReferenceStore: DeleteAsync is not used by Stage 5.1 tests.");

        public Task DeleteByChannelAsync(string tenantId, string channelId, CancellationToken ct)
            => throw new NotSupportedException("StubConversationReferenceStore: DeleteByChannelAsync is not used by Stage 5.1 tests.");
    }

    /// <summary>
    /// Recording <see cref="IUserRoleProvider"/> used by <see cref="RbacAuthorizationService"/>
    /// tests. Captures every <see cref="GetRoleAsync"/> call and returns a configurable
    /// per-user role.
    /// </summary>
    public sealed class StubUserRoleProvider : IUserRoleProvider
    {
        private readonly ConcurrentDictionary<string, string> _roles
            = new(StringComparer.Ordinal);

        public List<(string TenantId, string UserId)> Calls { get; } = new();

        public StubUserRoleProvider AssignRole(string userId, string role)
        {
            _roles[userId] = role;
            return this;
        }

        public Task<string?> GetRoleAsync(string tenantId, string aadObjectId, CancellationToken cancellationToken)
        {
            Calls.Add((tenantId, aadObjectId));
            _roles.TryGetValue(aadObjectId, out var role);
            return Task.FromResult<string?>(role);
        }
    }

    /// <summary>
    /// Recording <see cref="IUserDirectory"/> used by <see cref="EntraIdentityResolver"/>
    /// tests. Captures every <see cref="LookupAsync"/> call and returns a configurable
    /// per-AAD-object-ID record.
    /// </summary>
    public sealed class StubUserDirectory : IUserDirectory
    {
        private readonly ConcurrentDictionary<string, UserIdentity> _users
            = new(StringComparer.Ordinal);

        public List<string> Calls { get; } = new();

        public StubUserDirectory Add(UserIdentity identity)
        {
            _users[identity.AadObjectId] = identity;
            return this;
        }

        public Task<UserIdentity?> LookupAsync(string aadObjectId, CancellationToken cancellationToken)
        {
            Calls.Add(aadObjectId);
            _users.TryGetValue(aadObjectId, out var identity);
            return Task.FromResult<UserIdentity?>(identity);
        }
    }

    /// <summary>
    /// Test-only <see cref="BotFrameworkAuthentication"/>. Drives the connector-factory path
    /// either to throw (Degraded health) or to return a fake factory that returns null
    /// (Healthy health — <c>using var x = (IConnectorClient)null!</c> is a no-op).
    /// </summary>
    public sealed class FakeBotFrameworkAuthentication : BotFrameworkAuthentication
    {
        public Exception? CreateConnectorFactoryThrow { get; set; }
        public Exception? CreateAsyncThrow { get; set; }
        public List<ClaimsIdentity?> CreateConnectorFactoryCalls { get; } = new();

        public override ConnectorFactory CreateConnectorFactory(ClaimsIdentity claimsIdentity)
        {
            CreateConnectorFactoryCalls.Add(claimsIdentity);
            if (CreateConnectorFactoryThrow is not null)
            {
                throw CreateConnectorFactoryThrow;
            }

            return new FakeConnectorFactory(CreateAsyncThrow);
        }

        // The remaining members are unused by TeamsAppPolicyHealthCheck; tests that drive
        // health checks never reach these paths so a NotImplementedException sentinel is
        // appropriate (it would surface immediately if the production code regressed and
        // started exercising them).

        public override Task<AuthenticateRequestResult> AuthenticateRequestAsync(Microsoft.Bot.Schema.Activity activity, string authHeader, CancellationToken cancellationToken)
            => throw new NotImplementedException();

        public override Task<AuthenticateRequestResult> AuthenticateStreamingRequestAsync(string authHeader, string channelIdHeader, CancellationToken cancellationToken)
            => throw new NotImplementedException();

        public override Task<UserTokenClient> CreateUserTokenClientAsync(ClaimsIdentity claimsIdentity, CancellationToken cancellationToken)
            => throw new NotImplementedException();
    }

    private sealed class FakeConnectorFactory : ConnectorFactory
    {
        private readonly Exception? _throw;

        public FakeConnectorFactory(Exception? @throw)
        {
            _throw = @throw;
        }

        public override Task<IConnectorClient> CreateAsync(string serviceUrl, string audience, CancellationToken cancellationToken)
        {
            if (_throw is not null)
            {
                throw _throw;
            }

            // null is safe inside `using var client = ...` — the lowered code emits a null
            // check before Dispose, so the health check's success path tolerates a stub
            // factory that does not allocate a real client.
            return Task.FromResult<IConnectorClient>(null!);
        }
    }
}
