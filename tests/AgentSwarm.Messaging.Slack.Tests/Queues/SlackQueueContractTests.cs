using System.Linq;
using System.Reflection;
using AgentSwarm.Messaging.Slack.Queues;
using AgentSwarm.Messaging.Slack.Retry;
using AgentSwarm.Messaging.Slack.Transport;
using FluentAssertions;
using Xunit;

namespace AgentSwarm.Messaging.Slack.Tests.Queues;

/// <summary>
/// Stage 1.3 contract tests. Pin the public surface of every queue and
/// retry interface introduced by
/// <c>docs/stories/qq-SLACK-MESSENGER-SUPP/implementation-plan.md</c>
/// lines 49--54 so a future stage (or upstream <c>Core</c> swap-in) cannot
/// silently rename a method, change a parameter type, or change visibility
/// without a deliberate edit to these assertions.
/// </summary>
public sealed class SlackQueueContractTests
{
    [Fact]
    public void ISlackInboundQueue_is_internal_and_carries_canonical_methods()
    {
        Type t = typeof(ISlackInboundQueue);

        t.IsInterface.Should().BeTrue();
        t.IsPublic.Should().BeFalse("the brief specifies an internal interface");
        t.IsVisible.Should().BeFalse();

        MethodInfo enqueue = t.GetMethod(nameof(ISlackInboundQueue.EnqueueAsync))!;
        enqueue.Should().NotBeNull("plan.md line 49 lists EnqueueAsync(SlackInboundEnvelope)");
        enqueue.ReturnType.Should().Be(typeof(ValueTask));
        ParameterInfo[] enqueueParams = enqueue.GetParameters();
        enqueueParams.Should().HaveCount(1,
            because: "plan.md line 49 specifies EnqueueAsync(SlackInboundEnvelope) -- a single parameter, no cancellation token");
        enqueueParams[0].ParameterType.Should().Be(typeof(SlackInboundEnvelope));
        enqueueParams[0].Name.Should().Be("envelope");

        MethodInfo dequeue = t.GetMethod(nameof(ISlackInboundQueue.DequeueAsync))!;
        dequeue.Should().NotBeNull("plan.md line 49 lists DequeueAsync(CancellationToken)");
        dequeue.ReturnType.Should().Be(typeof(ValueTask<SlackInboundEnvelope>));
        ParameterInfo[] dequeueParams = dequeue.GetParameters();
        dequeueParams.Should().HaveCount(1);
        dequeueParams[0].ParameterType.Should().Be(typeof(CancellationToken));
    }

    [Fact]
    public void ISlackOutboundQueue_is_internal_and_carries_canonical_methods()
    {
        Type t = typeof(ISlackOutboundQueue);

        t.IsInterface.Should().BeTrue();
        t.IsPublic.Should().BeFalse("the brief specifies an internal interface");
        t.IsVisible.Should().BeFalse();

        MethodInfo enqueue = t.GetMethod(nameof(ISlackOutboundQueue.EnqueueAsync))!;
        enqueue.Should().NotBeNull("plan.md line 50 lists EnqueueAsync(SlackOutboundEnvelope)");
        enqueue.ReturnType.Should().Be(typeof(ValueTask));
        ParameterInfo[] enqueueParams = enqueue.GetParameters();
        enqueueParams.Should().HaveCount(1,
            because: "plan.md line 50 specifies EnqueueAsync(SlackOutboundEnvelope) -- a single parameter, no cancellation token");
        enqueueParams[0].ParameterType.Should().Be(typeof(SlackOutboundEnvelope));
        enqueueParams[0].Name.Should().Be("envelope");

        MethodInfo dequeue = t.GetMethod(nameof(ISlackOutboundQueue.DequeueAsync))!;
        dequeue.Should().NotBeNull("plan.md line 50 lists DequeueAsync(CancellationToken)");
        dequeue.ReturnType.Should().Be(typeof(ValueTask<SlackOutboundEnvelope>));
        ParameterInfo[] dequeueParams = dequeue.GetParameters();
        dequeueParams.Should().HaveCount(1);
        dequeueParams[0].ParameterType.Should().Be(typeof(CancellationToken));
    }

    [Fact]
    public void ISlackDeadLetterQueue_is_internal_and_exposes_enqueue_and_inspect()
    {
        Type t = typeof(ISlackDeadLetterQueue);

        t.IsInterface.Should().BeTrue();
        t.IsPublic.Should().BeFalse("the brief specifies an internal interface");

        string[] methods = t.GetMethods().Select(m => m.Name).OrderBy(n => n).ToArray();
        methods.Should().BeEquivalentTo(new[]
        {
            nameof(ISlackDeadLetterQueue.EnqueueAsync),
            nameof(ISlackDeadLetterQueue.InspectAsync),
        });

        MethodInfo enqueue = t.GetMethod(nameof(ISlackDeadLetterQueue.EnqueueAsync))!;
        enqueue.GetParameters()[0].ParameterType.Should().Be(typeof(SlackDeadLetterEntry));

        MethodInfo inspect = t.GetMethod(nameof(ISlackDeadLetterQueue.InspectAsync))!;
        inspect.ReturnType.Should().Be(typeof(ValueTask<IReadOnlyList<SlackDeadLetterEntry>>));
    }

