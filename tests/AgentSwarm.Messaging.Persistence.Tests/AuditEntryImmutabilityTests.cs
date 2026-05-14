using System.Reflection;
using System.Runtime.CompilerServices;

namespace AgentSwarm.Messaging.Persistence.Tests;

/// <summary>
/// Stage 1.3 test scenario: "Audit entry immutability — Given an <c>AuditEntry</c> record,
/// When created, Then all properties are init-only and cannot be modified." Verified via
/// reflection — every public instance property on <see cref="AuditEntry"/> must expose an
/// init-only setter (never an unrestricted <c>set</c>).
/// </summary>
public sealed class AuditEntryImmutabilityTests
{
    [Fact]
    public void AuditEntry_PublicProperties_AreInitOnly()
    {
        var publicSetters = typeof(AuditEntry)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => new
            {
                Property = p,
                Setter = p.GetSetMethod(nonPublic: false),
            })
            .Where(x => x.Setter is not null)
            .ToArray();

        Assert.NotEmpty(publicSetters);

        foreach (var entry in publicSetters)
        {
            Assert.True(
                IsInitOnly(entry.Setter!),
                $"Public property AuditEntry.{entry.Property.Name} must be init-only " +
                "(no unrestricted set accessor). Mutating it would violate the Stage 1.3 " +
                "audit-entry immutability invariant.");
        }
    }

    [Theory]
    [InlineData(nameof(AuditEntry.Timestamp))]
    [InlineData(nameof(AuditEntry.CorrelationId))]
    [InlineData(nameof(AuditEntry.EventType))]
    [InlineData(nameof(AuditEntry.ActorId))]
    [InlineData(nameof(AuditEntry.ActorType))]
    [InlineData(nameof(AuditEntry.TenantId))]
    [InlineData(nameof(AuditEntry.AgentId))]
    [InlineData(nameof(AuditEntry.TaskId))]
    [InlineData(nameof(AuditEntry.ConversationId))]
    [InlineData(nameof(AuditEntry.Action))]
    [InlineData(nameof(AuditEntry.PayloadJson))]
    [InlineData(nameof(AuditEntry.Outcome))]
    [InlineData(nameof(AuditEntry.Checksum))]
    public void AuditEntry_NamedProperty_HasInitOnlySetterAndNoPublicSet(string propertyName)
    {
        var property = typeof(AuditEntry).GetProperty(propertyName);
        Assert.NotNull(property);

        var setter = property!.GetSetMethod(nonPublic: false);
        Assert.NotNull(setter);
        Assert.True(IsInitOnly(setter!), $"AuditEntry.{propertyName} must have an init-only setter.");
    }

    [Fact]
    public void AuditEntry_CarriesAllCanonicalFields()
    {
        // The canonical schema in tech-spec.md §4.3 defines exactly these required fields
        // (plus the implementation-specific Checksum). This regression guard ensures the
        // record stays in sync with the source-of-truth schema.
        var expected = new[]
        {
            nameof(AuditEntry.Timestamp),
            nameof(AuditEntry.CorrelationId),
            nameof(AuditEntry.EventType),
            nameof(AuditEntry.ActorId),
            nameof(AuditEntry.ActorType),
            nameof(AuditEntry.TenantId),
            nameof(AuditEntry.AgentId),
            nameof(AuditEntry.TaskId),
            nameof(AuditEntry.ConversationId),
            nameof(AuditEntry.Action),
            nameof(AuditEntry.PayloadJson),
            nameof(AuditEntry.Outcome),
            nameof(AuditEntry.Checksum),
        };

        var actual = typeof(AuditEntry)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        var expectedSorted = expected
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(expectedSorted, actual);
    }

    private static bool IsInitOnly(MethodInfo setter)
    {
        return setter.ReturnParameter
            .GetRequiredCustomModifiers()
            .Any(m => m == typeof(IsExternalInit));
    }
}
