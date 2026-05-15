// -----------------------------------------------------------------------
// <copyright file="PackagingScriptSmokeTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Teams.Manifest.Tests;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;
using Xunit;

/// <summary>
/// Smoke tests for <c>scripts/package-teams-app.ps1</c>, the build-time script that
/// renders <c>manifest.json</c> from a template, bundles it with the color/outline
/// icons, and produces the <c>teams-app.zip</c> sideloading package required by
/// MSG-MT-001 (Microsoft Teams Support).
/// </summary>
/// <remarks>
/// <para>
/// These tests assert the full generated-package contract: not only does the
/// script emit a zip with the three required files, the rendered
/// <c>manifest.json</c> inside the zip must (a) substitute the
/// MicrosoftAppId across every required id site, (b) substitute the bot
/// domain across <c>validDomains</c>, the developer URLs, and
/// <c>webApplicationInfo.resource</c>, (c) leave no placeholder GUID or
/// placeholder host behind, and (d) still validate against the Teams v1.16
/// schema.
/// </para>
/// <para>
/// Each invocation of <see cref="RunPackaging"/> creates a unique temporary
/// working directory under <c>%TEMP%</c>. The class implements
/// <see cref="IDisposable"/> so xUnit will dispose the instance after every
/// test, and every temp directory is tracked in <see cref="tempDirs"/> and
/// best-effort deleted in <see cref="Dispose"/>. This prevents unbounded
/// accumulation of leaked package directories under <c>%TEMP%</c> across CI
/// runs.
/// </para>
/// </remarks>
public sealed class PackagingScriptSmokeTests : IDisposable
{
    private const string SampleAppId = "11111111-2222-3333-4444-555555555555";
    private const string SampleVersion = "1.2.3";
    private const string SampleBotDomain = "bots.contoso.com";
    private const string PlaceholderGuid = "00000000-0000-0000-0000-000000000000";
    private const string PlaceholderDomain = "bot.example.com";

    private static readonly string RepoRoot = ResolveRepoRoot();
    private static readonly string ScriptPath = Path.Combine(RepoRoot, "scripts", "package-teams-app.ps1");
    private static readonly TimeSpan ScriptTimeout = TimeSpan.FromSeconds(60);

    private readonly List<string> tempDirs = new();
    private bool disposed;

    [Fact]
    public void PackagingScript_ExistsAtExpectedPath()
    {
        Assert.True(
            File.Exists(ScriptPath),
            $"Packaging script not found at '{ScriptPath}'. The Teams app manifest stage requires this script.");
    }

    [Fact]
    public void PackagingScript_ProducesZipAtRequestedPath()
    {
        var result = this.RunPackaging(SampleAppId, SampleVersion, SampleBotDomain);

        Assert.True(File.Exists(result.ZipPath), $"Expected zip at '{result.ZipPath}'.");
        Assert.True(new FileInfo(result.ZipPath).Length > 0, "Zip is empty.");
    }

