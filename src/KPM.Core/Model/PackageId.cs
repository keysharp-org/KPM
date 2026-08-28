using System.Diagnostics.CodeAnalysis;

namespace Kpm.Model;

/// <summary>
/// A package's registry identity, <c>owner/name</c>. Both halves are lowercase and match
/// <c>[a-z0-9]([a-z0-9-]*[a-z0-9])?</c>: the corpus this registry imports from has many
/// same-named libraries (several JSONs), so identity is namespaced, and the case rule keeps
/// two ids from colliding on a case-insensitive filesystem.
/// </summary>
public readonly record struct PackageId
{
	public string Owner { get; }
	public string Name { get; }

	private PackageId(string owner, string name)
	{
		Owner = owner;
		Name = name;
	}

	public static bool IsValidSegment(string? s)
	{
		if (string.IsNullOrEmpty(s) || s.Length > 64)
			return false;

		for (var i = 0; i < s.Length; i++)
		{
			var c = s[i];

			if (c == '-')
			{
				if (i == 0 || i == s.Length - 1)   // no leading/trailing hyphen, so no id reads as a flag
					return false;
			}
			else if (!(char.IsAsciiDigit(c) || (c >= 'a' && c <= 'z')))
				return false;
		}

		return true;
	}

	public static bool TryParse(string? text, [NotNullWhen(true)] out PackageId? id, out string? error)
	{
		id = null;
		error = null;

		if (string.IsNullOrWhiteSpace(text))
		{
			error = "package id is empty";
			return false;
		}

		var slash = text.IndexOf('/');

		if (slash < 0)
		{
			error = $"'{text}' is not a package id: expected 'owner/name'";
			return false;
		}

		if (text.IndexOf('/', slash + 1) >= 0)
		{
			error = $"'{text}' is not a package id: expected exactly one '/'";
			return false;
		}

		var owner = text[..slash];
		var name = text[(slash + 1)..];

		if (!IsValidSegment(owner))
		{
			error = $"'{owner}' is not a valid owner: lowercase letters, digits and inner hyphens only";
			return false;
		}

		if (!IsValidSegment(name))
		{
			error = $"'{name}' is not a valid package name: lowercase letters, digits and inner hyphens only";
			return false;
		}

		id = new PackageId(owner, name);
		return true;
	}

	public static PackageId Parse(string text) =>
		TryParse(text, out var id, out var error) ? id.Value : throw new ArgumentException(error, nameof(text));

	/// <summary>The registry path this id owns, always forward-slashed.</summary>
	public string RegistryPath => $"packages/{Owner}/{Name}";

	public override string ToString() => $"{Owner}/{Name}";
}
