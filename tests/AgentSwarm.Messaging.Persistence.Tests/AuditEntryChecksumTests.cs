using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace AgentSwarm.Messaging.Persistence.Tests;

/// <summary>
/// Verifies the <see cref="AuditEntry.ComputeChecksum"/> helper produces a stable SHA-256
/// digest over the canonical fields, that the digest changes when any canonical field
/// (including the nullable ones) is altered, and — most importantly — that the encoding is
/// not susceptible to delimiter-collision attacks where two distinct field decompositions
/// produce the same canonical byte sequence.
/// </summary>
public sealed class AuditEntryChecksumTests
{
    private static AuditEntry SampleEntry()
    {
        var ts = new DateTimeOffset(2025, 5, 10, 12, 34, 56, TimeSpan.Zero);
        var checksum = AuditEntry.ComputeChecksum(
            ts,
            "corr-1",
            AuditEventTypes.CardActionReceived,
            "user-aad-1",
            AuditActorTypes.User,
            "tenant-1",
            "agent-1",
            "task-1",
            "conv-1",
            "approve",
            "{}",
            AuditOutcomes.Success);

        return new AuditEntry
        {
            Timestamp = ts,
            CorrelationId = "corr-1",
            EventType = AuditEventTypes.CardActionReceived,
            ActorId = "user-aad-1",
            ActorType = AuditActorTypes.User,
            TenantId = "tenant-1",
            AgentId = "agent-1",
            TaskId = "task-1",
            ConversationId = "conv-1",
            Action = "approve",
            PayloadJson = "{}",
            Outcome = AuditOutcomes.Success,
            Checksum = checksum,
        };
    }

    [Fact]
    public void ComputeChecksum_IsDeterministic()
    {
        var entry = SampleEntry();

        var recomputed = AuditEntry.ComputeChecksum(
            entry.Timestamp,
            entry.CorrelationId,
            entry.EventType,
            entry.ActorId,
            entry.ActorType,
            entry.TenantId,
            entry.AgentId,
            entry.TaskId,
            entry.ConversationId,
            entry.Action,
            entry.PayloadJson,
            entry.Outcome);

        Assert.Equal(entry.Checksum, recomputed);
    }

    [Fact]
    public void ComputeChecksum_ReturnsLowercaseHexSha256()
    {
        var entry = SampleEntry();

        // SHA-256 hex is exactly 64 lowercase hex characters.
        Assert.Equal(64, entry.Checksum.Length);
        Assert.All(entry.Checksum, ch => Assert.True(
            (ch >= '0' && ch <= '9') || (ch >= 'a' && ch <= 'f'),
            $"Checksum must be lowercase hex; saw '{ch}'."));
    }

