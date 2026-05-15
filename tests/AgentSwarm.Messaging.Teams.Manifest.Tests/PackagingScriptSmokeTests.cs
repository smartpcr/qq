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
using System.Threading.Tasks;
using Xunit;

/// <summary>
/// Smoke tests for the PowerShell packaging script that produces the
/// Microsoft Teams app manifest zip (manifest.json + icons). These tests
/// shell out to <c>pwsh</c>; if pwsh is not on PATH the tests are skipped
/// rather than failed so they can run on developer machines without a
/// PowerShell 7 install.
/// </summary>
public sealed class PackagingScriptSmokeTests : IDisposable
{
    private const string TeamsManifestSchemaUrl =
        "https://developer.microsoft.com/en-us/json-schemas/teams/v1.16/MicrosoftTeams.schema.json";

    private static readonly TimeSpan PwshTimeout = TimeSpan.FromSeconds(60);

    private readonly string repoRoot;
    private readonly string workingDirectory;

    public PackagingScriptSmokeTests()
    {
        this.repoRoot = LocateRepoRoot();
        this.workingDirectory = Path.Combine(
            Path.GetTempPath(),
            "agentswarm-teams-manifest-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(this.workingDirectory);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(this.workingDirectory))
            {
                Directory.Delete(this.workingDirectory, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup; never fail Dispose.
        }
    }

    [Fact]
    public void PackagingScript_ProducesManifestZip_ContainingExpectedAssets()
    {
        SkipIfPwshUnavailable();

        var scriptPath = this.GetPackagingScriptPath();
        Assert.True(File.Exists(scriptPath), $"packaging script not found at {scriptPath}");

        var outputZip = Path.Combine(this.workingDirectory, "manifest.zip");

        var result = RunPwsh(
            scriptPath,
            "-OutputPath", outputZip,
            "-Environment", "Development");

        Assert.True(
            result.ExitCode == 0,
            $"packaging script exited with code {result.ExitCode}.\nSTDOUT:\n{result.Stdout}\nSTDERR:\n{result.Stderr}");
        Assert.True(File.Exists(outputZip), "packaging script did not produce the expected zip");

        using var archive = ZipFile.OpenRead(outputZip);
        var entries = archive.Entries
            .Select(e => e.FullName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.Contains("manifest.json", entries);
        Assert.Contains("color.png", entries);
        Assert.Contains("outline.png", entries);

        var manifestEntry = archive.GetEntry("manifest.json")!;
        using var manifestStream = manifestEntry.Open();
        using var manifestReader = new StreamReader(manifestStream);
        var manifestJson = manifestReader.ReadToEnd();

        using var manifestDoc = JsonDocument.Parse(manifestJson);
        var root = manifestDoc.RootElement;

        Assert.Equal(TeamsManifestSchemaUrl, root.GetProperty("$schema").GetString());
        Assert.False(
            string.IsNullOrWhiteSpace(root.GetProperty("id").GetString()),
            "manifest.id must be present");

        var bots = root.GetProperty("bots");
        Assert.True(bots.GetArrayLength() >= 1, "manifest must declare at least one bot");
        var bot = bots[0];
        Assert.False(
            string.IsNullOrWhiteSpace(bot.GetProperty("botId").GetString()),
            "bots[0].botId must be present");
    }

    [Fact]
    public void PackagingScript_RejectsMissingEnvironmentArgument()
    {
        SkipIfPwshUnavailable();

        var scriptPath = this.GetPackagingScriptPath();
        var outputZip = Path.Combine(this.workingDirectory, "manifest.zip");

        var result = RunPwsh(
            scriptPath,
            "-OutputPath", outputZip);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("Environment", result.Stderr, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(outputZip), "no zip should be produced on failure");
    }

    private string GetPackagingScriptPath() => Path.Combine(
        this.repoRoot,
        "src",
        "AgentSwarm.Messaging.Teams.Manifest",
        "Scripts",
        "Pack-TeamsManifest.ps1");

    private static string LocateRepoRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (Directory.EnumerateFiles(current.FullName, "*.sln").Any())
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new InvalidOperationException(
            "unable to locate repository root (no *.sln found) from " + AppContext.BaseDirectory);
    }

    private static void SkipIfPwshUnavailable()
    {
        Skip.IfNot(
            IsPwshOnPath(),
            "pwsh (PowerShell 7+) is not available on PATH; skipping packaging smoke test.");
    }

    private static bool IsPwshOnPath()
    {
        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        var exts = (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);

        foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var ext in exts.Concat(new[] { string.Empty }))
            {
                var candidate = Path.Combine(dir, "pwsh" + ext);
                if (File.Exists(candidate))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static PwshResult RunPwsh(string scriptPath, params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "pwsh",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-NoLogo");
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-NonInteractive");
        psi.ArgumentList.Add("-ExecutionPolicy");
        psi.ArgumentList.Add("Bypass");
        psi.ArgumentList.Add("-File");
        psi.ArgumentList.Add(scriptPath);
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        using var process = new Process { StartInfo = psi };
        if (!process.Start())
        {
            throw new InvalidOperationException("failed to start pwsh");
        }

        // Drain stdout and stderr concurrently. Reading them sequentially while
        // both are redirected is a documented deadlock pattern: if the child
        // fills its 4 KB stderr buffer before stdout is fully consumed it will
        // block on stderr writes while we block on the stdout ReadToEnd(), and
        // neither side ever makes progress.
        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
        Task<string> stderrTask = process.StandardError.ReadToEndAsync();

        if (!process.WaitForExit((int)PwshTimeout.TotalMilliseconds))
        {
            TryKill(process);
            // Give the reader tasks a brief chance to drain after the kill so we
            // can include any captured output in the timeout message.
            string stdoutOnTimeout = SafeWait(stdoutTask, TimeSpan.FromSeconds(2));
            string stderrOnTimeout = SafeWait(stderrTask, TimeSpan.FromSeconds(2));
            throw new TimeoutException(
                $"pwsh did not exit within {PwshTimeout.TotalSeconds:F0}s while running '{scriptPath}'.\n"
                + $"STDOUT (partial):\n{stdoutOnTimeout}\nSTDERR (partial):\n{stderrOnTimeout}");
        }

        // WaitForExit(int) does not guarantee the async stream readers have
        // observed EOF; call the parameterless overload to flush them.
        process.WaitForExit();

        var stdout = stdoutTask.GetAwaiter().GetResult();
        var stderr = stderrTask.GetAwaiter().GetResult();
        return new PwshResult(process.ExitCode, stdout, stderr);
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Best-effort kill; the TimeoutException will still surface.
        }
    }

    private static string SafeWait(Task<string> task, TimeSpan timeout)
    {
        try
        {
            return task.Wait(timeout) ? task.Result : "<not drained>";
        }
        catch (Exception ex)
        {
            return "<error draining stream: " + ex.Message + ">";
        }
    }

    private readonly record struct PwshResult(int ExitCode, string Stdout, string Stderr);
}

/// <summary>
/// Minimal xUnit skip helper. Mirrors the API of <c>Xunit.SkipException</c>
/// from Xunit.SkippableFact so tests can declare environmental prerequisites
/// without taking an extra package dependency.
/// </summary>
internal static class Skip
{
    public static void IfNot(bool condition, string reason)
    {
        if (!condition)
        {
            throw new SkipException(reason);
        }
    }
}

internal sealed class SkipException : Exception
{
    public SkipException(string reason)
        : base(reason)
    {
    }
}
