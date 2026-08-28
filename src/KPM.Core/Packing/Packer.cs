using System.Security.Cryptography;
using System.Text;
using Kpm.Model;

namespace Kpm.Packing;

/// <summary>What the archive's own <c>package.json</c> carries: enough to identify and use the package offline.</summary>
public sealed class EmbeddedManifest
{
	public int Schema { get; set; } = 1;
	public string Name { get; set; } = "";
	public string Owner { get; set; } = "";
	public string Version { get; set; } = "";
	public int Revision { get; set; } = 1;
	public string Entry { get; set; } = "";
	public string Platform { get; set; } = Platforms.Any;
	public Dictionary<string, string> Engines { get; set; } = [];
	public List<string> Capabilities { get; set; } = [];
	public Dictionary<string, string> Dependencies { get; set; } = [];
}

public sealed record PackResult(string FileName, byte[] Bytes, string Sha256, IReadOnlyList<string> Paths)
{
	public long Size => Bytes.LongLength;
}

/// <summary>
/// Builds a <c>.kspkg</c> from a package directory. Packing is a pure function of the files it
/// selects and the metadata it embeds: the registry re-runs it on a submission and requires the
/// same SHA-256, which is what lets an artifact be verified without trusting whoever built it.
/// </summary>
public static class Packer
{
	private static readonly string[] documentPrefixes = ["LICENSE", "LICENCE", "README", "CHANGELOG", "NOTICE"];

	/// <summary>Files that describe the package to the registry rather than belonging to it.</summary>
	private static bool IsRegistryFile(string name) =>
		name is "port.json" or "package.json" or "kpm.json" or "kpm.lock.json";

	public static PackResult Pack(string packageDirectory, EmbeddedManifest manifest, string platform)
	{
		ArgumentException.ThrowIfNullOrEmpty(packageDirectory);
		ArgumentNullException.ThrowIfNull(manifest);

		if (!Platforms.IsValid(platform))
			throw new ArgumentException($"'{platform}' is not a known platform", nameof(platform));

		if (!Directory.Exists(packageDirectory))
			throw new DirectoryNotFoundException($"package directory not found: {packageDirectory}");

		var files = Collect(packageDirectory, platform);

		if (files.Count == 0)
			throw new InvalidOperationException($"{packageDirectory} has no src/ files to package");

		manifest.Platform = platform;
		var entries = new List<DeterministicZip.Entry>(files.Count + 1)
		{
			new("package.json", Encoding.UTF8.GetBytes(ManifestJson.Write(manifest)))
		};

		foreach (var (archivePath, absolute) in files)
			entries.Add(new DeterministicZip.Entry(archivePath, File.ReadAllBytes(absolute)));

		// Sorting here rather than at each call site is what makes the archive independent of the
		// order the filesystem happened to enumerate.
		entries.Sort(static (a, b) => string.CompareOrdinal(a.Path, b.Path));
		var duplicate = entries.Select(e => e.Path)
					   .GroupBy(p => p, StringComparer.OrdinalIgnoreCase)
					   .FirstOrDefault(g => g.Count() > 1);

		if (duplicate is not null)
			throw new InvalidOperationException(
				$"'{duplicate.Key}' appears more than once when compared case-insensitively; "
				+ "such a package cannot be extracted on Windows or macOS");

		var bytes = DeterministicZip.Write(entries);
		var sha = Convert.ToHexStringLower(SHA256.HashData(bytes));
		var fileName = $"{manifest.Name}-{manifest.Version}-r{manifest.Revision}-{platform}.kspkg";
		return new PackResult(fileName, bytes, sha, entries.Select(e => e.Path).ToArray());
	}

