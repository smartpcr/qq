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
using Xunit;

/// <summary>
/// Smoke tests for <c>scripts/package-teams-app.ps1</c>, the build-time script that
/// renders <c>manifest.json</c> from a template, bundles it with the color/outline
/// icons, and produces the <c>teams-app.zip</c> sideloading package required by
/// MSG-MT-001 (Microsoft Teams Support).
/// </summary>
/// <remarks>
/// Each invocation of <see cref="RunPackaging"/> creates a unique temporary working
/// directory under <c>%TEMP%</c>. The class implements <see cref="IDisposable"/> so
/// xUnit will dispose the instance after every test, and every temp directory is
/// tracked in <see cref="tempDirs"/> and best-effort deleted in <see cref="Dispose"/>.
/// This prevents unbounded accumulation of leaked package directories under
/// <c>%TEMP%</c> across CI runs.
/// </remarks>
public sealed class PackagingScriptSmokeTests : IDisposable
{
    private const string SampleAppId = "11111111-2222-3333-4444-555555555555";
    private const string SampleBotId = "66666666-7777-8888-9999-aaaaaaaaaaaa";
    private const string SampleVersion = "1.0.0";

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
        var result = this.RunPackaging(SampleAppId, SampleBotId, SampleVersion);

        Assert.True(File.Exists(result.ZipPath), $"Expected zip at '{result.ZipPath}'.");
        Assert.True(new FileInfo(result.ZipPath).Length > 0, "Zip is empty.");
    }

    [Fact]
    public void PackagingScript_ZipContainsManifestAndIcons()
    {
        var result = this.RunPackaging(SampleAppId, SampleBotId, SampleVersion);

        using var archive = ZipFile.OpenRead(result.ZipPath);
        var entries = archive.Entries
            .Select(e => e.FullName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.Contains("manifest.json", entries);
        Assert.Contains("color.png", entries);
        Assert.Contains("outline.png", entries);
    }

    [Fact]
    public void PackagingScript_SubstitutesAppIdBotIdAndVersionIntoManifest()
    {
        var result = this.RunPackaging(SampleAppId, SampleBotId, SampleVersion);

        using var archive = ZipFile.OpenRead(result.ZipPath);
        var manifestEntry = archive.GetEntry("manifest.json")
            ?? throw new Xunit.Sdk.XunitException("Zip is missing manifest.json.");

        using var stream = manifestEntry.Open();
        using var doc = JsonDocument.Parse(stream);
        var root = doc.RootElement;

        Assert.Equal(SampleAppId, root.GetProperty("id").GetString());
        Assert.Equal(SampleVersion, root.GetProperty("version").GetString());

        var bots = root.GetProperty("bots");
        Assert.True(bots.GetArrayLength() >= 1, "manifest.bots must contain at least one entry.");
        Assert.Equal(SampleBotId, bots[0].GetProperty("botId").GetString());
    }

    [Fact]
    public void PackagingScript_RejectsInvalidAppIdGuid()
    {
        var ex = Assert.Throws<PackagingScriptFailedException>(
            () => this.RunPackaging("not-a-guid", SampleBotId, SampleVersion));

        Assert.Contains("AppId", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PackagingScript_RejectsInvalidBotIdGuid()
    {
        var ex = Assert.Throws<PackagingScriptFailedException>(
            () => this.RunPackaging(SampleAppId, "not-a-guid", SampleVersion));

        Assert.Contains("BotId", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("v1.0.0")]
    [InlineData("1")]
    [InlineData("1.0")]
    [InlineData("1.0.0.0")]
    [InlineData("01.0.0")]
    [InlineData("1.0.0-")]
    [InlineData("not-a-version")]
    public void PackagingScript_RejectsInvalidSemVerVersion(string invalidVersion)
    {
        var ex = Assert.Throws<PackagingScriptFailedException>(
            () => this.RunPackaging(SampleAppId, SampleBotId, invalidVersion));

        Assert.Contains("Version", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("1.0.0")]
    [InlineData("0.1.0")]
    [InlineData("10.20.30")]
    [InlineData("1.0.0-rc.1")]
    [InlineData("2.3.4+build.7")]
    [InlineData("1.0.0-alpha.1+exp.sha.5114f85")]
    public void PackagingScript_AcceptsValidSemVerVersion(string validVersion)
    {
        var result = this.RunPackaging(SampleAppId, SampleBotId, validVersion);

        Assert.True(File.Exists(result.ZipPath), $"Packaging should succeed for SemVer '{validVersion}'.");
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

    private PackagingResult RunPackaging(string appId, string botId, string version)
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
        psi.ArgumentList.Add("-BotId");
        psi.ArgumentList.Add(botId);
        psi.ArgumentList.Add("-Version");
        psi.ArgumentList.Add(version);
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
