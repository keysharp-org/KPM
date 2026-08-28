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

	public IndexedPackage? Find(PackageId id)
	{
		lookup ??= Packages.ToDictionary(p => $"{p.Package.Owner}/{p.Package.Name}", StringComparer.Ordinal);
		return lookup.GetValueOrDefault(id.ToString());
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
