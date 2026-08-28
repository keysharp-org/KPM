using System.IO.Compression;
using System.Text;
using Kpm.Model;

namespace Kpm.Registry;

/// <summary>A catalog row: what a browser or GUI shows without needing the resolver's data.</summary>
public sealed class CatalogEntry
{
	public string Id { get; set; } = "";
	public string Name { get; set; } = "";
	public string Owner { get; set; } = "";
	[System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
	public List<string>? Authors { get; set; }
	public string? DerivedFrom { get; set; }
	public string? DisplayName { get; set; }
	public string Description { get; set; } = "";
	public string Version { get; set; } = "";
	public string? License { get; set; }
	public string? Homepage { get; set; }
	public List<string> Categories { get; set; } = [];
	public List<string> Engines { get; set; } = [];
	public List<string> Platforms { get; set; } = [];
	public List<string> Capabilities { get; set; } = [];
	public DateTimeOffset Published { get; set; }
}

public sealed class Catalog
{
	public int Schema { get; set; } = 1;
	public DateTimeOffset Generated { get; set; }
	public List<CatalogEntry> Packages { get; set; } = [];
}

/// <summary>
/// Compiles the registry's file tree into the two documents clients and GUIs actually download.
/// Everything a client needs is in these, so the repository layout stays an implementation detail.
/// </summary>
public static class IndexBuilder
{
	public static RegistryIndex BuildIndex(IEnumerable<RegistryPackage> packages) => new()
	{
		Generated = DateTimeOffset.UtcNow,
		Packages = packages
		.Where(p => p.Versions.Count > 0)
		.OrderBy(p => p.Id.ToString(), StringComparer.Ordinal)
		.Select(p => new IndexedPackage
		{
			Package = p.Package,
			Versions = [.. p.Versions.OrderBy(v => v.Release)]
		})
		.ToList()
	};

	public static Catalog BuildCatalog(RegistryIndex index)
	{
		var catalog = new Catalog { Generated = index.Generated };

		foreach (var package in index.Packages)
		{
			var newest = package.Installable().MaxBy(v => v.Release);

			if (newest is null)
				continue;

			catalog.Packages.Add(new CatalogEntry
			{
				Id = $"{package.Package.Owner}/{package.Package.Name}",
				Name = package.Package.Name,
				Owner = package.Package.Owner,
				Authors = package.Package.Authors,
				DerivedFrom = package.Package.DerivedFrom,
				DisplayName = package.Package.DisplayName,
				Description = package.Package.Description,
				Version = newest.Version,
				License = package.Package.License,
				Homepage = package.Package.Homepage,
				Categories = package.Package.Categories,
				Engines = [.. newest.Engines.Keys.OrderBy(k => k, StringComparer.Ordinal)],
				Platforms = newest.Platforms,
				Capabilities = newest.Capabilities,
				Published = newest.Published
			});
		}

		return catalog;
	}

	/// <summary>
	/// Writes the published artifacts. The index is gzipped because it is the file every client
	/// downloads and it compresses by roughly an order of magnitude; the catalog is left plain so a
	/// page or a script can read it with no decoding step.
	/// </summary>
	public static async Task WriteAsync(string outputDirectory, RegistryIndex index, Catalog catalog,
										CancellationToken ct = default)
	{
		_ = Directory.CreateDirectory(outputDirectory);
		var json = ManifestJson.Write(index);
		var path = Path.Combine(outputDirectory, "index.json.gz");

		await using (var file = File.Create(path))
		await using (var gzip = new GZipStream(file, CompressionLevel.Optimal))
			await gzip.WriteAsync(Encoding.UTF8.GetBytes(json), ct);

		await File.WriteAllTextAsync(Path.Combine(outputDirectory, "catalog.json"), ManifestJson.Write(catalog), ct);
	}
}
