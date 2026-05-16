using AgentSwarm.Messaging.Abstractions;
using AgentSwarm.Messaging.Telegram.Webhook;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace AgentSwarm.Messaging.Tests;

/// <summary>
/// Stage 2.4 — pins <see cref="InboundUpdateProcessor.ProcessAsync"/>
/// against the Stage 2.2 hybrid retry contract (throw = retryable
/// → Failed; return = terminal → Completed) and the
/// dispatcher-vs-sweep claim race protocol.
/// </summary>
public sealed class InboundUpdateProcessorTests
{
    private const string CorrelationId = "trace-test";

    private static InboundUpdate SampleRow(long updateId = 1) =>
        new()
        {
            UpdateId = updateId,
            // Minimal valid Telegram Update payload so the mapper succeeds.
            RawPayload = "{\"update_id\":" + updateId
                + ",\"message\":{\"message_id\":1,\"chat\":{\"id\":1,\"type\":\"private\"},"
                + "\"from\":{\"id\":2,\"is_bot\":false,\"first_name\":\"u\"},\"text\":\"/status\"}}",
            ReceivedAt = DateTimeOffset.UtcNow,
            IdempotencyStatus = IdempotencyStatus.Received,
        };

    private static InboundUpdateProcessor NewProcessor(
        out Mock<IInboundUpdateStore> storeMock,
        out Mock<ITelegramUpdatePipeline> pipelineMock)
    {
        storeMock = new Mock<IInboundUpdateStore>();
        pipelineMock = new Mock<ITelegramUpdatePipeline>();
        return new InboundUpdateProcessor(
            storeMock.Object,
            pipelineMock.Object,
            NullLogger<InboundUpdateProcessor>.Instance);
    }

