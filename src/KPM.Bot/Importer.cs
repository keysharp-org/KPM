using System.Text.RegularExpressions;
using Kpm.Model;
using Kpm.Packing;
using Kpm.Registry;
using Semver;

namespace Kpm.Bot;

public sealed record ImportOptions
{
	public required string RegistryRoot { get; init; }

	/// <summary>How many upstream-tagged releases to keep. These are the author's own numbers.</summary>
	public int MaxVersions { get; init; } = 10;

	/// <summary>
	/// How many synthesized <c>0.x</c> releases to keep, newest first.
	///
	/// Far fewer than the tagged kind, and deliberately: a registry version is an ordinal this
	/// importer assigned by commit date, so pinning <c>^0.3</c> of a forum script promises nothing
	/// its author ever offered. Keeping the current snapshot is the honest amount of history, and
	/// the numbering is derived from a commit's position in the full history, so keeping fewer never
	/// renumbers what remains.
	/// </summary>
	public int MaxRegistryVersions { get; init; } = 1;
	public int? Limit { get; init; }
	public long MaxPackageBytes { get; init; } = 8 * 1024 * 1024;
	public bool DryRun { get; init; }
	public string? Only { get; init; }
	public string ArtifactRepository { get; init; } = "keysharp-org/Packages";
	public string? ArtifactOutput { get; init; }
}

public sealed record ImportOutcome(string Key, PackageId? Id, int Releases, string Status, string? Note = null);

/// <summary>
/// Imports the existing AutoHotkey ecosystem into the registry.
///
/// Two things this deliberately does NOT do. It never claims Keysharp compatibility: every import
/// declares AutoHotkey only, and the Keysharp claim is added later by a port or by a pull request
/// that passes engine validation — otherwise the catalog would be mostly broken for the audience it
/// serves. And it never builds from a repository's convenience copies when tags exist: cJSON's
/// <c>Dist/</c> file is byte-identical across releases while the real fix sits untagged, so
/// provenance has to be the tag's own tree or per-version compatibility means nothing.
/// </summary>
public sealed class Importer(GitHub github, ImportOptions options)
{
	private static readonly string[] sourceExtensions = [".ahk", ".ah2", ".ks", ".txt", ".md", ".json"];

	public async Task<List<ImportOutcome>> ImportAsync(Dictionary<string, ArisEntry> entries,
													   IProgress<string>? progress = null,
													   CancellationToken ct = default)
	{
		var outcomes = new List<ImportOutcome>();
		// Only dependencies that are themselves being imported can be recorded, so the set of known
		// ids has to exist before any manifest is written.
		var known = new Dictionary<string, PackageId>(StringComparer.OrdinalIgnoreCase);

		foreach (var key in entries.Keys)
		{
			if (Slug.TryMap(key, out var id, out _))
				known[key] = id;
		}

		var selected = entries
					   .Where(e => options.Only is null || e.Key.Contains(options.Only, StringComparison.OrdinalIgnoreCase))
					   .OrderBy(e => e.Key, StringComparer.OrdinalIgnoreCase)
					   .Take(options.Limit ?? int.MaxValue);

		foreach (var (key, entry) in selected)
		{
			ct.ThrowIfCancellationRequested();

			try
			{
				var outcome = await ImportOneAsync(key, entry, known, progress, ct);
				outcomes.Add(outcome);
				progress?.Report($"{outcome.Status,-9} {key} {(outcome.Note ?? "")}");
			}
			catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException
									   or TaskCanceledException or JsonExceptionAlias)
			{
				outcomes.Add(new ImportOutcome(key, null, 0, "failed", ex.Message));
				progress?.Report($"failed    {key} {ex.Message}");
			}
		}

		if (!options.DryRun)
		{
			await ReconcileDependenciesAsync(progress, ct);
			await BackfillSetupNotesAsync(entries, progress, ct);
		}

