using System.Text.Json.Nodes;

namespace AgentSwarm.Messaging.Teams.Manifest.Tests;

/// <summary>
/// Verifies the bot capability declared in <c>manifest.json</c> exposes the
/// scopes called out in the Stage 2.4 test scenarios (personal + team).
/// </summary>
public sealed class BotScopesTests
{
    [Fact]
    public void Manifest_DeclaresExactlyOneBot()
    {
        // Teams v1.16 schema caps bots at maxItems: 1. The manifest declares
        // a single bot — assert that explicitly so future edits cannot silently
        // multiply the entries (which would still pass the schema array shape
        // check but break the implicit "one bot" invariant the rest of the
        // story documents).
        var manifest = ManifestFixture.LoadManifest().AsObject();
        var bots = manifest["bots"]!.AsArray();
        Assert.Single(bots);
    }

    [Fact]
    public void Bot_ScopesContainPersonal()
    {
        var scopes = GetBotScopes();
        Assert.Contains("personal", scopes);
    }

    [Fact]
    public void Bot_ScopesContainTeam()
    {
        var scopes = GetBotScopes();
        Assert.Contains("team", scopes);
    }

    [Fact]
    public void Bot_SupportsFilesIsFalse()
    {
        // Out of scope per tech-spec.md §2.2.
        var manifest = ManifestFixture.LoadManifest().AsObject();
        var bot = manifest["bots"]![0]!.AsObject();
        Assert.False(bot["supportsFiles"]!.GetValue<bool>());
    }

    [Fact]
    public void Bot_BotIdIsAValidGuid()
    {
        var manifest = ManifestFixture.LoadManifest().AsObject();
        var botId = manifest["bots"]![0]!["botId"]!.GetValue<string>();
        Assert.True(Guid.TryParse(botId, out _), $"bots[0].botId '{botId}' is not a GUID");
    }

    private static IReadOnlyList<string> GetBotScopes()
    {
        var manifest = ManifestFixture.LoadManifest().AsObject();
        var scopes = manifest["bots"]![0]!["scopes"]!.AsArray();
        return scopes.Select(s => s!.GetValue<string>()).ToList();
    }
}
