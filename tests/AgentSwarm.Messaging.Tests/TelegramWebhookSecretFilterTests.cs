using AgentSwarm.Messaging.Telegram;
using AgentSwarm.Messaging.Telegram.Webhook;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AgentSwarm.Messaging.Tests;

/// <summary>
/// Stage 2.4 — pins <see cref="TelegramWebhookSecretFilter"/> behavior
/// against the "Security" row of the story brief ("Validate chat/user
/// allowlist before accepting commands") and architecture.md §11.3
/// ("X-Telegram-Bot-Api-Secret-Token must match the configured secret;
/// constant-time compare").
/// </summary>
public sealed class TelegramWebhookSecretFilterTests
{
    private const string ConfiguredSecret = "shared-secret-32-chars-min-length";

    private static EndpointFilterInvocationContext NewContext(string? suppliedHeader)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Features.Set<IHttpConnectionFeature>(new HttpConnectionFeature
        {
            RemoteIpAddress = System.Net.IPAddress.Parse("203.0.113.7"),
        });
        if (suppliedHeader is not null)
        {
            httpContext.Request.Headers[TelegramWebhookSecretFilter.HeaderName] = suppliedHeader;
        }
        return new DefaultEndpointFilterInvocationContext(httpContext);
    }

    private static TelegramWebhookSecretFilter NewFilter(string configuredSecret)
    {
        var options = new TelegramOptions
        {
            BotToken = "sample-bot-token",
            WebhookUrl = "https://example.com/api/telegram/webhook",
            UsePolling = false,
            SecretToken = configuredSecret,
        };
        var monitor = new FakeOptionsMonitor<TelegramOptions>(options);
        return new TelegramWebhookSecretFilter(monitor, NullLogger<TelegramWebhookSecretFilter>.Instance);
    }

    private static EndpointFilterDelegate NextThatThrows() =>
        _ => throw new InvalidOperationException(
            "next() must not be invoked when the filter rejects the request");

    private static EndpointFilterDelegate NextThatReturns(object result) =>
        _ => new ValueTask<object?>(result);

    [Fact]
    public async Task MatchingHeader_AllowsRequest()
    {
        var filter = NewFilter(ConfiguredSecret);
        var ctx = NewContext(ConfiguredSecret);
        var inner = new object();

        var actual = await filter.InvokeAsync(ctx, NextThatReturns(inner));

        actual.Should().BeSameAs(inner);
    }

    [Theory]
    [InlineData("wrong-secret")]
    [InlineData("")]
    [InlineData("shared-secret-32-chars-min-lengtX")]   // last byte differs
    public async Task MismatchedOrMissingHeader_Returns403(string suppliedHeader)
    {
        var filter = NewFilter(ConfiguredSecret);
        var ctx = NewContext(suppliedHeader);

        var actual = await filter.InvokeAsync(ctx, NextThatThrows());

        actual.Should().BeOfType<Microsoft.AspNetCore.Http.HttpResults.StatusCodeHttpResult>()
            .Which.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
    }

    [Fact]
    public async Task NoHeaderSent_Returns403()
    {
        var filter = NewFilter(ConfiguredSecret);
        var ctx = NewContext(suppliedHeader: null);

        var actual = await filter.InvokeAsync(ctx, NextThatThrows());

        actual.Should().BeOfType<Microsoft.AspNetCore.Http.HttpResults.StatusCodeHttpResult>()
            .Which.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task BlankConfiguredSecret_RejectsEveryRequest(string configured)
    {
        // Fail-safe: a webhook deployment with no configured secret must
        // not accept ANY request. The TelegramOptionsValidator should
        // have rejected this configuration at startup; the filter is the
        // defense-in-depth backstop.
        var filter = NewFilter(configured);
        var ctx = NewContext(suppliedHeader: "anything");

        var actual = await filter.InvokeAsync(ctx, NextThatThrows());

        actual.Should().BeOfType<Microsoft.AspNetCore.Http.HttpResults.StatusCodeHttpResult>()
            .Which.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
    }

    [Fact]
    public async Task ConstantTimeCompare_DoesNotShortCircuitOnLengthMismatch()
    {
        // Behavioral test for the timing-attack mitigation: a short
        // supplied header and a long supplied header must BOTH be
        // rejected with the same shape (403), with no observable
        // difference in the result type.
        var filter = NewFilter(ConfiguredSecret);

        var shortCtx = NewContext("a");
        var longCtx = NewContext(new string('a', 1024));

        var shortResult = await filter.InvokeAsync(shortCtx, NextThatThrows());
        var longResult = await filter.InvokeAsync(longCtx, NextThatThrows());

        shortResult.Should().BeOfType<Microsoft.AspNetCore.Http.HttpResults.StatusCodeHttpResult>()
            .Which.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        longResult.Should().BeOfType<Microsoft.AspNetCore.Http.HttpResults.StatusCodeHttpResult>()
            .Which.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
    }

    [Fact]
    public void Ctor_NullArgs_Throws()
    {
        var monitor = new FakeOptionsMonitor<TelegramOptions>(new TelegramOptions { BotToken = "x" });

        FluentActions
            .Invoking(() => new TelegramWebhookSecretFilter(null!, NullLogger<TelegramWebhookSecretFilter>.Instance))
            .Should().Throw<ArgumentNullException>();
        FluentActions
            .Invoking(() => new TelegramWebhookSecretFilter(monitor, null!))
            .Should().Throw<ArgumentNullException>();
    }

    private sealed class DefaultEndpointFilterInvocationContext : EndpointFilterInvocationContext
    {
        public DefaultEndpointFilterInvocationContext(HttpContext httpContext)
        {
            HttpContext = httpContext;
        }

        public override HttpContext HttpContext { get; }

        public override IList<object?> Arguments { get; } = new List<object?>();

        public override T GetArgument<T>(int index) =>
            throw new NotSupportedException("No arguments in the test harness.");
    }

    private sealed class HttpConnectionFeature : IHttpConnectionFeature
    {
        public string ConnectionId { get; set; } = "test-connection";
        public System.Net.IPAddress? RemoteIpAddress { get; set; }
        public System.Net.IPAddress? LocalIpAddress { get; set; }
        public int RemotePort { get; set; }
        public int LocalPort { get; set; }
    }

    private sealed class FakeOptionsMonitor<T> : IOptionsMonitor<T>
    {
        public FakeOptionsMonitor(T current) { CurrentValue = current; }
        public T CurrentValue { get; }
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
