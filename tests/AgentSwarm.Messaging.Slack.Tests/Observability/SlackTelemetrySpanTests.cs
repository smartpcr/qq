// -----------------------------------------------------------------------
// <copyright file="SlackTelemetrySpanTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Tests.Observability;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AgentSwarm.Messaging.Core.Secrets;
using AgentSwarm.Messaging.Slack.Configuration;
using AgentSwarm.Messaging.Slack.Entities;
using AgentSwarm.Messaging.Slack.Observability;
using AgentSwarm.Messaging.Slack.Persistence;
using AgentSwarm.Messaging.Slack.Pipeline;
using AgentSwarm.Messaging.Slack.Queues;
using AgentSwarm.Messaging.Slack.Retry;
using AgentSwarm.Messaging.Slack.Security;
using AgentSwarm.Messaging.Slack.Transport;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

/// <summary>
/// Stage 7.2 span-emission tests for the Slack messenger connector.
/// Implements the brief's first scenario end-to-end: drive a complete
/// slash-command processing flow (signature -&gt; authorization -&gt;
/// idempotency -&gt; command dispatch), capture every emitted span via
/// an in-memory <see cref="ActivityListener"/>, and assert all four
/// expected spans are present AND carry the <c>correlation_id</c>
/// attribute mandated by architecture.md §6.3.
/// </summary>
/// <remarks>
/// The signature validator and authorization filter emit spans before
/// any <c>SlackInboundEnvelope</c> exists, so they seed
/// <c>correlation_id</c> from their own <see cref="Activity.TraceId"/>.
/// The pipeline-side spans (authorization, idempotency, dispatch)
/// override <c>correlation_id</c> with the envelope's
/// <c>IdempotencyKey</c> -- the same handle Stage 4.3 uses as the
/// audit-table correlation key. The test asserts the attribute is
/// populated regardless of which source it came from.
/// </remarks>
public sealed class SlackTelemetrySpanTests
{
    private const string TeamId = "T0123ABCD";
    private const string SigningSecretRef = "env://SLACK_SIGNING_SECRET";
    private const string SigningSecret = "8f742231b10e8888abcd99yyyzzz85a5";
    private const string DefaultPath = "/api/slack/events";

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    [Fact]
    public async Task Complete_slash_command_flow_emits_spans_for_signature_authorization_idempotency_and_command_dispatch_with_correlation_id_attribute()
    {
        // Brief scenario: "Given a complete slash command processing
        // flow, When an in-memory ActivityListener captures activities,
        // Then spans for signature validation, authorization,
        // idempotency, and command dispatch are present with
        // correlation_id attribute."
        using CapturingActivityListener listener = CapturingActivityListener.Subscribe();

        // === Stage 3.1: signature validator (HTTP middleware). ===
        DateTimeOffset now = DateTimeOffset.FromUnixTimeSeconds(1_714_410_000);
        string body = @"{""team_id"":""T0123ABCD"",""type"":""event_callback"",""event"":{}}";
        long timestamp = now.ToUnixTimeSeconds();
        string signature = ComputeSignatureHeader(SigningSecret, timestamp, body);

        SignatureHarness harness = SignatureHarness.Build(now);
        HttpContext ctx = BuildJsonContext(body, signature, timestamp.ToString(CultureInfo.InvariantCulture));

        await harness.Validator.InvokeAsync(ctx, _ => Task.CompletedTask);

        // === Stage 4.3: drive the pipeline directly for the same flow. ===
        // In a real HTTP request these would share the same Activity
        // tree; the unit test composes them sequentially so each span
        // is asserted on its own merits (presence + correlation_id).
        SlackInboundProcessingPipeline pipeline = BuildPipeline(out RecordingHandler handler);
        SlackInboundEnvelope envelope = BuildCommandEnvelope(
            idempotencyKey: "cmd:T1:U1:/agent:span-flow-" + Guid.NewGuid().ToString("N"));

        SlackInboundProcessingOutcome outcome = await pipeline.ProcessAsync(envelope, CancellationToken.None);

        outcome.Should().Be(SlackInboundProcessingOutcome.Processed,
            "the pipeline MUST drive the envelope through the success path so the command-dispatch span fires");
        handler.InvocationCount.Should().Be(1,
            "the command handler MUST be invoked once so the dispatch span captures the handler frame");

        // === Assertions: all four mandated spans present. ===
        // Filter the process-wide listener's catch to spans this test
        // produced. xUnit runs test classes in parallel against the
        // SAME ActivitySource; without a per-test correlation filter
        // the FirstOrDefault below could grab a span emitted by a
        // sibling test (e.g. SlackTelemetryMetricsTests' ingestor
        // also fires inbound.receive / authorization spans), and the
        // bonus correlation_id assertion would then fail comparing
        // this test's idempotency key against the other test's span.
        // The signature span pre-dates the envelope so it does not
        // carry envelope.IdempotencyKey; we filter that one by HTTP
        // route instead (DefaultPath is unique to this test class).
        IReadOnlyList<Activity> activities = listener.Activities;

        Activity? signatureSpan = activities.FirstOrDefault(a =>
            a.OperationName == SlackTelemetry.SignatureValidationSpanName
            && string.Equals(TagValue(a, "http.route"), DefaultPath, StringComparison.Ordinal));
        Activity? authSpan = activities.FirstOrDefault(a =>
            a.OperationName == SlackTelemetry.AuthorizationSpanName
            && string.Equals(TagValue(a, SlackTelemetry.AttributeCorrelationId), envelope.IdempotencyKey, StringComparison.Ordinal));
        Activity? idemSpan = activities.FirstOrDefault(a =>
            a.OperationName == SlackTelemetry.IdempotencyCheckSpanName
            && string.Equals(TagValue(a, SlackTelemetry.AttributeCorrelationId), envelope.IdempotencyKey, StringComparison.Ordinal));
        Activity? commandSpan = activities.FirstOrDefault(a =>
            a.OperationName == SlackTelemetry.CommandDispatchSpanName
            && string.Equals(TagValue(a, SlackTelemetry.AttributeCorrelationId), envelope.IdempotencyKey, StringComparison.Ordinal));

        signatureSpan.Should().NotBeNull("Stage 7.2 step 2 mandates a span on the signature-validation path");
        authSpan.Should().NotBeNull("Stage 7.2 step 2 mandates a span on the authorization path");
        idemSpan.Should().NotBeNull("Stage 7.2 step 2 mandates a span on the idempotency-check path");
        commandSpan.Should().NotBeNull("Stage 7.2 step 2 mandates a span on the command-dispatch path");

        AssertCorrelationIdPopulated(signatureSpan!, "signature validation");
        AssertCorrelationIdPopulated(authSpan!, "authorization");
        AssertCorrelationIdPopulated(idemSpan!, "idempotency check");
        AssertCorrelationIdPopulated(commandSpan!, "command dispatch");

        // === Bonus assertions to lock in the architecture.md §6.3 contract. ===
        // The pipeline spans MUST stamp the envelope's idempotency key
        // as correlation_id (the §6.3 convention is "envelope's
        // dedup-key doubles as correlation handle until the
        // orchestrator assigns a task id"). The signature span
        // pre-dates the envelope and seeds correlation from the trace
        // id, so we exclude it from this assertion.
        TagValue(authSpan!, SlackTelemetry.AttributeCorrelationId).Should().Be(envelope.IdempotencyKey,
            "pipeline-side spans MUST use the envelope's idempotency key as the correlation handle (architecture.md §6.3)");
        TagValue(idemSpan!, SlackTelemetry.AttributeCorrelationId).Should().Be(envelope.IdempotencyKey);
        TagValue(commandSpan!, SlackTelemetry.AttributeCorrelationId).Should().Be(envelope.IdempotencyKey);

        TagValue(authSpan!, SlackTelemetry.AttributeTeamId).Should().Be(envelope.TeamId,
            "team_id MUST be stamped so dashboards can split spans per workspace");
        TagValue(idemSpan!, SlackTelemetry.AttributeChannelId).Should().Be(envelope.ChannelId,
            "channel_id MUST be stamped so dashboards can split spans per channel");
    }

