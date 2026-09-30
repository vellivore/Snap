using System.Text.Json;
using System.Text.Json.Serialization;

namespace Snap.Models;

/// <summary>
/// A saved folder with an optional display name (tabs / bookmarks).
/// Serialized as <c>{"path": "...", "name": "..."}</c>; a bare string (the format written
/// up to v1.4.2) is also accepted on read.
/// </summary>
[JsonConverter(typeof(PathEntryConverter))]
public sealed class PathEntry
{
    public string Path { get; set; } = "";
    public string? Name { get; set; }

    public PathEntry() { }

    public PathEntry(string path, string? name = null)
    {
        Path = path;
        Name = name;
    }
}

public sealed class PathEntryConverter : JsonConverter<PathEntry>
{
    public override PathEntry? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Null:
                return null;
            case JsonTokenType.String:
                return new PathEntry(reader.GetString() ?? "");
            case JsonTokenType.StartObject:
                var entry = new PathEntry();
                while (reader.Read())
                {
                    if (reader.TokenType == JsonTokenType.EndObject)
                        return entry;
                    if (reader.TokenType != JsonTokenType.PropertyName)
                        throw new JsonException("PathEntry: property name expected");
                    var prop = reader.GetString();
                    reader.Read();
                    if (string.Equals(prop, "path", StringComparison.OrdinalIgnoreCase))
                        entry.Path = reader.TokenType == JsonTokenType.String ? reader.GetString() ?? "" : "";
                    else if (string.Equals(prop, "name", StringComparison.OrdinalIgnoreCase))
                        entry.Name = reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
                    else
                        reader.Skip();
                }
                throw new JsonException("PathEntry: unexpected end of object");
            default:
                throw new JsonException($"PathEntry: unexpected token {reader.TokenType}");
        }
    }

    public override void Write(Utf8JsonWriter writer, PathEntry value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("path", value.Path);
        if (!string.IsNullOrEmpty(value.Name))
            writer.WriteString("name", value.Name);
        writer.WriteEndObject();
    }
}
