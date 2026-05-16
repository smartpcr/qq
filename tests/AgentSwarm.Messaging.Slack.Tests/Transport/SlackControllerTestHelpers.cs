// -----------------------------------------------------------------------
// <copyright file="SlackControllerTestHelpers.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Tests.Transport;

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AgentSwarm.Messaging.Slack.Queues;
using AgentSwarm.Messaging.Slack.Transport;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

/// <summary>
/// Shared helpers for the Stage 4.1 controller unit tests. Builds a
/// <see cref="DefaultHttpContext"/> wired with the minimum services
/// the controllers resolve via
/// <see cref="HttpContext.RequestServices"/> (envelope factory,
/// inbound queue, modal fast-path handler, logger) AND a custom
/// <see cref="IHttpResponseFeature"/> that captures
/// <see cref="HttpResponse.OnCompleted(Func{Task})"/> callbacks so the
/// post-ACK enqueue path (Stage 4.1 evaluator iter-1 item 3) can be
/// driven from a unit test.
/// </summary>
internal static class SlackControllerTestHelpers
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public static SlackTestHttpContext BuildContext(
        string body,
        string contentType,
        ISlackModalFastPathHandler? overrideFastPath = null)
    {
        ChannelBasedSlackInboundQueue queue = new();
        RecordingModalFastPathHandler fastPath = overrideFastPath as RecordingModalFastPathHandler
            ?? new RecordingModalFastPathHandler();
        InMemorySlackInboundEnqueueDeadLetterSink deadLetter =
            new(NullLogger<InMemorySlackInboundEnqueueDeadLetterSink>.Instance);

        ServiceCollection services = new();
        services.AddSingleton(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>),
            typeof(NullLogger<>));
        services.AddSingleton<TimeProvider>(TimeProvider.System);
        services.AddSingleton<SlackInboundEnvelopeFactory>();
        services.AddSingleton<ISlackInboundQueue>(queue);
        services.AddSingleton<ISlackModalFastPathHandler>(overrideFastPath ?? fastPath);
        services.AddSingleton<InMemorySlackInboundEnqueueDeadLetterSink>(deadLetter);
        services.AddSingleton<ISlackInboundEnqueueDeadLetterSink>(deadLetter);

        TestableHttpResponseFeature responseFeature = new();
        DefaultHttpContext ctx = new()
        {
            RequestServices = services.BuildServiceProvider(),
        };
        ctx.Features.Set<IHttpResponseFeature>(responseFeature);

        byte[] bytes = Utf8NoBom.GetBytes(body);
        ctx.Request.Method = "POST";
        ctx.Request.ContentType = contentType;
        ctx.Request.ContentLength = bytes.Length;
        MemoryStream stream = new();
        stream.Write(bytes, 0, bytes.Length);
        stream.Position = 0;
        ctx.Request.Body = stream;

        return new SlackTestHttpContext(ctx, queue, fastPath, responseFeature, deadLetter);
    }

    public static async Task<SlackInboundEnvelope> DequeueWithTimeoutAsync(
        ISlackInboundQueue queue,
        int millisecondTimeout = 500)
    {
        using CancellationTokenSource cts = new(millisecondTimeout);
        return await queue.DequeueAsync(cts.Token);
    }
}

/// <summary>
/// Convenience bundle returned by
/// <see cref="SlackControllerTestHelpers.BuildContext"/>.
/// </summary>
internal sealed class SlackTestHttpContext
{
    public SlackTestHttpContext(
        DefaultHttpContext context,
        ChannelBasedSlackInboundQueue queue,
        RecordingModalFastPathHandler fastPath,
        TestableHttpResponseFeature responseFeature,
        InMemorySlackInboundEnqueueDeadLetterSink deadLetter)
    {
        this.Context = context;
        this.Queue = queue;
        this.FastPath = fastPath;
        this.ResponseFeature = responseFeature;
        this.DeadLetter = deadLetter;
    }

