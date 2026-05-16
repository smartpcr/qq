namespace AgentSwarm.Messaging.Discord;

/// <summary>
/// Classification of an inbound Discord interaction recorded in
/// <see cref="DiscordInteractionRecord"/>. Mirrors the four interaction shapes
/// the Gateway delivers to the bot (per architecture.md Section 3.1).
/// </summary>
public enum DiscordInteractionType
{
    /// <summary>A <c>/agent ...</c> slash command invocation.</summary>
    SlashCommand = 0,

    /// <summary>A button click on a previously posted message component.</summary>
    ButtonClick = 1,

    /// <summary>A select-menu choice on a previously posted message component.</summary>
    SelectMenu = 2,

    /// <summary>A modal dialog submission (used to capture comment rationale).</summary>
    ModalSubmit = 3,
}
