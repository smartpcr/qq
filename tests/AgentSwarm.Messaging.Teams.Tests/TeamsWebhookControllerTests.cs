using AgentSwarm.Messaging.Teams.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Bot.Builder;
using Microsoft.Bot.Builder.Integration.AspNet.Core;

namespace AgentSwarm.Messaging.Teams.Tests;

/// <summary>
/// Unit tests for <see cref="TeamsWebhookController"/>. The controller is intentionally a
/// thin pass-through: POST <c>/api/messages</c> delegates to
/// <see cref="IBotFrameworkHttpAdapter.ProcessAsync"/> with the controller's request, response,
/// configured <see cref="IBot"/>, and the cancellation token from
/// <see cref="HttpContext.RequestAborted"/>.
/// </summary>
/// <remarks>
/// These assertions defend against accidental regressions in the controller's wiring — e.g.,
/// passing <see cref="CancellationToken.None"/> instead of <see cref="HttpContext.RequestAborted"/>,
/// or swapping the adapter and bot arguments.
/// </remarks>
public sealed class TeamsWebhookControllerTests
{
    [Fact]
    public void Controller_Decorations_AreCorrect()
    {
        var type = typeof(TeamsWebhookController);

        Assert.NotNull(type.GetCustomAttributes(typeof(ApiControllerAttribute), inherit: true)
            .Cast<ApiControllerAttribute>()
            .FirstOrDefault());

        var route = type.GetCustomAttributes(typeof(RouteAttribute), inherit: true)
            .Cast<RouteAttribute>()
            .FirstOrDefault();
        Assert.NotNull(route);
        Assert.Equal("api/messages", route!.Template);
    }

    [Fact]
    public void Controller_RejectsNullDependencies()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new TeamsWebhookController(adapter: null!, bot: new FakeBot()));

        var fakeAdapter = new RecordingAdapter();
        Assert.Throws<ArgumentNullException>(() =>
            new TeamsWebhookController(adapter: fakeAdapter, bot: null!));
    }

    [Fact]
    public async Task PostAsync_DelegatesToAdapter_WithSameRequestResponseBotAndRequestAbortedToken()
    {
        // The pass-through contract: the adapter receives THE controller's HttpRequest,
        // HttpResponse, the registered IBot, and the cancellation token bound to the HTTP
        // request lifetime (HttpContext.RequestAborted).
        using var cts = new CancellationTokenSource();
        var bot = new FakeBot();
        var adapter = new RecordingAdapter();
        var controller = new TeamsWebhookController(adapter, bot);

        var httpContext = new DefaultHttpContext { RequestAborted = cts.Token };
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = httpContext,
        };

        await controller.PostAsync();

        Assert.Equal(1, adapter.CallCount);
        Assert.Same(httpContext.Request, adapter.LastRequest);
        Assert.Same(httpContext.Response, adapter.LastResponse);
        Assert.Same(bot, adapter.LastBot);
        Assert.Equal(cts.Token, adapter.LastCancellationToken);
    }

    [Fact]
    public async Task PostAsync_PropagatesAdapterException()
    {
        // If the adapter throws (e.g., authentication failure), the controller does NOT
        // swallow the exception — ASP.NET Core's exception handler must see it.
        var adapter = new RecordingAdapter
        {
            ExceptionToThrow = new InvalidOperationException("boom"),
        };
        var controller = new TeamsWebhookController(adapter, new FakeBot());
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext(),
        };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => controller.PostAsync());
        Assert.Equal("boom", ex.Message);
    }

    /// <summary>Recording double for <see cref="IBotFrameworkHttpAdapter"/>.</summary>
    private sealed class RecordingAdapter : IBotFrameworkHttpAdapter
    {
        public int CallCount { get; private set; }
        public HttpRequest? LastRequest { get; private set; }
        public HttpResponse? LastResponse { get; private set; }
        public IBot? LastBot { get; private set; }
        public CancellationToken LastCancellationToken { get; private set; }
        public Exception? ExceptionToThrow { get; set; }

        public Task ProcessAsync(HttpRequest httpRequest, HttpResponse httpResponse, IBot bot, CancellationToken cancellationToken = default)
        {
            CallCount++;
            LastRequest = httpRequest;
            LastResponse = httpResponse;
            LastBot = bot;
            LastCancellationToken = cancellationToken;
            if (ExceptionToThrow is not null)
            {
                throw ExceptionToThrow;
            }
            return Task.CompletedTask;
        }
    }

    /// <summary>Minimal <see cref="IBot"/> double — controller never invokes it directly.</summary>
    private sealed class FakeBot : IBot
    {
        public Task OnTurnAsync(ITurnContext turnContext, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