    [Fact]
    public void ComputeChecksum_MatchesLengthPrefixedReferenceEncoding()
    {
        // Reproduces the canonical length-prefixed encoding independently of the
        // implementation under test so a future refactor to ComputeChecksum cannot quietly
        // change the on-the-wire format.
        var ts = new DateTimeOffset(2025, 5, 10, 12, 34, 56, TimeSpan.Zero);

        using var ms = new MemoryStream();
        using (var bw = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
        {
            bw.Write(1); // canonical encoding version
            WriteField(bw, ts.ToString("O", CultureInfo.InvariantCulture));
            WriteField(bw, "corr-1");
            WriteField(bw, AuditEventTypes.CardActionReceived);
            WriteField(bw, "user-aad-1");
            WriteField(bw, AuditActorTypes.User);
            WriteField(bw, "tenant-1");
            WriteNullableField(bw, "agent-1");
            WriteNullableField(bw, "task-1");
            WriteNullableField(bw, "conv-1");
            WriteField(bw, "approve");
            WriteField(bw, "{}");
            WriteField(bw, AuditOutcomes.Success);
        }

        var expected = Convert.ToHexString(SHA256.HashData(ms.ToArray())).ToLowerInvariant();

        var actual = AuditEntry.ComputeChecksum(
            ts,
            "corr-1",
            AuditEventTypes.CardActionReceived,
            "user-aad-1",
            AuditActorTypes.User,
            "tenant-1",
            "agent-1",
            "task-1",
            "conv-1",
            "approve",
            "{}",
            AuditOutcomes.Success);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void ComputeChecksum_DiffersWhenAnyCanonicalFieldChanges()
    {
        var entry = SampleEntry();
        var baseline = entry.Checksum;

        var mutations = new (string Label, Func<string> Compute)[]
        {
            ("Timestamp", () => AuditEntry.ComputeChecksum(
                entry.Timestamp.AddSeconds(1), entry.CorrelationId, entry.EventType, entry.ActorId,
                entry.ActorType, entry.TenantId, entry.AgentId, entry.TaskId, entry.ConversationId,
                entry.Action, entry.PayloadJson, entry.Outcome)),
            ("CorrelationId", () => AuditEntry.ComputeChecksum(
                entry.Timestamp, "corr-2", entry.EventType, entry.ActorId, entry.ActorType,
                entry.TenantId, entry.AgentId, entry.TaskId, entry.ConversationId, entry.Action,
                entry.PayloadJson, entry.Outcome)),
            ("EventType", () => AuditEntry.ComputeChecksum(
                entry.Timestamp, entry.CorrelationId, AuditEventTypes.Error, entry.ActorId,
                entry.ActorType, entry.TenantId, entry.AgentId, entry.TaskId, entry.ConversationId,
                entry.Action, entry.PayloadJson, entry.Outcome)),
            ("ActorId", () => AuditEntry.ComputeChecksum(
                entry.Timestamp, entry.CorrelationId, entry.EventType, "other-actor",
                entry.ActorType, entry.TenantId, entry.AgentId, entry.TaskId, entry.ConversationId,
                entry.Action, entry.PayloadJson, entry.Outcome)),
            ("ActorType", () => AuditEntry.ComputeChecksum(
                entry.Timestamp, entry.CorrelationId, entry.EventType, entry.ActorId,
                AuditActorTypes.Agent, entry.TenantId, entry.AgentId, entry.TaskId,
                entry.ConversationId, entry.Action, entry.PayloadJson, entry.Outcome)),
            ("TenantId", () => AuditEntry.ComputeChecksum(
                entry.Timestamp, entry.CorrelationId, entry.EventType, entry.ActorId,
                entry.ActorType, "tenant-2", entry.AgentId, entry.TaskId, entry.ConversationId,
                entry.Action, entry.PayloadJson, entry.Outcome)),
            ("AgentId", () => AuditEntry.ComputeChecksum(
                entry.Timestamp, entry.CorrelationId, entry.EventType, entry.ActorId,
                entry.ActorType, entry.TenantId, "agent-2", entry.TaskId, entry.ConversationId,
                entry.Action, entry.PayloadJson, entry.Outcome)),
            ("TaskId", () => AuditEntry.ComputeChecksum(
                entry.Timestamp, entry.CorrelationId, entry.EventType, entry.ActorId,
                entry.ActorType, entry.TenantId, entry.AgentId, "task-2", entry.ConversationId,
                entry.Action, entry.PayloadJson, entry.Outcome)),
            ("ConversationId", () => AuditEntry.ComputeChecksum(
                entry.Timestamp, entry.CorrelationId, entry.EventType, entry.ActorId,
                entry.ActorType, entry.TenantId, entry.AgentId, entry.TaskId, "conv-2",
                entry.Action, entry.PayloadJson, entry.Outcome)),
            ("Action", () => AuditEntry.ComputeChecksum(
                entry.Timestamp, entry.CorrelationId, entry.EventType, entry.ActorId,
                entry.ActorType, entry.TenantId, entry.AgentId, entry.TaskId, entry.ConversationId,
                "reject", entry.PayloadJson, entry.Outcome)),
            ("PayloadJson", () => AuditEntry.ComputeChecksum(
                entry.Timestamp, entry.CorrelationId, entry.EventType, entry.ActorId,
                entry.ActorType, entry.TenantId, entry.AgentId, entry.TaskId, entry.ConversationId,
                entry.Action, "{\"k\":1}", entry.Outcome)),
            ("Outcome", () => AuditEntry.ComputeChecksum(
                entry.Timestamp, entry.CorrelationId, entry.EventType, entry.ActorId,
                entry.ActorType, entry.TenantId, entry.AgentId, entry.TaskId, entry.ConversationId,
                entry.Action, entry.PayloadJson, AuditOutcomes.Failed)),
        };

        foreach (var (label, compute) in mutations)
        {
            Assert.NotEqual(baseline, compute());
            _ = label; // silence unused-variable warning under TreatWarningsAsErrors
        }
    }

    [Fact]
    public void ComputeChecksum_DistinguishesNullFromEmptyForNullableFields()
    {
        var ts = new DateTimeOffset(2025, 5, 10, 12, 34, 56, TimeSpan.Zero);

        var withNull = AuditEntry.ComputeChecksum(
            ts, "corr", AuditEventTypes.Error, "actor", AuditActorTypes.User, "tenant",
            agentId: null, taskId: null, conversationId: null,
            "act", "{}", AuditOutcomes.Failed);

        var withEmpty = AuditEntry.ComputeChecksum(
            ts, "corr", AuditEventTypes.Error, "actor", AuditActorTypes.User, "tenant",
            agentId: string.Empty, taskId: string.Empty, conversationId: string.Empty,
            "act", "{}", AuditOutcomes.Failed);

        Assert.NotEqual(withNull, withEmpty);
    }

    [Fact]
    public void ComputeChecksum_NullRequiredField_Throws()
    {
        var ts = DateTimeOffset.UtcNow;

        Assert.Throws<ArgumentNullException>(() => AuditEntry.ComputeChecksum(
            ts, correlationId: null!, AuditEventTypes.Error, "actor", AuditActorTypes.User,
            "tenant", null, null, null, "act", "{}", AuditOutcomes.Failed));
    }

    /// <summary>
    /// Regression guard against the delimiter-collision attack: under the pre-iteration-2
    /// implementation, <c>string.Join("|", action, payload)</c> produced
    /// <c>"a|b|c"</c> regardless of whether the boundary was <c>("a|b", "c")</c> or
    /// <c>("a", "b|c")</c>, so two semantically different audit records could be hashed to
    /// the same digest. The length-prefixed canonical encoding makes this impossible — the
    /// 32-bit byte-length header is part of the hashed bytes, so a shifted boundary always
    /// produces different headers.
    /// </summary>
    [Theory]
    [InlineData("a|b", "c", "a", "b|c")]
    [InlineData("approve|reject", "{}", "approve", "reject|{}")]
    [InlineData("act", "x", "ac", "tx")]
    public void ComputeChecksum_DelimiterCollision_DistinguishedAcrossFieldBoundary(
        string actionA, string payloadA,
        string actionB, string payloadB)
    {
        var ts = new DateTimeOffset(2025, 5, 10, 12, 34, 56, TimeSpan.Zero);

        var hashA = AuditEntry.ComputeChecksum(
            ts, "corr", AuditEventTypes.CommandReceived, "actor", AuditActorTypes.User,
            "tenant", null, null, null, actionA, payloadA, AuditOutcomes.Success);

        var hashB = AuditEntry.ComputeChecksum(
            ts, "corr", AuditEventTypes.CommandReceived, "actor", AuditActorTypes.User,
            "tenant", null, null, null, actionB, payloadB, AuditOutcomes.Success);

        Assert.NotEqual(hashA, hashB);
    }

    /// <summary>
    /// Additional collision regression for the nullable-field boundary: under the
    /// pre-iteration-2 implementation nulls were rendered as a literal <c>"\0"</c> string,
    /// so embedding that same content inside a non-null field could shift boundaries
    /// without affecting the canonical bytes. The length-prefixed encoding uses a
    /// <c>-1</c> sentinel byte length distinct from any payload content.
    /// </summary>
    [Fact]
    public void ComputeChecksum_NullableFieldCollision_DistinguishedFromSentinelContent()
    {
        var ts = new DateTimeOffset(2025, 5, 10, 12, 34, 56, TimeSpan.Zero);

        var withSentinelInPayload = AuditEntry.ComputeChecksum(
            ts, "corr", AuditEventTypes.CommandReceived, "actor", AuditActorTypes.User,
            "tenant", "\0", "\0", "\0", "act", "{}", AuditOutcomes.Success);

        var withNulls = AuditEntry.ComputeChecksum(
            ts, "corr", AuditEventTypes.CommandReceived, "actor", AuditActorTypes.User,
            "tenant", null, null, null, "act", "{}", AuditOutcomes.Success);

        Assert.NotEqual(withSentinelInPayload, withNulls);
    }

    private static void WriteField(BinaryWriter writer, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        writer.Write(bytes.Length);
        writer.Write(bytes);
    }

    private static void WriteNullableField(BinaryWriter writer, string? value)
    {
        if (value is null)
        {
            writer.Write(-1);
            return;
        }

        WriteField(writer, value);
    }
}
