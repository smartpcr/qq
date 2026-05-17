// -----------------------------------------------------------------------
// <copyright file="SlackSocketModeOptionsBindingTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Tests.Transport;

using System;
using System.Collections.Generic;
using AgentSwarm.Messaging.Slack.Transport;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

/// <summary>
/// Stage 4.2 evaluator iter-1 item 3 regression tests for the
/// configuration-bound <see cref="SlackSocketModeOptions"/>. The
/// <c>AddSlackSocketModeTransport(IServiceCollection, IConfiguration)</c>
/// overload must read overrides from the <c>Slack:SocketMode</c>
/// configuration section so operators can tune reconnect bounds, ACK
/// timeout, and receive-buffer size without recompiling.
/// </summary>
public sealed class SlackSocketModeOptionsBindingTests
{
    [Fact]
    public void AddSlackSocketModeTransport_binds_options_from_Slack_SocketMode_section()
    {
        Dictionary<string, string?> settings = new()
        {
            ["Slack:SocketMode:InitialReconnectDelay"] = "00:00:02",
            ["Slack:SocketMode:MaxReconnectDelay"] = "00:00:45",
            ["Slack:SocketMode:AckTimeout"] = "00:00:03",
            ["Slack:SocketMode:ReceiveBufferSize"] = "32768",
        };
        IConfigurationRoot config = new ConfigurationBuilder()
            .AddInMemoryCollection(settings)
            .Build();

        ServiceCollection services = new();
        services.AddSlackSocketModeTransport(config);

        using ServiceProvider sp = services.BuildServiceProvider();
        SlackSocketModeOptions bound = sp.GetRequiredService<IOptions<SlackSocketModeOptions>>().Value;

        bound.InitialReconnectDelay.Should().Be(TimeSpan.FromSeconds(2));
        bound.MaxReconnectDelay.Should().Be(TimeSpan.FromSeconds(45));
        bound.AckTimeout.Should().Be(TimeSpan.FromSeconds(3));
        bound.ReceiveBufferSize.Should().Be(32768);

        // The eagerly-resolved value singleton must reflect the same
        // values so consumers that take SlackSocketModeOptions by value
        // (the connection factory, the receiver) see the operator's
        // overrides too.
        SlackSocketModeOptions valueSingleton = sp.GetRequiredService<SlackSocketModeOptions>();
        valueSingleton.InitialReconnectDelay.Should().Be(TimeSpan.FromSeconds(2));
        valueSingleton.MaxReconnectDelay.Should().Be(TimeSpan.FromSeconds(45));
        valueSingleton.AckTimeout.Should().Be(TimeSpan.FromSeconds(3));
        valueSingleton.ReceiveBufferSize.Should().Be(32768);
    }

    [Fact]
    public void AddSlackSocketModeTransport_uses_defaults_when_no_configuration_supplied()
    {
        ServiceCollection services = new();
        services.AddSlackSocketModeTransport();

        using ServiceProvider sp = services.BuildServiceProvider();
        SlackSocketModeOptions bound = sp.GetRequiredService<IOptions<SlackSocketModeOptions>>().Value;

        // Architecture.md §2.2.3: initial 1 s, max 30 s.
        bound.InitialReconnectDelay.Should().Be(TimeSpan.FromSeconds(1));
        bound.MaxReconnectDelay.Should().Be(TimeSpan.FromSeconds(30));
        bound.AckTimeout.Should().Be(TimeSpan.FromSeconds(4));
        bound.ReceiveBufferSize.Should().Be(16 * 1024);
    }

    [Fact]
    public void AddSlackSocketModeTransport_uses_defaults_when_section_is_missing()
    {
        IConfigurationRoot config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build();

        ServiceCollection services = new();
        services.AddSlackSocketModeTransport(config);

        using ServiceProvider sp = services.BuildServiceProvider();
        SlackSocketModeOptions bound = sp.GetRequiredService<IOptions<SlackSocketModeOptions>>().Value;

        bound.InitialReconnectDelay.Should().Be(TimeSpan.FromSeconds(1));
        bound.MaxReconnectDelay.Should().Be(TimeSpan.FromSeconds(30));
        bound.AckTimeout.Should().Be(TimeSpan.FromSeconds(4));
        bound.ReceiveBufferSize.Should().Be(16 * 1024);
    }
}