    [Fact]
    public void PackagingScript_ZipContainsManifestAndIcons()
    {
        var result = this.RunPackaging(SampleAppId, SampleVersion, SampleBotDomain);

        using var archive = ZipFile.OpenRead(result.ZipPath);
        var entries = archive.Entries
            .Select(e => e.FullName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.Contains("manifest.json", entries);
        Assert.Contains("color.png", entries);
        Assert.Contains("outline.png", entries);
    }

    [Fact]
    public void PackagingScript_ZipIconsAreRealPngsAndPreserveSourceBytes()
    {
        // Teams refuses to sideload a package whose icon files are not real
        // PNGs (e.g. a UTF-8 text pipeline rewrote 0x89 to 0xEF 0xBF 0xBD).
        // Compress-Archive would silently produce such a zip if the source
        // icons were corrupted, OR if a future maintainer adds a step that
        // routes the icons through a text-mode read/write. Guard against
        // both: assert the icons inside the zip start with the canonical PNG
        // signature AND are byte-identical to the source icons on disk.
        var result = this.RunPackaging(SampleAppId, SampleVersion, SampleBotDomain);
        using var archive = ZipFile.OpenRead(result.ZipPath);

        AssertZipEntryIsPngMatchingSource(archive, "color.png", ManifestFixture.ColorIconPath);
        AssertZipEntryIsPngMatchingSource(archive, "outline.png", ManifestFixture.OutlineIconPath);
    }

    [Fact]
    public void PackagingScript_SubstitutesAppIdAcrossEveryRequiredIdSite()
    {
        // The Teams sideloading contract is: every id site in the manifest
        // (top-level `id`, bots[*].botId, composeExtensions[*].botId, and
        // webApplicationInfo.id) must equal the bot's MicrosoftAppId. The
        // script collapses all four to `-AppId`; this test guards the
        // generated-package contract end-to-end.
        var root = this.LoadGeneratedManifest();

        Assert.Equal(SampleAppId, root["id"]!.GetValue<string>());
        Assert.Equal(SampleVersion, root["version"]!.GetValue<string>());
        Assert.Equal(SampleAppId, root["bots"]![0]!["botId"]!.GetValue<string>());
        Assert.Equal(SampleAppId, root["composeExtensions"]![0]!["botId"]!.GetValue<string>());
        Assert.Equal(SampleAppId, root["webApplicationInfo"]!["id"]!.GetValue<string>());
    }

    [Fact]
    public void PackagingScript_SubstitutesBotDomainAcrossValidDomainsAndDeveloperUrls()
    {
        var root = this.LoadGeneratedManifest();

        // validDomains should contain exactly the substituted bot domain.
        // A relaxed Assert.Contains would silently tolerate stray hosts
        // landing in the package, so assert the full collection shape.
        var validDomains = root["validDomains"]!.AsArray()
            .Select(n => n!.GetValue<string>())
            .ToList();
        Assert.Equal(new[] { SampleBotDomain }, validDomains);

        var dev = root["developer"]!.AsObject();
        Assert.Equal($"https://{SampleBotDomain}", dev["websiteUrl"]!.GetValue<string>());
        Assert.Equal($"https://{SampleBotDomain}/privacy", dev["privacyUrl"]!.GetValue<string>());
        Assert.Equal($"https://{SampleBotDomain}/terms", dev["termsOfUseUrl"]!.GetValue<string>());
    }

    [Fact]
    public void PackagingScript_RewritesWebApplicationInfoResourceWithDomainAndAppId()
    {
        // webApplicationInfo.resource embeds BOTH the bot domain (host) and
        // the AppId (GUID component) in the form api://<host>/<appId>.
        // Teams' SSO/Entra resource indicator depends on this exact shape,
        // so assert the full string.
        var root = this.LoadGeneratedManifest();

        Assert.Equal(
            $"api://{SampleBotDomain}/{SampleAppId}",
            root["webApplicationInfo"]!["resource"]!.GetValue<string>());
    }

    [Fact]
    public void PackagingScript_RemovesEveryPlaceholderFromGeneratedManifest()
    {
        // A successful exit must produce a tenant-deployable artifact: no
        // placeholder GUID and no placeholder host may survive anywhere in
        // the rendered JSON (not just in the fields we explicitly checked
        // above).
        var manifestText = this.ReadGeneratedManifestText();

        Assert.DoesNotContain(PlaceholderGuid, manifestText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(PlaceholderDomain, manifestText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PackagingScript_GeneratedManifest_ValidatesAgainstTeamsV116Schema()
    {
        // The substituted manifest is what actually lands in the tenant. If
        // substitution somehow breaks schema validity (e.g. a future field
        // gets the wrong type), the package must not be considered good.
        var manifestText = this.ReadGeneratedManifestText();
        using var manifest = JsonDocument.Parse(manifestText);

        var schemaJson = ManifestFixture.LoadSchemaTextForEvaluation();
        var schema = JsonSchema.FromText(schemaJson);
        var options = new EvaluationOptions { OutputFormat = OutputFormat.List };

        var result = schema.Evaluate(manifest.RootElement, options);

        if (!result.IsValid)
        {
            var failures = (result.Details ?? new List<EvaluationResults>())
                .Where(d => !d.IsValid && d.HasErrors)
                .SelectMany(d => (d.Errors ?? new Dictionary<string, string>())
                    .Select(e => $"  {d.InstanceLocation} ({d.EvaluationPath}): {e.Key} -> {e.Value}"))
                .ToList();

            Assert.Fail(
                "Generated manifest.json failed Microsoft Teams v1.16 schema validation:" +
                Environment.NewLine + string.Join(Environment.NewLine, failures));
        }
    }

    [Fact]
    public void PackagingScript_RejectsInvalidAppIdGuid()
    {
        var ex = Assert.Throws<PackagingScriptFailedException>(
            () => this.RunPackaging("not-a-guid", SampleVersion, SampleBotDomain));

        Assert.Contains("AppId", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PackagingScript_RejectsPlaceholderAppId()
    {
        // The whole point of the post-substitution placeholder check is to
        // prevent shipping a package that still carries the all-zero GUID.
        var ex = Assert.Throws<PackagingScriptFailedException>(
            () => this.RunPackaging(PlaceholderGuid, SampleVersion, SampleBotDomain));

        Assert.Contains("AppId", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PackagingScript_RejectsInvalidBotDomain()
    {
        var ex = Assert.Throws<PackagingScriptFailedException>(
            () => this.RunPackaging(SampleAppId, SampleVersion, "not a domain"));

        Assert.Contains("BotDomain", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PackagingScript_RejectsPlaceholderBotDomain()
    {
        var ex = Assert.Throws<PackagingScriptFailedException>(
            () => this.RunPackaging(SampleAppId, SampleVersion, PlaceholderDomain));

        Assert.Contains("BotDomain", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (this.disposed)
        {
            return;
        }

        this.disposed = true;

        foreach (var dir in this.tempDirs)
        {
            TryDeleteDirectory(dir);
        }

        this.tempDirs.Clear();
    }

    private JsonObject LoadGeneratedManifest()
    {
        var text = this.ReadGeneratedManifestText();
        var node = JsonNode.Parse(text)
            ?? throw new Xunit.Sdk.XunitException("Generated manifest.json is empty or unparseable.");
        return node.AsObject();
    }

    private string ReadGeneratedManifestText()
    {
        var result = this.RunPackaging(SampleAppId, SampleVersion, SampleBotDomain);

        using var archive = ZipFile.OpenRead(result.ZipPath);
        var manifestEntry = archive.GetEntry("manifest.json")
            ?? throw new Xunit.Sdk.XunitException("Zip is missing manifest.json.");

        using var stream = manifestEntry.Open();
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private PackagingResult RunPackaging(string appId, string version, string botDomain)
    {
        // Each invocation gets a unique temp directory; the path is registered with
        // `this.tempDirs` BEFORE the script runs so partial output is still cleaned
        // up in Dispose() even if the script throws or times out.
        var tempDir = Path.Combine(
            Path.GetTempPath(),
            "teams-pkg-smoke-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        this.tempDirs.Add(tempDir);

        var outputZip = Path.Combine(tempDir, "teams-app.zip");

        var psi = new ProcessStartInfo
        {
            FileName = "pwsh",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = RepoRoot,
        };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-NonInteractive");
        psi.ArgumentList.Add("-ExecutionPolicy");
        psi.ArgumentList.Add("Bypass");
        psi.ArgumentList.Add("-File");
        psi.ArgumentList.Add(ScriptPath);
        psi.ArgumentList.Add("-AppId");
        psi.ArgumentList.Add(appId);
        psi.ArgumentList.Add("-Version");
        psi.ArgumentList.Add(version);
        psi.ArgumentList.Add("-BotDomain");
        psi.ArgumentList.Add(botDomain);
        psi.ArgumentList.Add("-OutputPath");
        psi.ArgumentList.Add(outputZip);

        using var proc = Process.Start(psi)
            ?? throw new PackagingScriptFailedException("Failed to launch 'pwsh' for packaging script.");

        var stdout = proc.StandardOutput.ReadToEnd();
        var stderr = proc.StandardError.ReadToEnd();

        if (!proc.WaitForExit((int)ScriptTimeout.TotalMilliseconds))
        {
            try
            {
                proc.Kill(entireProcessTree: true);
            }
            catch
            {
            }

            throw new PackagingScriptFailedException(
                $"Packaging script timed out after {ScriptTimeout.TotalSeconds:0}s." +
                Environment.NewLine + "STDOUT:" + Environment.NewLine + stdout +
                Environment.NewLine + "STDERR:" + Environment.NewLine + stderr);
        }

        if (proc.ExitCode != 0)
        {
            throw new PackagingScriptFailedException(
                $"Packaging script exited with code {proc.ExitCode}." +
                Environment.NewLine + "STDOUT:" + Environment.NewLine + stdout +
                Environment.NewLine + "STDERR:" + Environment.NewLine + stderr);
        }

        return new PackagingResult(outputZip, tempDir, stdout, stderr);
    }

    private static void AssertZipEntryIsPngMatchingSource(ZipArchive archive, string entryName, string sourcePath)
    {
        var entry = archive.GetEntry(entryName)
            ?? throw new Xunit.Sdk.XunitException($"Zip is missing '{entryName}'.");

        byte[] zipBytes;
        using (var stream = entry.Open())
        using (var memory = new MemoryStream())
        {
            stream.CopyTo(memory);
            zipBytes = memory.ToArray();
        }

        // PNG signature: 0x89 0x50 0x4E 0x47 0x0D 0x0A 0x1A 0x0A
        byte[] pngSignature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
        Assert.True(
            zipBytes.Length >= pngSignature.Length,
            $"'{entryName}' inside the zip is too short to hold a PNG signature (length {zipBytes.Length}).");
        for (var i = 0; i < pngSignature.Length; i++)
        {
            Assert.True(
                zipBytes[i] == pngSignature[i],
                $"'{entryName}' inside the zip is not a PNG at offset {i}: " +
                $"got 0x{zipBytes[i]:X2}, expected 0x{pngSignature[i]:X2}.");
        }

        var sourceBytes = File.ReadAllBytes(sourcePath);
        Assert.Equal(sourceBytes, zipBytes);
    }

    private static void TryDeleteDirectory(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort cleanup: a virus scanner or another process may briefly
            // lock a file. Leaking on rare CI failure is preferable to failing the
            // test run during teardown.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static string ResolveRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "AgentSwarm.Messaging.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName
            ?? throw new InvalidOperationException(
                $"Could not locate AgentSwarm.Messaging.sln walking up from '{AppContext.BaseDirectory}'.");
    }

    private sealed record PackagingResult(string ZipPath, string TempDir, string Stdout, string Stderr);

    private sealed class PackagingScriptFailedException : Exception
    {
        public PackagingScriptFailedException(string message)
            : base(message)
        {
        }
    }
}
