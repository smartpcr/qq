using System.Collections.Concurrent;
using AgentSwarm.Messaging.Abstractions;
using AgentSwarm.Messaging.Persistence;
using Microsoft.Bot.Builder;
using Microsoft.Bot.Schema;

namespace AgentSwarm.Messaging.Teams.Tests;

/// <summary>
/// Hand-rolled in-memory test doubles for the nine dependencies <see cref="TeamsSwarmActivityHandler"/>
/// takes via DI. Following the convention in <c>AgentSwarm.Messaging.Abstractions.Tests</c>
/// — no mocking library — these doubles record observed calls onto public fields the
/// test cases assert against.
/// </summary>
internal static class TestDoubles
{
    public sealed class RecordingConversationReferenceStore : IConversationReferenceStore
    {
        public List<TeamsConversationReference> Saved { get; } = new();
        public List<(string TenantId, string AadObjectId)> MarkedInactive { get; } = new();
        public List<(string TenantId, string ChannelId)> MarkedInactiveByChannel { get; } = new();
        public TeamsConversationReference? Preload { get; set; }

        /// <summary>
        /// Channel-by-team query results — set by tests that want to assert the
        /// per-channel <c>MarkInactiveByChannelAsync</c> fan-out path for team-scope
        /// uninstalls. Keyed by <c>(TenantId, TeamId)</c>.
        /// </summary>
        public Dictionary<(string TenantId, string TeamId), List<TeamsConversationReference>> TeamChannels { get; } = new();

        public List<(string TenantId, string TeamId)> TeamChannelLookups { get; } = new();

        public Task SaveOrUpdateAsync(TeamsConversationReference reference, CancellationToken ct)
        {
            Saved.Add(reference);
            return Task.CompletedTask;
        }

        public Task<TeamsConversationReference?> GetAsync(string referenceId, CancellationToken ct)
            => Task.FromResult<TeamsConversationReference?>(null);

        public Task<TeamsConversationReference?> GetByAadObjectIdAsync(string tenantId, string aadObjectId, CancellationToken ct)
            => Task.FromResult(Preload);

        public Task<TeamsConversationReference?> GetByInternalUserIdAsync(string tenantId, string internalUserId, CancellationToken ct)
            => Task.FromResult<TeamsConversationReference?>(null);

        public Task<TeamsConversationReference?> GetByChannelIdAsync(string tenantId, string channelId, CancellationToken ct)
            => Task.FromResult(Preload);

        public Task<IReadOnlyList<TeamsConversationReference>> GetAllActiveAsync(string tenantId, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<TeamsConversationReference>>(Array.Empty<TeamsConversationReference>());

        public Task<IReadOnlyList<TeamsConversationReference>> GetActiveChannelsByTeamIdAsync(string tenantId, string teamId, CancellationToken ct)
        {
            TeamChannelLookups.Add((tenantId, teamId));
            if (TeamChannels.TryGetValue((tenantId, teamId), out var list))
            {
                return Task.FromResult<IReadOnlyList<TeamsConversationReference>>(list);
            }

            return Task.FromResult<IReadOnlyList<TeamsConversationReference>>(Array.Empty<TeamsConversationReference>());
        }

        public Task<bool> IsActiveAsync(string tenantId, string aadObjectId, CancellationToken ct)
            => Task.FromResult(false);

        public Task<bool> IsActiveByInternalUserIdAsync(string tenantId, string internalUserId, CancellationToken ct)
            => Task.FromResult(false);

        public Task<bool> IsActiveByChannelAsync(string tenantId, string channelId, CancellationToken ct)
            => Task.FromResult(false);

        public Task MarkInactiveAsync(string tenantId, string aadObjectId, CancellationToken ct)
        {
            MarkedInactive.Add((tenantId, aadObjectId));
            return Task.CompletedTask;
        }

        public Task MarkInactiveByChannelAsync(string tenantId, string channelId, CancellationToken ct)
        {
            MarkedInactiveByChannel.Add((tenantId, channelId));
            return Task.CompletedTask;
        }

        public Task DeleteAsync(string tenantId, string aadObjectId, CancellationToken ct) => Task.CompletedTask;
        public Task DeleteByChannelAsync(string tenantId, string channelId, CancellationToken ct) => Task.CompletedTask;
    }

    public sealed class RecordingCommandDispatcher : ICommandDispatcher
    {
        public List<CommandContext> Dispatched { get; } = new();

        public Task DispatchAsync(CommandContext context, CancellationToken ct)
        {
            Dispatched.Add(context);
            return Task.CompletedTask;
        }
    }

