using System.Reflection;
using System.Runtime.CompilerServices;

namespace AgentSwarm.Messaging.Abstractions.Tests;

/// <summary>
/// Stage 1.1 test scenario: "Immutable records — Given a HumanDecisionEvent, When attempting
/// to modify QuestionId, Then a compile-time error prevents mutation." A compile-time
/// failure cannot be expressed in a passing unit test, so this test verifies the equivalent
/// invariant via reflection: every public instance property on the record has an
/// init-only setter (never an unrestricted <c>set</c>) so callers outside the type cannot
/// mutate an instance after construction. The same check is applied to the other shared
/// records to prevent regression on any of the Stage 1.1 data models.
/// </summary>
public sealed class RecordImmutabilityTests
{
    public static IEnumerable<object[]> ImmutableRecordTypes()
    {
        yield return new object[] { typeof(MessengerMessage) };
        yield return new object[] { typeof(HumanAction) };
        yield return new object[] { typeof(HumanDecisionEvent) };
        yield return new object[] { typeof(AgentQuestion) };
        yield return new object[] { typeof(ParsedCommand) };
        yield return new object[] { typeof(MessengerEvent) };
        yield return new object[] { typeof(CommandEvent) };
        yield return new object[] { typeof(DecisionEvent) };
        yield return new object[] { typeof(TextEvent) };
    }

    [Theory]
    [MemberData(nameof(ImmutableRecordTypes))]
    public void Record_PublicProperties_AreInitOnly(Type recordType)
    {
        var publicSetters = recordType
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
                $"Public property {recordType.Name}.{entry.Property.Name} must be init-only " +
                "(no unrestricted set accessor). Mutating it would violate the Stage 1.1 " +
                "immutability invariant.");
        }
    }

    [Fact]
    public void HumanDecisionEvent_QuestionIdPropertyHasNoPublicSetter()
    {
        // Explicit guard for the exact field called out in the Stage 1.1 test scenario.
        var property = typeof(HumanDecisionEvent).GetProperty(nameof(HumanDecisionEvent.QuestionId));

        Assert.NotNull(property);
        var setter = property!.GetSetMethod(nonPublic: false);
        Assert.NotNull(setter);
        Assert.True(IsInitOnly(setter!),
            "HumanDecisionEvent.QuestionId must be init-only — attempting to mutate it must " +
            "fail at compile time. See Stage 1.1 'Immutable records' test scenario.");
    }

    [Fact]
    public void MessengerEvent_EventTypeSetter_IsProtectedInitOnly()
    {
        // The EventType discriminator must not be settable by external callers; subtype
        // constructors stamp the value and `with` expressions originating outside the type
        // must not be able to corrupt it.
        var property = typeof(MessengerEvent).GetProperty(nameof(MessengerEvent.EventType));
        Assert.NotNull(property);

        // No public setter at all.
        Assert.Null(property!.GetSetMethod(nonPublic: false));

        // Non-public init-only setter exists for subclass use.
        var nonPublicSetter = property.GetSetMethod(nonPublic: true);
        Assert.NotNull(nonPublicSetter);
        Assert.True(nonPublicSetter!.IsFamily || nonPublicSetter.IsFamilyOrAssembly,
            "MessengerEvent.EventType setter must be protected (or protected internal) " +
            "so only subtype constructors can stamp the discriminator.");
        Assert.True(IsInitOnly(nonPublicSetter),
            "MessengerEvent.EventType setter must be init-only.");
    }

    private static bool IsInitOnly(MethodInfo setter)
    {
        return setter.ReturnParameter
            .GetRequiredCustomModifiers()
            .Any(m => m == typeof(IsExternalInit));
    }
}
