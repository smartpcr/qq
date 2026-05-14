using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;

namespace AgentSwarm.Messaging.Teams.Manifest.Tests;

/// <summary>
/// Exercises <c>build-manifest.ps1</c> end-to-end: runs the script with a real
/// bot AAD app id and bot endpoint domain, unzips the resulting archive, and
/// re-validates the substituted <c>manifest.json</c> against the v1.16 schema.
/// </summary>
/// <remarks>
/// Skipped automatically when <c>pwsh</c> is not on PATH so the suite still passes
/// on hosts that lack PowerShell 7+ (e.g. minimal Linux containers).
/// </remarks>
public sealed class PackagingScriptSmokeTests
{
    private const string TestAppId = "1a2b3c4d-1234-5678-9abc-1a2b3c4d5e6f";
    private const string TestBotDomain = "bots.contoso.com";
    private const string PlaceholderAppId = "00000000-0000-0000-0000-000000000000";
    private const string PlaceholderDomain = "bot.example.com";

    [SkippableFact]
    public void Script_ProducesZipContainingAllThreeAssets()
    {
        Skip.IfNot(PwshAvailable(), "pwsh not on PATH");

        var (zipPath, _) = RunPackaging();

        using var archive = ZipFile.OpenRead(zipPath);
        var entryNames = archive.Entries.Select(e => e.FullName).ToHashSet(StringComparer.Ordinal);
        Assert.Contains("manifest.json", entryNames);
        Assert.Contains("color.png", entryNames);
        Assert.Contains("outline.png", entryNames);
    }

    [SkippableFact]
    public void Script_SubstitutesAllAppIdAndDomainPlaceholders()
    {
        Skip.IfNot(PwshAvailable(), "pwsh not on PATH");

        var (_, manifestText) = RunPackagingAndReadManifest();

        Assert.DoesNotContain(PlaceholderAppId, manifestText, StringComparison.Ordinal);
        Assert.DoesNotContain(PlaceholderDomain, manifestText, StringComparison.Ordinal);
        Assert.Contains(TestAppId, manifestText, StringComparison.Ordinal);
        Assert.Contains(TestBotDomain, manifestText, StringComparison.Ordinal);
    }

    [SkippableFact]
    public void Script_PreservesAppIdConsistencyAcrossAllFourSites()
    {
        Skip.IfNot(PwshAvailable(), "pwsh not on PATH");

        var (_, manifestText) = RunPackagingAndReadManifest();
        var manifest = JsonNode.Parse(manifestText)!.AsObject();

        Assert.Equal(TestAppId, manifest["id"]!.GetValue<string>());
        Assert.Equal(TestAppId, manifest["bots"]![0]!["botId"]!.GetValue<string>());
        Assert.Equal(TestAppId, manifest["composeExtensions"]![0]!["botId"]!.GetValue<string>());
        Assert.Equal(TestAppId, manifest["webApplicationInfo"]!["id"]!.GetValue<string>());
        Assert.Equal(
            $"api://{TestBotDomain}/{TestAppId}",
            manifest["webApplicationInfo"]!["resource"]!.GetValue<string>());
    }

    [SkippableFact]
    public void Script_OutputStillValidatesAgainstV116Schema()
    {
        Skip.IfNot(PwshAvailable(), "pwsh not on PATH");

        var (_, manifestText) = RunPackagingAndReadManifest();
        var schema = JsonSchema.FromText(ManifestFixture.LoadSchemaTextForEvaluation());
        using var doc = JsonDocument.Parse(manifestText);

        var result = schema.Evaluate(doc.RootElement, new EvaluationOptions
        {
            OutputFormat = OutputFormat.List,
        });

        Assert.True(result.IsValid, "Substituted manifest no longer validates against the Teams v1.16 schema.");
    }

    [SkippableFact]
    public void Script_RefusesPlaceholderAppId()
    {
        Skip.IfNot(PwshAvailable(), "pwsh not on PATH");

        var exit = Invoke(PlaceholderAppId, TestBotDomain, out var stderr, out _);
        Assert.NotEqual(0, exit);
        Assert.Contains("placeholder", stderr, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public void Script_RefusesPlaceholderDomain()
    {
        Skip.IfNot(PwshAvailable(), "pwsh not on PATH");

        var exit = Invoke(TestAppId, PlaceholderDomain, out var stderr, out _);
        Assert.NotEqual(0, exit);
        Assert.Contains("placeholder", stderr, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public void Script_RefusesMalformedAppId()
    {
        Skip.IfNot(PwshAvailable(), "pwsh not on PATH");

        var exit = Invoke("not-a-guid", TestBotDomain, out var stderr, out _);
        Assert.NotEqual(0, exit);
        Assert.Contains("guid", stderr, StringComparison.OrdinalIgnoreCase);
    }

    private static (string ZipPath, string ManifestText) RunPackagingAndReadManifest()
    {
        var (zipPath, _) = RunPackaging();
        using var archive = ZipFile.OpenRead(zipPath);
        var entry = archive.GetEntry("manifest.json")
            ?? throw new InvalidOperationException("manifest.json missing from packaged zip");
        using var reader = new StreamReader(entry.Open());
        return (zipPath, reader.ReadToEnd());
    }

    private static (string ZipPath, string Stdout) RunPackaging()
    {
        var tempDir = Path.Combine(
            Path.GetTempPath(),
            "agentswarm-teams-pkgsmoke-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var zipPath = Path.Combine(tempDir, "manifest.zip");

        var exit = Invoke(TestAppId, TestBotDomain, out var stderr, out var stdout, zipPath);
        if (exit != 0)
        {
            throw new InvalidOperationException(
                $"build-manifest.ps1 failed with exit {exit}.{Environment.NewLine}" +
                $"STDOUT: {stdout}{Environment.NewLine}STDERR: {stderr}");
        }

        Assert.True(File.Exists(zipPath), $"zip not produced at {zipPath}");
        return (zipPath, stdout);
    }

    private static int Invoke(string appId, string botDomain, out string stderr, out string stdout, string? outputPath = null)
    {
        var args = new List<string>
        {
            "-NoProfile",
            "-NonInteractive",
            "-ExecutionPolicy", "Bypass",
            "-File", ManifestFixture.PackagingScriptPath,
            "-AppId", appId,
            "-BotDomain", botDomain,
        };
        if (outputPath is not null)
        {
            args.Add("-OutputPath");
            args.Add(outputPath);
        }

        var psi = new ProcessStartInfo("pwsh")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start pwsh");
        stdout = process.StandardOutput.ReadToEnd();
        stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return process.ExitCode;
    }

    private static bool PwshAvailable()
    {
        try
        {
            var psi = new ProcessStartInfo("pwsh", "-NoProfile -Command \"$PSVersionTable.PSVersion.Major\"")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var process = Process.Start(psi);
            if (process is null) return false;
            process.WaitForExit(5000);
            return process.HasExited && process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}