    [Fact]
    public void ISlackRetryPolicy_is_internal_and_carries_canonical_methods()
    {
        Type t = typeof(ISlackRetryPolicy);

        t.IsInterface.Should().BeTrue();
        t.IsPublic.Should().BeFalse("the brief specifies an internal interface");

        MethodInfo shouldRetry = t.GetMethod(nameof(ISlackRetryPolicy.ShouldRetry))!;
        shouldRetry.Should().NotBeNull("plan.md line 52 lists ShouldRetry(int, Exception)");
        shouldRetry.ReturnType.Should().Be(typeof(bool));
        ParameterInfo[] retryParams = shouldRetry.GetParameters();
        retryParams.Should().HaveCount(2);
        retryParams[0].ParameterType.Should().Be(typeof(int));
        retryParams[0].Name.Should().Be("attemptNumber");
        retryParams[1].ParameterType.Should().Be(typeof(Exception));
        retryParams[1].Name.Should().Be("exception");

        MethodInfo getDelay = t.GetMethod(nameof(ISlackRetryPolicy.GetDelay))!;
        getDelay.Should().NotBeNull("plan.md line 52 lists GetDelay(int)");
        getDelay.ReturnType.Should().Be(typeof(TimeSpan));
        ParameterInfo[] delayParams = getDelay.GetParameters();
        delayParams.Should().HaveCount(1);
        delayParams[0].ParameterType.Should().Be(typeof(int));
        delayParams[0].Name.Should().Be("attemptNumber");
    }

    [Fact]
    public void ChannelBasedSlackQueue_is_internal_sealed_generic()
    {
        Type open = typeof(ChannelBasedSlackQueue<>);

        open.IsClass.Should().BeTrue();
        open.IsSealed.Should().BeTrue();
        open.IsPublic.Should().BeFalse("queue implementation is internal; production wires through DI");
        open.IsGenericTypeDefinition.Should().BeTrue();
        open.GetGenericArguments().Should().HaveCount(1);
    }

    [Fact]
    public void SlackInboundEnvelope_carries_the_Stage_3_1_canonical_fields()
    {
        // Pinning the field surface here means Stage 3.1 (Slack Inbound
        // Transport) can adopt the record without re-litigating the field
        // list spelled out in implementation-plan.md line 193.
        Type t = typeof(SlackInboundEnvelope);
        string[] props = t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .Where(n => n != "EqualityContract")
            .OrderBy(n => n)
            .ToArray();
        props.Should().BeEquivalentTo(new[]
        {
            nameof(SlackInboundEnvelope.IdempotencyKey),
            nameof(SlackInboundEnvelope.SourceType),
            nameof(SlackInboundEnvelope.TeamId),
            nameof(SlackInboundEnvelope.ChannelId),
            nameof(SlackInboundEnvelope.UserId),
            nameof(SlackInboundEnvelope.RawPayload),
            nameof(SlackInboundEnvelope.TriggerId),
            nameof(SlackInboundEnvelope.ReceivedAt),
        });
    }

    [Fact]
    public void SlackOutboundEnvelope_carries_the_Stage_4_1_canonical_fields()
    {
        // Pinning the field surface here means Stage 4.1 (Slack Outbound
        // Dispatcher) can adopt the record without re-litigating the field
        // list spelled out in implementation-plan.md line 355.
        //
        // Stage 6.3 iter 2 added three OPTIONAL init-only members
        // (MessageTs / ViewId / EnvelopeId) so the dispatcher can
        // address chat.update / views.update targets end-to-end and
        // the durable FileSystemSlackOutboundQueue can identify
        // individual journal entries to delete after a terminal
        // disposition. The primary constructor parameter list is
        // unchanged so all existing SendMessage / SendQuestion
        // call-sites continue to compile; the test below asserts the
        // FULL public-surface, which now includes those extensions.
        Type t = typeof(SlackOutboundEnvelope);
        string[] props = t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .Where(n => n != "EqualityContract")
            .OrderBy(n => n)
            .ToArray();
        props.Should().BeEquivalentTo(new[]
        {
            nameof(SlackOutboundEnvelope.TaskId),
            nameof(SlackOutboundEnvelope.CorrelationId),
            nameof(SlackOutboundEnvelope.MessageType),
            nameof(SlackOutboundEnvelope.BlockKitPayload),
            nameof(SlackOutboundEnvelope.ThreadTs),
            nameof(SlackOutboundEnvelope.MessageTs),
            nameof(SlackOutboundEnvelope.ViewId),
            nameof(SlackOutboundEnvelope.EnvelopeId),
        });

        // Independently assert that the PRIMARY-CONSTRUCTOR positional
        // parameter list remains EXACTLY the brief-mandated 5 fields
        // so existing producers (SendMessageAsync / SendQuestionAsync)
        // do not have to update their `new SlackOutboundEnvelope(...)`
        // call-sites.
        var ctorParams = t.GetConstructors(BindingFlags.Public | BindingFlags.Instance)
            .OrderByDescending(c => c.GetParameters().Length)
            .First()
            .GetParameters()
            .Select(p => p.Name)
            .ToArray();
        ctorParams.Should().BeEquivalentTo(new[]
        {
            nameof(SlackOutboundEnvelope.TaskId),
            nameof(SlackOutboundEnvelope.CorrelationId),
            nameof(SlackOutboundEnvelope.MessageType),
            nameof(SlackOutboundEnvelope.BlockKitPayload),
            nameof(SlackOutboundEnvelope.ThreadTs),
        });
    }
}
