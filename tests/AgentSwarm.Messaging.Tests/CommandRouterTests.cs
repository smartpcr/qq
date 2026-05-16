using AgentSwarm.Messaging.Abstractions;
using AgentSwarm.Messaging.Core;
using AgentSwarm.Messaging.Core.Commands;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentSwarm.Messaging.Tests;

/// <summary>
/// Stage 3.2 — verifies <see cref="CommandRouter"/> dispatches to the
/// handler advertising the matching <see cref="ICommandHandler.CommandName"/>,
/// rejects unknown commands with a help message listing the available
/// vocabulary, and fails fast on bad handler registrations.
/// </summary>
public class CommandRouterTests
{
    [Fact]
    public async Task RouteAsync_DispatchesToMatchingHandler()
    {
        var handler = new RecordingHandler("status", "✅ swarm idle");
        var router = new CommandRouter(
            new ICommandHandler[] { handler },
            NullLogger<CommandRouter>.Instance);

        var result = await router.RouteAsync(NewCommand("status"), NewOperator(), default);

        result.Success.Should().BeTrue();
        result.ResponseText.Should().Be("✅ swarm idle");
        handler.InvocationCount.Should().Be(1);
    }

    [Fact]
    public async Task RouteAsync_IsCaseInsensitive()
    {
        var handler = new RecordingHandler("agents", "roster");
        var router = new CommandRouter(
            new ICommandHandler[] { handler },
            NullLogger<CommandRouter>.Instance);

        var result = await router.RouteAsync(NewCommand("Agents"), NewOperator(), default);

        result.Success.Should().BeTrue();
        handler.InvocationCount.Should().Be(1);
    }

    [Fact]
    public async Task RouteAsync_UnknownCommand_ReturnsHelpfulErrorListingValidCommands()
    {
        var router = new CommandRouter(
            Array.Empty<ICommandHandler>(),
            NullLogger<CommandRouter>.Instance);

        var result = await router.RouteAsync(NewCommand("foo"), NewOperator(), default);

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(CommandRouter.UnknownCommandErrorCode);
        result.ResponseText.Should().NotBeNullOrWhiteSpace();
        foreach (var name in TelegramCommands.All)
        {
            result.ResponseText.Should().Contain("/" + name,
                "the unknown-command reply must list every recognized command");
        }
        result.ResponseText.Should().Contain("foo",
            "the reply should echo what the operator typed so they can correct it");
    }

    [Fact]
    public void Ctor_DuplicateCommandNames_ThrowsAtRegistrationTime()
    {
        var a = new RecordingHandler("status", "a");
        var b = new RecordingHandler("status", "b");

        var act = () => new CommandRouter(
            new ICommandHandler[] { a, b },
            NullLogger<CommandRouter>.Instance);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Duplicate ICommandHandler*status*");
    }

    [Fact]
    public void Ctor_BlankCommandName_Throws()
    {
        var blank = new RecordingHandler("", "x");

        var act = () => new CommandRouter(
            new ICommandHandler[] { blank },
            NullLogger<CommandRouter>.Instance);

        act.Should().Throw<ArgumentException>()
            .WithMessage("*blank CommandName*");
    }

    private static ParsedCommand NewCommand(string name, params string[] args) => new()
    {
        CommandName = name,
        Arguments = args,
        RawText = "/" + name + (args.Length == 0 ? "" : " " + string.Join(' ', args)),
        IsValid = true,
    };

    internal static AuthorizedOperator NewOperator() => new()
    {
        OperatorId = Guid.NewGuid(),
        TenantId = "t-a",
        WorkspaceId = "w-1",
        TelegramUserId = 42,
        TelegramChatId = 100,
        OperatorAlias = "@op",
    };

    private sealed class RecordingHandler : ICommandHandler
    {
        private readonly string _response;
        public string CommandName { get; }
        public int InvocationCount { get; private set; }

        public RecordingHandler(string commandName, string response)
        {
            CommandName = commandName;
            _response = response;
        }

        public Task<CommandResult> HandleAsync(ParsedCommand command, AuthorizedOperator @operator, CancellationToken ct)
        {
            InvocationCount++;
            return Task.FromResult(new CommandResult
            {
                Success = true,
                ResponseText = _response,
                CorrelationId = Guid.NewGuid().ToString("N"),
            });
        }
    }
}