		return outcomes;
	}

	// Alias so the catch filter above reads as one list; System.Text.Json's exception is the only
	// other kind a malformed upstream response produces.
	private sealed class JsonExceptionAlias : System.Text.Json.JsonException;

	/// <summary>
	/// Applies <see cref="ImportOptions.MaxRegistryVersions"/> to what is already in the registry,
	/// dropping the oldest synthesized releases of each package.
	///
	/// Only ever touches registry-versioned packages: an upstream tag is the author's own release
	/// and someone may legitimately depend on it, whereas a synthesized 0.x is an ordinal this
	/// importer assigned. Safe to run only before publishing — once a release has an artifact and a
	/// lockfile can name it, removal is a yank, not a delete.
	/// </summary>
	public async Task<int> TrimAsync(IProgress<string>? progress = null, CancellationToken ct = default)
	{
		var removed = 0;

		foreach (var package in await RegistryTree.ReadAsync(options.RegistryRoot, ct))
		{
			if (package.Package.Versioning != VersioningKind.Registry || package.Versions.Count <= options.MaxRegistryVersions)
				continue;

			var drop = package.Versions
					   .OrderByDescending(v => v.Release)
					   .Skip(options.MaxRegistryVersions)
					   .ToList();

			foreach (var version in drop)
			{
				var path = Path.Combine(package.Directory, "versions", $"{version.Version}-r{version.Revision}.json");

				if (!options.DryRun)
					File.Delete(path);

				removed++;
			}

			var kept = package.Versions.Except(drop).Select(v => v.Version);
			progress?.Report($"{(options.DryRun ? "would trim" : "trimmed")} {package.Id}: "
							 + $"dropped {drop.Count}, kept {string.Join(", ", kept)}");
		}

		return removed;
	}

	/// <summary>
	/// Adds setup notes to releases imported before the note existed. Idempotent, and it only ever
	/// fills in a missing note — a note written by hand is never overwritten by a generated one.
	/// </summary>
	private async Task BackfillSetupNotesAsync(Dictionary<string, ArisEntry> entries, IProgress<string>? progress,
											   CancellationToken ct)
	{
		var byId = new Dictionary<string, ArisEntry>(StringComparer.OrdinalIgnoreCase);

		foreach (var (key, entry) in entries)
		{
			if (entry.Scripts.Count > 0 && Slug.TryMap(key, out var id, out _))
				byId[id.ToString()] = entry;
		}

		if (byId.Count == 0)
			return;

		var updated = 0;

		foreach (var package in await RegistryTree.ReadAsync(options.RegistryRoot, ct))
		{
			if (!byId.TryGetValue(package.Id.ToString(), out var entry))
				continue;

			foreach (var version in package.Versions.Where(v => v.Setup is null))
			{
				version.Setup = DescribeSetup(entry);
				var path = Path.Combine(package.Directory, "versions", $"{version.Version}-r{version.Revision}.json");
				await ManifestJson.WriteFileAsync(path, version, ct);
				updated++;
			}
		}

		if (updated > 0)
			progress?.Report($"setup     recorded an upstream setup step on {updated} release(s)");
	}

	/// <summary>
	/// Drops dependencies on packages that did not make it into the registry.
	///
	/// A dependency is recorded when its key appears in the source index, but an entry can still
	/// fail to import — a dead repository, a path the index gets wrong, an exhausted rate limit —
	/// and a manifest that names a package the registry does not have makes the whole registry
	/// invalid. Dropping is the honest option: these imports are AutoHotkey-only and not installable
	/// through this registry yet, and the alternative is a registry that cannot be published at all.
	/// Anything dropped is reported so it can be re-imported later.
	/// </summary>
	private async Task ReconcileDependenciesAsync(IProgress<string>? progress, CancellationToken ct)
	{
		var packages = await RegistryTree.ReadAsync(options.RegistryRoot, ct);
		var available = packages.Where(p => p.Versions.Count > 0)
						.Select(p => p.Id.ToString())
						.ToHashSet(StringComparer.OrdinalIgnoreCase);
		var dropped = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

		foreach (var package in packages)
		{
			foreach (var version in package.Versions)
			{
				var missing = version.Dependencies.Keys.Where(d => !available.Contains(d)).ToList();

				if (missing.Count == 0)
					continue;

				foreach (var dependency in missing)
				{
					_ = version.Dependencies.Remove(dependency);
					dropped[dependency] = dropped.GetValueOrDefault(dependency) + 1;
				}

				var path = Path.Combine(package.Directory, "versions", $"{version.Version}-r{version.Revision}.json");
				await ManifestJson.WriteFileAsync(path, version, ct);
			}
		}

		foreach (var (dependency, count) in dropped.OrderByDescending(d => d.Value))
			progress?.Report($"dropped   dependency on '{dependency}' from {count} release(s): not in the registry");
	}

	private async Task<ImportOutcome> ImportOneAsync(string key, ArisEntry entry,
													 Dictionary<string, PackageId> known,
													 IProgress<string>? progress, CancellationToken ct)
	{
		if (!Slug.TryMap(key, out var id, out var mapError))
			return new ImportOutcome(key, null, 0, "skipped", mapError);

		var repository = entry.RepositoryOwnerAndName;

		if (repository is null)
			return new ImportOutcome(key, id, 0, "skipped", "no repository");

		var directory = Path.Combine(options.RegistryRoot, RegistryTree.PackagesDirectory, id.Owner, id.Name);
		var existing = Directory.Exists(directory)
					   ? await RegistryTree.ReadPackageAsync(directory, ct)
					   : null;
		List<PlannedRelease> plan = await PlanVersionsAsync(entry, repository, ct);

		if (plan.Count == 0)
			return new ImportOutcome(key, id, 0, "skipped", "no importable revisions found");

		var wrote = 0;
		var alreadyPresent = 0;
		var noContent = 0;
		var manifests = new List<VersionManifest>();

		foreach (var planned in plan)
		{
			// Re-running an import must be a no-op: a release already present is never rebuilt,
			// which is also what keeps the synthesized 0.x numbering stable across runs.
			if (existing?.Versions.Any(v => v.Version == planned.Version) == true)
			{
				alreadyPresent++;
				continue;
			}

			var built = await BuildReleaseAsync(id, entry, repository, planned, known, ct);

			if (built is null)
			{
				noContent++;
				continue;
			}

			manifests.Add(built);
			wrote++;
		}

		// A repository that restructured after its last tag has an index entry describing the branch
		// layout and tags whose trees predate it, so every tagged revision looks empty.
		// AutoHotInterception is the case in point: the index names "AHK v2/Lib/...", which exists
		// only on master. Falling back to the branch is what the index is actually describing, and
		// the release is registry-versioned, which correctly says the number is ours and not theirs.
		if (wrote == 0 && alreadyPresent == 0 && noContent > 0 && plan[0].IsUpstreamVersioned)
		{
			var head = await github.GetLatestCommitAsync(repository, entry.RepositoryBranch, ct);

			if (head is not null)
			{
				var fromBranch = new PlannedRelease("0.1.0", head.Sha, head.Sha, head.Date, false, null);
				var built = await BuildReleaseAsync(id, entry, repository, fromBranch, known, ct);

				if (built is not null)
				{
					manifests.Add(built);
					wrote++;
					plan = [fromBranch];
					progress?.Report($"branch    {key}: no tagged revision holds the files the index names; "
									 + $"imported {entry.RepositoryBranch} as 0.1.0");
				}
			}
		}

		if (wrote == 0)
		{
			// "Nothing to do" and "the entry points at files that do not exist" look the same from
			// the outside but need opposite responses, so they are reported as different outcomes.
			return alreadyPresent > 0
				   ? new ImportOutcome(key, id, 0, "current", $"{alreadyPresent} release(s) already imported")
				   : new ImportOutcome(key, id, 0, "empty",
									   $"none of the {noContent} candidate revision(s) contained the files "
									   + $"'{string.Join(", ", entry.Files.Concat([entry.Main ?? ""]).Where(f => f.Length > 0))}' "
									   + $"in {repository}");
		}

		if (options.DryRun)
			return new ImportOutcome(key, id, wrote, "would-add", string.Join(", ", manifests.Select(m => m.Version)));

		_ = Directory.CreateDirectory(Path.Combine(directory, "versions"));
		var package = existing?.Package ?? new PackageManifest
		{
			Name = id.Name,
			Owner = id.Owner,
			DisplayName = key.Split('/')[^1],
			Description = Summarize(entry.Description, key),
			License = entry.License ?? "NOASSERTION",
			Homepage = entry.Homepage ?? $"https://github.com/{repository}",
			Categories = [.. entry.Keywords.Take(8)],
			Versioning = plan[0].IsUpstreamVersioned ? VersioningKind.Upstream : VersioningKind.Registry
		};
		await ManifestJson.WriteFileAsync(Path.Combine(directory, "package.json"), package, ct);

		foreach (var manifest in manifests)
		{
			var path = Path.Combine(directory, "versions", $"{manifest.Version}-r{manifest.Revision}.json");
			await ManifestJson.WriteFileAsync(path, manifest, ct);
		}

		return new ImportOutcome(key, id, wrote, "imported", string.Join(", ", manifests.Select(m => m.Version)));
	}

	private sealed record PlannedRelease(string Version, string Reference, string Commit, DateTimeOffset Date,
										 bool IsUpstreamVersioned, string? Tag);

	/// <summary>
	/// Decides what versions a package gets. A repository that tags its releases keeps its own
	/// numbers; anything else — a forum script, an untagged repository — gets a synthesized 0.x
	/// series in commit-date order, which promises nothing about compatibility the way a 1.0 would.
	/// </summary>
	private async Task<List<PlannedRelease>> PlanVersionsAsync(ArisEntry entry, string repository, CancellationToken ct)
	{
		if (!entry.IsScriptHub)
		{
			var tags = await github.GetTagsAsync(repository, ct);
			var versioned = new List<PlannedRelease>();

			foreach (var tag in tags)
			{
				var text = tag.Name.TrimStart('v', 'V');

				if (!SemVersion.TryParse(text, SemVersionStyles.Any, out var semver))
					continue;

				versioned.Add(new PlannedRelease(semver.ToString(), tag.Commit, tag.Commit,
												 DateTimeOffset.MinValue, true, tag.Name));
			}

			if (versioned.Count > 0)
			{
				return versioned
					   .GroupBy(v => v.Version, StringComparer.Ordinal).Select(g => g.First())
					   .OrderByDescending(v => SemVersion.Parse(v.Version), SemVersion.PrecedenceComparer)
					   .Take(options.MaxVersions)
					   .OrderBy(v => SemVersion.Parse(v.Version), SemVersion.PrecedenceComparer)
					   .ToList();
			}
		}

		// No usable tags: number the file's own history instead.
		var path = entry.Files.FirstOrDefault(f => !f.Contains('*')) ?? entry.Main;
		var commits = path is not null
					  ? await github.GetCommitsAsync(repository, path, ct)
					  : [];

		if (commits.Count == 0)
		{
			var head = await github.GetLatestCommitAsync(repository, entry.RepositoryBranch, ct);
			return head is null
				   ? []
				   : [new PlannedRelease("0.1.0", head.Sha, head.Sha, head.Date, false, null)];
		}

		// Oldest first, ties broken by sha: the mapping from commit to version number must be the
		// same on every run, or a re-import would renumber a package's whole history.
		var ordered = commits.OrderBy(c => c.Date).ThenBy(c => c.Sha, StringComparer.Ordinal).ToList();
		var planned = new List<PlannedRelease>();

		for (var i = 0; i < ordered.Count; i++)
			planned.Add(new PlannedRelease($"0.{i + 1}.0", ordered[i].Sha, ordered[i].Sha, ordered[i].Date, false, null));

		return planned.TakeLast(options.MaxRegistryVersions).ToList();
	}

	private async Task<VersionManifest?> BuildReleaseAsync(PackageId id, ArisEntry entry, string repository,
														   PlannedRelease planned,
														   Dictionary<string, PackageId> known, CancellationToken ct)
	{
		var patterns = new List<string>(entry.Files);

		// The index sometimes names a main file that no pattern covers; without this the package
		// would ship everything except the file it points at.
		if (!string.IsNullOrEmpty(entry.Main))
			patterns.Add(entry.Main);

		if (patterns.Count == 0)
			return null;

		var wanted = new List<string>();
		var globs = patterns.Where(IsGlob).ToList();

		// An exact path is fetched directly. Besides being far cheaper, this is the only thing that
		// works for a repository whose tree the API truncates — which is every single-file entry
		// mirrored in ScriptHub, i.e. most of this corpus.
		foreach (var exact in patterns.Where(p => !IsGlob(p)).Select(p => p.Replace('\\', '/')))
		{
			if (IsSourceFile(exact))
				wanted.Add(exact);
		}

		if (globs.Count > 0)
		{
			var (tree, truncated) = await github.GetTreeAsync(repository, planned.Reference, ct);

			if (truncated && wanted.Count == 0)
				throw new InvalidOperationException(
					$"{repository}'s file list is too large for the API to return in full, and "
					+ $"'{string.Join(", ", globs)}' needs it. Name exact paths in the index entry instead.");

			foreach (var file in tree.Where(t => t.Type == "blob"))
			{
				if (IsSourceFile(file.Path) && globs.Any(g => GlobToRegex(g).IsMatch(file.Path)))
					wanted.Add(file.Path);
			}
		}

		wanted = [.. wanted.Distinct(StringComparer.Ordinal).OrderBy(p => p, StringComparer.Ordinal)];

		if (wanted.Count == 0)
			return null;

		var staging = Path.Combine(Path.GetTempPath(), "kpm-import", Guid.NewGuid().ToString("N"));

		try
		{
			// Repository-relative structure is preserved under src/ so that relative includes
			// between a package's own files keep working; the forwarder means no consumer ever
			// types these paths.
			long total = 0;
			var fetched = new List<string>();

			foreach (var path in wanted)
			{
				var bytes = await github.GetFileAsync(repository, planned.Reference, path, ct);

				if (bytes is null)
					continue;

				total += bytes.Length;

				if (total > options.MaxPackageBytes)
					throw new InvalidOperationException(
						$"{total} bytes exceeds the {options.MaxPackageBytes} byte import limit");

				var target = Path.Combine(staging, "src", path.Replace('/', Path.DirectorySeparatorChar));
				_ = Directory.CreateDirectory(Path.GetDirectoryName(target)!);
				await File.WriteAllBytesAsync(target, bytes, ct);
				fetched.Add(path);
			}

			if (fetched.Count == 0)
				return null;

			var entryPath = ResolveEntry(entry, fetched);
			var dependencies = new Dictionary<string, string>();

			foreach (var (dependencyKey, _) in entry.Dependencies)
			{
				// A dependency outside the imported set cannot be recorded: the registry would fail
				// validation for naming a package it does not have.
				if (known.TryGetValue(dependencyKey, out var dependencyId))
					dependencies[dependencyId.ToString()] = "*";
			}

			var embedded = new EmbeddedManifest
			{
				Name = id.Name,
				Owner = id.Owner,
				Version = planned.Version,
				Revision = 1,
				Entry = entryPath,
				Engines = { [Engines.AutoHotkey] = ">=2.0" },
				Dependencies = dependencies
			};
			var packed = Packer.Pack(staging, embedded, Platforms.Any);
			var release = $"{planned.Version}-r1";
			var fileName = packed.FileName;

			if (options.ArtifactOutput is not null)
			{
				var directory = Path.Combine(options.ArtifactOutput, id.Owner, id.Name);
				_ = Directory.CreateDirectory(directory);
				await File.WriteAllBytesAsync(Path.Combine(directory, fileName), packed.Bytes, ct);
			}

			return new VersionManifest
			{
				Name = id.Name,
				Owner = id.Owner,
				Version = planned.Version,
				Revision = 1,
				Published = planned.Date == DateTimeOffset.MinValue
							? new DateTimeOffset(DateTime.UtcNow.Date, TimeSpan.Zero)
							: new DateTimeOffset(planned.Date.UtcDateTime.Date, TimeSpan.Zero),
				Entry = entryPath,
				// AutoHotkey only, always. A Keysharp claim has to be earned by validation.
				Engines = { [Engines.AutoHotkey] = ">=2.0" },
				Platforms = [Platforms.Any],
				Dependencies = dependencies,
				Setup = DescribeSetup(entry),
				Source = new SourceRef
				{
					Kind = entry.IsScriptHub ? "scripthub" : "git",
					Repository = repository,
					Commit = planned.Commit,
					Url = entry.Homepage,
					Path = planned.Tag
				},
				Artifacts =
				{
					[Platforms.Any] = new ArtifactRef
					{
						Sha256 = packed.Sha256,
						Size = packed.Size,
						Sources = [RegistryTree.ReleaseAssetUrl(options.ArtifactRepository, id, release, fileName)]
					}
				}
			};
		}
		finally
		{
			if (Directory.Exists(staging))
				Directory.Delete(staging, recursive: true);
		}
	}

	/// <summary>
	/// Records that an imported package had install-time commands upstream, without carrying the
	/// commands themselves.
	///
	/// Most were packaging fixups — rewriting encodings, generating an aggregator include — which a
	/// built artifact simply does not need, because the author does them once before packing. What
	/// cannot be carried over is the rest: one of these downloads a zip, elevates, installs a kernel
	/// driver and runs PowerShell with the execution policy bypassed. That is exactly the code this
	/// registry declines to run, so the honest thing is to say so and point at the upstream.
	/// </summary>
	private static SetupNote? DescribeSetup(ArisEntry entry)
	{
		if (entry.Scripts.Count == 0)
			return null;

		var phases = string.Join(", ", entry.Scripts.Keys.OrderBy(k => k, StringComparer.Ordinal));
		return new SetupNote
		{
			Message = $"Upstream defines install-time commands ({phases}) that this registry does not run. "
					  + "If this package needs a driver, a tool, or a setup step, follow its own instructions.",
			Url = entry.Homepage
		};
	}

	/// <summary>
	/// Fits an upstream description into the catalog's one-line budget.
	///
	/// Upstream descriptions have no length rule and occasionally run to a paragraph, which the
	/// registry's schema rejects — one import did exactly that and was only caught by CI. Cut at a
	/// sentence where possible, since the first sentence is nearly always the summary, and fall back
	/// to a word boundary.
	/// </summary>
	private static string Summarize(string? description, string key)
	{
		if (string.IsNullOrWhiteSpace(description))
			return $"Imported from {key}.";

		description = description.Trim();

		if (description.Length <= Registry.RegistryValidator.MaxDescription)
			return description;

		var budget = Registry.RegistryValidator.MaxDescription - 1;
		var sentence = description.LastIndexOf(". ", budget, StringComparison.Ordinal);

		if (sentence > budget / 2)
			return description[..(sentence + 1)];

		var word = description.LastIndexOf(' ', budget);
		return description[..(word > 0 ? word : budget)].TrimEnd(',', ';', ':', '-') + "…";
	}

	private static bool IsGlob(string pattern) => pattern.Contains('*') || pattern.Contains('?');

	/// <summary>
	/// Binaries and assets are not what a script package is for, and they are what would make the
	/// registry's storage grow without bound.
	/// </summary>
	private static bool IsSourceFile(string path)
	{
		var extension = Path.GetExtension(path);
		return extension.Length == 0 || sourceExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase);
	}

	private static Regex GlobToRegex(string pattern)
	{
		var builder = new System.Text.StringBuilder("^");

		for (var i = 0; i < pattern.Length; i++)
		{
			var c = pattern[i];

			if (c == '*')
			{
				if (i + 1 < pattern.Length && pattern[i + 1] == '*')
				{
					builder.Append(".*");
					i++;
				}
				else
					builder.Append("[^/]*");   // a single star stays inside one path segment
			}
			else if (c == '?')
				builder.Append("[^/]");
			else
				builder.Append(Regex.Escape(c.ToString()));
		}

		return new Regex(builder.Append('$').ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
	}

	private static string ResolveEntry(ArisEntry entry, List<string> files)
	{
		if (!string.IsNullOrEmpty(entry.Main))
			return $"src/{entry.Main.Replace('\\', '/')}";

		var script = files.FirstOrDefault(f => f.EndsWith(".ahk", StringComparison.OrdinalIgnoreCase)
											   || f.EndsWith(".ah2", StringComparison.OrdinalIgnoreCase))
					 ?? files[0];
		return $"src/{script}";
	}
}