    [Fact]
    public async Task Pipeline_emits_inbound_receive_span_when_driven_through_ingestor_loop()
    {
        // Supplementary coverage: the outermost `slack.inbound.receive`
        // span (the Stage 4.3 BackgroundService boundary) is captured
        // by the same listener -- ingestor instrumentation pins this
        // separately to prove the wire-up survives composition.
        using CapturingActivityListener listener = CapturingActivityListener.Subscribe();

        FakeInboundQueue queue = new();
        SlackInboundProcessingPipeline pipeline = BuildPipeline(out _);
        SlackInboundIngestor ingestor = new(
            queue,
            pipeline,
            new InMemorySlackInboundEnqueueDeadLetterSink(NullLogger<InMemorySlackInboundEnqueueDeadLetterSink>.Instance),
            NullLogger<SlackInboundIngestor>.Instance);

        SlackInboundEnvelope env = BuildCommandEnvelope("cmd:T1:U1:/agent:span-ingestor-" + Guid.NewGuid().ToString("N"));
        await queue.EnqueueAsync(env);

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));
        Task run = ingestor.StartAsync(cts.Token);

        // xUnit runs sibling test classes in parallel; the same
        // SlackTelemetry.ActivitySource is process-wide so the
        // listener captures inbound.receive spans from any other
        // ingestor-driven test that happens to be running. Filter by
        // this test's per-envelope correlation_id (= IdempotencyKey)
        // so we deterministically pick up the span THIS test produced
        // regardless of execution order.
        await WaitUntilAsync(
            () => listener.Activities.Any(a =>
                a.OperationName == SlackTelemetry.InboundReceiveSpanName
                && string.Equals(TagValue(a, SlackTelemetry.AttributeCorrelationId), env.IdempotencyKey, StringComparison.Ordinal)),
            TimeSpan.FromSeconds(5));

        await ingestor.StopAsync(CancellationToken.None);
        cts.Cancel();
        await run;

        Activity receiveSpan = listener.Activities.First(a =>
            a.OperationName == SlackTelemetry.InboundReceiveSpanName
            && string.Equals(TagValue(a, SlackTelemetry.AttributeCorrelationId), env.IdempotencyKey, StringComparison.Ordinal));
        receiveSpan.Kind.Should().Be(ActivityKind.Consumer,
            "the queue-drain boundary MUST be ActivityKind.Consumer per OTel semantic conventions for queue-based ingest");
        TagValue(receiveSpan, SlackTelemetry.AttributeCorrelationId).Should().Be(env.IdempotencyKey);
        TagValue(receiveSpan, SlackTelemetry.AttributeTeamId).Should().Be(env.TeamId,
            "team_id MUST be stamped on the receive span so the §6.3 attribute set is uniform from the outermost frame down");
    }

    private static void AssertCorrelationIdPopulated(Activity activity, string description)
    {
        string? value = TagValue(activity, SlackTelemetry.AttributeCorrelationId);
        value.Should().NotBeNullOrEmpty(
            $"the {description} span MUST carry a populated correlation_id attribute per architecture.md §6.3 and Stage 7.2");
    }

    private static string? TagValue(Activity activity, string key)
        => activity.Tags.FirstOrDefault(t => t.Key == key).Value;

    private static SlackInboundProcessingPipeline BuildPipeline(out RecordingHandler handler)
    {
        handler = new RecordingHandler();
        return new SlackInboundProcessingPipeline(
            new AlwaysAuthorizingAuthorizer(),
            new InMemorySlackIdempotencyGuard(),
            new RecordingCommandHandler(handler),
            new RecordingAppMentionHandler(new RecordingHandler()),
            new RecordingInteractionHandler(new RecordingHandler()),
            new ZeroDelayRetryPolicy(maxAttempts: 1),
            new InMemoryDeadLetterQueue(),
            new SlackInboundAuditRecorder(
                new InMemorySlackAuditEntryWriter(),
                NullLogger<SlackInboundAuditRecorder>.Instance,
                TimeProvider.System),
            NullLogger<SlackInboundProcessingPipeline>.Instance,
            TimeProvider.System);
    }

    private static SlackInboundEnvelope BuildCommandEnvelope(string idempotencyKey) => new(
        IdempotencyKey: idempotencyKey,
        SourceType: SlackInboundSourceType.Command,
        TeamId: "T1",
        ChannelId: "C1",
        UserId: "U1",
        RawPayload: "team_id=T1&user_id=U1&command=/agent",
        TriggerId: "trig",
        ReceivedAt: DateTimeOffset.UtcNow);

    private static async Task WaitUntilAsync(Func<bool> predicate, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (predicate())
            {
                return;
            }

            await Task.Delay(20);
        }
    }

    private static HttpContext BuildJsonContext(string body, string? signatureHeader, string? timestampHeader)
    {
        DefaultHttpContext ctx = new();
        ctx.Request.Method = "POST";
        ctx.Request.Path = DefaultPath;
        ctx.Request.ContentType = "application/json";

        byte[] bodyBytes = Utf8NoBom.GetBytes(body);
        MemoryStream stream = new(bodyBytes, writable: false);
        ctx.Request.Body = stream;
        ctx.Request.ContentLength = bodyBytes.Length;

        if (signatureHeader is not null)
        {
            ctx.Request.Headers[SlackSignatureValidator.SignatureHeaderName] = signatureHeader;
        }

        if (timestampHeader is not null)
        {
            ctx.Request.Headers[SlackSignatureValidator.TimestampHeaderName] = timestampHeader;
        }

        ctx.Response.Body = new MemoryStream();
        return ctx;
    }

    private static string ComputeSignatureHeader(string signingSecret, long timestamp, string body)
    {
        string baseString = FormattableString.Invariant($"v0:{timestamp}:{body}");
        byte[] hash = HMACSHA256.HashData(Utf8NoBom.GetBytes(signingSecret), Utf8NoBom.GetBytes(baseString));
        return "v0=" + Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    /// In-memory <see cref="ActivityListener"/> that subscribes to the
    /// Slack <see cref="ActivitySource"/> by name and records every
    /// stopped activity (so the assertions see the final tag set).
    /// </summary>
    private sealed class CapturingActivityListener : IDisposable
    {
        private readonly ActivityListener listener;
        private readonly ConcurrentBag<Activity> activities = new();

        private CapturingActivityListener()
        {
            this.listener = new ActivityListener
            {
                ShouldListenTo = src => src.Name == SlackTelemetry.SourceName,
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
                SampleUsingParentId = (ref ActivityCreationOptions<string> _) => ActivitySamplingResult.AllDataAndRecorded,
                ActivityStopped = a => this.activities.Add(a),
            };
            ActivitySource.AddActivityListener(this.listener);
        }

        public IReadOnlyList<Activity> Activities => this.activities.ToArray();

        public static CapturingActivityListener Subscribe() => new();

        public void Dispose() => this.listener.Dispose();
    }

    private sealed class SignatureHarness
    {
        private SignatureHarness(SlackSignatureValidator validator)
        {
            this.Validator = validator;
        }

        public SlackSignatureValidator Validator { get; }

        public static SignatureHarness Build(DateTimeOffset currentTime)
        {
            SlackWorkspaceConfig workspace = new()
            {
                TeamId = TeamId,
                WorkspaceName = "Acme",
                BotTokenSecretRef = "env://SLACK_BOT_TOKEN",
                SigningSecretRef = SigningSecretRef,
                DefaultChannelId = "C0123",
                Enabled = true,
                CreatedAt = currentTime,
                UpdatedAt = currentTime,
            };

            InMemorySlackWorkspaceConfigStore workspaceStore = new(seed: new[] { workspace });
            InMemorySecretProvider secretProvider = new();
            secretProvider.Set(SigningSecretRef, SigningSecret);
            InMemorySlackSignatureAuditSink auditSink = new();
            FixedTimeProvider timeProvider = new(currentTime);

            SlackSignatureValidator validator = new(
                workspaceStore,
                secretProvider,
                auditSink,
                new StaticOptionsMonitor<SlackSignatureOptions>(new SlackSignatureOptions()),
                NullLogger<SlackSignatureValidator>.Instance,
                timeProvider);

            return new SignatureHarness(validator);
        }
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset utcNow;

        public FixedTimeProvider(DateTimeOffset utcNow)
        {
            this.utcNow = utcNow;
        }

        public override DateTimeOffset GetUtcNow() => this.utcNow;
    }

    private sealed class StaticOptionsMonitor<T> : IOptionsMonitor<T>
        where T : class
    {
        private readonly T value;

        public StaticOptionsMonitor(T value)
        {
            this.value = value;
        }

        public T CurrentValue => this.value;

        public T Get(string? name) => this.value;

        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }

    private sealed class FakeInboundQueue : ISlackInboundQueue
    {
        private readonly System.Threading.Channels.Channel<SlackInboundEnvelope> channel =
            System.Threading.Channels.Channel.CreateUnbounded<SlackInboundEnvelope>();

        public ValueTask EnqueueAsync(SlackInboundEnvelope envelope)
        {
            this.channel.Writer.TryWrite(envelope);
            return ValueTask.CompletedTask;
        }

        public ValueTask<SlackInboundEnvelope> DequeueAsync(CancellationToken ct)
            => this.channel.Reader.ReadAsync(ct);
    }

    private sealed class AlwaysAuthorizingAuthorizer : ISlackInboundAuthorizer
    {
        public Task<SlackInboundAuthorizationResult> AuthorizeAsync(SlackInboundEnvelope envelope, CancellationToken ct)
            => Task.FromResult(SlackInboundAuthorizationResult.Authorized(new SlackWorkspaceConfig
            {
                TeamId = envelope.TeamId,
                Enabled = true,
            }));
    }

    private sealed class RecordingHandler
    {
        private int count;

        public int InvocationCount => Volatile.Read(ref this.count);

        public Task HandleAsync(SlackInboundEnvelope envelope, CancellationToken ct)
        {
            Interlocked.Increment(ref this.count);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingCommandHandler : ISlackCommandHandler
    {
        private readonly RecordingHandler inner;

        public RecordingCommandHandler(RecordingHandler inner)
        {
            this.inner = inner;
        }

        public Task HandleAsync(SlackInboundEnvelope envelope, CancellationToken ct)
            => this.inner.HandleAsync(envelope, ct);
    }

    private sealed class RecordingAppMentionHandler : ISlackAppMentionHandler
    {
        private readonly RecordingHandler inner;

        public RecordingAppMentionHandler(RecordingHandler inner)
        {
            this.inner = inner;
        }

        public Task HandleAsync(SlackInboundEnvelope envelope, CancellationToken ct)
            => this.inner.HandleAsync(envelope, ct);
    }

    private sealed class RecordingInteractionHandler : ISlackInteractionHandler
    {
        private readonly RecordingHandler inner;

        public RecordingInteractionHandler(RecordingHandler inner)
        {
            this.inner = inner;
        }

        public Task HandleAsync(SlackInboundEnvelope envelope, CancellationToken ct)
            => this.inner.HandleAsync(envelope, ct);
    }

    private sealed class InMemoryDeadLetterQueue : ISlackDeadLetterQueue
    {
        private readonly ConcurrentQueue<SlackDeadLetterEntry> entries = new();

        public ValueTask EnqueueAsync(SlackDeadLetterEntry entry, CancellationToken ct = default)
        {
            this.entries.Enqueue(entry);
            return ValueTask.CompletedTask;
        }

        public ValueTask<IReadOnlyList<SlackDeadLetterEntry>> InspectAsync(CancellationToken ct = default)
            => ValueTask.FromResult<IReadOnlyList<SlackDeadLetterEntry>>(this.entries.ToArray());
    }

    private sealed class ZeroDelayRetryPolicy : ISlackRetryPolicy
    {
        private readonly int maxAttempts;

        public ZeroDelayRetryPolicy(int maxAttempts)
        {
            this.maxAttempts = maxAttempts;
        }

        public bool ShouldRetry(int attemptNumber, Exception exception)
            => exception is not OperationCanceledException && attemptNumber < this.maxAttempts;

        public TimeSpan GetDelay(int attemptNumber) => TimeSpan.Zero;
    }
}
