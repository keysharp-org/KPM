using Kpm.Model;

namespace Kpm.Bot;

/// <summary>
/// Turns an upstream name into a registry identifier.
///
/// Upstream names are GitHub handles and forum nicknames — mixed case, occasional underscores and
/// dots — while registry ids are lowercase and hyphenated so two ids cannot collide on a
/// case-insensitive filesystem. The mapping is pure and stable: re-running an import must produce
/// the same ids, or every package would move.
/// </summary>
public static class Slug
{
	public static string Make(string text)
	{
		var builder = new System.Text.StringBuilder(text.Length);

		foreach (var c in text)
		{
			if (char.IsAsciiLetterOrDigit(c))
				builder.Append(char.ToLowerInvariant(c));
			else if (builder.Length > 0 && builder[^1] != '-')
				builder.Append('-');   // any other run of punctuation becomes a single separator
		}

		var result = builder.ToString().Trim('-');
		return result.Length > 64 ? result[..64].Trim('-') : result;
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
