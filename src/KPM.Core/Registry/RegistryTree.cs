using Kpm.Model;
using Kpm.Packing;

namespace Kpm.Registry;

/// <summary>One package directory in the registry repository.</summary>
public sealed class RegistryPackage
{
	public required string Directory { get; init; }
	public required PackageManifest Package { get; init; }
	public required List<VersionManifest> Versions { get; init; }

	/// <summary>Present when the package's source is maintained in the registry itself (a "port").</summary>
	public PortManifest? Port { get; init; }

	public PackageId Id => PackageId.Parse($"{Package.Owner}/{Package.Name}");
	public bool IsSourceHosted => Port is not null;
}

/// <summary>
/// Reads the registry repository's file tree.
///
/// Only the registry's own tooling does this — CI, the index builder, the bot. Clients read the
/// built index instead, which is what lets this layout change without breaking installed clients.
/// </summary>
public static class RegistryTree
{
	public const string PackagesDirectory = "packages";

	public static async Task<List<RegistryPackage>> ReadAsync(string registryRoot, CancellationToken ct = default)
	{
		var packagesRoot = Path.Combine(registryRoot, PackagesDirectory);

		if (!Directory.Exists(packagesRoot))
			throw new DirectoryNotFoundException($"no {PackagesDirectory}/ directory under {registryRoot}");

		var packages = new List<RegistryPackage>();

		foreach (var ownerDirectory in Directory.EnumerateDirectories(packagesRoot).OrderBy(d => d, StringComparer.Ordinal))
		{
			foreach (var directory in Directory.EnumerateDirectories(ownerDirectory).OrderBy(d => d, StringComparer.Ordinal))
			{
				var packagePath = Path.Combine(directory, "package.json");

				if (!File.Exists(packagePath))
					continue;

				packages.Add(await ReadPackageAsync(directory, ct));
			}
		}

		return packages;
	}

	public static async Task<RegistryPackage> ReadPackageAsync(string directory, CancellationToken ct = default)
	{
		var package = await ManifestJson.ReadFileAsync<PackageManifest>(Path.Combine(directory, "package.json"), ct);
		var portPath = Path.Combine(directory, "port.json");
		var port = File.Exists(portPath) ? await ManifestJson.ReadFileAsync<PortManifest>(portPath, ct) : null;
		var versionsDirectory = Path.Combine(directory, "versions");
		var versions = new List<VersionManifest>();

		if (Directory.Exists(versionsDirectory))
		{
			foreach (var file in Directory.EnumerateFiles(versionsDirectory, "*.json").OrderBy(f => f, StringComparer.Ordinal))
				versions.Add(await ManifestJson.ReadFileAsync<VersionManifest>(file, ct));
		}

		return new RegistryPackage { Directory = directory, Package = package, Port = port, Versions = versions };
	}

	/// <summary>
	/// Builds the version manifest a source-hosted package's current source produces, filling in the
	/// artifact hashes by packing it. Port maintainers run this rather than writing hashes by hand,
	/// and CI runs the same code to check the result reproduces.
	/// </summary>
	public static VersionManifest BuildVersionManifest(RegistryPackage package, int revision,
													   Func<string, string, string>? artifactUrl = null)
	{
		var port = package.Port
				   ?? throw new InvalidOperationException($"{package.Id} has no port.json; only source-hosted packages can be packed");
		var manifest = new VersionManifest
		{
			Name = package.Package.Name,
			Owner = package.Package.Owner,
			Version = port.Version,
			Revision = revision,
			// Whole seconds: sub-second precision only makes a regenerated manifest differ from the
			// committed one in a way no reviewer cares about.
			Published = new DateTimeOffset(DateTime.UtcNow.Ticks / TimeSpan.TicksPerSecond * TimeSpan.TicksPerSecond,
										   TimeSpan.Zero),
			Entry = port.Entry,
			Engines = new Dictionary<string, string>(port.Engines),
			Platforms = [.. port.Platforms],
			Capabilities = [.. port.Capabilities],
			Dependencies = new Dictionary<string, string>(port.Dependencies),
			Source = new SourceRef
			{
				Kind = "ports",
				Repository = "keysharp-org/Packages",
				Path = $"{PackagesDirectory}/{package.Package.Owner}/{package.Package.Name}",
				Upstream = port.Upstream
			}
		};

		foreach (var platform in port.Platforms)
		{
			var embedded = new EmbeddedManifest
			{
				Name = package.Package.Name,
				Owner = package.Package.Owner,
				Version = port.Version,
				Revision = revision,
				Entry = port.Entry,
				Engines = new Dictionary<string, string>(port.Engines),
				Capabilities = [.. port.Capabilities],
				Dependencies = new Dictionary<string, string>(port.Dependencies)
			};
			var packed = Packer.Pack(package.Directory, embedded, platform);
			var reference = new ArtifactRef { Sha256 = packed.Sha256, Size = packed.Size };
			var url = artifactUrl?.Invoke(packed.FileName, $"{manifest.Version}-r{revision}");

			if (url is not null)
				reference.Sources.Add(url);

			manifest.Artifacts[platform] = reference;
		}

		return manifest;
	}

	/// <summary>The release tag an artifact is published under, and the URL that tag's asset gets.</summary>
	public static string ReleaseTag(PackageId id, string versionWithRevision) =>
		$"pkg/{id.Owner}/{id.Name}/{versionWithRevision}";

	public static string ReleaseAssetUrl(string repository, PackageId id, string versionWithRevision, string fileName) =>
		$"https://github.com/{repository}/releases/download/{ReleaseTag(id, versionWithRevision)}/{fileName}";
}
