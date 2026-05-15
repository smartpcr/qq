using AgentSwarm.Messaging.Abstractions;
using AgentSwarm.Messaging.Teams.Cards;
using AgentSwarm.Messaging.Teams.Commands;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using static AgentSwarm.Messaging.Teams.Tests.TestDoubles;

namespace AgentSwarm.Messaging.Teams.Tests.Commands;

/// <summary>
/// Stage 3.2 DI-wiring tests for
/// <see cref="TeamsServiceCollectionExtensions.AddTeamsCommandDispatcher"/>. Verifies that
/// every canonical handler is registered, the dispatcher resolves with the full handler
/// set, the helper is idempotent, and <see cref="TeamsServiceCollectionExtensions.AddTeamsMessengerConnector"/>
/// transitively pulls the dispatcher in so existing hosts get command routing without an
/// extra registration call.
/// </summary>
public sealed class CommandDispatcherDiTests
{
    private static ServiceCollection BuildBaseServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IAgentQuestionStore>(new InMemoryAgentQuestionStore());
        services.AddSingleton<IAdaptiveCardRenderer, AdaptiveCardBuilder>();
        services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>), typeof(NullLogger<>));
        return services;
    }

    [Fact]
    public void AddTeamsCommandDispatcher_RegistersAllSevenHandlers_AndDispatcher()
    {
        var services = BuildBaseServices();
        services.AddTeamsCommandDispatcher();
        using var sp = services.BuildServiceProvider(validateScopes: true);

        var handlers = sp.GetServices<ICommandHandler>().ToList();
        Assert.Equal(7, handlers.Count);

        var names = handlers.Select(h => h.CommandName).OrderBy(n => n).ToArray();
        Assert.Equal(
            new[]
            {
                CommandNames.AgentAsk,
                CommandNames.AgentStatus,
                CommandNames.Approve,
                CommandNames.Escalate,
                CommandNames.Pause,
                CommandNames.Reject,
                CommandNames.Resume,
            }.OrderBy(n => n).ToArray(),
            names);

        var dispatcher = sp.GetRequiredService<ICommandDispatcher>();
        Assert.IsType<CommandDispatcher>(dispatcher);
    }

    [Fact]
    public void AddTeamsCommandDispatcher_CalledTwice_IsIdempotent()
    {
        var services = BuildBaseServices();
        services.AddTeamsCommandDispatcher();
        services.AddTeamsCommandDispatcher();

        Assert.Equal(1, services.Count(d => d.ServiceType == typeof(AskCommandHandler) || d.ImplementationType == typeof(AskCommandHandler)));
        Assert.Equal(1, services.Count(d => d.ImplementationType == typeof(ApproveCommandHandler)));
        Assert.Equal(1, services.Count(d => d.ImplementationType == typeof(RejectCommandHandler)));
        Assert.Equal(1, services.Count(d => d.ServiceType == typeof(ICommandDispatcher)));

        using var sp = services.BuildServiceProvider(validateScopes: true);
        Assert.Equal(7, sp.GetServices<ICommandHandler>().Count());
    }

    [Fact]
    public void AddTeamsCommandDispatcher_RegisteresAdaptiveCardRenderer_WhenAbsent()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IAgentQuestionStore>(new InMemoryAgentQuestionStore());
        services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>), typeof(NullLogger<>));

        services.AddTeamsCommandDispatcher();
        using var sp = services.BuildServiceProvider(validateScopes: true);

        var renderer = sp.GetRequiredService<IAdaptiveCardRenderer>();
        Assert.IsType<AdaptiveCardBuilder>(renderer);
    }

    [Fact]
    public void AddTeamsCommandDispatcher_NullServices_Throws()
    {
        Assert.Throws<ArgumentNullException>(
            () => TeamsServiceCollectionExtensions.AddTeamsCommandDispatcher(null!));
    }
}
