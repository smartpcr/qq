// -----------------------------------------------------------------------
// <copyright file="SlackWorkspaceSecretRefSourceTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Tests.Security;

using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AgentSwarm.Messaging.Core.Secrets;
using AgentSwarm.Messaging.Slack.Entities;
using AgentSwarm.Messaging.Slack.Security;
using FluentAssertions;
using Xunit;

/// <summary>
/// Stage 3.3 iter-3 evaluator item 2 regression tests:
/// <see cref="SlackWorkspaceSecretRefSource"/> must yield every
/// signing/bot/app-level secret reference held by enabled workspace
/// configurations, tagged with the right
/// <see cref="SecretRefRequirement"/> so the warmup hosted service
/// fails closed on missing critical secrets while continuing past
/// missing Socket Mode tokens.
/// </summary>
public sealed class SlackWorkspaceSecretRefSourceTests
{
    [Fact]
    public async Task GetSecretRefsAsync_yields_all_three_ref_fields_for_enabled_workspace()
    {
        InMemorySlackWorkspaceConfigStore store = new(new[]
        {
            new SlackWorkspaceConfig
            {
                TeamId = "T1",
                Enabled = true,
                SigningSecretRef = "env://T1_SIGNING",
                BotTokenSecretRef = "env://T1_BOT",
                AppLevelTokenRef = "env://T1_APP",
                DefaultChannelId = "C-default",
            },
        });

        SlackWorkspaceSecretRefSource source = new(store);

        List<SecretRefDescriptor> refs = await Collect(source);

        refs.Should().BeEquivalentTo(new[]
        {
            SecretRefDescriptor.Required("env://T1_SIGNING"),
            SecretRefDescriptor.Required("env://T1_BOT"),
            SecretRefDescriptor.Optional("env://T1_APP"),
        });
    }

    [Fact]
    public async Task GetSecretRefsAsync_marks_signing_and_bot_token_as_Required()
    {
        // Stage 3.3 iter-3 evaluator item 2: every Slack request needs
        // the signing secret for HMAC verification and every reply
        // needs the bot token for the Web API call. A workspace
        // missing either of these CANNOT serve traffic, so warmup
        // must fail closed.
        InMemorySlackWorkspaceConfigStore store = new(new[]
        {
            new SlackWorkspaceConfig
            {
                TeamId = "T1",
                Enabled = true,
                SigningSecretRef = "env://T1_SIGNING",
                BotTokenSecretRef = "env://T1_BOT",
                DefaultChannelId = "C-default",
            },
        });

        SlackWorkspaceSecretRefSource source = new(store);

        List<SecretRefDescriptor> refs = await Collect(source);

        refs.Should().OnlyContain(d => d.Requirement == SecretRefRequirement.Required);
        refs.Select(d => d.SecretRef).Should().Equal("env://T1_SIGNING", "env://T1_BOT");
    }

    [Fact]
    public async Task GetSecretRefsAsync_marks_app_level_token_as_Optional()
    {
        // Stage 3.3 iter-3 evaluator item 2: AppLevelTokenRef is only
        // consumed by Socket Mode workspaces. HTTP Events API
        // workspaces leave it null. Marking it Required would force
        // every HTTP-mode workspace to provision a token they never
        // use; marking it Optional lets warmup log a warning and
        // continue.
        InMemorySlackWorkspaceConfigStore store = new(new[]
        {
            new SlackWorkspaceConfig
            {
                TeamId = "T-socket",
                Enabled = true,
                SigningSecretRef = "env://SIGNING",
                BotTokenSecretRef = "env://BOT",
                AppLevelTokenRef = "env://APP",
                DefaultChannelId = "C",
            },
        });

        SlackWorkspaceSecretRefSource source = new(store);
        List<SecretRefDescriptor> refs = await Collect(source);

        SecretRefDescriptor appLevel = refs.Single(d => d.SecretRef == "env://APP");
        appLevel.Requirement.Should().Be(
            SecretRefRequirement.Optional,
            "Socket Mode is opt-in; HTTP Events API workspaces leave AppLevelTokenRef null and must still boot");
    }

    [Fact]
    public async Task GetSecretRefsAsync_skips_null_app_level_token_ref()
    {
        InMemorySlackWorkspaceConfigStore store = new(new[]
        {
            new SlackWorkspaceConfig
            {
                TeamId = "T1",
                Enabled = true,
                SigningSecretRef = "env://T1_SIGNING",
                BotTokenSecretRef = "env://T1_BOT",
                AppLevelTokenRef = null,
                DefaultChannelId = "C-default",
            },
        });

        SlackWorkspaceSecretRefSource source = new(store);

        List<SecretRefDescriptor> refs = await Collect(source);

        refs.Select(d => d.SecretRef).Should().BeEquivalentTo(new[]
        {
            "env://T1_SIGNING",
            "env://T1_BOT",
        });
    }

    [Fact]
    public async Task GetSecretRefsAsync_skips_disabled_workspaces()
    {
        InMemorySlackWorkspaceConfigStore store = new(new[]
        {
            new SlackWorkspaceConfig
            {
                TeamId = "T1",
                Enabled = true,
                SigningSecretRef = "env://T1_SIGNING",
                BotTokenSecretRef = "env://T1_BOT",
                DefaultChannelId = "C1",
            },
            new SlackWorkspaceConfig
            {
                TeamId = "T2",
                Enabled = false,
                SigningSecretRef = "env://T2_SIGNING",
                BotTokenSecretRef = "env://T2_BOT",
                DefaultChannelId = "C2",
            },
        });

        SlackWorkspaceSecretRefSource source = new(store);

        List<SecretRefDescriptor> refs = await Collect(source);

        refs.Should().NotContain(d => d.SecretRef.StartsWith("env://T2_", System.StringComparison.Ordinal),
            "disabled workspaces would never have their secrets resolved at runtime, so warming them would generate spurious SecretNotFoundException log noise");
        refs.Select(d => d.SecretRef).Should().BeEquivalentTo(new[] { "env://T1_SIGNING", "env://T1_BOT" });
    }

    [Fact]
    public async Task GetSecretRefsAsync_yields_refs_from_multiple_workspaces()
    {
        InMemorySlackWorkspaceConfigStore store = new(new[]
        {
            new SlackWorkspaceConfig
            {
                TeamId = "T1",
                Enabled = true,
                SigningSecretRef = "env://T1_SIGNING",
                BotTokenSecretRef = "env://T1_BOT",
                DefaultChannelId = "C1",
            },
            new SlackWorkspaceConfig
            {
                TeamId = "T2",
                Enabled = true,
                SigningSecretRef = "env://T2_SIGNING",
                BotTokenSecretRef = "env://T2_BOT",
                DefaultChannelId = "C2",
            },
        });

        SlackWorkspaceSecretRefSource source = new(store);

        List<SecretRefDescriptor> refs = await Collect(source);
        IEnumerable<string> values = refs.Select(d => d.SecretRef);

        values.Should().Contain("env://T1_SIGNING")
            .And.Contain("env://T1_BOT")
            .And.Contain("env://T2_SIGNING")
            .And.Contain("env://T2_BOT");
    }

    private static async Task<List<SecretRefDescriptor>> Collect(SlackWorkspaceSecretRefSource source)
    {
        List<SecretRefDescriptor> result = new();
        await foreach (SecretRefDescriptor r in source.GetSecretRefsAsync(CancellationToken.None))
        {
            result.Add(r);
        }
        return result;
    }
}

