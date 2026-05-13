using Qq.Messaging.Abstractions;

namespace Qq.Messaging.Contracts.Tests;

public class InboundInteractionTests
{
    private static PlatformPrincipal MakePrincipal() =>
        new("Telegram", "chat-1", "user-1", "update-100");

    [Fact]
    public void FromCommand_SetsTypeAndExposesCommandPrincipal()
    {
        var cmd = new InboundCommand(
            CommandType.Ask,
            "/ask deploy",
            ["deploy"],
            MakePrincipal(),
            null,
            new CorrelationContext(),
            DateTimeOffset.UtcNow);

        var interaction = InboundInteraction.FromCommand(cmd);

        Assert.Equal(InteractionType.Command, interaction.Type);
        Assert.Same(cmd, interaction.Command);
        Assert.Equal("chat-1", interaction.Principal.ChatId);
    }

    [Fact]
    public void FromCallback_SetsTypeAndExposesCallbackPrincipal()
    {
        var cb = new InboundCallbackResponse(
            "cb-1",
            "approved",
            "q-42",
            MakePrincipal(),
            null,
            new CorrelationContext(),
            DateTimeOffset.UtcNow);

        var interaction = InboundInteraction.FromCallback(cb);

        Assert.Equal(InteractionType.CallbackResponse, interaction.Type);
        Assert.Same(cb, interaction.Callback);
        Assert.Equal("user-1", interaction.Principal.UserId);
    }
}
