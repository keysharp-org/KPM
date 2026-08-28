using Kpm.Artifacts;
using Kpm.Install;
using Kpm.Model;
using Kpm.Registry;
using Kpm.Resolution;

namespace Kpm;

/// <summary>
/// The operations a front end performs, in one place.
///
/// The CLI is a thin shell over this and the GUI is meant to be another, so anything a user can do
/// belongs here rather than in a command handler — a behaviour implemented in the CLI would
/// otherwise have to be reimplemented, and would drift.
/// </summary>
public sealed class KpmService(RegistryClient? registry = null, ArtifactStore? store = null)
{
	private readonly RegistryClient registry = registry ?? new RegistryClient();
	private readonly ArtifactStore store = store ?? new ArtifactStore();

	public Task<IndexFetchResult> GetIndexAsync(bool refresh = true, CancellationToken ct = default) =>
		registry.GetIndexAsync(refresh, ct);

	/// <summary>Resolves the project's manifest and installs the result, rewriting the lockfile.</summary>
	public async Task<InstallReport> UpdateAsync(Project project, ResolveContext context, bool refresh = true,
												 CancellationToken ct = default)
	{
		var fetch = await registry.GetIndexAsync(refresh, ct);
		var resolution = new Resolver(fetch.Index).Resolve(project.Manifest.Dependencies, context);
		var installed = await new Installer(store).InstallAsync(resolution, project.Directory, ct);
		await project.SaveLockAsync(ToLockFile(resolution), ct);
		var warnings = new List<string?> { fetch.Warning, DescribeNameCollisions(resolution) };
		return new InstallReport(installed, resolution, string.Join("\n", warnings.Where(w => w is not null)) is { Length: > 0 } text ? text : null);
	}

	/// <summary>
	/// Installs exactly what the lockfile names. Never consults the registry, so this is the path
	/// that works offline and the one that reproduces a previous install byte for byte.
	/// </summary>
	public async Task<InstallReport> InstallLockedAsync(Project project, CancellationToken ct = default)
	{
		if (project.Lock is null)
			throw new InvalidOperationException($"no {Project.LockFileName}; run 'kpm update' first");

		var installed = await new Installer(store).InstallAsync(project.Lock, project.Directory, ct);
		return new InstallReport(installed, null, null);
	}

	/// <summary>
	/// Warns when two resolved packages share a name.
	///
	/// The registry is namespaced but the language is not: <c>Descolada/OCR</c> and
	/// <c>Keysharp/OCR</c> both define a global <c>OCR</c> class, and including both is a
	/// redefinition error no package manager can prevent. The collision is worth naming at install
	/// time, because the error it eventually causes points at the script, not at the two packages
	/// that disagree.
	/// </summary>
	private static string? DescribeNameCollisions(DependencyResolution resolution)
	{
		var collisions = resolution.Packages
						 .GroupBy(p => p.Id.Name, StringComparer.OrdinalIgnoreCase)
						 .Where(g => g.Count() > 1)
						 .Select(g => $"{string.Join(" and ", g.Select(p => p.Id))} are both installed and both "
									  + $"provide '{g.Key}'; including both will collide")
						 .ToList();
		return collisions.Count > 0 ? string.Join("\n", collisions) : null;
	}

	public static LockFile ToLockFile(DependencyResolution resolution) => new()
	{
		Packages = resolution.Packages.Select(p => new LockedPackage
		{
			Id = p.Id.ToString(),
			Version = p.Manifest.Version,
			Revision = p.Manifest.Revision,
			Platform = p.Platform,
			Sha256 = p.Artifact.Sha256,
			Size = p.Artifact.Size,
			Sources = p.Artifact.Sources,
			SourceCommit = p.Manifest.Source?.Commit,
			Entry = p.Manifest.Entry
		}).ToList()
	};

	/// <summary>
	/// Downloads every artifact the index names into the local cache: the offline story. A machine
	/// that has run this can install anything in the registry with no network.
	/// </summary>
	public async Task<int> MirrorAsync(IProgress<string>? progress = null, CancellationToken ct = default)
	{
		var fetch = await registry.GetIndexAsync(true, ct);
		var count = 0;

		foreach (var package in fetch.Index.Packages)
		{
			foreach (var version in package.Versions)
			{
				foreach (var (platform, artifact) in version.Artifacts)
				{
					if (store.Contains(artifact.Sha256))
						continue;

					progress?.Report($"{package.Package.Owner}/{package.Package.Name} {version.Version}-r{version.Revision} ({platform})");
					_ = await store.GetAsync(artifact, $"{package.Package.Name} {version.Version}", ct);
					count++;
				}
			}
		}

		return count;
	}
}

public sealed record InstallReport(IReadOnlyList<InstalledPackage> Installed, DependencyResolution? Resolution, string? Warning);
