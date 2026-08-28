using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kpm.Bot;

/// <summary>
/// One entry of the Aris package index, which is the migration's main input.
///
/// The index is hand-maintained JSON, so several fields are "a string or a list of strings"
/// depending on who wrote the entry; <see cref="StringOrArrayConverter"/> accepts both rather than
/// making the importer care.
/// </summary>
public sealed class ArisEntry
{
	public string Description { get; set; } = "";

	/// <summary><c>owner/repo/branch</c> — the branch is part of the field, not a separate one.</summary>
	public string? Repository { get; set; }

	/// <summary>The file a consumer includes. Absent when the entry ships exactly one file.</summary>
	public string? Main { get; set; }

	[JsonConverter(typeof(StringOrArrayConverter))]
	public List<string> Files { get; set; } = [];

	[JsonConverter(typeof(StringOrArrayConverter))]
	public List<string> Keywords { get; set; } = [];

	public string? License { get; set; }
	public string? Homepage { get; set; }
	public Dictionary<string, string> Dependencies { get; set; } = [];

	/// <summary>ScriptHub is a mirror of forum posts, not an upstream project of its own.</summary>
	[JsonIgnore]
	public bool IsScriptHub =>
		Repository?.StartsWith("ahkscript/ScriptHub", StringComparison.OrdinalIgnoreCase) ?? false;

	[JsonIgnore]
	public string? RepositoryOwnerAndName
	{
		get
		{
			if (string.IsNullOrEmpty(Repository))
				return null;

			var parts = Repository.Split('/');
			return parts.Length >= 2 ? $"{parts[0]}/{parts[1]}" : null;
		}
	}

	[JsonIgnore]
	public string RepositoryBranch
	{
		get
		{
			var parts = Repository?.Split('/') ?? [];
			return parts.Length >= 3 ? string.Join('/', parts[2..]) : "main";
		}
	}
}

/// <summary>Reads a field written either as <c>"a"</c> or as <c>["a", "b"]</c>.</summary>
public sealed class StringOrArrayConverter : JsonConverter<List<string>>
{
	public override List<string> Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
	{
		if (reader.TokenType == JsonTokenType.String)
		{
			var value = reader.GetString() ?? "";
			// A single string may still be a comma-separated list; that is how several keyword
			// fields in the index are written.
			return [.. value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)];
		}

		if (reader.TokenType == JsonTokenType.StartArray)
		{
			var items = new List<string>();

			while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
			{
				if (reader.TokenType == JsonTokenType.String)
					items.Add(reader.GetString() ?? "");
			}

			return items;
		}

		return [];
	}

	public override void Write(Utf8JsonWriter writer, List<string> value, JsonSerializerOptions options)
	{
		writer.WriteStartArray();

		foreach (var item in value)
			writer.WriteStringValue(item);

		writer.WriteEndArray();
	}
}

public static class ArisIndex
{
	private static readonly JsonSerializerOptions options = new()
	{
		PropertyNameCaseInsensitive = true,
		ReadCommentHandling = JsonCommentHandling.Skip,
		AllowTrailingCommas = true
	};

	/// <summary>
	/// Reads the index, which is keyed by AutoHotkey version at the top level. Only the v2 section
	/// is imported: v1 code does not run on either engine this registry serves.
	/// </summary>
	public static Dictionary<string, ArisEntry> Read(string json)
	{
		using var document = JsonDocument.Parse(json, new JsonDocumentOptions
		{
			CommentHandling = JsonCommentHandling.Skip,
			AllowTrailingCommas = true
		});
		var entries = new Dictionary<string, ArisEntry>(StringComparer.OrdinalIgnoreCase);

		foreach (var section in document.RootElement.EnumerateObject())
		{
			// The document mixes version sections with scalar metadata such as a top-level
			// "version" string, so sections are recognised by shape as well as by name.
			if (section.Value.ValueKind != JsonValueKind.Object
				|| !section.Name.StartsWith("v2", StringComparison.OrdinalIgnoreCase))
				continue;

			foreach (var package in section.Value.EnumerateObject())
			{
				if (package.Value.ValueKind != JsonValueKind.Object)
					continue;

				var entry = package.Value.Deserialize<ArisEntry>(options);

				if (entry is not null)
					entries[package.Name] = entry;
			}
		}

		return entries;
	}
}
