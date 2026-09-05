using System.Globalization;
using Microsoft.Data.Sqlite;

namespace AwgEasy.Control;

internal static class SqliteExtensions
{
    public static SqliteCommand Sql(this SqliteConnection connection, string sql, params (string Name, object? Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        return command;
    }

    public static string GetString(this SqliteDataReader reader, string name) => reader.GetString(reader.GetOrdinal(name));

    public static string? GetStringOrNull(this SqliteDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }

    public static long GetInt64(this SqliteDataReader reader, string name) => reader.GetInt64(reader.GetOrdinal(name));

    public static int GetInt32(this SqliteDataReader reader, string name) => reader.GetInt32(reader.GetOrdinal(name));

    public static int? GetInt32OrNull(this SqliteDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);
    }

    public static bool GetBoolean(this SqliteDataReader reader, string name) => reader.GetInt64(reader.GetOrdinal(name)) != 0;

    /// <summary>Timestamps are stored as ISO-8601 round-trip strings so the file stays human-readable.</summary>
    public static DateTimeOffset GetTimestamp(this SqliteDataReader reader, string name)
        => DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal(name)), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    public static DateTimeOffset? GetTimestampOrNull(this SqliteDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal)
            ? null
            : DateTimeOffset.Parse(reader.GetString(ordinal), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
    }

    public static string ToStorage(this DateTimeOffset value) => value.ToString("O", CultureInfo.InvariantCulture);

    public static string? ToStorage(this DateTimeOffset? value) => value?.ToString("O", CultureInfo.InvariantCulture);
}
