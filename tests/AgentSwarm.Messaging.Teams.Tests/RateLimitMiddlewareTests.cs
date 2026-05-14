using System.Text;
using AgentSwarm.Messaging.Teams;
using AgentSwarm.Messaging.Teams.Middleware;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentSwarm.Messaging.Teams.Tests;

public sealed class RateLimitMiddlewareTests
{
    [Fact]
    public async Task NonWebhookPath_BypassesRateLimit()
    {
        var (middleware, next, invoked) = CreateMiddleware(rateLimit: 1);

        var context = TenantValidationMiddlewareTests.NewContext("/health", HttpMethods.Get, body: null);
        await middleware.InvokeAsync(context, next);

        Assert.True(invoked());
        Assert.Equal(200, context.Response.StatusCode);
    }

    [Fact]
    public async Task RequestWithinLimit_PassesThrough()
    {
        var (middleware, next, invoked) = CreateMiddleware(rateLimit: 2);
        var body = "{\"channelData\":{\"tenant\":{\"id\":\"tenant-a\"}}}";

        var context = TenantValidationMiddlewareTests.NewContext(TenantValidationMiddleware.WebhookPath, HttpMethods.Post, body);
        await middleware.InvokeAsync(context, next);

        Assert.True(invoked());
        Assert.NotEqual(StatusCodes.Status429TooManyRequests, context.Response.StatusCode);
    }

    [Fact]
    public async Task RequestOverLimit_Returns429WithRetryAfter()
    {
        var (middleware, next, _) = CreateMiddleware(rateLimit: 1);
        var body = "{\"channelData\":{\"tenant\":{\"id\":\"tenant-a\"}}}";

        await middleware.InvokeAsync(NewBufferedContext(body), next);
        var second = NewBufferedContext(body);
        await middleware.InvokeAsync(second, next);

        Assert.Equal(StatusCodes.Status429TooManyRequests, second.Response.StatusCode);
        Assert.True(second.Response.Headers.TryGetValue("Retry-After", out var retryAfter));
        Assert.True(int.TryParse(retryAfter.ToString(), out var seconds));
        Assert.True(seconds >= 1);
    }

    [Fact]
    public async Task DifferentTenants_DoNotShareCounters()
    {
        var (middleware, next, _) = CreateMiddleware(rateLimit: 1);
        var bodyA = "{\"channelData\":{\"tenant\":{\"id\":\"tenant-a\"}}}";
        var bodyB = "{\"channelData\":{\"tenant\":{\"id\":\"tenant-b\"}}}";

        await middleware.InvokeAsync(NewBufferedContext(bodyA), next);
        var ctxBoth = NewBufferedContext(bodyB);
        await middleware.InvokeAsync(ctxBoth, next);

        Assert.NotEqual(StatusCodes.Status429TooManyRequests, ctxBoth.Response.StatusCode);
    }

    [Fact]
    public async Task ZeroOrNegativeLimit_BypassesRateLimit()
    {
        var (middleware, next, invoked) = CreateMiddleware(rateLimit: 0);

        var context = NewBufferedContext("{\"channelData\":{\"tenant\":{\"id\":\"tenant-a\"}}}");
        await middleware.InvokeAsync(context, next);

        Assert.True(invoked());
    }

    private static HttpContext NewBufferedContext(string body)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = TenantValidationMiddleware.WebhookPath;
        context.Request.Method = HttpMethods.Post;
        var bytes = Encoding.UTF8.GetBytes(body);
        var ms = new MemoryStream();
        ms.Write(bytes, 0, bytes.Length);
        ms.Position = 0;
        context.Request.Body = ms;
        context.Request.ContentLength = bytes.Length;
        context.Request.ContentType = "application/json";
        context.Request.EnableBuffering();
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static (RateLimitMiddleware middleware, RequestDelegate next, Func<bool> invoked) CreateMiddleware(int rateLimit)
    {
        var invoked = false;
        RequestDelegate next = _ =>
        {
            invoked = true;
            return Task.CompletedTask;
        };

        var options = new TeamsMessagingOptions
        {
            MicrosoftAppId = "app",
            MicrosoftAppPassword = "p",
            MicrosoftAppTenantId = "tenant-a",
            AllowedTenantIds = new List<string> { "tenant-a" },
            RateLimitPerTenantPerMinute = rateLimit,
        };
        var monitor = new TestOptionsMonitor<TeamsMessagingOptions>(options);
        return (new RateLimitMiddleware(monitor, NullLogger<RateLimitMiddleware>.Instance), next, () => invoked);
    }
}
