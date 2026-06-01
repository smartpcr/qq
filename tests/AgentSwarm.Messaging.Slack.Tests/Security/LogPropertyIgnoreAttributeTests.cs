// -----------------------------------------------------------------------
// <copyright file="LogPropertyIgnoreAttributeTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Tests.Security;

using System;
using System.Linq;
using System.Reflection;
using AgentSwarm.Messaging.Core.Secrets;
using FluentAssertions;
using Xunit;

/// <summary>
/// Stage 3.3 regression tests for the "secrets are never logged" rule
/// (architecture.md §7.3, FR-022). The tests verify two complementary
/// guarantees:
/// <list type="bullet">
///   <item>
///     A <c>[LogPropertyIgnore]</c> marker exists on every in-process
///     member that holds a resolved secret value. Without this marker
///     a future structured-logging change can silently start emitting
///     the secret.
///   </item>
///   <item>
///     <see cref="SecretScrubber"/> never returns the raw value, even
///     when the value itself is short, empty, or contains the
///     placeholder text by coincidence.
///   </item>
/// </list>
/// </summary>
public sealed class LogPropertyIgnoreAttributeTests
{
    [Fact]
    public void Attribute_targets_properties_fields_and_parameters_only()
    {
        AttributeUsageAttribute usage = typeof(LogPropertyIgnoreAttribute)
            .GetCustomAttribute<AttributeUsageAttribute>()
            .Should().NotBeNull().And.Subject.As<AttributeUsageAttribute>();

        usage.ValidOn.Should().Be(
            AttributeTargets.Property | AttributeTargets.Field | AttributeTargets.Parameter);
        usage.AllowMultiple.Should().BeFalse();
        usage.Inherited.Should().BeFalse();
    }

    [Fact]
    public void CompositeSecretProvider_CacheEntry_Value_is_tagged_LogPropertyIgnore()
    {
        // The cache entry is a private nested type, so reflect via the
        // composite's BindingFlags-discoverable members rather than a
        // name string -- if the type is renamed or removed the test
        // FAILS rather than silently passing.
        Type? cacheEntry = typeof(CompositeSecretProvider)
            .GetNestedTypes(BindingFlags.NonPublic)
            .FirstOrDefault(t => t.Name == "CacheEntry");

        cacheEntry.Should().NotBeNull(
            "CompositeSecretProvider must continue to encapsulate cached secret values in a CacheEntry nested type");

        PropertyInfo? valueProperty = cacheEntry!.GetProperty(
            "Value",
            BindingFlags.Public | BindingFlags.Instance);

        valueProperty.Should().NotBeNull(
            "CacheEntry.Value is the in-memory holder for the resolved secret");

        valueProperty!
            .GetCustomAttribute<LogPropertyIgnoreAttribute>()
            .Should().NotBeNull(
                "CacheEntry.Value holds the plain-text secret; it MUST be tagged [LogPropertyIgnore] so structured-logging walks skip it");
    }

    [Fact]
    public void SlackSignatureValidator_SecretResolutionOutcome_Secret_is_tagged_LogPropertyIgnore()
    {
        // The signature validator's resolution outcome carries the
        // resolved HMAC key on the success path. Architecture.md §7.3
        // (FR-022) requires the value never leaves the validator via a
        // log sink; the attribute is the static contract.
        Type validator = typeof(AgentSwarm.Messaging.Slack.Security.SlackSignatureValidator);
        Type? outcome = validator
            .GetNestedTypes(BindingFlags.NonPublic)
            .FirstOrDefault(t => t.Name == "SecretResolutionOutcome");

        outcome.Should().NotBeNull(
            "SlackSignatureValidator must continue to model its secret-resolution result via a SecretResolutionOutcome nested type");

        PropertyInfo? secretProperty = outcome!.GetProperty(
            "Secret",
            BindingFlags.Public | BindingFlags.Instance);

        secretProperty.Should().NotBeNull();
        secretProperty!
            .GetCustomAttribute<LogPropertyIgnoreAttribute>()
            .Should().NotBeNull(
                "SecretResolutionOutcome.Secret holds the resolved HMAC key on the OK path; it MUST be tagged [LogPropertyIgnore]");
    }

    [Theory]
    [InlineData("xoxb-1234-abcd-the-real-token", SecretScrubber.Placeholder)]
    [InlineData("a", SecretScrubber.Placeholder)]
    [InlineData("***", SecretScrubber.Placeholder)]
    [InlineData("", SecretScrubber.EmptyPlaceholder)]
    [InlineData(null, SecretScrubber.EmptyPlaceholder)]
    public void Scrub_never_returns_the_raw_value(string? input, string expected)
    {
        SecretScrubber.Scrub(input).Should().Be(expected);
    }

    [Fact]
    public void CompositeSecretProvider_CacheEntry_ToString_does_not_include_the_secret_value()
    {
        // Defence in depth: even if a future logger ignores the
        // attribute and falls back to ToString, the secret MUST NOT
        // surface in the resulting string.
        const string Secret = "xoxb-extremely-sensitive-token-value";
        Type cacheEntry = typeof(CompositeSecretProvider)
            .GetNestedTypes(BindingFlags.NonPublic)
            .First(t => t.Name == "CacheEntry");

        object instance = Activator.CreateInstance(
            cacheEntry,
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public,
            binder: null,
            args: new object[] { Secret, DateTimeOffset.UtcNow },
            culture: null)!;

        string rendered = instance.ToString()!;

        rendered.Should().Contain(SecretScrubber.Placeholder);
        rendered.Should().NotContain(Secret, "ToString must scrub the raw secret value");
    }
}
