using AgentSwarm.Messaging.Abstractions;
using FluentAssertions;

namespace AgentSwarm.Messaging.Tests.Models;

public class QuestionIdValidatorTests
{
    [Theory]
    [InlineData("Q-1")]
    [InlineData("question_42")]
    [InlineData("abcdefghij1234567890ABCDEFGHIJ")] // exactly 30 chars
    [InlineData("Q.with.dots")]
    [InlineData("Q-with-dashes")]
    public void TryValidate_AcceptsValidIds(string id)
    {
        var ok = QuestionIdValidator.TryValidate(id, out var error);

        ok.Should().BeTrue();
        error.Should().BeNull();
    }

    [Fact]
    public void TryValidate_RejectsNull()
    {
        var ok = QuestionIdValidator.TryValidate(null, out var error);

        ok.Should().BeFalse();
        error.Should().NotBeNullOrWhiteSpace();
        error.Should().Contain("null");
    }

    [Fact]
    public void TryValidate_RejectsEmpty()
    {
        var ok = QuestionIdValidator.TryValidate(string.Empty, out var error);

        ok.Should().BeFalse();
        error.Should().NotBeNullOrWhiteSpace();
        error.Should().Contain("empty");
    }

    [Fact]
    public void TryValidate_RejectsIdLongerThanThirtyChars()
    {
        var tooLong = new string('a', QuestionIdValidator.MaxLength + 1);

        var ok = QuestionIdValidator.TryValidate(tooLong, out var error);

        ok.Should().BeFalse();
        error.Should().NotBeNullOrWhiteSpace();
        error.Should().Contain(QuestionIdValidator.MaxLength.ToString());
        error.Should().Contain(tooLong.Length.ToString());
    }

    [Theory]
    [InlineData("Q-\u00e9")] // Latin-1 é
    [InlineData("Q-\u4e2d")] // CJK ideograph
    [InlineData("Q-\u00ff")] // y-with-diaeresis
    public void TryValidate_RejectsNonAsciiCharacters(string id)
    {
        var ok = QuestionIdValidator.TryValidate(id, out var error);

        ok.Should().BeFalse();
        error.Should().NotBeNullOrWhiteSpace();
        error.Should().Contain("ASCII");
    }

    [Theory]
    [InlineData("Q-\u0001")] // SOH
    [InlineData("Q-\n")]     // line feed
    [InlineData("Q-\t")]     // tab
    [InlineData("Q-\u007f")] // DEL
    public void TryValidate_RejectsControlCharacters(string id)
    {
        var ok = QuestionIdValidator.TryValidate(id, out var error);

        ok.Should().BeFalse();
        error.Should().NotBeNullOrWhiteSpace();
        error.Should().Contain("ASCII");
    }

    [Theory]
    [InlineData("q:1")]
    [InlineData("Q-1:approve")]
    public void TryValidate_RejectsColonSeparator(string id)
    {
        var ok = QuestionIdValidator.TryValidate(id, out var error);

        ok.Should().BeFalse();
        error.Should().NotBeNullOrWhiteSpace();
        error.Should().Contain(":");
    }

    [Fact]
    public void EnsureValid_ThrowsArgumentException_WithDescriptiveMessage()
    {
        var tooLong = new string('x', QuestionIdValidator.MaxLength + 5);

        var act = () => QuestionIdValidator.EnsureValid(tooLong);

        act.Should().Throw<ArgumentException>()
            .WithMessage("*exceeds the maximum*");
    }

    [Fact]
    public void EnsureValid_DoesNotThrowForValidId()
    {
        var act = () => QuestionIdValidator.EnsureValid("Q-42");

        act.Should().NotThrow();
    }
}
