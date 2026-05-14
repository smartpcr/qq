using System.Text.Json.Nodes;

namespace AgentSwarm.Messaging.Teams.Manifest.Tests;

/// <summary>
/// Verifies the <c>composeExtensions</c> action-command declaration in
/// <c>manifest.json</c>. Aligned with the Stage 2.4 test scenarios and the
/// downstream Stage 3.4 handler expectations (`MessageExtensionHandler`,
/// `forwardToAgent`).
/// </summary>
public sealed class ComposeExtensionTests
{
    [Fact]
    public void Manifest_DeclaresExactlyOneComposeExtension()
    {
        // v1.16 schema caps composeExtensions at maxItems: 1.
        var manifest = ManifestFixture.LoadManifest().AsObject();
        var extensions = manifest["composeExtensions"]!.AsArray();
        Assert.Single(extensions);
    }

    [Fact]
    public void ComposeExtension_HasAtLeastOneCommand()
    {
        var commands = GetCommands();
        Assert.NotEmpty(commands);
    }

    [Fact]
    public void ComposeExtension_FirstCommandIsForwardToAgent()
    {
        var command = GetFirstCommand();
        Assert.Equal("forwardToAgent", command["id"]!.GetValue<string>());
    }

    [Fact]
    public void ComposeExtension_ActionCommand_HasAllRequiredFields()
    {
        // Stage 2.4 test scenario: "composeExtensions contains at least one
        // action command entry with all required fields (id, title,
        // description, type: 'action', fetchTask: false)."
        var command = GetFirstCommand();

        Assert.True(command.TryGetPropertyValue("id", out var id));
        Assert.False(string.IsNullOrWhiteSpace(id!.GetValue<string>()));

        Assert.True(command.TryGetPropertyValue("title", out var title));
        Assert.False(string.IsNullOrWhiteSpace(title!.GetValue<string>()));

        Assert.True(command.TryGetPropertyValue("description", out var description));
        Assert.False(string.IsNullOrWhiteSpace(description!.GetValue<string>()));

        Assert.True(command.TryGetPropertyValue("type", out var type));
        Assert.Equal("action", type!.GetValue<string>());

        Assert.True(command.TryGetPropertyValue("fetchTask", out var fetchTask));
        Assert.False(fetchTask!.GetValue<bool>());
    }

    [Fact]
    public void ComposeExtension_ActionCommand_ContextSupportsMessageAndCommandBox()
    {
        // architecture.md §2.15 + e2e-scenarios.md §Message Actions require
        // the action to be invokable from both message context (right-click a
        // message) and command box context.
        var command = GetFirstCommand();
        var context = command["context"]!.AsArray().Select(n => n!.GetValue<string>()).ToList();

        Assert.Contains("message", context);
        Assert.Contains("commandBox", context);
    }

    [Fact]
    public void ComposeExtension_BotIdMatchesTopLevelId()
    {
        // Teams will not route composeExtension/submitAction invocations unless
        // composeExtensions[0].botId equals the bot's MicrosoftAppId. The
        // build-manifest.ps1 script substitutes a single placeholder GUID
        // across all four sites; this test guards against drift if a maintainer
        // edits the manifest by hand.
        var manifest = ManifestFixture.LoadManifest().AsObject();
        var topId = manifest["id"]!.GetValue<string>();
        var extensionBotId = manifest["composeExtensions"]![0]!["botId"]!.GetValue<string>();
        Assert.Equal(topId, extensionBotId);
    }

    private static JsonArray GetCommands()
    {
        var manifest = ManifestFixture.LoadManifest().AsObject();
        return manifest["composeExtensions"]![0]!["commands"]!.AsArray();
    }

    private static JsonObject GetFirstCommand()
    {
        var commands = GetCommands();
        Assert.NotEmpty(commands);
        var first = commands[0];
        Assert.NotNull(first);
        return first!.AsObject();
    }
}
