using Kpm.Model;
using Kpm.Packing;

namespace Kpm.Artifacts;

/// <summary>
/// The local artifact cache, addressed by content hash.
///
/// Because the key is the hash, an entry can never be stale and never needs invalidating: a
/// resolution that names a hash either finds those exact bytes locally or fetches them. This is
/// what makes an install from a lockfile work with no network at all.
/// </summary>
public sealed class ArtifactStore(HttpClient? http = null, string? cacheRoot = null)
{
	private readonly HttpClient http = http ?? Registry.RegistryClient.CreateDefaultClient();
	private readonly string cacheRoot = cacheRoot ?? KpmPaths.CacheRoot;

	public string PathFor(string sha256)
	{
		var hash = sha256.ToLowerInvariant();
		// Sharded by the first byte so a large cache does not become one directory with 10,000 entries.
		return Path.Combine(cacheRoot, "sha256", hash[..2], $"{hash}.kspkg");
	}

	public bool Contains(string sha256) => File.Exists(PathFor(sha256));

	/// <summary>
	/// Returns the artifact's bytes, downloading only if the cache lacks them. Sources are tried in
	/// order and their content is verified against the hash, so an untrusted mirror cannot change
	/// what gets installed — only whether the download succeeds.
	/// </summary>
	public async Task<byte[]> GetAsync(string sha256, IReadOnlyList<string> sources, string what,
									   CancellationToken ct = default)
	{
		var path = PathFor(sha256);

		if (File.Exists(path))
		{
			var cached = await File.ReadAllBytesAsync(path, ct);

			// A cache file that no longer hashes correctly means local corruption, not a bad source;
			// drop it and re-fetch rather than failing the install.
			try
			{
				Unpacker.VerifyHash(cached, sha256, $"cached {what}");
				return cached;
			}
			catch (InvalidDataException)
			{
				File.Delete(path);
			}
		}

		if (sources.Count == 0)
			throw new InvalidOperationException($"{what} is not cached and lists no download sources");

		var failures = new List<string>();

		foreach (var url in sources)
		{
			try
			{
				var bytes = await http.GetByteArrayAsync(url, ct);
				Unpacker.VerifyHash(bytes, sha256, $"{what} from {url}");
				await StoreAsync(sha256, bytes, ct);
				return bytes;
			}
			catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidDataException or IOException)
			{
				failures.Add($"{url}: {ex.Message}");
			}
		}

		throw new InvalidOperationException(
			$"could not download {what} from any source:\n  " + string.Join("\n  ", failures));
	}

	public Task<byte[]> GetAsync(ArtifactRef artifact, string what, CancellationToken ct = default) =>
		GetAsync(artifact.Sha256, artifact.Sources, what, ct);

	public async Task StoreAsync(string sha256, byte[] bytes, CancellationToken ct = default)
	{
		var path = PathFor(sha256);
		_ = KpmPaths.EnsureDirectory(Path.GetDirectoryName(path)!);
		var temporary = $"{path}.{Environment.ProcessId}.tmp";
		await File.WriteAllBytesAsync(temporary, bytes, ct);

		// Move into place only once the bytes are complete, so a killed process cannot leave a
		// truncated file under a name that claims to be a verified artifact.
		try
		{
			File.Move(temporary, path, overwrite: true);
		}
		catch (IOException) when (File.Exists(path))
		{
			File.Delete(temporary);
		}
	}

	public void Clear()
	{
		var root = Path.Combine(cacheRoot, "sha256");

		if (Directory.Exists(root))
			Directory.Delete(root, recursive: true);
	}
}
