// -----------------------------------------------------------------------
// <copyright file="IconAssetsTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Teams.Manifest.Tests;

using System.IO;
using Xunit;

/// <summary>
/// Verifies the Teams sideload icons (<c>color.png</c>, <c>outline.png</c>) are
/// real PNG files with the dimensions required by the Teams app manifest
/// (Stage 2.4 — "<c>color.png</c> (192×192) and <c>outline.png</c> (32×32) icons
/// for sideloading or admin deployment").
/// </summary>
/// <remarks>
/// <para>
/// The Stage 2.4 packaging contract requires both icons to be valid PNG
/// files in their declared dimensions; Microsoft Teams refuses to sideload an
/// app package whose icon files are not real PNGs or whose dimensions do not
/// match the schema's icon size constraints. These tests guard against two
/// concrete regression modes:
/// </para>
/// <list type="bullet">
///   <item><description>
///     <b>Text-mode corruption</b> — if a contributor (or an automated tool)
///     opens these binary files through a UTF-8 text pipeline, the PNG magic
///     byte <c>0x89</c> and every other high-bit byte gets replaced with the
///     UTF-8 replacement sequence <c>EF BF BD</c>. The resulting "PNG" still
///     opens with <see cref="ZipFile"/> and still has the right file name,
///     but Teams rejects it at sideload time. The signature check below
///     catches this before the package ships.
///   </description></item>
///   <item><description>
///     <b>Wrong dimensions</b> — the manifest schema (and the Teams admin
///     center) requires <c>color.png</c> to be 192×192 and <c>outline.png</c>
///     to be 32×32. We decode the PNG IHDR chunk directly so the assertion
///     does not depend on <see cref="System.Drawing"/> (which is not
///     available on every test runner).
///   </description></item>
/// </list>
/// </remarks>
public sealed class IconAssetsTests
{
    private static readonly byte[] PngSignature =
    {
        0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
    };

    [Fact]
    public void ColorIcon_FileExists()
    {
        Assert.True(
            File.Exists(ManifestFixture.ColorIconPath),
            $"color.png not staged at '{ManifestFixture.ColorIconPath}'.");
    }

    [Fact]
    public void OutlineIcon_FileExists()
    {
        Assert.True(
            File.Exists(ManifestFixture.OutlineIconPath),
            $"outline.png not staged at '{ManifestFixture.OutlineIconPath}'.");
    }

    [Fact]
    public void ColorIcon_HasValidPngSignature()
    {
        AssertHasPngSignature(ManifestFixture.ColorIconPath);
    }

    [Fact]
    public void OutlineIcon_HasValidPngSignature()
    {
        AssertHasPngSignature(ManifestFixture.OutlineIconPath);
    }

    [Fact]
    public void ColorIcon_Is192By192()
    {
        var (width, height) = ReadPngDimensions(ManifestFixture.ColorIconPath);
        Assert.Equal(192, width);
        Assert.Equal(192, height);
    }

    [Fact]
    public void OutlineIcon_Is32By32()
    {
        var (width, height) = ReadPngDimensions(ManifestFixture.OutlineIconPath);
        Assert.Equal(32, width);
        Assert.Equal(32, height);
    }

    private static void AssertHasPngSignature(string path)
    {
        var bytes = File.ReadAllBytes(path);
        Assert.True(
            bytes.Length >= PngSignature.Length,
            $"'{path}' is too short to contain a PNG signature (length {bytes.Length}).");

        for (var i = 0; i < PngSignature.Length; i++)
        {
            Assert.True(
                bytes[i] == PngSignature[i],
                $"'{path}' is not a PNG: byte {i} is 0x{bytes[i]:X2}, expected 0x{PngSignature[i]:X2}. " +
                "If you see 0xEF 0xBF 0xBD at the start of the file, the icon was corrupted by a UTF-8 text pipeline " +
                "(0x89 was rewritten to U+FFFD). Replace the icon with a real PNG.");
        }
    }

    private static (int Width, int Height) ReadPngDimensions(string path)
    {
        // PNG layout (RFC 2083 §3.1):
        //   bytes 0-7   : signature
        //   bytes 8-11  : IHDR chunk length (always 0x0000000D)
        //   bytes 12-15 : IHDR chunk type ("IHDR")
        //   bytes 16-19 : width  (big-endian uint32)
        //   bytes 20-23 : height (big-endian uint32)
        var bytes = File.ReadAllBytes(path);
        Assert.True(bytes.Length >= 24, $"'{path}' is too short to hold an IHDR chunk.");
        Assert.True(
            bytes[12] == (byte)'I' && bytes[13] == (byte)'H' && bytes[14] == (byte)'D' && bytes[15] == (byte)'R',
            $"'{path}' is missing the IHDR chunk at offset 12.");

        var width = (bytes[16] << 24) | (bytes[17] << 16) | (bytes[18] << 8) | bytes[19];
        var height = (bytes[20] << 24) | (bytes[21] << 16) | (bytes[22] << 8) | bytes[23];
        return (width, height);
    }
}
