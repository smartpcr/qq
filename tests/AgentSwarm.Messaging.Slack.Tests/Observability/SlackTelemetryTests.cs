// -----------------------------------------------------------------------
// <copyright file="SlackTelemetryTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Tests.Observability;

using System.Diagnostics;
using System.Diagnostics.Metrics;
using AgentSwarm.Messaging.Slack.Observability;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// Stage 7.2 primitives tests for <see cref="SlackTelemetry"/>: pins the
/// public contract every OpenTelemetry exporter and dashboard depends on
/// -- the ActivitySource / Meter name strings, the six metric names
/// called out by the brief, and the DI extension surface. A rename or
/// drift on any of these symbols silently breaks downstream subscribers,
/// so the assertions here are intentionally narrow and literal.
/// </summary>
public sealed class SlackTelemetryTests
{
    [Fact]
    public void ActivitySource_name_matches_architecture_spec()
    {
        SlackTelemetry.SourceName.Should().Be("AgentSwarm.Messaging.Slack",
            "architecture.md §6.3 pins the activity-source name; renaming it breaks every downstream OTel listener");
        SlackTelemetry.ActivitySource.Should().NotBeNull();
        SlackTelemetry.ActivitySource.Name.Should().Be(SlackTelemetry.SourceName);
    }

    [Fact]
    public void Meter_name_matches_architecture_spec()
    {
        SlackTelemetry.MeterName.Should().Be("AgentSwarm.Messaging.Slack",
            "architecture.md §6.3 pins the meter name; renaming it breaks every downstream metric scrape");
        SlackTelemetry.Meter.Should().NotBeNull();
        SlackTelemetry.Meter.Name.Should().Be(SlackTelemetry.MeterName);
    }

    [Fact]
    public void All_six_metric_instruments_use_the_brief_mandated_names()
    {
        // The Stage 7.2 brief lists EXACTLY six instruments. A rename of
        // any one breaks the operator runbook + downstream Prometheus
        // scrape configs, so we pin every literal here.
        SlackTelemetry.InboundCount.Name.Should().Be("slack.inbound.count");
        SlackTelemetry.OutboundCount.Name.Should().Be("slack.outbound.count");
        SlackTelemetry.OutboundLatencyMs.Name.Should().Be("slack.outbound.latency_ms");
        SlackTelemetry.IdempotencyDuplicateCount.Name.Should().Be("slack.idempotency.duplicate_count");
        SlackTelemetry.AuthRejectedCount.Name.Should().Be("slack.auth.rejected_count");
        SlackTelemetry.RateLimitBackoffCount.Name.Should().Be("slack.ratelimit.backoff_count");
    }

    [Fact]
    public void Instrument_units_and_types_match_observability_contract()
    {
        // Counters are long-valued, the latency histogram is double-valued
        // and reports milliseconds. Pinning unit strings prevents an
        // accidental change ("s" vs. "ms") that would silently corrupt
        // every dashboard's P95 panel.
        SlackTelemetry.InboundCount.Should().BeOfType<Counter<long>>();
        SlackTelemetry.OutboundCount.Should().BeOfType<Counter<long>>();
        SlackTelemetry.IdempotencyDuplicateCount.Should().BeOfType<Counter<long>>();
        SlackTelemetry.AuthRejectedCount.Should().BeOfType<Counter<long>>();
        SlackTelemetry.RateLimitBackoffCount.Should().BeOfType<Counter<long>>();
        SlackTelemetry.OutboundLatencyMs.Should().BeOfType<Histogram<double>>();
        SlackTelemetry.OutboundLatencyMs.Unit.Should().Be("ms",
            "architecture.md §6.3 calls out outbound latency in milliseconds; renaming the unit silently corrupts dashboards");
    }

    [Fact]
    public void All_brief_span_names_are_exposed_as_constants()
    {
        // The brief enumerates the spans the instrumentation MUST emit:
        // inbound receive, signature validation, authorization check,
        // idempotency check, command dispatch, outbound send, modal
        // open. Pinning the literals here means a rename in production
        // code is a compile-time break here rather than a silent
        // observability regression.
        SlackTelemetry.InboundReceiveSpanName.Should().Be("slack.inbound.receive");
        SlackTelemetry.SignatureValidationSpanName.Should().Be("slack.signature.validate");
        SlackTelemetry.AuthorizationSpanName.Should().Be("slack.authorization");
        SlackTelemetry.IdempotencyCheckSpanName.Should().Be("slack.idempotency.check");
        SlackTelemetry.CommandDispatchSpanName.Should().Be("slack.command.dispatch");
        SlackTelemetry.InteractionDispatchSpanName.Should().Be("slack.interaction.dispatch");
        SlackTelemetry.EventDispatchSpanName.Should().Be("slack.event.dispatch");
        SlackTelemetry.OutboundSendSpanName.Should().Be("slack.outbound.send");
        SlackTelemetry.ModalOpenSpanName.Should().Be("slack.modal.open");
    }

    [Fact]
    public void All_section_6_3_attribute_keys_are_exposed_as_constants()
    {
        // architecture.md §6.3 lists the five correlation attributes
        // that every Slack span MUST carry. The brief's first test
        // scenario asserts `correlation_id` is present on spans; if
        // these literals drift the assertion would still pass against
        // the wrong key. Pin them here so a typo surfaces immediately.
        SlackTelemetry.AttributeCorrelationId.Should().Be("correlation_id");
        SlackTelemetry.AttributeTaskId.Should().Be("task_id");
        SlackTelemetry.AttributeAgentId.Should().Be("agent_id");
        SlackTelemetry.AttributeTeamId.Should().Be("team_id");
        SlackTelemetry.AttributeChannelId.Should().Be("channel_id");
    }

    [Fact]
    public void AddSlackTelemetry_registers_singleton_ActivitySource_and_Meter()
    {
        ServiceCollection services = new();
        services.AddSlackTelemetry();
        using ServiceProvider provider = services.BuildServiceProvider();

        ActivitySource resolvedSource = provider.GetRequiredService<ActivitySource>();
        Meter resolvedMeter = provider.GetRequiredService<Meter>();

        resolvedSource.Should().BeSameAs(SlackTelemetry.ActivitySource,
            "the DI registration must surface the process-wide singleton so listeners and producers share the same instance");
        resolvedMeter.Should().BeSameAs(SlackTelemetry.Meter,
            "the DI registration must surface the process-wide singleton so MeterListeners and instrument producers agree on identity");
    }

    [Fact]
    public void AddSlackTelemetry_is_idempotent_when_called_more_than_once()
    {
        // The Worker host's BuildApp may call AddSlackTelemetry early;
        // additional callers (test harnesses, integration fixtures)
        // must be able to re-register without throwing or replacing the
        // singletons. TryAddSingleton guarantees this, but the test
        // pins the contract so a future refactor cannot silently
        // regress to AddSingleton (which would duplicate registrations
        // and surface as "already registered" exceptions in some hosts).
        ServiceCollection services = new();
        services.AddSlackTelemetry();
        services.AddSlackTelemetry();
        services.AddSlackTelemetry();

        using ServiceProvider provider = services.BuildServiceProvider();
        provider.GetRequiredService<ActivitySource>().Should().BeSameAs(SlackTelemetry.ActivitySource);
        provider.GetRequiredService<Meter>().Should().BeSameAs(SlackTelemetry.Meter);
    }
}
