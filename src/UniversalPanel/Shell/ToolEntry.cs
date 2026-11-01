using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace UniversalPanel.Shell;

/// <summary>
/// One Tools entry as saved. Only <see cref="Path"/> is required; the rest override what the
/// panel works out itself (name and icon from the file, categories from the catalog).
/// Deliberately no "run as administrator" flag: see docs/SECURITY.md.
/// </summary>
[JsonConverter(typeof(ToolEntryConverter))]
internal sealed class ToolEntry
{
    public string Path { get; set; } = string.Empty;

    public string? Name { get; set; }

    /// <summary>Custom icon: an .ico, .exe, .dll or image file.</summary>
    public string? Icon { get; set; }

    /// <summary>Sections it's listed in; null means the catalog's defaults.</summary>
    public List<string>? Categories { get; set; }

    /// <summary>The user's actions for this tool; null means the catalog's defaults (Catalog/tools.json).</summary>
    public List<Native.ToolAction>? Actions { get; set; }

    public ToolEntry Clone() => new()
    {
        Path = Path,
        Name = Name,
        Icon = Icon,
        Categories = Categories?.ToList(),
        Actions = Actions?.Select(a => a.Clone()).ToList(),
    };

    /// <summary>False for anything a hand-edited settings file shouldn't contain.</summary>
    public bool Sanitize()
    {
        if (!IsSafePath(Path) && !Native.WslTool.TryParse(Path, out _, out _))
        {
            return false;
        }

        Name = string.IsNullOrWhiteSpace(Name) ? null : Name.Trim()[..Math.Min(Name.Trim().Length, 80)];
        Icon = IsSafePath(Icon) ? Icon : null;
        Categories = Categories?.Where(c => !string.IsNullOrWhiteSpace(c) && c.Length <= 40).Distinct().Take(10).ToList();
        Actions = Actions?
            .Where(a => a is not null && a.Name.Length is > 0 and <= 80 && a.Args.Length <= 2000
                && a.Target is Native.ToolTargets.Process or Native.ToolTargets.File or Native.ToolTargets.None
                && a.Run is Native.ToolRunModes.Window or Native.ToolRunModes.Console)
            .Take(30)
            .ToList();
        return true;
    }

    /// <summary>
    /// A local, fully qualified path with no quotes. Network paths (\\server\share, \\?\UNC\,
    /// device paths, //host) are refused: just reading a tool's version or icon from one would
    /// make the (possibly elevated) panel reach out over SMB and leak the user's NTLM hash, and
    /// the tool list is user-writable. Mapped drive letters (Z:) aren't detected (known limitation).
    /// </summary>
    public static bool IsSafePath(string? path) =>
        path is { Length: > 3 and < 1024 } && System.IO.Path.IsPathFullyQualified(path) && !path.Contains('"') && !IsNetworkPath(path);

    public static bool IsNetworkPath(string path) =>
        path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal);
}

/// <summary>Reads both the object form and the plain-path strings older versions saved.</summary>
internal sealed class ToolEntryConverter : JsonConverter<ToolEntry>
{
    public override ToolEntry? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            return new ToolEntry { Path = reader.GetString() ?? string.Empty };
        }

        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException("Expected a tool entry.");
        }

        var entry = new ToolEntry();
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            string name = reader.GetString() ?? string.Empty;
            reader.Read();
            switch (name)
            {
                case nameof(ToolEntry.Path):
                    entry.Path = reader.GetString() ?? string.Empty;
                    break;
                case nameof(ToolEntry.Name):
                    entry.Name = reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
                    break;
                case nameof(ToolEntry.Icon):
                    entry.Icon = reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
                    break;
                case nameof(ToolEntry.Categories):
                    entry.Categories = reader.TokenType == JsonTokenType.StartArray ? JsonSerializer.Deserialize<List<string>>(ref reader, options) : null;
                    break;
                case nameof(ToolEntry.Actions):
                    entry.Actions = reader.TokenType == JsonTokenType.StartArray ? JsonSerializer.Deserialize<List<Native.ToolAction>>(ref reader, options) : null;
                    break;
                default:
                    reader.Skip();
                    break;
            }
        }

        return entry;
    }

    public override void Write(Utf8JsonWriter writer, ToolEntry value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString(nameof(ToolEntry.Path), value.Path);
        if (value.Name is not null)
        {
            writer.WriteString(nameof(ToolEntry.Name), value.Name);
        }

        if (value.Icon is not null)
        {
            writer.WriteString(nameof(ToolEntry.Icon), value.Icon);
        }

        if (value.Categories is not null)
        {
            writer.WritePropertyName(nameof(ToolEntry.Categories));
            JsonSerializer.Serialize(writer, value.Categories, options);
        }

        if (value.Actions is not null)
        {
            writer.WritePropertyName(nameof(ToolEntry.Actions));
            JsonSerializer.Serialize(writer, value.Actions, options);
        }

        writer.WriteEndObject();
    }
}
