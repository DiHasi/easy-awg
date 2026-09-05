using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AwgEasy.Contracts;

/// <summary>
/// AmneziaWG header and packet fields (H1-H4, I1-I5) are accepted as either JSON strings
/// or JSON numbers depending on where the value was authored. Normalize both to string.
/// </summary>
public sealed class StringOrNumberJsonConverter : JsonConverter<string?>
{
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => reader.TokenType switch
        {
            JsonTokenType.Null => null,
            JsonTokenType.String => Normalize(reader.GetString()),
            JsonTokenType.Number => reader.TryGetInt64(out var longValue)
                ? longValue.ToString(CultureInfo.InvariantCulture)
                : reader.GetDouble().ToString("R", CultureInfo.InvariantCulture),
            _ => throw new JsonException("Expected string, number or null.")
        };

    public override void Write(Utf8JsonWriter writer, string? value, JsonSerializerOptions options)
    {
        if (value is null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStringValue(value);
    }

    private static string? Normalize(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
