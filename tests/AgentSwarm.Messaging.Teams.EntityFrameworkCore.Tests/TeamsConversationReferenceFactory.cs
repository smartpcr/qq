using AgentSwarm.Messaging.Teams;

namespace AgentSwarm.Messaging.Teams.EntityFrameworkCore.Tests;

/// <summary>
/// Helpers that materialize representative <see cref="TeamsConversationReference"/> instances
/// for the Stage 4.1 store tests.
/// </summary>
internal static class TeamsConversationReferenceFactory
{
    public static TeamsConversationReference UserScoped(
        string tenantId = "tenant-1",
        string aadObjectId = "aad-user-1",
        string? internalUserId = null,
        string? id = null,
        string serviceUrl = "https://smba.trafficmanager.net/",
        string conversationId = "a:conversation-aad-user-1",
        string botId = "00000000-0000-0000-0000-000000000bot",
        string? referenceJson = null,
        DateTimeOffset? createdAt = null,
        DateTimeOffset? updatedAt = null)
    {
        return new TeamsConversationReference
        {
            Id = id ?? Guid.NewGuid().ToString("D"),
            TenantId = tenantId,
            AadObjectId = aadObjectId,
            InternalUserId = internalUserId,
            ChannelId = null,
            TeamId = null,
            ServiceUrl = serviceUrl,
            ConversationId = conversationId,
            BotId = botId,
            ReferenceJson = referenceJson ?? $"{{\"user\":\"{aadObjectId}\",\"tenant\":\"{tenantId}\"}}",
            IsActive = true,
            CreatedAt = createdAt ?? DateTimeOffset.UtcNow,
            UpdatedAt = updatedAt ?? DateTimeOffset.UtcNow,
        };
    }

    public static TeamsConversationReference ChannelScoped(
        string tenantId = "tenant-1",
        string channelId = "channel-general",
        string? teamId = "team-platform",
        string? id = null,
        string serviceUrl = "https://smba.trafficmanager.net/",
        string conversationId = "channel-general:conversation",
        string botId = "00000000-0000-0000-0000-000000000bot",
        string? referenceJson = null,
        DateTimeOffset? createdAt = null,
        DateTimeOffset? updatedAt = null)
    {
        return new TeamsConversationReference
        {
            Id = id ?? Guid.NewGuid().ToString("D"),
            TenantId = tenantId,
            AadObjectId = null,
            InternalUserId = null,
            ChannelId = channelId,
            TeamId = teamId,
            ServiceUrl = serviceUrl,
            ConversationId = conversationId,
            BotId = botId,
            ReferenceJson = referenceJson ?? $"{{\"channel\":\"{channelId}\",\"tenant\":\"{tenantId}\"}}",
            IsActive = true,
            CreatedAt = createdAt ?? DateTimeOffset.UtcNow,
            UpdatedAt = updatedAt ?? DateTimeOffset.UtcNow,
        };
    }
}
