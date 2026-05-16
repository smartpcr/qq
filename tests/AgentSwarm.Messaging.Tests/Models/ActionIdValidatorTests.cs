using AgentSwarm.Messaging.Abstractions;
using FluentAssertions;

namespace AgentSwarm.Messaging.Tests.Models;

public class ActionIdValidatorTests
{
    [Theory]
    [InlineData("approve")]
    [InlineData("reject")]
    [InlineData("ack")]
    [InlineData("a")] // single char
    [InlineData("action_42")]
    [InlineData("action-with-dashes")]
    [InlineData("123456789012345678901234567890")] // exactly 30 chars
    public void TryValidate_AcceptsValidIds(string id)
    {
        var ok = ActionIdValidator.TryValidate(id, out var error);

        ok.Should().BeTrue();
        error.Should().BeNull();
    }

    [Fact]
    public void TryValidate_RejectsNull()
    {
        var ok = ActionIdValidator.TryValidate(null, out var error);

        ok.Should().BeFalse();
        error.Should().NotBeNullOrWhiteSpace();
        error.Should().Contain("null");
    }

    [Fact]
    public void TryValidate_RejectsEmpty()
    {
        var ok = ActionIdValidator.TryValidate(string.Empty, out var error);

        ok.Should().BeFalse();
        error.Should().NotBeNullOrWhiteSpace();
        error.Should().Contain("empty");
    }

    [Fact]
    public void TryValidate_RejectsIdLongerThanThirtyChars()
    {
        var tooLong = new string('a', ActionIdValidator.MaxLength + 1);

        var ok = ActionIdValidator.TryValidate(tooLong, out var error);

        ok.Should().BeFalse();
        error.Should().NotBeNullOrWhiteSpace();
        error.Should().Contain(ActionIdValidator.MaxLength.ToString());
        error.Should().Contain(tooLong.Length.ToString());
    }

    [Theory]
    [InlineData("approve\u00e9")] // Latin-1 é
    [InlineData("reject\u4e2d")] // CJK ideograph
    [InlineData("\u00ff")]        // y-with-diaeresis
    public void TryValidate_RejectsNonAsciiCharacters(string id)
    {
        var ok = ActionIdValidator.TryValidate(id, out var error);

        ok.Should().BeFalse();
        error.Should().NotBeNullOrWhiteSpace();
        error.Should().Contain("ASCII");
    }

    [Theory]
    [InlineData("a\u0001")] // SOH
    [InlineData("a\n")]     // line feed
    [InlineData("a\t")]     // tab
    [InlineData("a\u007f")] // DEL
    public void TryValidate_RejectsControlCharacters(string id)
    {
        var ok = ActionIdValidator.TryValidate(id, out var error);

        ok.Should().BeFalse();
        error.Should().NotBeNullOrWhiteSpace();
        error.Should().Contain("ASCII");
    }

    [Theory]
    [InlineData("a:b")]
    [InlineData(":approve")]
    [InlineData("approve:")]
    public void TryValidate_RejectsColonSeparator(string id)
    {
        var ok = ActionIdValidator.TryValidate(id, out var error);

        ok.Should().BeFalse();
        error.Should().NotBeNullOrWhiteSpace();
        error.Should().Contain(":");
    }

    [Fact]
    public void EnsureValid_ThrowsArgumentException_WithDescriptiveMessage()
    {
        var tooLong = new string('x', ActionIdValidator.MaxLength + 5);

        var act = () => ActionIdValidator.EnsureValid(tooLong);

        act.Should().Throw<ArgumentException>()
            .WithMessage("*exceeds the maximum*");
    }

    [Fact]
    public void EnsureValid_DoesNotThrowForValidId()
    {
        var act = () => ActionIdValidator.EnsureValid("approve");

        act.Should().NotThrow();
    }

    [Fact]
    public void CompositeCustomId_AtMaxQuestionIdAndActionId_FitsWithinTelegramCallbackDataLimit()
    {
        // The composite component id used across connectors is `q:{QuestionId}:{ActionId}`.
        // Telegram's callback_data is capped at 64 bytes (UTF-8) and Discord's
        // custom_id is capped at 100 characters. With ASCII-only inputs the byte
        // count equals the character count, so the most restrictive check is the
        // 64-byte Telegram cap. Compose the worst-case id and verify the limit.
        var maxQuestionId = new string('Q', QuestionIdValidator.MaxLength);
        var maxActionId = new string('A', ActionIdValidator.MaxLength);

        QuestionIdValidator.TryValidate(maxQuestionId, out _).Should().BeTrue();
        ActionIdValidator.TryValidate(maxActionId, out _).Should().BeTrue();

        var composite = $"q:{maxQuestionId}:{maxActionId}";

        composite.Length.Should().Be(2 + QuestionIdValidator.MaxLength + 1 + ActionIdValidator.MaxLength);
        composite.Length.Should().BeLessThanOrEqualTo(64, "must fit Telegram's callback_data 64-byte cap");
        composite.Length.Should().BeLessThanOrEqualTo(100, "must fit Discord's custom_id 100-char cap");
    }
}