    [Fact]
    public async Task LostClaim_SkipsWithoutInvokingPipeline()
    {
        var processor = NewProcessor(out var store, out var pipeline);
        store.Setup(s => s.TryMarkProcessingAsync(1, It.IsAny<CancellationToken>()))
             .ReturnsAsync(false);

        var ran = await processor.ProcessAsync(SampleRow(1), CorrelationId, CancellationToken.None);

        ran.Should().BeFalse(
            "iter-5 evaluator item 4 — the processor's return value MUST be false when TryMarkProcessing CAS lost the claim, so the sweep does not over-count CAS-rejected rows");
        pipeline.Verify(p => p.ProcessAsync(It.IsAny<MessengerEvent>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "a lost claim means another worker owns the row — the pipeline must not be invoked");
        store.Verify(s => s.MarkCompletedAsync(It.IsAny<long>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Never);
        store.Verify(s => s.MarkFailedAsync(It.IsAny<long>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task PipelineRan_ReturnsTrue_RegardlessOfSuccessOutcome()
    {
        // iter-5 evaluator item 4 — the bool return signals "did the
        // pipeline run?" not "did it succeed?". A pipeline that
        // returned Succeeded=false still ran (and the row is now
        // Completed-with-HandlerErrorDetail), so the sweep counts it.
        var processor = NewProcessor(out var store, out var pipeline);
        store.Setup(s => s.TryMarkProcessingAsync(99, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        pipeline.Setup(p => p.ProcessAsync(It.IsAny<MessengerEvent>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new PipelineResult
                {
                    Handled = true,
                    Succeeded = false,
                    ErrorCode = "handler-error",
                    ResponseText = "boom",
                    CorrelationId = CorrelationId,
                });

        var ran = await processor.ProcessAsync(SampleRow(99), CorrelationId, CancellationToken.None);

        ran.Should().BeTrue(
            "the pipeline ran (even though it returned Succeeded=false) so the processor must report it as a processed row to the sweep");
    }

    [Fact]
    public async Task PipelineThrew_ReturnsTrue_RowMarkedFailed()
    {
        // iter-5 evaluator item 4 — the bool is "did pipeline run?".
        // A pipeline that THREW still ran; the sweep counts it.
        var processor = NewProcessor(out var store, out var pipeline);
        store.Setup(s => s.TryMarkProcessingAsync(98, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        pipeline.Setup(p => p.ProcessAsync(It.IsAny<MessengerEvent>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException("downstream-503"));

        var ran = await processor.ProcessAsync(SampleRow(98), CorrelationId, CancellationToken.None);

        ran.Should().BeTrue(
            "the pipeline ran (and threw) so the processor must report it as a processed row — the sweep counts attempts, not just successes");
        store.Verify(s => s.MarkFailedAsync(98, It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DeserializerThrew_ReturnsTrue_RowMarkedFailed()
    {
        // iter-5 evaluator item 4 — the bool is "did the processor
        // do work past TryMarkProcessing?". A row whose RawPayload
        // failed to deserialize still consumed a sweep slot (the
        // processor advanced the row to Failed); the sweep counts it.
        var processor = NewProcessor(out var store, out _);
        store.Setup(s => s.TryMarkProcessingAsync(97, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var badRow = new InboundUpdate
        {
            UpdateId = 97,
            RawPayload = "{ this is not valid json",
            ReceivedAt = DateTimeOffset.UtcNow,
            IdempotencyStatus = IdempotencyStatus.Received,
        };

        var ran = await processor.ProcessAsync(badRow, CorrelationId, CancellationToken.None);

        ran.Should().BeTrue();
        store.Verify(s => s.MarkFailedAsync(97, It.Is<string>(d => d.Contains("deserialization")),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task PipelineSucceeded_True_TransitionsToCompleted_WithNullHandlerErrorDetail()
    {
        var processor = NewProcessor(out var store, out var pipeline);
        store.Setup(s => s.TryMarkProcessingAsync(2, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        pipeline.Setup(p => p.ProcessAsync(It.IsAny<MessengerEvent>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new PipelineResult
                {
                    Handled = true,
                    Succeeded = true,
                    CorrelationId = CorrelationId,
                });

        await processor.ProcessAsync(SampleRow(2), CorrelationId, CancellationToken.None);

        store.Verify(s => s.MarkCompletedAsync(2, null, It.IsAny<CancellationToken>()), Times.Once);
        store.Verify(s => s.MarkFailedAsync(It.IsAny<long>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task PipelineSucceeded_False_TransitionsToCompleted_WithHandlerErrorDetail()
    {
        var processor = NewProcessor(out var store, out var pipeline);
        store.Setup(s => s.TryMarkProcessingAsync(3, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        pipeline.Setup(p => p.ProcessAsync(It.IsAny<MessengerEvent>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new PipelineResult
                {
                    Handled = true,
                    Succeeded = false,
                    ErrorCode = "ROUTING_DENIED",
                    ResponseText = "Unauthorized",
                    CorrelationId = CorrelationId,
                });

        await processor.ProcessAsync(SampleRow(3), CorrelationId, CancellationToken.None);

        // Hybrid retry contract: return = terminal. The handler ran to
        // completion; the operator has already seen the response; this
        // row must NOT enter the sweep retry path.
        store.Verify(s => s.MarkCompletedAsync(
            3,
            It.Is<string?>(d => d != null && d.Contains("ROUTING_DENIED") && d.Contains("Unauthorized")),
            It.IsAny<CancellationToken>()),
            Times.Once);
        store.Verify(s => s.MarkFailedAsync(It.IsAny<long>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task PipelineThrows_TransitionsToFailed_WithErrorDetail()
    {
        var processor = NewProcessor(out var store, out var pipeline);
        store.Setup(s => s.TryMarkProcessingAsync(4, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        pipeline.Setup(p => p.ProcessAsync(It.IsAny<MessengerEvent>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException("transient downstream failure"));

        await processor.ProcessAsync(SampleRow(4), CorrelationId, CancellationToken.None);

        // Hybrid retry contract: throw = retryable. The sweep replays
        // this row up to MaxRetries times.
        store.Verify(s => s.MarkFailedAsync(
            4,
            It.Is<string>(d => d.Contains("InvalidOperationException") && d.Contains("transient downstream failure")),
            It.IsAny<CancellationToken>()),
            Times.Once);
        store.Verify(s => s.MarkCompletedAsync(It.IsAny<long>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task PipelineThrows_OperationCanceled_ReleasesRowProcessingToReceived_AndPropagates()
    {
        var processor = NewProcessor(out var store, out var pipeline);
        store.Setup(s => s.TryMarkProcessingAsync(5, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        store.Setup(s => s.ReleaseProcessingAsync(5, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        pipeline.Setup(p => p.ProcessAsync(It.IsAny<MessengerEvent>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new OperationCanceledException());

        // OCE during pipeline = host shutdown. The cancel handler must
        // call ReleaseProcessingAsync (Processing→Received without
        // bumping AttemptCount) so the row is naturally picked up by
        // the next sweep tick — architecture.md §4.8 forbids leaving
        // Processing rows stranded.
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            processor.ProcessAsync(SampleRow(5), CorrelationId, CancellationToken.None));

        store.Verify(
            s => s.ReleaseProcessingAsync(5, It.IsAny<CancellationToken>()),
            Times.Once,
            "cancel handler must release the row Processing→Received without incrementing AttemptCount");
        store.Verify(s => s.MarkCompletedAsync(It.IsAny<long>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Never);
        store.Verify(s => s.MarkFailedAsync(It.IsAny<long>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "cancellation is not a pipeline failure — AttemptCount must NOT count toward the retry budget");
    }

    [Fact]
    public async Task PipelineThrows_OperationCanceled_ReleaseFailureSwallowed_DoesNotMaskOriginalCancellation()
    {
        var processor = NewProcessor(out var store, out var pipeline);
        store.Setup(s => s.TryMarkProcessingAsync(15, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        store.Setup(s => s.ReleaseProcessingAsync(15, It.IsAny<CancellationToken>()))
             .ThrowsAsync(new InvalidOperationException("DB went away mid-shutdown"));
        pipeline.Setup(p => p.ProcessAsync(It.IsAny<MessengerEvent>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new OperationCanceledException());

        // The cancel-handler release attempt may itself fail (DB
        // dropped, etc). The original OperationCanceledException must
        // still propagate — masking it would prevent the host from
        // observing the shutdown. The next startup's ResetInterruptedAsync
        // is the safety net for this stranded-row scenario.
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            processor.ProcessAsync(SampleRow(15), CorrelationId, CancellationToken.None));
    }

    [Fact]
    public async Task PipelineThrows_OperationCanceled_UsesNonCancelledTokenForRelease()
    {
        var processor = NewProcessor(out var store, out var pipeline);
        store.Setup(s => s.TryMarkProcessingAsync(16, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        CancellationToken capturedReleaseToken = default;
        store.Setup(s => s.ReleaseProcessingAsync(16, It.IsAny<CancellationToken>()))
             .Callback<long, CancellationToken>((_, ct) => capturedReleaseToken = ct)
             .ReturnsAsync(true);
        pipeline.Setup(p => p.ProcessAsync(It.IsAny<MessengerEvent>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new OperationCanceledException());

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            processor.ProcessAsync(SampleRow(16), CorrelationId, cts.Token));

        // The inbound ct is cancelled — passing it through to
        // ReleaseProcessingAsync would prevent the release from running
        // and strand the row. The handler must use a fresh / None token.
        capturedReleaseToken.IsCancellationRequested.Should().BeFalse(
            "release must use a non-cancelled token so the cleanup actually executes");
    }

    [Fact]
    public async Task MalformedRawPayload_TransitionsToFailed_WithoutInvokingPipeline()
    {
        var processor = NewProcessor(out var store, out var pipeline);
        store.Setup(s => s.TryMarkProcessingAsync(6, It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var row = SampleRow(6) with { RawPayload = "{this is not valid json" };

        await processor.ProcessAsync(row, CorrelationId, CancellationToken.None);

        pipeline.Verify(p => p.ProcessAsync(It.IsAny<MessengerEvent>(), It.IsAny<CancellationToken>()),
            Times.Never);
        store.Verify(s => s.MarkFailedAsync(
            6,
            It.Is<string>(d => d.Contains("deserialization failed")),
            It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task BlankCorrelationId_Throws()
    {
        var processor = NewProcessor(out _, out _);

        var act = () => processor.ProcessAsync(SampleRow(7), "   ", CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task NullRow_Throws()
    {
        var processor = NewProcessor(out _, out _);

        var act = () => processor.ProcessAsync(null!, CorrelationId, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }
}
