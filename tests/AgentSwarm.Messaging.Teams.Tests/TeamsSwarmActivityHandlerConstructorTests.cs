using AgentSwarm.Messaging.Abstractions;
using AgentSwarm.Messaging.Persistence;
using Microsoft.Extensions.Logging.Abstractions;
using static AgentSwarm.Messaging.Teams.Tests.HandlerFactory;

namespace AgentSwarm.Messaging.Teams.Tests;

/// <summary>
/// Verifies the handler's nine-argument constructor null-guards every dependency per
/// <c>implementation-plan.md</c> §2.2 step 1 ("All nine are required for the handler to
/// compile and function").
/// </summary>
public sealed class TeamsSwarmActivityHandlerConstructorTests
{
    private static TestDoubles.RecordingConversationReferenceStore Store() => new();
    private static TestDoubles.RecordingCommandDispatcher Dispatcher() => new();
    private static TestDoubles.FakeIdentityResolver IdentityResolver() => new();
    private static TestDoubles.AlwaysAuthorizationService Authorization() => new();
    private static TestDoubles.StubAgentQuestionStore Questions() => new();
    private static TestDoubles.RecordingAuditLogger Audit() => new();
    private static TestDoubles.RecordingCardActionHandler Cards() => new();
    private static TestDoubles.RecordingInboundEventPublisher Publisher() => new();
    private static NullLogger<TeamsSwarmActivityHandler> Logger() => NullLogger<TeamsSwarmActivityHandler>.Instance;

    [Fact]
    public void Constructor_RejectsNullConversationReferenceStore()
        => Assert.Throws<ArgumentNullException>(() => new TeamsSwarmActivityHandler(
            null!, Dispatcher(), IdentityResolver(), Authorization(), Questions(), Audit(), Cards(), Publisher(), Logger()));

    [Fact]
    public void Constructor_RejectsNullCommandDispatcher()
        => Assert.Throws<ArgumentNullException>(() => new TeamsSwarmActivityHandler(
            Store(), null!, IdentityResolver(), Authorization(), Questions(), Audit(), Cards(), Publisher(), Logger()));

    [Fact]
    public void Constructor_RejectsNullIdentityResolver()
        => Assert.Throws<ArgumentNullException>(() => new TeamsSwarmActivityHandler(
            Store(), Dispatcher(), null!, Authorization(), Questions(), Audit(), Cards(), Publisher(), Logger()));

    [Fact]
    public void Constructor_RejectsNullAuthorizationService()
        => Assert.Throws<ArgumentNullException>(() => new TeamsSwarmActivityHandler(
            Store(), Dispatcher(), IdentityResolver(), null!, Questions(), Audit(), Cards(), Publisher(), Logger()));

    [Fact]
    public void Constructor_RejectsNullAgentQuestionStore()
        => Assert.Throws<ArgumentNullException>(() => new TeamsSwarmActivityHandler(
            Store(), Dispatcher(), IdentityResolver(), Authorization(), null!, Audit(), Cards(), Publisher(), Logger()));

    [Fact]
    public void Constructor_RejectsNullAuditLogger()
        => Assert.Throws<ArgumentNullException>(() => new TeamsSwarmActivityHandler(
            Store(), Dispatcher(), IdentityResolver(), Authorization(), Questions(), null!, Cards(), Publisher(), Logger()));

    [Fact]
    public void Constructor_RejectsNullCardActionHandler()
        => Assert.Throws<ArgumentNullException>(() => new TeamsSwarmActivityHandler(
            Store(), Dispatcher(), IdentityResolver(), Authorization(), Questions(), Audit(), null!, Publisher(), Logger()));

    [Fact]
    public void Constructor_RejectsNullInboundEventPublisher()
        => Assert.Throws<ArgumentNullException>(() => new TeamsSwarmActivityHandler(
            Store(), Dispatcher(), IdentityResolver(), Authorization(), Questions(), Audit(), Cards(), null!, Logger()));

    [Fact]
    public void Constructor_RejectsNullLogger()
        => Assert.Throws<ArgumentNullException>(() => new TeamsSwarmActivityHandler(
            Store(), Dispatcher(), IdentityResolver(), Authorization(), Questions(), Audit(), Cards(), Publisher(), null!));
}
