// -----------------------------------------------------------------------
// <copyright file="KubernetesSecretProviderTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Tests.Security;

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AgentSwarm.Messaging.Core.Secrets;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Xunit;

/// <summary>
/// Stage 3.3 iter-2 evaluator item 1 regression tests:
/// <see cref="KubernetesSecretProvider"/> must resolve <c>k8s://</c>
/// references from the configured mount path with a trailing-newline
/// strip and traversal protection, and must raise
/// <see cref="SecretNotFoundException"/> when the mounted file is
/// missing.
/// </summary>
public sealed class KubernetesSecretProviderTests
{
    [Fact]
    public async Task Resolves_k8s_scheme_against_mounted_file()
    {
        string mount = CreateTempMount();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(mount, "slack-signing-secret"), "deadbeef-signing-secret");

            KubernetesSecretProvider provider = new(Options.Create(new KubernetesSecretProviderOptions { MountPath = mount }));

            string value = await provider.GetSecretAsync("k8s://slack-signing-secret", CancellationToken.None);
            value.Should().Be("deadbeef-signing-secret");
        }
        finally
        {
            DeleteTempMount(mount);
        }
    }

    [Fact]
    public async Task Strips_trailing_newline_by_default()
    {
        // Kubernetes' secret-as-file projection appends a trailing
        // newline to most values; without the strip the HMAC computed
        // by the validator silently disagrees with one computed by
        // tools that read the secret directly.
        string mount = CreateTempMount();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(mount, "signing"), "deadbeef\n");

            KubernetesSecretProvider provider = new(Options.Create(new KubernetesSecretProviderOptions { MountPath = mount }));

            string value = await provider.GetSecretAsync("k8s://signing", CancellationToken.None);
            value.Should().Be("deadbeef");
        }
        finally
        {
            DeleteTempMount(mount);
        }
    }

    [Fact]
    public async Task Preserves_value_when_TrimTrailingNewline_disabled()
    {
        string mount = CreateTempMount();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(mount, "raw"), "value\n");

            KubernetesSecretProvider provider = new(Options.Create(new KubernetesSecretProviderOptions
            {
                MountPath = mount,
                TrimTrailingNewline = false,
            }));

            string value = await provider.GetSecretAsync("k8s://raw", CancellationToken.None);
            value.Should().Be("value\n");
        }
        finally
        {
            DeleteTempMount(mount);
        }
    }

    [Fact]
    public async Task Accepts_kube_alternate_scheme()
    {
        string mount = CreateTempMount();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(mount, "alt"), "ok");

            KubernetesSecretProvider provider = new(Options.Create(new KubernetesSecretProviderOptions { MountPath = mount }));

            string value = await provider.GetSecretAsync("kube://alt", CancellationToken.None);
            value.Should().Be("ok");
        }
        finally
        {
            DeleteTempMount(mount);
        }
    }

    [Fact]
    public async Task Missing_file_throws_SecretNotFoundException_with_ref_in_message()
    {
        string mount = CreateTempMount();
        try
        {
            KubernetesSecretProvider provider = new(Options.Create(new KubernetesSecretProviderOptions { MountPath = mount }));

            Func<Task> act = async () => await provider.GetSecretAsync("k8s://does-not-exist", CancellationToken.None);
            var ex = await act.Should().ThrowAsync<SecretNotFoundException>();
            ex.Which.Message.Should().Contain("k8s://does-not-exist",
                "the secret ref must appear in the exception message to triage which reference is unresolved");
            ex.Which.SecretRef.Should().Be("k8s://does-not-exist");
        }
        finally
        {
            DeleteTempMount(mount);
        }
    }

    [Theory]
    [InlineData("k8s://../etc/passwd")]
    [InlineData("k8s://subdir/secret")]
    [InlineData("k8s:///etc/passwd")]
    [InlineData("k8s://foo\\bar")]
    public async Task Rejects_path_traversal_attempts(string maliciousRef)
    {
        string mount = CreateTempMount();
        try
        {
            KubernetesSecretProvider provider = new(Options.Create(new KubernetesSecretProviderOptions { MountPath = mount }));

            Func<Task> act = async () => await provider.GetSecretAsync(maliciousRef, CancellationToken.None);
            await act.Should().ThrowAsync<ArgumentException>(
                "path traversal outside the mount path must be rejected regardless of the surface form");
        }
        finally
        {
            DeleteTempMount(mount);
        }
    }

    [Fact]
    public async Task Empty_file_throws_SecretNotFoundException()
    {
        string mount = CreateTempMount();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(mount, "empty"), string.Empty);

            KubernetesSecretProvider provider = new(Options.Create(new KubernetesSecretProviderOptions { MountPath = mount }));

            Func<Task> act = async () => await provider.GetSecretAsync("k8s://empty", CancellationToken.None);
            await act.Should().ThrowAsync<SecretNotFoundException>();
        }
        finally
        {
            DeleteTempMount(mount);
        }
    }

    [Fact]
    public void Empty_mount_path_in_options_throws_at_construction()
    {
        Action act = () => new KubernetesSecretProvider(Options.Create(new KubernetesSecretProviderOptions { MountPath = string.Empty }));
        act.Should().Throw<ArgumentException>(
            "an empty mount path is a configuration mistake that would silently route every k8s:// reference to the working directory");
    }

    private static string CreateTempMount()
    {
        string path = Path.Combine(Path.GetTempPath(), "agentswarm-k8s-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteTempMount(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch
        {
            // Best-effort cleanup -- the temp directory will eventually be reaped.
        }
    }
}
