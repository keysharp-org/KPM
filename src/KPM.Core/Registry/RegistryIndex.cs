using Kpm.Model;

namespace Kpm.Registry;

/// <summary>
/// The whole registry in one document: what the client downloads and resolves against. Clients read
/// only this built index, never the registry repository's file tree, so the repository's layout can
/// change without breaking installed clients.
/// </summary>
public sealed class RegistryIndex
{
	public int Schema { get; set; } = 1;
	public DateTimeOffset Generated { get; set; }
	public List<IndexedPackage> Packages { get; set; } = [];

	private Dictionary<string, IndexedPackage>? lookup;

	/// <summary>
	/// Finds a package by id, ignoring case. Ids keep their author's casing, but nobody should have
	/// to reproduce it to install something.
	/// </summary>
	public IndexedPackage? Find(PackageId id)
	{
		lookup ??= Packages
				   .GroupBy(p => $"{p.Package.Owner}/{p.Package.Name}".ToLowerInvariant(), StringComparer.Ordinal)
				   .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
		return lookup.GetValueOrDefault(id.ToComparisonKey());
	}

	/// <summary>
	/// Resolves a bare package name, for the convenience form <c>kpm add findtext</c>. Returns null
	/// when nothing matches and throws when several do: guessing between two packages that share a
	/// name is exactly the mistake a namespaced registry exists to prevent.
	/// </summary>
	public IndexedPackage? FindByName(string name)
	{
		var matches = Packages
					  .Where(p => string.Equals(p.Package.Name, name, StringComparison.OrdinalIgnoreCase))
					  .ToList();

		if (matches.Count > 1)
		{
			var candidates = matches.Select(m => $"{m.Package.Owner}/{m.Package.Name}").OrderBy(c => c, StringComparer.Ordinal);
			throw new InvalidOperationException(
				$"'{name}' is ambiguous; name the owner: {string.Join(", ", candidates)}");
		}

		return matches.Count == 1 ? matches[0] : null;
	}

	/// <summary>Every package whose id or description mentions <paramref name="text"/>.</summary>
	public IEnumerable<IndexedPackage> Search(string text)
	{
		if (string.IsNullOrWhiteSpace(text))
			return Packages;

		return Packages.Where(p =>
			p.Package.Name.Contains(text, StringComparison.OrdinalIgnoreCase)
			|| p.Package.Owner.Contains(text, StringComparison.OrdinalIgnoreCase)
			|| (p.Package.DisplayName?.Contains(text, StringComparison.OrdinalIgnoreCase) ?? false)
			|| p.Package.Description.Contains(text, StringComparison.OrdinalIgnoreCase)
			|| p.Package.Categories.Any(c => c.Contains(text, StringComparison.OrdinalIgnoreCase)));
	}
}

public sealed class IndexedPackage
{
	public PackageManifest Package { get; set; } = new();
	public List<VersionManifest> Versions { get; set; } = [];

	public bool IsYanked(PackageVersion version) =>
		Package.Yanked.Contains(version.ToString(), StringComparer.OrdinalIgnoreCase);

	/// <summary>
	/// Releases eligible for a fresh resolve: not yanked, and only the highest revision of each
	/// version — earlier revisions of the same version are superseded packagings of one release.
	/// </summary>
	public IEnumerable<VersionManifest> Installable() =>
		Versions.Where(v => !IsYanked(v.Release))
		.GroupBy(v => v.Version, StringComparer.Ordinal)
		.Select(g => g.MaxBy(v => v.Revision)!);
}
