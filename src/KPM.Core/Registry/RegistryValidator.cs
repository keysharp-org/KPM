using Kpm.Model;
using Kpm.Packing;
using Kpm.Resolution;
using Semver;

namespace Kpm.Registry;

public sealed record ValidationProblem(string Where, string Message)
{
	public override string ToString() => $"{Where}: {Message}";
}

/// <summary>
/// The rules a submission must satisfy before it can be merged.
///
/// These run in CI on every pull request, and the same code runs locally through
/// <c>kpm validate</c> so a contributor sees the same answer before pushing. Engine validation is
/// not here: it needs the engines installed and is driven by the workflow.
/// </summary>
public static class RegistryValidator
{
	public static async Task<List<ValidationProblem>> ValidateAsync(string registryRoot, CancellationToken ct = default)
	{
		var packages = await RegistryTree.ReadAsync(registryRoot, ct);
		var problems = new List<ValidationProblem>();

		foreach (var package in packages)
			problems.AddRange(ValidatePackage(package));

		problems.AddRange(ValidateDependencies(packages));
		return problems;
	}

	public static List<ValidationProblem> ValidatePackage(RegistryPackage package)
	{
		var problems = new List<ValidationProblem>();
		var where = Path.GetFileName(package.Directory);
		var manifest = package.Package;

		if (!PackageId.TryParse($"{manifest.Owner}/{manifest.Name}", out var id, out var idError))
		{
			problems.Add(new ValidationProblem(where, idError!));
			return problems;
		}

		where = id.Value.ToString();

		// The directory path is the identity, so a manifest that disagrees with it would make the
		// same package reachable under two names.
		var expected = Path.Combine(RegistryTree.PackagesDirectory, id.Value.Owner, id.Value.Name)
					   .Replace('\\', '/');

		if (!package.Directory.Replace('\\', '/').EndsWith(expected, StringComparison.Ordinal))
			problems.Add(new ValidationProblem(where, $"manifest says '{id}' but it lives in {package.Directory}"));

		if (!VersioningKind.IsValid(manifest.Versioning))
			problems.Add(new ValidationProblem(where, $"versioning must be 'upstream' or 'registry', not '{manifest.Versioning}'"));

		if (string.IsNullOrWhiteSpace(manifest.Description))
			problems.Add(new ValidationProblem(where, "description is empty"));

		foreach (var yanked in manifest.Yanked)
		{
			if (!PackageVersion.TryParse(yanked, out _, out var error))
				problems.Add(new ValidationProblem(where, $"yanked entry '{yanked}': {error}"));
		}

		foreach (var version in package.Versions)
			problems.AddRange(ValidateVersion(package, version, id.Value));

		problems.AddRange(ValidateRevisionSequence(package, id.Value));

		if (package.Port is not null)
			problems.AddRange(ValidatePort(package, id.Value));

		return problems;
	}

	private static IEnumerable<ValidationProblem> ValidateVersion(RegistryPackage package, VersionManifest version,
																  PackageId id)
	{
		var where = $"{id} {version.Version}-r{version.Revision}";

		if (version.Owner != id.Owner || version.Name != id.Name)
			yield return new ValidationProblem(where, $"version manifest claims '{version.Owner}/{version.Name}'");

		if (!SemVersion.TryParse(version.Version, SemVersionStyles.Strict, out _))
			yield return new ValidationProblem(where, $"'{version.Version}' is not a valid version");

		if (version.Revision < 1)
			yield return new ValidationProblem(where, "revision must be at least 1");

		if (version.Published == default)
			yield return new ValidationProblem(where, "published timestamp is missing");

		if (string.IsNullOrWhiteSpace(version.Entry))
			yield return new ValidationProblem(where, "entry is empty");

		if (version.Engines.Count == 0)
			yield return new ValidationProblem(where, "no engines declared; a release nothing can run is not installable");

		foreach (var (engine, range) in version.Engines)
		{
			if (!Engines.IsValid(engine))
				yield return new ValidationProblem(where, $"'{engine}' is not a known engine");

			if (!EngineRange.TryParse(range, out _, out var rangeError))
				yield return new ValidationProblem(where, $"engine '{engine}': {rangeError}");
		}

		foreach (var (dependency, range) in version.Dependencies)
		{
			if (!PackageId.TryParse(dependency, out _, out var dependencyError))
				yield return new ValidationProblem(where, $"dependency '{dependency}': {dependencyError}");

			if (!SemVersionRange.TryParseNpm(range, out _))
				yield return new ValidationProblem(where, $"dependency '{dependency}' has an unparseable range '{range}'");
		}

		if (version.Artifacts.Count == 0)
			yield return new ValidationProblem(where, "no artifacts");

		foreach (var (platform, artifact) in version.Artifacts)
		{
			if (!Platforms.IsValid(platform))
				yield return new ValidationProblem(where, $"'{platform}' is not a known platform");

			if (artifact.Sha256.Length != 64 || !artifact.Sha256.All(Uri.IsHexDigit))
				yield return new ValidationProblem(where, $"artifact '{platform}' has a malformed sha256");

			if (artifact.Size <= 0)
				yield return new ValidationProblem(where, $"artifact '{platform}' has no size");

			if (!version.Platforms.Contains(platform))
				yield return new ValidationProblem(where, $"artifact '{platform}' is not listed in platforms");
		}

		// The manifest's own file name is how the tree is indexed, so a mismatch would hide a release.
		var expectedFile = $"{version.Version}-r{version.Revision}.json";
		var actual = Path.Combine(package.Directory, "versions", expectedFile);

		if (!File.Exists(actual))
			yield return new ValidationProblem(where, $"expected this release to be in versions/{expectedFile}");
	}

