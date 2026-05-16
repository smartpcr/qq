using AgentSwarm.Messaging.Abstractions;
using AgentSwarm.Messaging.Core;
using AgentSwarm.Messaging.Core.Commands;
using AgentSwarm.Messaging.Telegram;
using AgentSwarm.Messaging.Telegram.Pipeline;
using AgentSwarm.Messaging.Telegram.Pipeline.Stubs;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AgentSwarm.Messaging.Tests;

/// <summary>
/// Locks the Stage 2.2 DI surface added by
/// <see cref="TelegramServiceCollectionExtensions.AddTelegram"/>: the
/// inbound pipeline and its five stub dependencies must register so that
/// downstream Phase 3/4 stages can replace them with last-wins semantics.
/// </summary>
public class TelegramPipelineRegistrationTests
{
    private const string SampleToken = "1234567890:AAH9hyTeleGramSecRetToken_test_value_only";

    [Theory]
    [InlineData(typeof(IDeduplicationService), typeof(SlidingWindowDeduplicationService))]
    [InlineData(typeof(IPendingQuestionStore), typeof(InMemoryPendingQuestionStore))]
    [InlineData(typeof(IPendingDisambiguationStore), typeof(InMemoryPendingDisambiguationStore))]
    // Stage 3.1 swapped StubCommandParser for the production
    // TelegramCommandParser, so the locked-down registration now
    // points at the real parser type.
    [InlineData(typeof(ICommandParser), typeof(TelegramCommandParser))]
    // Stage 3.2 swapped StubCommandRouter for the production
    // CommandRouter — the dispatch dictionary is built from the nine
    // ICommandHandler registrations injected via IEnumerable<>.
    [InlineData(typeof(ICommandRouter), typeof(CommandRouter))]
    // Stage 3.3 swapped StubCallbackHandler for the production
    // CallbackQueryHandler — inline-button presses now decode
    // QuestionId:ActionId payloads, emit HumanDecisionEvent, audit,
    // and answer the Telegram callback.
    [InlineData(typeof(ICallbackHandler), typeof(CallbackQueryHandler))]
    [InlineData(typeof(ITelegramUpdatePipeline), typeof(TelegramUpdatePipeline))]
    public void AddTelegram_RegistersStage22Service(Type serviceType, Type implementationType)
    {
        var services = BuildServices();

        var descriptor = services.FirstOrDefault(d => d.ServiceType == serviceType);

        descriptor.Should().NotBeNull(
            "{0} must be registered by AddTelegram so the Stage 2.2 inbound pipeline composes",
            serviceType.FullName);
        descriptor!.ImplementationType.Should().Be(implementationType);
        descriptor.Lifetime.Should().Be(ServiceLifetime.Singleton,
            "stub dependencies hold in-memory state that must survive between pipeline invocations");
    }

    [Fact]
    public void AddTelegram_RegistersTimeProvider()
    {
        var services = BuildServices();

        var descriptor = services.FirstOrDefault(d => d.ServiceType == typeof(TimeProvider));

        descriptor.Should().NotBeNull(
            "Stage 2.2 pipeline depends on TimeProvider for disambiguation TTL stamping");
        descriptor!.Lifetime.Should().Be(ServiceLifetime.Singleton);
        // TimeProvider.System is a static instance; AddTelegram registers
        // it as an instance (no implementation type). TryAddSingleton
        // semantics let tests substitute a FakeTimeProvider before
        // calling AddTelegram.
        descriptor.ImplementationInstance.Should().BeSameAs(TimeProvider.System);
    }

    [Fact]
    public void AddTelegram_DoesNotRegisterUserAuthorizationService()
    {
        var services = BuildServices();

        services.Should().NotContain(d => d.ServiceType == typeof(global::AgentSwarm.Messaging.Core.IUserAuthorizationService),
            "IUserAuthorizationService is a Phase 4 concern and is intentionally not stubbed by AddTelegram; "
            + "leaving it unregistered makes a missing authorization service a loud bootstrap failure rather than a silent allow-everything stub");
    }

    private static ServiceCollection BuildServices()
    {
        var services = new ServiceCollection();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Telegram:BotToken"] = SampleToken,
            })
            .Build();
        services.AddTelegram(config);
        return services;
    }
}
