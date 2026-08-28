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

		foreach (var (relative, absolute) in files)
			entries.Add(new DeterministicZip.Entry(relative, File.ReadAllBytes(absolute)));

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
	/// The files a package contributes: its <c>src/</c> tree, its top-level documents, and — for a
	/// platform-specific build — that platform's native payload only.
	/// </summary>
	private static List<(string Relative, string Absolute)> Collect(string root, string platform)
	{
		var result = new List<(string, string)>();
		var srcDir = Path.Combine(root, "src");

		if (Directory.Exists(srcDir))
			AddTree(root, srcDir, result);

		foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.TopDirectoryOnly))
		{
			var name = Path.GetFileName(file);

			if (IsRegistryFile(name) || name.StartsWith('.'))
				continue;

			if (documentPrefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
				result.Add((name, file));
		}

		if (platform != Platforms.Any)
		{
			var nativeDir = Path.Combine(root, "native", platform);

			if (Directory.Exists(nativeDir))
				AddTree(root, nativeDir, result);
		}

		return result;
	}

	private static void AddTree(string root, string directory, List<(string, string)> into)
	{
		foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
		{
			var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
			ValidateArchivePath(relative);

			if (Path.GetFileName(file).StartsWith('.'))
				continue;

			// A symlink's target is outside the archive's control, so what a consumer would extract
			// is not what the author reviewed. Packing the target's bytes silently would be worse.
			if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
				throw new InvalidOperationException($"'{relative}' is a symbolic link; packages must contain regular files");

			into.Add((relative, file));
		}
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
