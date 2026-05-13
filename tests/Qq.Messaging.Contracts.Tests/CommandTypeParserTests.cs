using Qq.Messaging.Abstractions;

namespace Qq.Messaging.Contracts.Tests;

public class CommandTypeParserTests
{
    [Theory]
    [InlineData("/start", CommandType.Start)]
    [InlineData("/status", CommandType.Status)]
    [InlineData("/agents", CommandType.Agents)]
    [InlineData("/ask", CommandType.Ask)]
    [InlineData("/approve", CommandType.Approve)]
    [InlineData("/reject", CommandType.Reject)]
    [InlineData("/handoff", CommandType.Handoff)]
    [InlineData("/pause", CommandType.Pause)]
    [InlineData("/resume", CommandType.Resume)]
    public void Parse_RecognizesAllRequiredCommands(string input, CommandType expected)
    {
        Assert.Equal(expected, CommandTypeParser.Parse(input));
    }

    [Theory]
    [InlineData("/START", CommandType.Start)]
    [InlineData("/Status", CommandType.Status)]
    [InlineData("/ASK", CommandType.Ask)]
    public void Parse_IsCaseInsensitive(string input, CommandType expected)
    {
        Assert.Equal(expected, CommandTypeParser.Parse(input));
    }

    [Theory]
    [InlineData("/ask build release notes for Solution12", CommandType.Ask)]
    [InlineData("/approve   with-extra-spaces", CommandType.Approve)]
    public void Parse_ExtractsCommandFromFullMessage(string input, CommandType expected)
    {
        Assert.Equal(expected, CommandTypeParser.Parse(input));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("hello")]
    [InlineData("/unknown")]
    public void Parse_ReturnsUnknownForInvalidInput(string? input)
    {
        Assert.Equal(CommandType.Unknown, CommandTypeParser.Parse(input));
    }
}
