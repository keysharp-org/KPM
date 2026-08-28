using System.IO.Compression;
using System.Net;
using System.Text;
using Kpm.Model;

namespace Kpm.Registry;

public sealed record RegistrySource(string Name, IReadOnlyList<string> BaseUrls)
{
	/// <summary>
	/// The default registry, with the raw branch as a fallback for when Pages is down but git is not.
	///
	/// Moving this later costs only a client release: the index host is client configuration, and is
	/// never written into a lockfile — those pin artifact URLs and hashes. That is why starting on
	/// github.io does not tie the registry to GitHub the way a pinned artifact host would.
	/// </summary>
	public static RegistrySource Default { get; set; } = new("keysharp",
	[
		"https://keysharp-org.github.io/Packages",
		"https://raw.githubusercontent.com/keysharp-org/Packages/gh-pages"
	]);
}

public sealed record IndexFetchResult(RegistryIndex Index, bool FromCache, string? Warning);

/// <summary>
/// Fetches and caches the built registry index.
///
/// Deliberately plain HTTPS GETs of a static file: no API, no token, no rate limit. That is what
/// keeps installing independent of any one host's API being reachable, and it is why the client
/// never calls a code-hosting API even though the registry happens to live on one.
/// </summary>
public sealed class RegistryClient(HttpClient? http = null, RegistrySource? source = null)
{
	private readonly HttpClient http = http ?? CreateDefaultClient();
	private readonly RegistrySource source = source ?? RegistrySource.Default;

	public static HttpClient CreateDefaultClient() =>
		new(new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.All })
	{
		Timeout = TimeSpan.FromSeconds(60),
		DefaultRequestHeaders = { { "User-Agent", $"kpm/{typeof(RegistryClient).Assembly.GetName().Version}" } }
	};

	private string CacheDirectory => Path.Combine(KpmPaths.RegistryRoot, source.Name);
	private string IndexPath => Path.Combine(CacheDirectory, "index.json");
	private string ETagPath => Path.Combine(CacheDirectory, "index.etag");

	/// <summary>
	/// Returns the index, refreshing it from the network when possible and falling back to the last
	/// copy when not. A stale index is a usable index — everything except discovering newly
	/// published versions still works — so an unreachable registry is a warning, not an error.
	/// </summary>
	public async Task<IndexFetchResult> GetIndexAsync(bool refresh = true, CancellationToken ct = default)
	{
		if (!refresh && File.Exists(IndexPath))
			return new IndexFetchResult(await ReadCachedAsync(ct), true, null);

		var failures = new List<string>();

		foreach (var baseUrl in source.BaseUrls)
		{
			try
			{
				var result = await TryFetchAsync(baseUrl, ct);

				if (result is not null)
					return result;
			}
			catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
			{
				failures.Add($"{baseUrl}: {ex.Message}");
			}
		}

		if (File.Exists(IndexPath))
		{
			var age = DateTime.UtcNow - File.GetLastWriteTimeUtc(IndexPath);
			return new IndexFetchResult(await ReadCachedAsync(ct), true,
									    $"registry unreachable; using the index cached {Describe(age)} ago");
		}

		throw new InvalidOperationException(
			"cannot reach the registry and no cached index exists:\n  " + string.Join("\n  ", failures));
	}

	private async Task<IndexFetchResult?> TryFetchAsync(string baseUrl, CancellationToken ct)
	{
		var url = $"{baseUrl.TrimEnd('/')}/index.json.gz";
		using var request = new HttpRequestMessage(HttpMethod.Get, url);
		var etag = File.Exists(ETagPath) && File.Exists(IndexPath) ? await File.ReadAllTextAsync(ETagPath, ct) : null;

		if (!string.IsNullOrWhiteSpace(etag))
			request.Headers.TryAddWithoutValidation("If-None-Match", etag.Trim());

		using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

		if (response.StatusCode == HttpStatusCode.NotModified)
			return new IndexFetchResult(await ReadCachedAsync(ct), true, null);

		if (!response.IsSuccessStatusCode)
			return null;

		var payload = await response.Content.ReadAsByteArrayAsync(ct);
		var json = Decompress(payload);
		var index = ManifestJson.Read<RegistryIndex>(json);
		index.EnsureSupported();
		_ = KpmPaths.EnsureDirectory(CacheDirectory);
		await File.WriteAllTextAsync(IndexPath, json, ct);

		if (response.Headers.ETag is { Tag: var tag })
			await File.WriteAllTextAsync(ETagPath, tag, ct);
		else if (File.Exists(ETagPath))
			File.Delete(ETagPath);

		return new IndexFetchResult(index, false, null);
	}

	/// <summary>
	/// The index is served gzipped, but a mirror may transparently decode it (or hold it unpacked),
	/// so accept either rather than failing on a well-formed document.
	/// </summary>
	private static string Decompress(byte[] payload)
	{
		if (payload.Length >= 2 && payload[0] == 0x1F && payload[1] == 0x8B)
		{
			using var input = new MemoryStream(payload, writable: false);
			using var gzip = new GZipStream(input, CompressionMode.Decompress);
			using var reader = new StreamReader(gzip, Encoding.UTF8);
			return reader.ReadToEnd();
		}

		return Encoding.UTF8.GetString(payload);
	}

	private async Task<RegistryIndex> ReadCachedAsync(CancellationToken ct)
	{
		var index = ManifestJson.Read<RegistryIndex>(await File.ReadAllTextAsync(IndexPath, ct));
		index.EnsureSupported();
		return index;
	}

	private static string Describe(TimeSpan age) =>
		age.TotalMinutes < 60 ? $"{(int)age.TotalMinutes} minute(s)"
		: age.TotalHours < 48 ? $"{(int)age.TotalHours} hour(s)"
		: $"{(int)age.TotalDays} day(s)";
}
