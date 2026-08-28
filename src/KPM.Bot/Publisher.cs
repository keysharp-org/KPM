using System.Diagnostics;
using Kpm.Model;
using Kpm.Registry;

namespace Kpm.Bot;

/// <summary>
/// Uploads packed artifacts to the registry's own GitHub releases.
///
/// Imported packages have no maintained source in the registry, so their artifacts cannot be
/// rebuilt from the tree the way a source-hosted package's can. They therefore have to exist before
/// their manifests are merged, or the recorded URL would point at nothing. Publishing is done
/// through the <c>gh</c> CLI rather than the API directly: it already handles asset upload,
/// retries and authentication, and this runs in the same environment that has it.
/// </summary>
public sealed class Publisher(string repository, bool dryRun = false)
{
	public async Task<int> PublishAsync(string registryRoot, string artifactRoot, IProgress<string>? progress = null,
										CancellationToken ct = default)
	{
		var packages = await RegistryTree.ReadAsync(registryRoot, ct);
		var published = 0;

		foreach (var package in packages)
		{
			foreach (var version in package.Versions.OrderBy(v => v.Release))
			{
				var release = $"{version.Version}-r{version.Revision}";
				var tag = RegistryTree.ReleaseTag(package.Id, release);
				var files = new List<string>();

				foreach (var platform in version.Artifacts.Keys)
				{
					var path = Path.Combine(artifactRoot, package.Id.Owner, package.Id.Name,
											$"{package.Package.Name}-{release}-{platform}.kspkg");

					if (File.Exists(path))
						files.Add(path);
				}

				if (files.Count == 0)
					continue;   // nothing staged for this release; it was imported on an earlier run

				if (await ReleaseExistsAsync(tag, ct))
					continue;   // releases are immutable, so an existing one is never re-uploaded

				if (dryRun)
				{
					progress?.Report($"would publish {tag}");
					published++;
					continue;
				}

				var notes = $"Artifacts for `{package.Id}` {version.Version} (revision {version.Revision}).\n\n"
							+ $"Imported from {version.Source?.Repository ?? "upstream"}"
							+ (version.Source?.Commit is { } commit ? $" at `{commit[..Math.Min(8, commit.Length)]}`" : "")
							+ ".\n\nThis release is immutable: a correction is published as a new revision.";
				var arguments = new List<string>
				{
					"release", "create", tag, "--repo", repository,
					"--title", $"{package.Id} {release}", "--notes", notes
				};
				arguments.AddRange(files);

				if (await RunAsync("gh", arguments, ct) != 0)
					throw new InvalidOperationException($"failed to publish {tag}");

				progress?.Report($"published {tag} ({files.Count} artifact(s))");
				published++;
			}
		}

		return published;
	}

	private async Task<bool> ReleaseExistsAsync(string tag, CancellationToken ct) =>
		await RunAsync("gh", ["release", "view", tag, "--repo", repository], ct, quiet: true) == 0;

	private static async Task<int> RunAsync(string file, IReadOnlyList<string> arguments, CancellationToken ct,
											bool quiet = false)
	{
		var info = new ProcessStartInfo(file)
		{
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false
		};

		foreach (var argument in arguments)
			info.ArgumentList.Add(argument);

		using var process = Process.Start(info)
							?? throw new InvalidOperationException($"could not start {file}; is the gh CLI installed?");
		var error = await process.StandardError.ReadToEndAsync(ct);
		_ = await process.StandardOutput.ReadToEndAsync(ct);
		await process.WaitForExitAsync(ct);

		if (process.ExitCode != 0 && !quiet && !string.IsNullOrWhiteSpace(error))
			Console.Error.WriteLine(error.Trim());

		return process.ExitCode;
	}
}