	/// <summary>
	/// The files a package contributes to one platform's artifact: everything in <c>src/</c>, plus
	/// this platform's own <c>platform/&lt;rid&gt;/</c> files, its top-level documents, and its
	/// native payload.
	///
	/// A file's location says exactly which artifacts contain it: <c>src/</c> means every one,
	/// <c>platform/&lt;rid&gt;/</c> means that one only. The two may not both provide the same
	/// archive path — that is an error rather than a silent win for either, because a reader
	/// looking at <c>src/Engine.ahk</c> would otherwise have no way to tell it is replaced on Linux.
	///
	/// This is how a package ships different code per platform while every file stays plain,
	/// portable script. The alternative — one source with compile-time platform branches — is a
	/// Keysharp-only construct that AutoHotkey rejects outright, so a package using it could never
	/// claim both engines.
	/// </summary>
	private static Dictionary<string, string> Collect(string root, string platform)
	{
		var files = new Dictionary<string, string>(StringComparer.Ordinal);
		var sourceDirectory = Path.Combine(root, "src");

		if (Directory.Exists(sourceDirectory))
			AddTree(sourceDirectory, "src/", files, "src");

		// Placed inside src/ so a package's own includes and the manifest's entry path never vary by
		// platform — only which artifact carries the file does.
		var specific = Path.Combine(root, "platform", platform);

		if (Directory.Exists(specific))
			AddTree(specific, "src/", files, $"platform/{platform}");

		if (platform != Platforms.Any)
		{
			var nativeDirectory = Path.Combine(root, "native", platform);

			if (Directory.Exists(nativeDirectory))
				AddTree(nativeDirectory, $"native/{platform}/", files, $"native/{platform}");
		}

		foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.TopDirectoryOnly))
		{
			var name = Path.GetFileName(file);

			if (IsRegistryFile(name) || name.StartsWith('.'))
				continue;

			if (documentPrefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
				files[name] = file;
		}

		return files;
	}

	private static void AddTree(string baseDirectory, string archivePrefix, Dictionary<string, string> into,
								string origin)
	{
		foreach (var file in Directory.EnumerateFiles(baseDirectory, "*", SearchOption.AllDirectories))
		{
			if (Path.GetFileName(file).StartsWith('.'))
				continue;

			var relative = Path.GetRelativePath(baseDirectory, file).Replace('\\', '/');
			var archivePath = archivePrefix + relative;
			ValidateArchivePath(archivePath);

			// A symlink's target is outside the archive's control, so what a consumer would extract
			// is not what the author reviewed. Packing the target's bytes silently would be worse.
			if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
				throw new InvalidOperationException($"'{archivePath}' is a symbolic link; packages must contain regular files");

			if (into.TryGetValue(archivePath, out var existing))
			{
				throw new InvalidOperationException(
					$"'{archivePath}' is provided by both '{Describe(existing, baseDirectory, relative, origin)}' and "
					+ $"'{origin}/{relative}'. A file belongs in src/ if every platform gets it, or in "
					+ "platform/<rid>/ if only one does — never both, so that where a file lives says which "
					+ "artifacts contain it.");
			}

			into[archivePath] = file;
		}
	}

	private static string Describe(string existingPath, string baseDirectory, string relative, string origin) =>
		existingPath.Replace('\\', '/').Contains("/src/", StringComparison.Ordinal) && origin != "src"
		? $"src/{relative}"
		: relative;

	/// <summary>The platforms a package directory ships platform-specific source for.</summary>
	public static IEnumerable<string> OverlayPlatforms(string packageDirectory)
	{
		var overlayRoot = Path.Combine(packageDirectory, "platform");

		if (!Directory.Exists(overlayRoot))
			return [];

		return Directory.EnumerateDirectories(overlayRoot).Select(Path.GetFileName).OfType<string>();
	}

	/// <summary>
	/// Rejects paths that would escape the extraction directory or cannot round-trip. Enforced when
	/// packing as well as extracting, so a malformed archive cannot be built in the first place.
	/// </summary>
	internal static void ValidateArchivePath(string path)
	{
		if (string.IsNullOrWhiteSpace(path))
			throw new InvalidOperationException("archive contains an empty path");

		if (path.Contains('\\'))
			throw new InvalidOperationException($"archive path '{path}' must use forward slashes");

		if (Path.IsPathRooted(path) || path.StartsWith('/') || (path.Length > 1 && path[1] == ':'))
			throw new InvalidOperationException($"archive path '{path}' must be relative");

		foreach (var segment in path.Split('/'))
		{
			if (segment is "" or "." or "..")
				throw new InvalidOperationException($"archive path '{path}' contains a '{segment}' segment");
		}
	}
}
