using Kpm.Model;

namespace Kpm.Registry;

/// <summary>
/// The whole registry in one document: what the client downloads and resolves against. Clients read
/// only this built index, never the registry repository's file tree, so the repository's layout can
/// change without breaking installed clients.
/// </summary>
public sealed class RegistryIndex
{
	/// <summary>The highest index schema this build understands.</summary>
	public const int SupportedSchema = 1;

	public int Schema { get; set; } = 1;
	public DateTimeOffset Generated { get; set; }
	public List<IndexedPackage> Packages { get; set; } = [];

	/// <summary>
	/// Refuses an index written to a newer schema.
	///
	/// Without this an old client would parse a future index with today's rules, ignore whatever it
	/// did not recognise, and resolve confidently against a half-understood document. Failing with
	/// "update kpm" is the only honest answer, and it is only possible because the version is
	/// declared rather than inferred.
	/// </summary>
	public void EnsureSupported()
	{
		if (Schema > SupportedSchema)
		{
			throw new InvalidOperationException(
				$"this registry index uses schema {Schema}, and this version of kpm understands "
				+ $"{SupportedSchema}. Update kpm to install from it.");
		}
	}

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
	/// Resolves a bare package name, for the convenience form <c>kpm add FindText</c>.
	///
	/// Candidates are narrowed to those the given engine can actually install before ambiguity is
	/// considered, which is what makes the short form usable in practice: a library and a port of it
	/// share a name but never an engine, so each engine sees exactly one. Only a genuine clash —
	/// two packages this user could install, both called <c>FindText</c> — is reported, because
	/// guessing between those is the mistake a namespaced registry exists to prevent.
	/// </summary>
	public IndexedPackage? FindByName(string name, string? engine = null)
	{
		var matches = Packages
					  .Where(p => string.Equals(p.Package.Name, name, StringComparison.OrdinalIgnoreCase))
					  .ToList();

		if (matches.Count > 1 && engine is not null)
		{
			var installable = matches
							  .Where(p => p.Installable().Any(v => v.Engines.ContainsKey(engine)))
							  .ToList();

			// Only narrow when something survives: with nothing installable the caller should see
			// every candidate, so its error can say what exists and which engines they need.
			if (installable.Count > 0)
				matches = installable;
		}

		if (matches.Count > 1)
		{
			// What survives the engine filter is a real choice between things this user could
			// install — typically a library and a port of it, differing in platform reach and in
			// how battle-tested they are. Listing bare ids would make them look interchangeable, so
			// show what actually distinguishes them and let the user decide.
			var lines = matches
						.OrderBy(m => $"{m.Package.Owner}/{m.Package.Name}", StringComparer.Ordinal)
						.Select(m =>
			{
				var newest = m.Installable().MaxBy(v => v.Release);
				var platforms = newest is null ? "" : $"   platforms: {string.Join(", ", newest.Platforms)}";
				var derived = m.Package.DerivedFrom is { } from ? $"   (a port of {from})" : "";
				return $"  {m.Package.Owner}/{m.Package.Name} {newest?.Version}{platforms}{derived}";
			});
			throw new InvalidOperationException(
				$"'{name}' matches more than one package{(engine is null ? "" : $" for {engine}")}:\n"
				+ string.Join("\n", lines) + "\nName the one you want, owner included.");
		}

		return matches.Count == 1 ? matches[0] : null;
	}

	/// <summary>
	/// Packages declaring themselves derived from <paramref name="id"/> that support
	/// <paramref name="engine"/>.
	///
	/// A port is a separate package because it versions and releases independently of the original,
	/// but someone asking for the original on the wrong engine is asking for the library, not the
	/// packaging — so every path that turns them away should be able to name the alternative.
	/// </summary>
	public IReadOnlyList<PackageId> PortsOf(PackageId id, string engine) =>
		Packages
		.Where(p => p.Package.DerivedFrom is { } from
					&& PackageId.TryParse(from, out var parsed, out _)
					&& parsed.Value == id)
		.Where(p => p.Installable().Any(v => v.Engines.ContainsKey(engine)))
		.Select(p => PackageId.Parse($"{p.Package.Owner}/{p.Package.Name}"))
		.ToList();

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
