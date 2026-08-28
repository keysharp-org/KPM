using Kpm.Model;

namespace Kpm;

/// <summary>
/// A consumer's project: its <c>kpm.json</c> and, once resolved, its <c>kpm.lock.json</c>.
///
/// The split is the usual one. The manifest holds ranges a human wrote and wants to keep; the
/// lockfile holds the exact releases a resolve settled on, so a rebuild installs what the last
/// resolve chose rather than whatever is newest today.
/// </summary>
public sealed class Project
{
	public const string ManifestFileName = "kpm.json";
	public const string LockFileName = "kpm.lock.json";

	public string Directory { get; }
	public ProjectManifest Manifest { get; private set; }
	public LockFile? Lock { get; private set; }

	public string ManifestPath => Path.Combine(Directory, ManifestFileName);
	public string LockPath => Path.Combine(Directory, LockFileName);
	public bool HasManifest => File.Exists(ManifestPath);

	private Project(string directory, ProjectManifest manifest, LockFile? lockFile)
	{
		Directory = directory;
		Manifest = manifest;
		Lock = lockFile;
	}

	public static async Task<Project> LoadAsync(string directory, CancellationToken ct = default)
	{
		directory = Path.GetFullPath(directory);
		var manifestPath = Path.Combine(directory, ManifestFileName);
		var lockPath = Path.Combine(directory, LockFileName);
		var manifest = File.Exists(manifestPath)
					   ? await ManifestJson.ReadFileAsync<ProjectManifest>(manifestPath, ct)
					   : new ProjectManifest();
		var lockFile = File.Exists(lockPath)
					   ? await ManifestJson.ReadFileAsync<LockFile>(lockPath, ct)
					   : null;
		return new Project(directory, manifest, lockFile);
	}

	/// <summary>
	/// Finds the project a path belongs to by walking up to the nearest <c>kpm.json</c>, so commands
	/// work from a subdirectory. Falls back to the starting directory, where a first
	/// <c>kpm add</c> creates one.
	/// </summary>
	public static string FindRoot(string startingDirectory)
	{
		var directory = new DirectoryInfo(Path.GetFullPath(startingDirectory));

		for (var current = directory; current is not null; current = current.Parent)
		{
			if (File.Exists(Path.Combine(current.FullName, ManifestFileName)))
				return current.FullName;
		}

		return directory.FullName;
	}

	public Task SaveManifestAsync(CancellationToken ct = default) =>
		ManifestJson.WriteFileAsync(ManifestPath, Manifest, ct);

	public async Task SaveLockAsync(LockFile lockFile, CancellationToken ct = default)
	{
		Lock = lockFile;
		await ManifestJson.WriteFileAsync(LockPath, lockFile, ct);
	}

	public void SetDependency(PackageId id, string range) => Manifest.Dependencies[id.ToString()] = range;

	public bool RemoveDependency(PackageId id) => Manifest.Dependencies.Remove(id.ToString());
}
