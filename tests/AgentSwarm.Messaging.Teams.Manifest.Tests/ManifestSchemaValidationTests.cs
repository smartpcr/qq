using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;

namespace AgentSwarm.Messaging.Teams.Manifest.Tests;

/// <summary>
/// Validates <c>manifest.json</c> against the official Microsoft Teams v1.16 JSON Schema.
/// </summary>
public sealed class ManifestSchemaValidationTests
{
    [Fact]
    public void Manifest_ConformsToTeamsV116Schema()
    {
        var schemaJson = ManifestFixture.LoadSchemaTextForEvaluation();
        var schema = JsonSchema.FromText(schemaJson);

        var manifestText = File.ReadAllText(ManifestFixture.ManifestPath);
        using var manifest = JsonDocument.Parse(manifestText);

        var options = new EvaluationOptions
        {
            OutputFormat = OutputFormat.List,
        };

        var result = schema.Evaluate(manifest.RootElement, options);

        if (!result.IsValid)
        {
            // Surface every failed assertion to make CI output actionable.
            var failures = (result.Details ?? new List<EvaluationResults>())
                .Where(d => !d.IsValid && d.HasErrors)
                .SelectMany(d => (d.Errors ?? new Dictionary<string, string>())
                    .Select(e => $"  {d.InstanceLocation} ({d.EvaluationPath}): {e.Key} -> {e.Value}"))
                .ToList();

            if (failures.Count == 0 && result.HasErrors && result.Errors is not null)
            {
                failures = result.Errors
                    .Select(e => $"  {result.InstanceLocation}: {e.Key} -> {e.Value}")
                    .ToList();
            }

            Assert.Fail(
                "manifest.json failed Microsoft Teams v1.16 schema validation:" +
                Environment.NewLine + string.Join(Environment.NewLine, failures));
        }
    }

    [Fact]
    public void Manifest_DeclaresV116SchemaUrl()
    {
        var manifest = ManifestFixture.LoadManifest().AsObject();
        Assert.True(manifest.TryGetPropertyValue("$schema", out var schemaUrl));
        Assert.Equal(
            "https://developer.microsoft.com/en-us/json-schemas/teams/v1.16/MicrosoftTeams.schema.json",
            schemaUrl!.GetValue<string>());
    }

    [Fact]
    public void Manifest_DeclaresV116ManifestVersion()
    {
        var manifest = ManifestFixture.LoadManifest().AsObject();
        Assert.Equal("1.16", manifest["manifestVersion"]!.GetValue<string>());
    }

    [Fact]
    public void Manifest_AppIdIsConsistentAcrossAllRequiredSites()
    {
        // Schema-valid manifests can still ship inconsistent ids because each
        // GUID field is independently validated. Teams sideloading only works
        // when all four ids match the bot's MicrosoftAppId.
        var manifest = ManifestFixture.LoadManifest().AsObject();
        var topId = manifest["id"]!.GetValue<string>();
        var botId = manifest["bots"]![0]!["botId"]!.GetValue<string>();
        var composeBotId = manifest["composeExtensions"]![0]!["botId"]!.GetValue<string>();
        var webId = manifest["webApplicationInfo"]!["id"]!.GetValue<string>();

        Assert.Equal(topId, botId);
        Assert.Equal(topId, composeBotId);
        Assert.Equal(topId, webId);
    }
}
