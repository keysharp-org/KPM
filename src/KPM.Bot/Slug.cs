using Kpm.Model;

namespace Kpm.Bot;

/// <summary>
/// Turns an upstream name into a registry identifier.
///
/// The author's own casing and punctuation are kept — <c>Descolada/OCR</c>, <c>thqby/child_process</c>
/// — because these are people's names and their libraries' names. Only characters an id cannot hold
/// are rewritten, which in this corpus means the spaces in a few forum handles ("Komrad Toast").
/// The mapping is pure and stable: re-running an import must produce the same ids, or every package
/// would move.
/// </summary>
public static class Slug
{
	public static string Make(string text)
	{
		var builder = new System.Text.StringBuilder(text.Length);

		foreach (var c in text)
		{
			if (char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.')
				builder.Append(c);
			else if (builder.Length > 0 && builder[^1] != '-')
				builder.Append('-');   // any other run of punctuation becomes a single separator
		}

		// A segment must begin and end alphanumeric, so trim any punctuation the rewrite left at
		// either end rather than producing an id that fails validation.
		var result = builder.ToString().Trim('-', '_', '.');
		return result.Length > 64 ? result[..64].Trim('-', '_', '.') : result;
	}

	/// <summary>Maps an Aris <c>Owner/Name</c> key to a registry id, or explains why it cannot.</summary>
	public static bool TryMap(string arisKey, out PackageId id, out string? error)
	{
		id = default;
		error = null;
		var parts = arisKey.Split('/');

		if (parts.Length != 2)
		{
			error = $"'{arisKey}' is not an owner/name pair";
			return false;
		}

		var candidate = $"{Make(parts[0])}/{Make(parts[1])}";

		if (!PackageId.TryParse(candidate, out var parsed, out var parseError))
		{
			error = $"'{arisKey}' maps to '{candidate}', which is not a valid id: {parseError}";
			return false;
		}

		id = parsed.Value;
		return true;
	}
}
