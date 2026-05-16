// -----------------------------------------------------------------------
// <copyright file="Ulid.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Core.Identifiers;

using System;
using System.Security.Cryptography;

/// <summary>
/// Minimal ULID (Universally Unique Lexicographically Sortable Identifier)
/// generator and validator. Produces 26-character Crockford base32 strings
/// composed of a 48-bit big-endian millisecond timestamp followed by
/// 80 bits of cryptographic randomness, per the
/// <see href="https://github.com/ulid/spec">ULID specification v0.5</see>.
/// </summary>
/// <remarks>
/// <para>
/// Why a hand-rolled ULID rather than a third-party package: the
/// project's NuGet feed is constrained to the corext_cache mirror; adding
/// a new dependency requires a feed bump that is out of scope for the
/// Stage 3.1 security workstream. A small in-process generator is the
/// smallest surface that satisfies the architecture.md §3.5 ULID-shaped
/// audit-id contract without expanding the dependency graph.
/// </para>
/// <para>
/// The output is lexicographically sortable: the timestamp prefix is
/// big-endian and Crockford base32 preserves byte ordering. Two IDs
/// generated in the same millisecond are differentiated by the random
/// suffix; cross-process monotonicity is NOT guaranteed (the spec's
/// monotonic-suffix recipe is intentionally omitted because the audit
/// pipeline does not depend on it).
/// </para>
/// </remarks>
public static class Ulid
{
    /// <summary>
    /// Fixed character length of every emitted ULID string (10 chars of
    /// timestamp + 16 chars of randomness, Crockford base32).
    /// </summary>
    public const int Length = 26;

    /// <summary>
    /// Crockford base32 alphabet. Excludes the visually ambiguous
    /// characters <c>I</c>, <c>L</c>, <c>O</c>, and <c>U</c>.
    /// </summary>
    private const string CrockfordAlphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    /// <summary>
    /// Generates a new ULID using the system clock and a cryptographically
    /// secure random suffix. Equivalent to
    /// <c>NewUlid(DateTimeOffset.UtcNow)</c>.
    /// </summary>
    public static string NewUlid()
        => NewUlid(DateTimeOffset.UtcNow);

    /// <summary>
    /// Generates a new ULID whose timestamp prefix encodes
    /// <paramref name="timestamp"/>'s Unix milliseconds and whose suffix
    /// is 80 bits of cryptographic randomness.
    /// </summary>
    /// <param name="timestamp">
    /// Source of the timestamp prefix. Must be non-negative when expressed
    /// as Unix milliseconds; values from years before 1970 and after
    /// 10889 AD throw <see cref="ArgumentOutOfRangeException"/>.
    /// </param>
    public static string NewUlid(DateTimeOffset timestamp)
    {
        long unixMs = timestamp.ToUnixTimeMilliseconds();
        if (unixMs < 0 || unixMs > 0xFFFF_FFFF_FFFFL)
        {
            throw new ArgumentOutOfRangeException(
                nameof(timestamp),
                "ULID timestamp must fit in 48 unsigned bits (year 1970 .. 10889).");
        }

        Span<byte> randomness = stackalloc byte[10];
        RandomNumberGenerator.Fill(randomness);

        return Encode(unixMs, randomness);
    }

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="value"/> is a
    /// well-formed ULID: exactly 26 characters, every character in the
    /// Crockford base32 alphabet (case-insensitive). Used by the audit
    /// tests to pin the id shape contract.
    /// </summary>
    public static bool IsValid(string? value)
    {
        if (value is null || value.Length != Length)
        {
            return false;
        }

        for (int i = 0; i < value.Length; i++)
        {
            char c = char.ToUpperInvariant(value[i]);
            if (CrockfordAlphabet.IndexOf(c) < 0)
            {
                return false;
            }
        }

        return true;
    }

    private static string Encode(long timestampMs, ReadOnlySpan<byte> randomness)
    {
        // ULID layout:
        //   [0..10) -- 48-bit timestamp, big-endian, Crockford base32
        //   [10..26) -- 80-bit randomness, Crockford base32
        //
        // The two halves are encoded separately so the timestamp prefix
        // is monotonic at character granularity (every ms produces a
        // strictly greater prefix).
        Span<char> buffer = stackalloc char[Length];

        // Encode timestamp -- 48 bits => 10 base32 chars. We pull 5 bits
        // at a time from the least-significant end and emit from the
        // right of the prefix so the resulting string is big-endian.
        long ts = timestampMs;
        for (int i = 9; i >= 0; i--)
        {
            int index = (int)(ts & 0x1F);
            buffer[i] = CrockfordAlphabet[index];
            ts >>= 5;
        }

        // Encode randomness -- 80 bits packed into 16 base32 chars by
        // streaming a bit-buffer MSB-first across the 10 bytes. Each
        // 5-bit slice is mapped through the Crockford alphabet.
        int bitBuffer = 0;
        int bitsInBuffer = 0;
        int outIndex = 10;
        for (int i = 0; i < randomness.Length; i++)
        {
            bitBuffer = (bitBuffer << 8) | randomness[i];
            bitsInBuffer += 8;
            while (bitsInBuffer >= 5)
            {
                bitsInBuffer -= 5;
                int chunk = (bitBuffer >> bitsInBuffer) & 0x1F;
                buffer[outIndex++] = CrockfordAlphabet[chunk];
            }
        }

        if (bitsInBuffer > 0)
        {
            int chunk = (bitBuffer << (5 - bitsInBuffer)) & 0x1F;
            buffer[outIndex++] = CrockfordAlphabet[chunk];
        }

        return new string(buffer);
    }
}