    public DefaultHttpContext Context { get; }

    public ChannelBasedSlackInboundQueue Queue { get; }

    public RecordingModalFastPathHandler FastPath { get; }

    public TestableHttpResponseFeature ResponseFeature { get; }

    public InMemorySlackInboundEnqueueDeadLetterSink DeadLetter { get; }

    /// <summary>
    /// Fires every registered
    /// <see cref="HttpResponse.OnCompleted(Func{Task})"/> callback in
    /// registration order, mirroring what the ASP.NET Core host does
    /// after a real response flush. Tests call this to drive the
    /// Stage 4.1 post-ACK enqueue path.
    /// </summary>
    public Task FireResponseCompletedAsync()
        => this.ResponseFeature.FireOnCompletedAsync();

    /// <summary>
    /// Deconstruction helper so callers can keep the original tuple
    /// shape <c>var (ctx, queue, fastPath) = BuildContext(...)</c>.
    /// </summary>
    public void Deconstruct(
        out DefaultHttpContext context,
        out ChannelBasedSlackInboundQueue queue,
        out RecordingModalFastPathHandler fastPath)
    {
        context = this.Context;
        queue = this.Queue;
        fastPath = this.FastPath;
    }
}

/// <summary>
/// Custom <see cref="IHttpResponseFeature"/> for unit tests: stores
/// OnCompleted callbacks in a list so the test can drive them
/// explicitly with <see cref="FireOnCompletedAsync"/>. The default
/// <c>HttpResponseFeature</c> shipped with <see cref="DefaultHttpContext"/>
/// silently discards callbacks because there is no real response
/// pipeline to flush; that hid the Stage 4.1 post-ACK enqueue from the
/// iter-1 controller unit tests.
/// </summary>
internal sealed class TestableHttpResponseFeature : IHttpResponseFeature
{
    private readonly List<(Func<object, Task> Callback, object State)> startingCallbacks = new();
    private readonly List<(Func<object, Task> Callback, object State)> completedCallbacks = new();
    private Stream body = Stream.Null;

    public int StatusCode { get; set; } = 200;

    public string? ReasonPhrase { get; set; }

    public IHeaderDictionary Headers { get; set; } = new HeaderDictionary();

    public Stream Body
    {
        get => this.body;
        set => this.body = value ?? Stream.Null;
    }

    public bool HasStarted { get; private set; }

    public void OnStarting(Func<object, Task> callback, object state)
    {
        this.startingCallbacks.Add((callback, state));
    }

    public void OnCompleted(Func<object, Task> callback, object state)
    {
        this.completedCallbacks.Add((callback, state));
    }

    /// <summary>
    /// Fires every recorded OnCompleted callback in registration
    /// order, awaiting each in turn. Callbacks that throw bubble out
    /// so test assertions still see the failure.
    /// </summary>
    public async Task FireOnCompletedAsync()
    {
        // Snapshot to allow callbacks to register additional callbacks
        // without causing a list-mutated-during-enumeration crash.
        var snapshot = this.completedCallbacks.ToArray();
        this.completedCallbacks.Clear();
        foreach (var (cb, st) in snapshot)
        {
            await cb(st).ConfigureAwait(false);
        }
    }
}

/// <summary>
/// Test double for <see cref="ISlackModalFastPathHandler"/> that records
/// invocations and returns a caller-specified result.
/// </summary>
internal sealed class RecordingModalFastPathHandler : ISlackModalFastPathHandler
{
    public int InvocationCount { get; private set; }

    public SlackInboundEnvelope? LastEnvelope { get; private set; }

    public SlackModalFastPathResult Result { get; set; } = SlackModalFastPathResult.AsyncFallback;

    public Task<SlackModalFastPathResult> HandleAsync(
        SlackInboundEnvelope envelope,
        HttpContext httpContext,
        CancellationToken ct)
    {
        this.InvocationCount++;
        this.LastEnvelope = envelope;
        return Task.FromResult(this.Result);
    }
}
