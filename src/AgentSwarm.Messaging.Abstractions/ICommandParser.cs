namespace AgentSwarm.Messaging.Abstractions;

/// <summary>
/// Splits raw inbound message text into a <see cref="ParsedCommand"/>.
/// Concrete implementations live in connector projects so the pipeline does
/// not depend on a particular parser.
/// </summary>
public interface ICommandParser
{
    /// <summary>Parse the supplied message text into a structured command.</summary>
    ParsedCommand Parse(string messageText);
}
