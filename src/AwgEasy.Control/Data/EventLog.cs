namespace AwgEasy.Control;

/// <summary>
/// Append-only audit trail. In an incident this is usually the only thing that answers
/// "what changed, when, and who did it" - so bundle fetches and enrollments are recorded
/// alongside operator actions.
/// </summary>
public sealed class EventLog(Database database)
{
    public void Record(string kind, string message, string? actor = null, string? nodeId = null)
    {
        using var connection = database.Open();
        using var command = connection.Sql(
            "INSERT INTO events (at, kind, actor, node_id, message) VALUES ($at, $kind, $actor, $nodeId, $message)",
            ("$at", DateTimeOffset.UtcNow.ToStorage()),
            ("$kind", kind),
            ("$actor", actor),
            ("$nodeId", nodeId),
            ("$message", message));

        command.ExecuteNonQuery();
    }

    public IReadOnlyList<EventResponse> Recent(int limit = 200)
    {
        using var connection = database.Open();
        using var command = connection.Sql("SELECT * FROM events ORDER BY id DESC LIMIT $limit", ("$limit", limit));
        using var reader = command.ExecuteReader();

        var events = new List<EventResponse>();
        while (reader.Read())
        {
            events.Add(new EventResponse(
                reader.GetInt64("id"),
                reader.GetTimestamp("at"),
                reader.GetString("kind"),
                reader.GetStringOrNull("actor"),
                reader.GetStringOrNull("node_id"),
                reader.GetString("message")));
        }

        return events;
    }
}
