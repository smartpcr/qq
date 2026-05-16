using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgentSwarm.Messaging.Persistence;

/// <summary>
/// Helpers for merging human-decision metadata into the shared
/// <c>AuditLog.Details</c> JSON column. The connector-specific JSON from the
/// originating <c>HumanResponseAuditEntry.Details</c> (Discord guild/channel/
/// interaction/thread ids) is augmented with the typed human-decision fields
/// (<c>QuestionId</c>, <c>SelectedActionId</c>, <c>ActionValue</c>,
/// <c>Comment</c>) so the rolled-up audit row remains both human-readable and
/// queryable by SQLite's <c>json_extract</c>.
/// </summary>
internal static class HumanResponseDetailsEncoder
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
    };

    public static string Combine(
        string baseDetails,
        string questionId,
        string selectedActionId,
        string actionValue,
        string? comment)
    {
        JsonObject root;
        if (string.IsNullOrWhiteSpace(baseDetails))
        {
            root = new JsonObject();
        }
        else
        {
            // Defensive parse: if the caller passed a non-object JSON value
            // (e.g. an array), fall back to a fresh object and surface the
            // original payload under "OriginalDetails" so we never silently
            // lose audit context.
            var parsed = JsonNode.Parse(baseDetails);
            if (parsed is JsonObject obj)
            {
                root = obj;
            }
            else
            {
                root = new JsonObject
                {
                    ["OriginalDetails"] = parsed,
                };
            }
        }

        root["QuestionId"] = questionId;
        root["SelectedActionId"] = selectedActionId;
        root["ActionValue"] = actionValue;
        if (comment is not null)
        {
            root["Comment"] = comment;
        }

        return root.ToJsonString(JsonOptions);
    }
}
