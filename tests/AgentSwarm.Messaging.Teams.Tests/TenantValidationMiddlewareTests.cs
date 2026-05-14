using System.Text;
using AgentSwarm.Messaging.Teams.Middleware;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AgentSwarm.Messaging.Teams.Tests;

public sealed class TenantValidationMiddlewareTests
{
    private sealed class TestMonitor : IOptionsMonitor<TeamsMessagingOptions>
    {
        public TestMonitor(TeamsMessagingOptions value) { CurrentValue = value; }
        public TeamsMessagingOptions CurrentValue { get; set; }
        public TeamsMessagingOptions Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<TeamsMessagingOptions, string?> listener) => null;
    }

    private static HttpContext MakePostContext(string body)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Method = HttpMethods.Post;
        ctx.Request.Path = "/api/messages";
        var bytes = Encoding.UTF8.GetBytes(body);
        ctx.Request.Body = new MemoryStream(bytes);
        ctx.Request.ContentLength = bytes.Length;
        ctx.Response.Body = new MemoryStream();
        return ctx;
    }

    [Fact]
    public async Task Activity_With_Allowed_Tenant_Passes_Through()
    {
        var options = new TestMonitor(new TeamsMessagingOptions { AllowedTenantIds = new List<string> { "ten-1" } });
        var nextCalled = false;
        RequestDelegate next = _ => { nextCalled = true; return Task.CompletedTask; };
        var middleware = new TenantValidationMiddleware(next, NullLogger<TenantValidationMiddleware>.Instance, options);

        var body = @"{""conversation"": {""tenantId"": ""ten-1""}}";
        var ctx = MakePostContext(body);
        await middleware.InvokeAsync(ctx);

        Assert.True(nextCalled);
        Assert.NotEqual(StatusCodes.Status403Forbidden, ctx.Response.StatusCode);
    }

    [Fact]
    public async Task Activity_With_Disallowed_Tenant_Returns_403()
    {
        var options = new TestMonitor(new TeamsMessagingOptions { AllowedTenantIds = new List<string> { "ten-1" } });
        var nextCalled = false;
        RequestDelegate next = _ => { nextCalled = true; return Task.CompletedTask; };
        var middleware = new TenantValidationMiddleware(next, NullLogger<TenantValidationMiddleware>.Instance, options);

        var body = @"{""conversation"": {""tenantId"": ""ten-evil""}}";
        var ctx = MakePostContext(body);
        await middleware.InvokeAsync(ctx);

        Assert.False(nextCalled);
        Assert.Equal(StatusCodes.Status403Forbidden, ctx.Response.StatusCode);
    }

    [Fact]
    public async Task ChannelData_Tenant_Is_Used_When_Conversation_TenantId_Missing()
    {
        var options = new TestMonitor(new TeamsMessagingOptions { AllowedTenantIds = new List<string> { "from-channel-data" } });
        var nextCalled = false;
        RequestDelegate next = _ => { nextCalled = true; return Task.CompletedTask; };
        var middleware = new TenantValidationMiddleware(next, NullLogger<TenantValidationMiddleware>.Instance, options);

        var body = @"{""channelData"": {""tenant"": {""id"": ""from-channel-data""}}}";
        var ctx = MakePostContext(body);
        await middleware.InvokeAsync(ctx);

        Assert.True(nextCalled);
    }

    [Fact]
    public async Task Missing_Tenant_Returns_403()
    {
        var options = new TestMonitor(new TeamsMessagingOptions { AllowedTenantIds = new List<string> { "ten-1" } });
        RequestDelegate next = _ => Task.CompletedTask;
        var middleware = new TenantValidationMiddleware(next, NullLogger<TenantValidationMiddleware>.Instance, options);

        var ctx = MakePostContext("{}");
        await middleware.InvokeAsync(ctx);

        Assert.Equal(StatusCodes.Status403Forbidden, ctx.Response.StatusCode);
    }

    [Fact]
    public async Task Non_Messages_Path_Is_Skipped()
    {
        var options = new TestMonitor(new TeamsMessagingOptions { AllowedTenantIds = new List<string> { "ten-1" } });
        var nextCalled = false;
        RequestDelegate next = _ => { nextCalled = true; return Task.CompletedTask; };
        var middleware = new TenantValidationMiddleware(next, NullLogger<TenantValidationMiddleware>.Instance, options);

        var ctx = new DefaultHttpContext();
        ctx.Request.Path = "/health";
        ctx.Request.Method = HttpMethods.Get;
        await middleware.InvokeAsync(ctx);

        Assert.True(nextCalled);
    }
}
