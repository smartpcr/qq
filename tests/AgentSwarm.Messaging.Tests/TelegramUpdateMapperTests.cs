using AgentSwarm.Messaging.Abstractions;
using AgentSwarm.Messaging.Telegram;
using FluentAssertions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace AgentSwarm.Messaging.Tests;

/// <summary>
/// Stage 2.5 — locks the contract of <see cref="TelegramUpdateMapper"/>.
///
/// The mapper is shared between the polling receiver (Stage 2.5) and the
/// webhook controller (Stage 2.4); both flows depend on identical
/// <see cref="MessengerEvent.EventId"/> shape so the dedup gate
/// (<see cref="IDeduplicationService"/>) short-circuits cross-transport
/// re-deliveries.
/// </summary>
public class TelegramUpdateMapperTests
{
    [Fact]
    public void Map_SlashCommandText_ReturnsCommandEvent()
    {
        var update = new Update
        {
            Id = 42,
            Message = new Message
            {
                Id = 100,
                Text = "/status ws-1",
                Date = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc),
                From = new User { Id = 555, IsBot = false, FirstName = "Op" },
                Chat = new Chat { Id = 999, Type = ChatType.Private },
            },
        };

        var evt = TelegramUpdateMapper.Map(update);

        evt.Should().NotBeNull();
        evt.EventType.Should().Be(EventType.Command);
        evt.EventId.Should().Be("tg-update-42");
        evt.RawCommand.Should().Be("/status ws-1");
        evt.Payload.Should().Be("/status ws-1");
        evt.UserId.Should().Be("555");
        evt.ChatId.Should().Be("999");
        evt.Timestamp.Should().Be(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));
        evt.CorrelationId.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Map_NonSlashText_ReturnsTextReplyEvent()
    {
        var update = new Update
        {
            Id = 7,
            Message = new Message
            {
                Id = 200,
                Text = "looks good to me",
                Date = new DateTime(2026, 1, 2, 9, 30, 0, DateTimeKind.Utc),
                From = new User { Id = 111, IsBot = false, FirstName = "Op" },
                Chat = new Chat { Id = 222, Type = ChatType.Private },
            },
        };

        var evt = TelegramUpdateMapper.Map(update);

        evt.EventType.Should().Be(EventType.TextReply);
        evt.EventId.Should().Be("tg-update-7");
        evt.RawCommand.Should().BeNull("text replies do not carry a slash command");
        evt.Payload.Should().Be("looks good to me");
        evt.UserId.Should().Be("111");
        evt.ChatId.Should().Be("222");
    }

    [Fact]
    public void Map_CallbackQuery_ReturnsCallbackResponseEvent()
    {
        var update = new Update
        {
            Id = 99,
            CallbackQuery = new CallbackQuery
            {
                Id = "cb-1",
                Data = "approve:question-1",
                From = new User { Id = 333, IsBot = false, FirstName = "Op" },
                Message = new Message
                {
                    Id = 400,
                    Chat = new Chat { Id = 444, Type = ChatType.Private },
                    Date = new DateTime(2026, 1, 3, 8, 0, 0, DateTimeKind.Utc),
                },
            },
        };

        var evt = TelegramUpdateMapper.Map(update);

        evt.EventType.Should().Be(EventType.CallbackResponse);
        evt.EventId.Should().Be("tg-update-99");
        evt.Payload.Should().Be("approve:question-1");
        evt.UserId.Should().Be("333");
        evt.ChatId.Should().Be("444");
        // Callbacks have no native timestamp; using UtcNow is the contract.
        evt.Timestamp.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void Map_MessageWithoutFrom_ReturnsUnknown()
    {
        var update = new Update
        {
            Id = 50,
            Message = new Message
            {
                Id = 1,
                Text = "/start",
                Date = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                From = null,
                Chat = new Chat { Id = 1, Type = ChatType.Private },
            },
        };

        var evt = TelegramUpdateMapper.Map(update);

        evt.EventType.Should().Be(EventType.Unknown,
            "anonymous admin posts and channel posts lack From; the pipeline must short-circuit them rather than authorize a synthetic operator");
        evt.EventId.Should().Be("tg-update-50");
    }

    [Fact]
    public void Map_CallbackWithoutMessageChat_ReturnsUnknown()
    {
        var update = new Update
        {
            Id = 51,
            CallbackQuery = new CallbackQuery
            {
                Id = "cb-orphan",
                Data = "approve",
                From = new User { Id = 1, IsBot = false, FirstName = "Op" },
                Message = null,
                InlineMessageId = "inline-only",
            },
        };

        var evt = TelegramUpdateMapper.Map(update);

        evt.EventType.Should().Be(EventType.Unknown,
            "inline-message callbacks (no parent Message) cannot be routed to a chat; pipeline must short-circuit them");
        evt.EventId.Should().Be("tg-update-51");
    }

    [Fact]
    public void Map_CallbackWithNullData_ReturnsUnknown()
    {
        // Telegram allows callback queries without a `data` payload (e.g.
        // game callbacks, callbacks dispatched server-side from outdated
        // inline keyboards). The approval/rejection contract for Stage 3.x
        // is built on a non-empty `callback_data` so a malformed/non-action
        // callback MUST NOT reach the ICallbackHandler as a real approval
        // event.
        var update = new Update
        {
            Id = 52,
            CallbackQuery = new CallbackQuery
            {
                Id = "cb-no-data",
                Data = null,
                From = new User { Id = 1, IsBot = false, FirstName = "Op" },
                Message = new Message
                {
                    Id = 1,
                    Chat = new Chat { Id = 1, Type = ChatType.Private },
                    Date = DateTime.UtcNow,
                },
            },
        };

        var evt = TelegramUpdateMapper.Map(update);

        evt.EventType.Should().Be(EventType.Unknown,
            "a CallbackQuery with null Data has no action payload; pipeline must short-circuit it rather than feed it to ICallbackHandler as an approval/rejection");
        evt.EventId.Should().Be("tg-update-52");
        evt.Payload.Should().BeNull("Unknown events carry no business payload");
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t")]
    public void Map_CallbackWithWhitespaceData_ReturnsUnknown(string data)
    {
        var update = new Update
        {
            Id = 53,
            CallbackQuery = new CallbackQuery
            {
                Id = "cb-empty-data",
                Data = data,
                From = new User { Id = 1, IsBot = false, FirstName = "Op" },
                Message = new Message
                {
                    Id = 1,
                    Chat = new Chat { Id = 1, Type = ChatType.Private },
                    Date = DateTime.UtcNow,
                },
            },
        };

        var evt = TelegramUpdateMapper.Map(update);

        evt.EventType.Should().Be(EventType.Unknown,
            "whitespace-only callback Data has no action payload and must not be promoted to CallbackResponse");
    }

    [Fact]
    public void Map_EditedMessage_ReturnsUnknown()
    {
        // The mapper deliberately does not subscribe to EditedMessage;
        // Stage 2.5 only routes Message + CallbackQuery. Everything else
        // (edits, channel posts, polls, member updates) is Unknown.
        var update = new Update
        {
            Id = 60,
            EditedMessage = new Message
            {
                Id = 1,
                Text = "/status",
                Date = DateTime.UtcNow,
                From = new User { Id = 1, IsBot = false, FirstName = "Op" },
                Chat = new Chat { Id = 1, Type = ChatType.Private },
            },
        };

        var evt = TelegramUpdateMapper.Map(update);

        evt.EventType.Should().Be(EventType.Unknown);
        evt.EventId.Should().Be("tg-update-60");
    }

    [Fact]
    public void Map_EmptyUpdate_ReturnsUnknown()
    {
        var update = new Update { Id = 1 };

        var evt = TelegramUpdateMapper.Map(update);

        evt.EventType.Should().Be(EventType.Unknown);
        evt.EventId.Should().Be("tg-update-1");
        evt.CorrelationId.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Map_NullUpdate_Throws()
    {
        var act = () => TelegramUpdateMapper.Map(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Map_MessageDate_WithUnspecifiedKind_IsCoercedToUtc()
    {
        // Defensive: hand-constructed Update with DateTimeKind=Unspecified
        // must not produce a local-time MessengerEvent.Timestamp.
        var update = new Update
        {
            Id = 70,
            Message = new Message
            {
                Id = 1,
                Text = "hi",
                Date = new DateTime(2026, 6, 15, 10, 0, 0, DateTimeKind.Unspecified),
                From = new User { Id = 1, IsBot = false, FirstName = "Op" },
                Chat = new Chat { Id = 1, Type = ChatType.Private },
            },
        };

        var evt = TelegramUpdateMapper.Map(update);

        evt.Timestamp.Offset.Should().Be(TimeSpan.Zero, "Timestamp must be UTC regardless of source Kind");
    }

    [Fact]
    public void Map_GeneratesUniqueCorrelationIdsPerCall()
    {
        var update = new Update
        {
            Id = 80,
            Message = new Message
            {
                Id = 1,
                Text = "/status",
                Date = DateTime.UtcNow,
                From = new User { Id = 1, IsBot = false, FirstName = "Op" },
                Chat = new Chat { Id = 1, Type = ChatType.Private },
            },
        };

        var first = TelegramUpdateMapper.Map(update);
        var second = TelegramUpdateMapper.Map(update);

        first.CorrelationId.Should().NotBe(second.CorrelationId,
            "each map invocation must produce a fresh correlation id (polling has no inbound trace header)");
    }
}
