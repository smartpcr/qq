using AgentSwarm.Messaging.Abstractions;
using AgentSwarm.Messaging.Telegram.Webhook;
using FluentAssertions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace AgentSwarm.Messaging.Tests;

/// <summary>
/// Stage 2.4 — pins <see cref="TelegramUpdateMapper.Map"/> behavior so
/// the contract between the webhook receiver and the
/// <see cref="ITelegramUpdatePipeline"/> stays stable when Stage 2.5
/// (long polling) starts feeding the same mapper.
/// </summary>
public class TelegramUpdateMapperTests
{
    private static readonly DateTimeOffset SampleReceivedAt =
        new(2025, 1, 15, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Map_SlashTextMessage_BecomesCommand()
    {
        var update = new Update
        {
            Id = 4242,
            Message = new Message
            {
                Id = 1,
                Chat = new Chat { Id = 11, Type = ChatType.Private },
                From = new User { Id = 22, IsBot = false, FirstName = "U" },
                Text = "/status",
            },
        };

        var evt = TelegramUpdateMapper.Map(update, "cid", SampleReceivedAt);

        evt.EventType.Should().Be(EventType.Command);
        evt.RawCommand.Should().Be("/status");
        evt.Payload.Should().BeNull();
        evt.EventId.Should().Be("tg-update-4242");
        evt.UserId.Should().Be("22");
        evt.ChatId.Should().Be("11");
        evt.CorrelationId.Should().Be("cid");
        evt.Timestamp.Should().Be(SampleReceivedAt);
    }

    [Fact]
    public void Map_NonSlashText_BecomesTextReply_WithPayload()
    {
        var update = new Update
        {
            Id = 100,
            Message = new Message
            {
                Id = 1,
                Chat = new Chat { Id = 9, Type = ChatType.Private },
                From = new User { Id = 8, IsBot = false, FirstName = "U" },
                Text = "approve please",
            },
        };

        var evt = TelegramUpdateMapper.Map(update, "cid", SampleReceivedAt);

        evt.EventType.Should().Be(EventType.TextReply);
        evt.RawCommand.Should().BeNull();
        evt.Payload.Should().Be("approve please");
    }

    [Fact]
    public void Map_CallbackQuery_BecomesCallbackResponse_WithDataPayload()
    {
        var update = new Update
        {
            Id = 7,
            CallbackQuery = new CallbackQuery
            {
                Id = "cb-1",
                From = new User { Id = 33, IsBot = false, FirstName = "U" },
                Message = new Message
                {
                    Id = 2,
                    Chat = new Chat { Id = 44, Type = ChatType.Private },
                },
                Data = "approve:tenant-a:ws-b",
            },
        };

        var evt = TelegramUpdateMapper.Map(update, "cid", SampleReceivedAt);

        evt.EventType.Should().Be(EventType.CallbackResponse);
        evt.RawCommand.Should().BeNull();
        evt.Payload.Should().Be("approve:tenant-a:ws-b");
        evt.UserId.Should().Be("33");
        evt.ChatId.Should().Be("44");
    }

    [Fact]
    public void Map_EmptyTextMessage_BecomesUnknown()
    {
        // Edge case: a Message with no Text field (e.g. a photo with no
        // caption) must not crash the mapper and must classify as
        // Unknown so the pipeline can skip it without consuming a
        // dedup slot.
        var update = new Update
        {
            Id = 9,
            Message = new Message
            {
                Id = 1,
                Chat = new Chat { Id = 55, Type = ChatType.Private },
                From = new User { Id = 66, IsBot = false, FirstName = "U" },
                Text = null,
            },
        };

        var evt = TelegramUpdateMapper.Map(update, "cid", SampleReceivedAt);

        evt.EventType.Should().Be(EventType.Unknown);
    }

    [Fact]
    public void Map_EditedMessageOrPoll_BecomesUnknown()
    {
        var update = new Update
        {
            Id = 11,
            EditedMessage = new Message
            {
                Id = 1,
                Chat = new Chat { Id = 1, Type = ChatType.Private },
                From = new User { Id = 2, IsBot = false, FirstName = "U" },
                Text = "/status (edited)",
            },
        };

        var evt = TelegramUpdateMapper.Map(update, "cid", SampleReceivedAt);

        evt.EventType.Should().Be(EventType.Unknown);
        evt.ChatId.Should().Be("1");
        evt.UserId.Should().Be("2");
    }

    [Fact]
    public void Map_StableEventId_ForSameUpdate()
    {
        var update = new Update
        {
            Id = 777,
            Message = new Message
            {
                Id = 1,
                Chat = new Chat { Id = 1, Type = ChatType.Private },
                From = new User { Id = 1, IsBot = false, FirstName = "U" },
                Text = "/status",
            },
        };

        var a = TelegramUpdateMapper.Map(update, "cid-a", SampleReceivedAt);
        var b = TelegramUpdateMapper.Map(update, "cid-b", SampleReceivedAt.AddSeconds(5));

        a.EventId.Should().Be(b.EventId);
        a.EventId.Should().Be("tg-update-777");
    }

    [Fact]
    public void Map_ThrowsOnNullUpdate()
    {
        var act = () => TelegramUpdateMapper.Map(null!, "cid", SampleReceivedAt);

        act.Should().Throw<ArgumentNullException>();
    }

    [Theory]
    [InlineData(ChatType.Private, "private")]
    [InlineData(ChatType.Group, "group")]
    [InlineData(ChatType.Supergroup, "supergroup")]
    [InlineData(ChatType.Channel, "channel")]
    public void Map_Command_PopulatesChatType_FromTelegramUpdate(
        ChatType source,
        string expectedToken)
    {
        // Stage 3.4 iter-3 (evaluator item 3) — the real
        // Update.Message.Chat.Type → MessengerEvent.ChatType
        // plumbing must be pinned directly on the mapper. Without
        // this assertion, the chat-type-aware OnboardAsync flow can
        // regress while the auth-service/pipeline tests continue to
        // pass with manually supplied chat-type strings.
        var update = new Update
        {
            Id = 4242,
            Message = new Message
            {
                Id = 1,
                Chat = new Chat { Id = 11, Type = source },
                From = new User { Id = 22, IsBot = false, FirstName = "U" },
                Text = "/start",
            },
        };

        var evt = TelegramUpdateMapper.Map(update, "cid", SampleReceivedAt);

        evt.ChatType.Should().Be(expectedToken,
            "the mapper must lowercase the Telegram chat-type enum so TelegramChatTypeParser can resolve it");
    }

    [Theory]
    [InlineData(ChatType.Private, "private")]
    [InlineData(ChatType.Group, "group")]
    [InlineData(ChatType.Supergroup, "supergroup")]
    [InlineData(ChatType.Channel, "channel")]
    public void Map_TextReply_PopulatesChatType_FromTelegramUpdate(
        ChatType source,
        string expectedToken)
    {
        // Stage 3.4 iter-3 — TextReply branch shares the
        // Command branch's chat-type plumbing, but must be asserted
        // independently because the mapper returns through a
        // different code path (non-slash text → TextReply).
        var update = new Update
        {
            Id = 100,
            Message = new Message
            {
                Id = 1,
                Chat = new Chat { Id = 9, Type = source },
                From = new User { Id = 8, IsBot = false, FirstName = "U" },
                Text = "approve please",
            },
        };

        var evt = TelegramUpdateMapper.Map(update, "cid", SampleReceivedAt);

        evt.EventType.Should().Be(EventType.TextReply);
        evt.ChatType.Should().Be(expectedToken);
    }

    [Theory]
    [InlineData(ChatType.Private, "private")]
    [InlineData(ChatType.Group, "group")]
    [InlineData(ChatType.Supergroup, "supergroup")]
    [InlineData(ChatType.Channel, "channel")]
    public void Map_CallbackQuery_PopulatesChatType_FromCallbackMessage(
        ChatType source,
        string expectedToken)
    {
        // Stage 3.4 iter-3 — CallbackResponse branch reads
        // CallbackQuery.Message.Chat.Type, NOT Update.Message —
        // pin both paths explicitly so a future mapper refactor
        // cannot silently drop ChatType from one branch.
        var update = new Update
        {
            Id = 7,
            CallbackQuery = new CallbackQuery
            {
                Id = "cb-1",
                From = new User { Id = 33, IsBot = false, FirstName = "U" },
                Message = new Message
                {
                    Id = 2,
                    Chat = new Chat { Id = 44, Type = source },
                },
                Data = "approve:tenant-a:ws-b",
            },
        };

        var evt = TelegramUpdateMapper.Map(update, "cid", SampleReceivedAt);

        evt.EventType.Should().Be(EventType.CallbackResponse);
        evt.ChatType.Should().Be(expectedToken);
    }

    [Fact]
    public void Map_Unknown_FromEditedMessage_PopulatesChatType_FromFallbackChat()
    {
        // Stage 3.4 iter-3 — even Unknown events derived from a
        // fallback chat (edited message, channel post) must carry
        // ChatType so downstream observability can attribute the
        // dropped event to the right chat surface.
        var update = new Update
        {
            Id = 11,
            EditedMessage = new Message
            {
                Id = 1,
                Chat = new Chat { Id = 1, Type = ChatType.Supergroup },
                From = new User { Id = 2, IsBot = false, FirstName = "U" },
                Text = "/status (edited)",
            },
        };

        var evt = TelegramUpdateMapper.Map(update, "cid", SampleReceivedAt);

        evt.EventType.Should().Be(EventType.Unknown);
        evt.ChatType.Should().Be("supergroup");
    }

    [Fact]
    public void Map_Unknown_WithNoFallbackChat_LeavesChatTypeNull()
    {
        // Stage 3.4 iter-3 — defensive: when no chat surface is
        // present (e.g. a Poll-only update with no Message), the
        // mapper must NOT invent a chat type. TelegramChatTypeParser
        // treats null as Private, but only because the
        // TelegramUserAuthorizationService layer needs a deterministic
        // default — the mapper itself must report the raw absence so
        // observability can flag the unusual shape.
        var update = new Update
        {
            Id = 99,
            Poll = null,
        };

        var evt = TelegramUpdateMapper.Map(update, "cid", SampleReceivedAt);

        evt.EventType.Should().Be(EventType.Unknown);
        evt.ChatType.Should().BeNull();
    }
}
