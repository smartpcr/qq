// -----------------------------------------------------------------------
// <copyright file="UlidTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Tests.Core;

using System;
using System.Collections.Generic;
using AgentSwarm.Messaging.Core.Identifiers;
using FluentAssertions;
using Xunit;

/// <summary>
/// Stage 3.1 evaluator iter-3 item 2 regression tests for the
/// <see cref="Ulid"/> generator. The iter-3 review flagged that
/// <c>SlackAuditEntrySignatureSink</c> generated 32-hex GUID audit IDs
/// instead of the ULID-shaped strings required by architecture.md §3.5
/// (and documented on <c>SlackAuditEntry.Id</c>). These tests pin the
/// ULID contract that the sink now relies on.
/// </summary>
public sealed class UlidTests
{
    [Fact]
    public void NewUlid_returns_a_26_character_crockford_base32_string()
    {
        string id = Ulid.NewUlid();

        id.Should().NotBeNullOrWhiteSpace();
        id.Length.Should().Be(Ulid.Length);
        Ulid.IsValid(id).Should().BeTrue();
    }

    [Fact]
    public void NewUlid_with_timestamp_encodes_a_lexicographically_sortable_prefix()
    {
        // Two ULIDs generated one second apart must compare in
        // timestamp order. The ULID spec guarantees this because the
        // 48-bit timestamp prefix is big-endian and Crockford base32
        // preserves byte ordering.
        DateTimeOffset earlier = DateTimeOffset.FromUnixTimeMilliseconds(1_714_410_000_000);
        DateTimeOffset later = DateTimeOffset.FromUnixTimeMilliseconds(1_714_410_001_000);

        string earlierId = Ulid.NewUlid(earlier);
        string laterId = Ulid.NewUlid(later);

        string.CompareOrdinal(earlierId, laterId).Should().BeLessThan(0,
            "ULIDs derived from monotonically-increasing timestamps must compare in timestamp order");
    }

    [Fact]
    public void NewUlid_with_same_timestamp_produces_different_random_suffixes()
    {
        // 80 bits of randomness => collision probability negligible.
        DateTimeOffset ts = DateTimeOffset.UtcNow;
        HashSet<string> seen = new();
        for (int i = 0; i < 1024; i++)
        {
            string id = Ulid.NewUlid(ts);
            seen.Add(id).Should().BeTrue(
                "the 80-bit random suffix must make collisions astronomically unlikely even for 1024 IDs in the same millisecond");
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-ulid")]
    [InlineData("ABCDEFGHIJKLMNOPQRSTUVWXYZ")] // 26 chars but contains I, L, O, U (banned by Crockford)
    [InlineData("01ARZ3NDEKTSV4RRFFQ69G5FA")] // 25 chars
    [InlineData("01ARZ3NDEKTSV4RRFFQ69G5FAVA")] // 27 chars
    public void IsValid_returns_false_for_malformed_values(string? candidate)
    {
        Ulid.IsValid(candidate).Should().BeFalse();
    }

    [Fact]
    public void IsValid_returns_true_for_well_formed_lower_case_ULID()
    {
        // Lower-case input is accepted (the alphabet is case-insensitive).
        Ulid.IsValid("01arz3ndektsv4rrffq69g5fav").Should().BeTrue();
    }

    [Fact]
    public void NewUlid_throws_when_timestamp_is_before_unix_epoch()
    {
        DateTimeOffset preEpoch = DateTimeOffset.FromUnixTimeSeconds(-1);
        System.Action act = () => Ulid.NewUlid(preEpoch);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