    public sealed class FakeIdentityResolver : IIdentityResolver
    {
        private readonly ConcurrentDictionary<string, UserIdentity> _map = new(StringComparer.Ordinal);

        public void Map(string aadObjectId, UserIdentity identity) => _map[aadObjectId] = identity;

        public Task<UserIdentity?> ResolveAsync(string aadObjectId, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            _map.TryGetValue(aadObjectId, out var identity);
            return Task.FromResult<UserIdentity?>(identity);
        }
    }

    public sealed class AlwaysAuthorizationService : IUserAuthorizationService
    {
        public bool IsAuthorized { get; set; } = true;
        public string? UserRole { get; set; } = "operator";
        public string? RequiredRole { get; set; }
        public List<(string TenantId, string UserId, string Command)> Calls { get; } = new();

        public Task<AuthorizationResult> AuthorizeAsync(string tenantId, string userId, string command, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Calls.Add((tenantId, userId, command));
            return Task.FromResult(new AuthorizationResult(IsAuthorized, UserRole, RequiredRole));
        }
    }

    public sealed class RecordingAuditLogger : IAuditLogger
    {
        public List<AuditEntry> Entries { get; } = new();

        public Task LogAsync(AuditEntry entry, CancellationToken cancellationToken)
        {
            Entries.Add(entry);
            return Task.CompletedTask;
        }
    }

    public sealed class StubAgentQuestionStore : IAgentQuestionStore
    {
        public Task SaveAsync(AgentQuestion question, CancellationToken ct) => Task.CompletedTask;
        public Task<AgentQuestion?> GetByIdAsync(string questionId, CancellationToken ct) => Task.FromResult<AgentQuestion?>(null);
        public Task<bool> TryUpdateStatusAsync(string questionId, string expectedStatus, string newStatus, CancellationToken ct) => Task.FromResult(false);
        public Task UpdateConversationIdAsync(string questionId, string conversationId, CancellationToken ct) => Task.CompletedTask;
        public Task<AgentQuestion?> GetMostRecentOpenByConversationAsync(string conversationId, CancellationToken ct) => Task.FromResult<AgentQuestion?>(null);
        public Task<IReadOnlyList<AgentQuestion>> GetOpenByConversationAsync(string conversationId, CancellationToken ct) => Task.FromResult<IReadOnlyList<AgentQuestion>>(Array.Empty<AgentQuestion>());
        public Task<IReadOnlyList<AgentQuestion>> GetOpenExpiredAsync(DateTimeOffset cutoff, int batchSize, CancellationToken ct) => Task.FromResult<IReadOnlyList<AgentQuestion>>(Array.Empty<AgentQuestion>());
    }

    public sealed class RecordingCardActionHandler : ICardActionHandler
    {
        public int Invocations { get; private set; }

        public AdaptiveCardInvokeResponse Response { get; set; } = new AdaptiveCardInvokeResponse
        {
            StatusCode = 200,
            Type = "application/vnd.microsoft.card.adaptive",
            Value = new { },
        };

        public Task<AdaptiveCardInvokeResponse> HandleAsync(ITurnContext turnContext, CancellationToken ct)
        {
            Invocations++;
            return Task.FromResult(Response);
        }
    }

    public sealed class RecordingInboundEventPublisher : IInboundEventPublisher
    {
        public List<MessengerEvent> Published { get; } = new();

        public Task PublishAsync(MessengerEvent messengerEvent, CancellationToken ct)
        {
            Published.Add(messengerEvent);
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Recording <see cref="IConversationReferenceRouter"/> used by Stage 2.3 connector
    /// tests. The router is a separate interface from
    /// <see cref="IConversationReferenceStore"/> so the canonical store contract (defined
    /// in <c>implementation-plan.md</c> §2.1) is not widened with a non-canonical
    /// <c>GetByConversationIdAsync</c> method.
    /// </summary>
    public sealed class RecordingConversationReferenceRouter : IConversationReferenceRouter
    {
        public Dictionary<string, TeamsConversationReference> PreloadByConversationId { get; } = new(StringComparer.Ordinal);
        public List<string> Lookups { get; } = new();

        public Task<TeamsConversationReference?> GetByConversationIdAsync(string conversationId, CancellationToken ct)
        {
            Lookups.Add(conversationId);
            PreloadByConversationId.TryGetValue(conversationId, out var hit);
            return Task.FromResult<TeamsConversationReference?>(hit);
        }
    }
}
