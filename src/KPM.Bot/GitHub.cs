using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Kpm.Bot;

public sealed record GitTag(string Name, string Commit);
public sealed record GitCommit(string Sha, DateTimeOffset Date, string Message);
public sealed record TreeEntry(string Path, string Type, long Size);

/// <summary>
/// The slice of the GitHub API the importer needs.
///
/// This runs server-side with a token, which is why it may use the API at all — the package
/// manager's own install path deliberately never does, so a user never hits a rate limit or needs
/// credentials.
/// </summary>
public sealed class GitHub : IDisposable
{
	private readonly HttpClient http;
	private readonly string? token;

	public GitHub(string? token = null)
	{
		this.token = token
					 ?? Environment.GetEnvironmentVariable("GITHUB_TOKEN")
					 ?? Environment.GetEnvironmentVariable("GH_TOKEN");
		http = new HttpClient(new RedirectAuthHandler(this.token)
		{
			InnerHandler = new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.All }
		})
		{
			Timeout = TimeSpan.FromMinutes(2),
			BaseAddress = new Uri("https://api.github.com/")
		};
		http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("kpm-bot", "0.1"));
		http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

		if (!string.IsNullOrEmpty(this.token))
			http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", this.token);
	}

	/// <summary>
	/// Re-attaches credentials when GitHub redirects an API request to a different host.
	///
	/// .NET strips the Authorization header on a cross-host redirect, which is the right default and
	/// exactly wrong here: api.github.com hands archive and blob downloads off to codeload and
	/// objects.githubusercontent.com, and without the header those become 404s on private content.
	/// Re-attaching is limited to GitHub's own hosts so a redirect elsewhere never leaks the token.
	/// </summary>
	private sealed class RedirectAuthHandler(string? token) : DelegatingHandler
	{
		private static readonly string[] allowed =
		[
			"api.github.com", "github.com", "codeload.github.com",
			"objects.githubusercontent.com", "raw.githubusercontent.com"
		];

		protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
		{
			var response = await base.SendAsync(request, ct);

			if (string.IsNullOrEmpty(token) || request.Headers.Authorization is not null)
				return response;

			var host = response.RequestMessage?.RequestUri?.Host;

			if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.NotFound
				&& host is not null && allowed.Contains(host, StringComparer.OrdinalIgnoreCase))
			{
				using var retry = new HttpRequestMessage(request.Method, response.RequestMessage!.RequestUri);
				retry.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
				retry.Headers.UserAgent.Add(new ProductInfoHeaderValue("kpm-bot", "0.1"));
				var second = await base.SendAsync(retry, ct);

				if (second.IsSuccessStatusCode)
				{
					response.Dispose();
					return second;
				}

				second.Dispose();
			}

			return response;
		}
	}

	private async Task<JsonDocument?> GetJsonAsync(string url, CancellationToken ct)
	{
		using var response = await http.GetAsync(url, ct);

		// Exhausting the rate limit must not look like "this repository has no tags": that would
		// turn a throttled run into a registry full of packages silently missing their history.
		if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests
			&& response.Headers.TryGetValues("x-ratelimit-remaining", out var remaining)
			&& remaining.FirstOrDefault() == "0")
		{
			var reset = response.Headers.TryGetValues("x-ratelimit-reset", out var values)
						&& long.TryParse(values.FirstOrDefault(), out var epoch)
						? DateTimeOffset.FromUnixTimeSeconds(epoch).ToLocalTime().ToString("HH:mm")
						: "shortly";
			throw new HttpRequestException(
				$"GitHub rate limit exhausted (resets at {reset}). "
				+ (string.IsNullOrEmpty(token)
				   ? "Set GITHUB_TOKEN or pass --token: unauthenticated requests are capped at 60 per hour."
				   : "Wait for the reset, or reduce --max-versions."));
		}

		if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden)
			return null;

		if (!response.IsSuccessStatusCode)
			throw new HttpRequestException($"{url} returned {(int)response.StatusCode}");

		return await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
	}

	public async Task<List<GitTag>> GetTagsAsync(string repository, CancellationToken ct = default)
	{
		var tags = new List<GitTag>();

		for (var page = 1; page <= 5; page++)   // 500 tags is far past what any of this corpus has
		{
			using var document = await GetJsonAsync($"repos/{repository}/tags?per_page=100&page={page}", ct);

			if (document is null)
				return tags;

			var count = 0;

			foreach (var element in document.RootElement.EnumerateArray())
			{
				count++;
				tags.Add(new GitTag(element.GetProperty("name").GetString() ?? "",
									element.GetProperty("commit").GetProperty("sha").GetString() ?? ""));
			}

			if (count < 100)
				break;
		}

		return tags;
	}

	/// <summary>Commits touching one path, newest first — the raw material for a synthesized 0.x history.</summary>
	public async Task<List<GitCommit>> GetCommitsAsync(string repository, string path, CancellationToken ct = default)
	{
		var commits = new List<GitCommit>();
		using var document = await GetJsonAsync(
								 $"repos/{repository}/commits?path={Uri.EscapeDataString(path)}&per_page=100", ct);

		if (document is null)
			return commits;

		foreach (var element in document.RootElement.EnumerateArray())
		{
			var commit = element.GetProperty("commit");
			commits.Add(new GitCommit(
							element.GetProperty("sha").GetString() ?? "",
							commit.GetProperty("committer").GetProperty("date").GetDateTimeOffset(),
							commit.GetProperty("message").GetString() ?? ""));
		}

		return commits;
	}

	public async Task<GitCommit?> GetLatestCommitAsync(string repository, string reference, CancellationToken ct = default)
	{
		using var document = await GetJsonAsync($"repos/{repository}/commits/{reference}", ct);

		if (document is null)
			return null;

		var commit = document.RootElement.GetProperty("commit");
		return new GitCommit(document.RootElement.GetProperty("sha").GetString() ?? "",
							 commit.GetProperty("committer").GetProperty("date").GetDateTimeOffset(),
							 commit.GetProperty("message").GetString() ?? "");
	}

	/// <summary>
	/// The whole file list at a commit, used to expand glob patterns.
	///
	/// GitHub caps a recursive tree and sets <c>truncated</c> when it does — which a repository the
	/// size of ScriptHub hits. The flag is returned rather than swallowed because a truncated tree
	/// silently loses files, and a caller matching globs against it would drop them without noticing.
	/// </summary>
	public async Task<(List<TreeEntry> Entries, bool Truncated)> GetTreeAsync(string repository, string reference,
																			  CancellationToken ct = default)
	{
		using var document = await GetJsonAsync($"repos/{repository}/git/trees/{reference}?recursive=1", ct);

		if (document is null)
			return ([], false);

		var entries = new List<TreeEntry>();

		foreach (var element in document.RootElement.GetProperty("tree").EnumerateArray())
		{
			entries.Add(new TreeEntry(
							element.GetProperty("path").GetString() ?? "",
							element.GetProperty("type").GetString() ?? "",
							element.TryGetProperty("size", out var size) ? size.GetInt64() : 0));
		}

		var truncated = document.RootElement.TryGetProperty("truncated", out var flag) && flag.GetBoolean();
		return (entries, truncated);
	}

	public async Task<byte[]?> GetFileAsync(string repository, string reference, string path, CancellationToken ct = default)
	{
		// Segment by segment: several authors in this corpus have spaces in their names, and the
		// separators must stay separators while the names get encoded.
		var encoded = string.Join('/', path.Split('/').Select(Uri.EscapeDataString));
		var url = $"https://raw.githubusercontent.com/{repository}/{reference}/{encoded}";
		using var response = await http.GetAsync(url, ct);
		return response.IsSuccessStatusCode ? await response.Content.ReadAsByteArrayAsync(ct) : null;
	}

	public void Dispose() => http.Dispose();
}
