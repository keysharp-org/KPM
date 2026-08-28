using System.Diagnostics.CodeAnalysis;

namespace Kpm.Model;

/// <summary>
/// A package's registry identity, <c>Owner/Name</c>.
///
/// Casing is preserved as the author writes it — <c>Descolada/OCR</c>, not <c>descolada/ocr</c> —
/// because these are people's names and their libraries' names, and flattening them makes the
/// registry read like it does not know who wrote what.
///
/// Comparison, however, is case-insensitive everywhere, which is what keeps that safe: two ids
/// differing only by case would be one directory on Windows and macOS, so the registry rejects the
/// second (see <c>RegistryValidator</c>) and lookups accept any casing the user types.
/// </summary>
public readonly struct PackageId : IEquatable<PackageId>
{
	public string Owner { get; }
	public string Name { get; }

	private PackageId(string owner, string name)
	{
		Owner = owner;
		Name = name;
	}

	/// <summary>
	/// A segment must start and end alphanumeric; inside, the punctuation real package names use
	/// (<c>.</c>, <c>_</c>, <c>-</c>) is allowed. Anything else — spaces above all, which several
	/// forum authors have in their names — is not, and importers map it to a hyphen.
	/// </summary>
	public static bool IsValidSegment(string? s)
	{
		if (string.IsNullOrEmpty(s) || s.Length > 64)
			return false;

		if (!char.IsAsciiLetterOrDigit(s[0]) || !char.IsAsciiLetterOrDigit(s[^1]))
			return false;

		foreach (var c in s)
		{
			if (!char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or '.'))
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
			error = $"'{text}' is not a package id: expected 'Owner/Name'";
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
			error = $"'{owner}' is not a valid owner: letters, digits, and inner '.', '_' or '-' only";
			return false;
		}

		if (!IsValidSegment(name))
		{
			error = $"'{name}' is not a valid package name: letters, digits, and inner '.', '_' or '-' only";
			return false;
		}

		// An installed package is a directory next to a generated forwarder script named after it, so
		// a package called "Foo.ks" would want the same path as the forwarder for a package "Foo".
		foreach (var extension in (string[])[".ks", ".ahk", ".ah2"])
		{
			if (name.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
			{
				error = $"'{name}' may not end in '{extension}': a package name is not a file name, "
						+ $"and it would collide with the include written for a package called "
						+ $"'{name[..^extension.Length]}'";
				return false;
			}
		}

		id = new PackageId(owner, name);
		return true;
	}

	public static PackageId Parse(string text) =>
		TryParse(text, out var id, out var error) ? id.Value : throw new ArgumentException(error, nameof(text));

	/// <summary>The registry path this id owns, always forward-slashed and in the id's own casing.</summary>
	public string RegistryPath => $"packages/{Owner}/{Name}";

	public bool Equals(PackageId other) =>
		string.Equals(Owner, other.Owner, StringComparison.OrdinalIgnoreCase)
		&& string.Equals(Name, other.Name, StringComparison.OrdinalIgnoreCase);

	public override bool Equals(object? obj) => obj is PackageId other && Equals(other);

	public override int GetHashCode() =>
		HashCode.Combine(Owner.ToLowerInvariant(), Name.ToLowerInvariant());

	public static bool operator ==(PackageId left, PackageId right) => left.Equals(right);

	public static bool operator !=(PackageId left, PackageId right) => !left.Equals(right);

	public override string ToString() => $"{Owner}/{Name}";

	/// <summary>The case-folded form, for uniqueness checks and dictionary keys.</summary>
	public string ToComparisonKey() => $"{Owner}/{Name}".ToLowerInvariant();
}
