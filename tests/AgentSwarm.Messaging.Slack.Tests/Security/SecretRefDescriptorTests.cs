// -----------------------------------------------------------------------
// <copyright file="SecretRefDescriptorTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Tests.Security;

using System;
using AgentSwarm.Messaging.Core.Secrets;
using FluentAssertions;
using Xunit;

/// <summary>
/// Stage 3.3 iter-3 evaluator item 2 unit tests for the
/// <see cref="SecretRefDescriptor"/> + <see cref="SecretRefRequirement"/>
/// types that drive the warmup fail-closed behaviour.
/// </summary>
public sealed class SecretRefDescriptorTests
{
    [Fact]
    public void Required_factory_produces_descriptor_with_Required_requirement()
    {
        SecretRefDescriptor d = SecretRefDescriptor.Required("env://A");

        d.SecretRef.Should().Be("env://A");
        d.Requirement.Should().Be(SecretRefRequirement.Required);
    }

    [Fact]
    public void Optional_factory_produces_descriptor_with_Optional_requirement()
    {
        SecretRefDescriptor d = SecretRefDescriptor.Optional("env://A");

        d.SecretRef.Should().Be("env://A");
        d.Requirement.Should().Be(SecretRefRequirement.Optional);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Ctor_rejects_blank_secret_ref(string? secretRef)
    {
        Action act = () => new SecretRefDescriptor(secretRef!, SecretRefRequirement.Required);
        act.Should().Throw<ArgumentException>()
            .WithMessage("*non-empty*");
    }

    [Fact]
    public void SecretCacheWarmupException_message_lists_every_failure_ref()
    {
        SecretCacheWarmupFailure[] failures =
        {
            new("env://A", new InvalidOperationException("boom-a")),
            new("env://B", new SecretNotFoundException("env://B")),
        };

        SecretCacheWarmupException ex = new(failures);

        ex.Failures.Should().HaveCount(2);
        ex.Message.Should().Contain("env://A").And.Contain("env://B");
    }

    [Fact]
    public void SecretCacheWarmupFailure_rejects_null_args()
    {
        Action nullRef = () => new SecretCacheWarmupFailure(null!, new Exception("x"));
        nullRef.Should().Throw<ArgumentNullException>();

        Action nullCause = () => new SecretCacheWarmupFailure("env://A", null!);
        nullCause.Should().Throw<ArgumentNullException>();
    }
}
