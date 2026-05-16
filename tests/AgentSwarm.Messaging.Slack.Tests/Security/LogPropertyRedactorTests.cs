// -----------------------------------------------------------------------
// <copyright file="LogPropertyRedactorTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Tests.Security;

using AgentSwarm.Messaging.Core.Secrets;
using FluentAssertions;
using Xunit;

/// <summary>
/// Stage 3.3 iter-2 evaluator item 3 tests:
/// <see cref="LogPropertyRedactor"/> is the built-in interceptor that
/// reads <see cref="LogPropertyIgnoreAttribute"/> and enforces the
/// "secrets are never logged" contract. These tests pin the reflection
/// contract so a future refactor cannot silently regress the
/// enforcement.
/// </summary>
public sealed class LogPropertyRedactorTests
{
    [Fact]
    public void RedactToString_emits_placeholder_for_tagged_properties()
    {
        Fixture instance = new()
        {
            Name = "slack",
            Secret = "xoxb-extremely-sensitive-token",
        };

        string rendered = LogPropertyRedactor.RedactToString(instance);

        rendered.Should().Contain("Name=slack");
        rendered.Should().Contain($"Secret={SecretScrubber.Placeholder}");
        rendered.Should().NotContain("xoxb-extremely-sensitive-token",
            "a tagged property must NEVER surface its raw value in the redacted rendering");
    }

    [Fact]
    public void RedactToString_emits_empty_placeholder_when_tagged_property_is_null()
    {
        Fixture instance = new() { Name = "ws", Secret = null };

        string rendered = LogPropertyRedactor.RedactToString(instance);

        rendered.Should().Contain($"Secret={SecretScrubber.EmptyPlaceholder}");
    }

    [Fact]
    public void RedactToString_emits_empty_placeholder_when_tagged_property_is_empty_string()
    {
        Fixture instance = new() { Name = "ws", Secret = string.Empty };

        string rendered = LogPropertyRedactor.RedactToString(instance);

        rendered.Should().Contain($"Secret={SecretScrubber.EmptyPlaceholder}");
    }

    [Fact]
    public void RedactToString_returns_null_placeholder_for_null_input()
    {
        LogPropertyRedactor.RedactToString(null).Should().Be("(null)");
    }

    [Fact]
    public void RedactToString_renders_type_name_prefix()
    {
        Fixture instance = new() { Name = "ws", Secret = "***" };

        string rendered = LogPropertyRedactor.RedactToString(instance);

        rendered.Should().StartWith(nameof(Fixture) + "(");
        rendered.Should().EndWith(")");
    }

    [Fact]
    public void RedactToString_uses_invariant_formatting_for_IFormattable_members()
    {
        FormattableFixture instance = new()
        {
            Created = new System.DateTimeOffset(2024, 12, 31, 23, 59, 0, System.TimeSpan.Zero),
        };

        string rendered = LogPropertyRedactor.RedactToString(instance);

        // Invariant ToString of a DateTimeOffset starts with the ISO date.
        rendered.Should().Contain("12/31/2024");
    }

    [Fact]
    public void RedactToString_continues_after_a_throwing_getter()
    {
        ThrowingFixture instance = new() { Secret = "xoxb-token" };

        string rendered = LogPropertyRedactor.RedactToString(instance);

        // Even the secret-tagged property whose getter throws must
        // render as the placeholder -- never as the exception text
        // (which could embed the secret itself).
        rendered.Should().Contain($"Secret={SecretScrubber.Placeholder}");
        rendered.Should().Contain("Throwing=<threw ");
    }

    private sealed class Fixture
    {
        public string Name { get; set; } = string.Empty;

        [LogPropertyIgnore]
        public string? Secret { get; set; }
    }

    private sealed class FormattableFixture
    {
        public System.DateTimeOffset Created { get; set; }
    }

    private sealed class ThrowingFixture
    {
        [LogPropertyIgnore]
        public string? Secret { get; set; }

        public string Throwing => throw new System.InvalidOperationException("boom");
    }
}
