using System.Text;
using AgentSwarm.Messaging.Teams.Middleware;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AgentSwarm.Messaging.Teams.Tests;

public sealed class RateLimitMiddlewareTests
{
    private sealed class TestMonitor : IOptionsMonitor<TeamsMessagingOptions>
    {
        public TestMonitor(TeamsMessagingOptions value) { CurrentValue = value; }
        public TeamsMessagingOptions CurrentValue { get; }
        public TeamsMessagingOptions Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<TeamsMessagingOptions, string?> listener) => null;
    }

    private static HttpContext MakeContext(string tenantId)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Method = HttpMethods.Post;
        ctx.Request.Path = "/api/messages";
        var body = $@"{{""conversation"":{{""tenantId"":""{tenantId}""}}}}";
        var bytes = Encoding.UTF8.GetBytes(body);
        ctx.Request.Body = new MemoryStream(bytes);
        ctx.Request.ContentLength = bytes.Length;
        ctx.Response.Body = new MemoryStream();
        return ctx;
    }

    [Fact]
    public async Task First_Hits_Below_Limit_Pass_Through()
    {
        var options = new TestMonitor(new TeamsMessagingOptions { RateLimitPerTenantPerMinute = 3, AllowedTenantIds = new List<string> { "t" } });
        RequestDelegate next = _ => Task.CompletedTask;
        var clock = DateTimeOffset.UtcNow;
        var middleware = new RateLimitMiddleware(next, NullLogger<RateLimitMiddleware>.Instance, options, () => clock);

        for (var i = 0; i < 3; i++)
        {
            var ctx = MakeContext("t");
            await middleware.InvokeAsync(ctx);
            Assert.NotEqual(StatusCodes.Status429TooManyRequests, ctx.Response.StatusCode);
        }
    }

    [Fact]
    public async Task Hit_Above_Limit_Returns_429_With_Retry_After()
    {
        var options = new TestMonitor(new TeamsMessagingOptions { RateLimitPerTenantPerMinute = 2, AllowedTenantIds = new List<string> { "t" } });
        RequestDelegate next = _ => Task.CompletedTask;
        var clock = DateTimeOffset.UtcNow;
        var middleware = new RateLimitMiddleware(next, NullLogger<RateLimitMiddleware>.Instance, options, () => clock);

        await middleware.InvokeAsync(MakeContext("t"));
        await middleware.InvokeAsync(MakeContext("t"));
        var ctx = MakeContext("t");
        await middleware.InvokeAsync(ctx);

        Assert.Equal(StatusCodes.Status429TooManyRequests, ctx.Response.StatusCode);
        Assert.True(ctx.Response.Headers.TryGetValue("Retry-After", out var retry));
        Assert.True(int.Parse(retry!) >= 1);
    }

    [Fact]
    public async Task Sliding_Window_Releases_After_Time_Passes()
    {
        var options = new TestMonitor(new TeamsMessagingOptions { RateLimitPerTenantPerMinute = 1, AllowedTenantIds = new List<string> { "t" } });
        RequestDelegate next = _ => Task.CompletedTask;
        var clock = DateTimeOffset.UtcNow;
        var middleware = new RateLimitMiddleware(next, NullLogger<RateLimitMiddleware>.Instance, options, () => clock);

        await middleware.InvokeAsync(MakeContext("t"));
        var blocked = MakeContext("t");
        await middleware.InvokeAsync(blocked);
        Assert.Equal(StatusCodes.Status429TooManyRequests, blocked.Response.StatusCode);

        clock = clock.AddMinutes(2);
        var ctx = MakeContext("t");
        await middleware.InvokeAsync(ctx);
        Assert.NotEqual(StatusCodes.Status429TooManyRequests, ctx.Response.StatusCode);
    }

    [Fact]
    public async Task Per_Tenant_Counters_Are_Independent()
    {
        var options = new TestMonitor(new TeamsMessagingOptions { RateLimitPerTenantPerMinute = 1 });
        RequestDelegate next = _ => Task.CompletedTask;
        var clock = DateTimeOffset.UtcNow;
        var middleware = new RateLimitMiddleware(next, NullLogger<RateLimitMiddleware>.Instance, options, () => clock);

        await middleware.InvokeAsync(MakeContext("a"));
        var ctx = MakeContext("b");
        await middleware.InvokeAsync(ctx);
        Assert.NotEqual(StatusCodes.Status429TooManyRequests, ctx.Response.StatusCode);
    }
}
