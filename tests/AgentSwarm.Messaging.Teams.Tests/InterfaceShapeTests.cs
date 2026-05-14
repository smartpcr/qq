using System.Reflection;

namespace AgentSwarm.Messaging.Teams.Tests;

public sealed class InterfaceShapeTests
{
    [Fact]
    public void IConversationReferenceStore_Lists_MarkInactive_Before_IsActive()
    {
        var methods = typeof(IConversationReferenceStore)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Select(m => m.Name)
            .ToList();

        // Canonical Stage 2.1 ordering: MarkInactive* MUST precede IsActive*.
        var markInactiveIndex = methods.IndexOf(nameof(IConversationReferenceStore.MarkInactiveAsync));
        var markInactiveByChannelIndex = methods.IndexOf(nameof(IConversationReferenceStore.MarkInactiveByChannelAsync));
        var isActiveIndex = methods.IndexOf(nameof(IConversationReferenceStore.IsActiveAsync));
        var isActiveByInternalIndex = methods.IndexOf(nameof(IConversationReferenceStore.IsActiveByInternalUserIdAsync));
        var isActiveByChannelIndex = methods.IndexOf(nameof(IConversationReferenceStore.IsActiveByChannelAsync));

        Assert.True(markInactiveIndex >= 0 && markInactiveByChannelIndex >= 0);
        Assert.True(isActiveIndex >= 0 && isActiveByInternalIndex >= 0 && isActiveByChannelIndex >= 0);
        Assert.True(markInactiveIndex < isActiveIndex, $"MarkInactiveAsync (idx {markInactiveIndex}) must come before IsActiveAsync (idx {isActiveIndex}).");
        Assert.True(markInactiveByChannelIndex < isActiveIndex, "MarkInactiveByChannelAsync must come before IsActive* methods.");
    }

    [Fact]
    public void TelemetryMiddlewareOptions_SensitiveActivityTypes_Is_StringArray()
    {
        var prop = typeof(Middleware.TelemetryMiddlewareOptions)
            .GetProperty(nameof(Middleware.TelemetryMiddlewareOptions.SensitiveActivityTypes));
        Assert.NotNull(prop);
        Assert.Equal(typeof(string[]), prop!.PropertyType);
    }

    [Fact]
    public void IConversationReferenceStore_Has_Full_Method_Surface()
    {
        var required = new[]
        {
            nameof(IConversationReferenceStore.SaveOrUpdateAsync),
            nameof(IConversationReferenceStore.GetAsync),
            nameof(IConversationReferenceStore.GetAllActiveAsync),
            nameof(IConversationReferenceStore.GetByAadObjectIdAsync),
            nameof(IConversationReferenceStore.GetByInternalUserIdAsync),
            nameof(IConversationReferenceStore.GetByChannelIdAsync),
            nameof(IConversationReferenceStore.MarkInactiveAsync),
            nameof(IConversationReferenceStore.MarkInactiveByChannelAsync),
            nameof(IConversationReferenceStore.IsActiveAsync),
            nameof(IConversationReferenceStore.IsActiveByInternalUserIdAsync),
            nameof(IConversationReferenceStore.IsActiveByChannelAsync),
            nameof(IConversationReferenceStore.DeleteAsync),
            nameof(IConversationReferenceStore.DeleteByChannelAsync),
        };
        var actual = typeof(IConversationReferenceStore)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Select(m => m.Name)
            .ToHashSet();
        foreach (var name in required)
        {
            Assert.Contains(name, actual);
        }
    }
}