	/// <summary>
	/// Revisions must run 1, 2, 3 with no gaps. A gap means a revision was deleted, which the
	/// registry's immutability rule forbids.
	/// </summary>
	private static IEnumerable<ValidationProblem> ValidateRevisionSequence(RegistryPackage package, PackageId id)
	{
		foreach (var group in package.Versions.GroupBy(v => v.Version, StringComparer.Ordinal))
		{
			var revisions = group.Select(v => v.Revision).OrderBy(r => r).ToList();

			for (var i = 0; i < revisions.Count; i++)
			{
				if (revisions[i] != i + 1)
				{
					yield return new ValidationProblem($"{id} {group.Key}",
													   $"revisions must start at 1 with no gaps, found {string.Join(", ", revisions)}");
					break;
				}
			}
		}
	}

	/// <summary>
	/// For a source-hosted package: the source in the repository must produce exactly the artifacts
	/// its manifests claim. This is the check that lets anyone verify an artifact without trusting
	/// whoever built it, and it only works because packing is deterministic.
	/// </summary>
	private static IEnumerable<ValidationProblem> ValidatePort(RegistryPackage package, PackageId id)
	{
		var port = package.Port!;
		var where = $"{id} port.json";

		if (!SemVersion.TryParse(port.Version, SemVersionStyles.Strict, out _))
		{
			yield return new ValidationProblem(where, $"'{port.Version}' is not a valid version");
			yield break;
		}

		if (string.IsNullOrWhiteSpace(port.Entry))
			yield return new ValidationProblem(where, "entry is empty");

		var declared = package.Versions.Where(v => v.Version == port.Version).ToList();

		if (declared.Count == 0)
		{
			yield return new ValidationProblem(where,
											   $"source is at version {port.Version} but versions/ has no manifest for it; "
											   + "a source change must ship with its release");
			yield break;
		}

		var newest = declared.MaxBy(v => v.Revision)!;
		var entryPath = Path.Combine(package.Directory, port.Entry.Replace('/', Path.DirectorySeparatorChar));

		if (!File.Exists(entryPath))
			yield return new ValidationProblem(where, $"entry '{port.Entry}' does not exist");

		foreach (var platform in port.Platforms)
		{
			if (!newest.Artifacts.TryGetValue(platform, out var expected))
			{
				yield return new ValidationProblem(where, $"no artifact recorded for declared platform '{platform}'");
				continue;
			}

			PackResult? packed = null;
			string? packError = null;

			try
			{
				packed = Packer.Pack(package.Directory, new EmbeddedManifest
				{
					Name = package.Package.Name,
					Owner = package.Package.Owner,
					Version = port.Version,
					Revision = newest.Revision,
					Entry = port.Entry,
					Engines = new Dictionary<string, string>(port.Engines),
					Capabilities = [.. port.Capabilities],
					Dependencies = new Dictionary<string, string>(port.Dependencies)
				}, platform);
			}
			catch (Exception ex) when (ex is InvalidOperationException or IOException)
			{
				packError = ex.Message;
			}

			if (packed is null)
			{
				yield return new ValidationProblem(where, $"packing '{platform}' failed: {packError}");
				continue;
			}

			if (!packed.Sha256.Equals(expected.Sha256, StringComparison.OrdinalIgnoreCase))
				yield return new ValidationProblem($"{id} {port.Version}-r{newest.Revision}",
												   $"the source in this repository packs to {packed.Sha256} for '{platform}', "
												   + $"but the manifest records {expected.Sha256}. "
												   + "Re-run 'kpm manifest' and commit the result.");
			else if (packed.Size != expected.Size)
				yield return new ValidationProblem($"{id} {port.Version}-r{newest.Revision}",
												   $"artifact '{platform}' size {expected.Size} should be {packed.Size}");
		}
	}

	/// <summary>Every dependency must name a package the registry actually has, at a version it has.</summary>
	private static IEnumerable<ValidationProblem> ValidateDependencies(List<RegistryPackage> packages)
	{
		var index = IndexBuilder.BuildIndex(packages);

		foreach (var package in packages)
		{
			foreach (var version in package.Versions)
			{
				foreach (var (dependency, range) in version.Dependencies)
				{
					if (!PackageId.TryParse(dependency, out var dependencyId, out _))
						continue;

					var target = index.Find(dependencyId.Value);

					if (target is null)
					{
						yield return new ValidationProblem($"{package.Id} {version.Version}-r{version.Revision}",
														   $"depends on '{dependency}', which is not in the registry");
						continue;
					}

					var installable = target.Installable().ToList();
					var prerelease = !RangeMatcher.HasStableRelease(installable.Select(v => v.Version));

					if (RangeMatcher.TryParse(range, out _)
						&& !installable.Any(v => RangeMatcher.Matches(range, v.Version, prerelease)))
					{
						yield return new ValidationProblem($"{package.Id} {version.Version}-r{version.Revision}",
														   $"depends on '{dependency}' {range}, which matches no installable release");
					}
				}
			}
		}
	}
}
