using System.IO;
using System.Reflection;
using System.Text.Json.Nodes;

namespace AgentSwarm.Messaging.Teams.Manifest.Tests;

/// <summary>
/// Test fixture helpers for loading the Teams app manifest and the
/// embedded Microsoft Teams v1.16 manifest schema.
/// </summary>
internal static class ManifestFixture
{
    /// <summary>
    /// Path to the canonical manifest.json copied into the test output by
    /// <c>AgentSwarm.Messaging.Teams.Manifest.Tests.csproj</c>.
    /// </summary>
    public static string ManifestPath { get; } =
        Path.Combine(AppContext.BaseDirectory, "ManifestArtifacts", "manifest.json");

    /// <summary>
    /// Path to the 192x192 colour icon staged alongside the manifest.
    /// </summary>
    public static string ColorIconPath { get; } =
        Path.Combine(AppContext.BaseDirectory, "ManifestArtifacts", "color.png");

    /// <summary>
    /// Path to the 32x32 outline icon staged alongside the manifest.
    /// </summary>
    public static string OutlineIconPath { get; } =
        Path.Combine(AppContext.BaseDirectory, "ManifestArtifacts", "outline.png");

    /// <summary>
    /// Path to the cross-platform packaging script staged alongside the manifest.
    /// </summary>
    public static string PackagingScriptPath { get; } =
        Path.Combine(AppContext.BaseDirectory, "ManifestArtifacts", "build-manifest.ps1");

    /// <summary>
    /// Loads <c>manifest.json</c> as a <see cref="JsonNode"/>. The file is required to
    /// exist — failure to parse is a test fixture failure, not a soft assertion.
    /// </summary>
    public static JsonNode LoadManifest()
    {
        Assert.True(File.Exists(ManifestPath), $"manifest.json not staged at {ManifestPath}");
        var text = File.ReadAllText(ManifestPath);
        var node = JsonNode.Parse(text);
        Assert.NotNull(node);
        return node!;
    }

    /// <summary>
    /// Loads the embedded official Microsoft Teams v1.16 manifest schema as a string.
    /// The Teams schema declares the legacy draft-04 dialect; callers that feed it
    /// into a modern JSON-Schema engine should use <see cref="LoadSchemaTextForEvaluation"/>
    /// which normalises the dialect declaration to draft-07.
    /// </summary>
    public static string LoadEmbeddedSchemaText()
    {
        const string resourceName =
            "AgentSwarm.Messaging.Teams.Manifest.Tests.Resources.MicrosoftTeams.v1.16.schema.json";

        var assembly = typeof(ManifestFixture).Assembly;
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException(
                $"Embedded schema resource not found: {resourceName}. " +
                $"Available: {string.Join(", ", assembly.GetManifestResourceNames())}");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// Loads the embedded v1.16 schema and rewrites the legacy draft-04 dialect
    /// declaration to draft-07 so it can be parsed by JsonSchema.Net 7.x
    /// (which dropped Draft 4 support). Draft-07 is a strict superset of the
    /// keywords actually used by the Teams v1.16 schema (`type`, `required`,
    /// `properties`, `additionalProperties`, `items`, `enum`, `const`,
    /// `pattern`, `minItems`/`maxItems`, `minLength`/`maxLength`,
    /// `$ref`, `definitions`), so the rewrite is semantically equivalent for
    /// validation purposes.
    /// </summary>
    public static string LoadSchemaTextForEvaluation()
    {
        return LoadEmbeddedSchemaText()
            .Replace(
                "\"http://json-schema.org/draft-04/schema#\"",
                "\"http://json-schema.org/draft-07/schema#\"",
                StringComparison.Ordinal);
    }
}
