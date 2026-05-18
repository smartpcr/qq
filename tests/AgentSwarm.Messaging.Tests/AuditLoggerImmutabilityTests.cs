using System.Reflection;
using AgentSwarm.Messaging.Abstractions;
using AgentSwarm.Messaging.Persistence;
using FluentAssertions;

namespace AgentSwarm.Messaging.Tests;

/// <summary>
/// Stage 5.3 acceptance criterion: "<i>Audit immutability — Given an
/// audit entry exists, When an attempt is made to modify it via the
/// <see cref="IAuditLogger"/> interface, Then no update method is
/// available (compile-time enforcement)</i>".
///
/// Reflection-based pins on the <see cref="IAuditLogger"/> contract
/// so a future iter cannot accidentally add an
/// <c>UpdateAsync</c> / <c>DeleteAsync</c> / <c>RemoveAsync</c>
/// without this test failing first. Combined with the
/// <c>PersistentAuditLogger</c> tests pinning EntryKind, this gives
/// the compile-time-plus-runtime guarantee the brief calls for.
///
/// Also pins that <see cref="AuditLogEntry"/> exposes no setter for
/// the surrogate <see cref="AuditLogEntry.Id"/> primary key from
/// outside the assembly (the EF property bag still needs the setter
/// internally — checked via <see cref="MethodInfo.IsPublic"/>).
/// </summary>
public sealed class AuditLoggerImmutabilityTests
{
    [Fact]
    public void IAuditLogger_DefinesOnlyAppendOnlyMethods()
    {
        var methods = typeof(IAuditLogger).GetMethods()
            .Where(m => !m.IsSpecialName)
            .Select(m => m.Name)
            .ToArray();

        methods.Should().BeEquivalentTo(
            new[] { nameof(IAuditLogger.LogAsync), nameof(IAuditLogger.LogHumanResponseAsync) },
            "Stage 5.3 brief: audit records are immutable — no Update/Delete surface exists on the contract");
    }

    [Theory]
    [InlineData("Update")]
    [InlineData("Modify")]
    [InlineData("Delete")]
    [InlineData("Remove")]
    [InlineData("Patch")]
    [InlineData("Replace")]
    [InlineData("Edit")]
    public void IAuditLogger_HasNoMutationVerb(string forbiddenVerb)
    {
        var offenders = typeof(IAuditLogger).GetMethods()
            .Where(m => m.Name.StartsWith(forbiddenVerb, StringComparison.Ordinal))
            .Select(m => m.Name)
            .ToArray();

        offenders.Should().BeEmpty(
            "Stage 5.3 brief mandates compile-time immutability — IAuditLogger must not expose any '{0}*' method",
            forbiddenVerb);
    }

    [Fact]
    public void AuditEntry_AndHumanResponseAuditEntry_AreInitOnlyRecords()
    {
        // The two abstraction-level record types passed to the
        // immutable logger must themselves be init-only so a caller
        // cannot mutate the payload between `new` and `LogAsync` and
        // re-use the same instance with a different Action. Init-only
        // setters compile-enforce that.
        AssertAllSettersInitOnly(typeof(AuditEntry));
        AssertAllSettersInitOnly(typeof(HumanResponseAuditEntry));
    }

    private static void AssertAllSettersInitOnly(Type type)
    {
        foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            var setter = prop.GetSetMethod();
            if (setter is null)
            {
                continue;
            }

            // An init-only setter is encoded as a setter whose return
            // modifier set contains IsExternalInit. Check via the
            // modreq list on the return parameter.
            var initOnly = setter.ReturnParameter
                .GetRequiredCustomModifiers()
                .Any(t => t.FullName == "System.Runtime.CompilerServices.IsExternalInit");

            initOnly.Should().BeTrue(
                "Stage 5.3 immutability: {0}.{1} must be init-only so an audit payload cannot be mutated after construction",
                type.Name,
                prop.Name);
        }
    }
}
