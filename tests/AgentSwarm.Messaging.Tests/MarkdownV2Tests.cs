using AgentSwarm.Messaging.Telegram.Sending;
using FluentAssertions;
using Xunit;

namespace AgentSwarm.Messaging.Tests;

public sealed class MarkdownV2Tests
{
    [Fact]
    public void Escape_NullOrEmpty_ReturnsEmpty()
    {
        MarkdownV2.Escape(null).Should().Be(string.Empty);
        MarkdownV2.Escape("").Should().Be(string.Empty);
    }

    [Fact]
    public void Escape_LeavesUnreservedAlone()
    {
        MarkdownV2.Escape("Hello world 123").Should().Be("Hello world 123");
    }

    [Theory]
    [InlineData("a_b", "a\\_b")]
    [InlineData("foo.bar", "foo\\.bar")]
    [InlineData("!!!", "\\!\\!\\!")]
    [InlineData("[link](url)", "\\[link\\]\\(url\\)")]
    [InlineData("path\\to\\file", "path\\\\to\\\\file")]
    [InlineData("a+b=c|d", "a\\+b\\=c\\|d")]
    public void Escape_EscapesReservedCharacters(string input, string expected)
    {
        MarkdownV2.Escape(input).Should().Be(expected);
    }
}
