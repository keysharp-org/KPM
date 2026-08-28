using Kpm.Model;
using Kpm.Registry;
using Semver;

namespace Kpm.Resolution;

/// <summary>The engine and machine a resolution is being performed for.</summary>
public sealed record ResolveContext(Version EngineVersion, string Platform, string Engine = Engines.Keysharp)
{
	public static ResolveContext ForKeysharp(Version version, string? platform = null) =>
		new(version, platform ?? Platforms.Current);
}

public sealed record ResolvedPackage(VersionManifest Manifest, string Platform, ArtifactRef Artifact)
{
	public PackageId Id => Manifest.Id;
	public PackageVersion Release => Manifest.Release;
}

public sealed record DependencyResolution(IReadOnlyList<ResolvedPackage> Packages);

public sealed class ResolutionException(string message) : Exception(message);

/// <summary>
/// Turns requested ranges into exact releases.
///
/// Deliberately without backtracking: when two packages want ranges that do not overlap, this
/// reports both requesters and stops rather than searching for older combinations that might fit.
/// For a registry of small script libraries the search almost never buys anything, and a clear
/// "these two disagree, pin one" beats a solver that silently downgrades a package the user asked
/// for by name.
/// </summary>
public sealed class Resolver(RegistryIndex index)
{
	private sealed record Requirement(string Text, string Requester);

	public DependencyResolution Resolve(IReadOnlyDictionary<string, string> roots, ResolveContext context)
	{
		var constraints = new Dictionary<PackageId, List<Requirement>>();
		var selected = new Dictionary<PackageId, ResolvedPackage>();
		var queue = new Queue<PackageId>();

		foreach (var (idText, rangeText) in roots)
			Require(idText, rangeText, "kpm.json", constraints, queue);

		// Selecting a different version can introduce or drop dependencies, so this runs to a
		// fixpoint rather than in one pass. The cap is a safety net against a pathological registry,
		// not an expected limit.
		var iterations = 0;
		var maximum = 1000 + (roots.Count * 100);

		while (queue.Count > 0)
		{
			if (++iterations > maximum)
				throw new ResolutionException("dependency resolution did not settle; the registry index may be inconsistent");

			var id = queue.Dequeue();
			var requirements = constraints[id];
			var picked = Select(id, requirements, context);

			if (selected.TryGetValue(id, out var current) && current.Release == picked.Release)
				continue;

			selected[id] = picked;

			foreach (var (depId, depRange) in picked.Manifest.Dependencies)
				Require(depId, depRange, id.ToString(), constraints, queue);
		}

		DetectCycles(selected);
		return new DependencyResolution(selected.Values.OrderBy(p => p.Id.ToString(), StringComparer.Ordinal).ToArray());
	}

	private static void Require(string idText, string rangeText, string requester,
								Dictionary<PackageId, List<Requirement>> constraints, Queue<PackageId> queue)
	{
		if (!PackageId.TryParse(idText, out var id, out var idError))
			throw new ResolutionException($"{requester} requests an invalid package id: {idError}");

		if (!RangeMatcher.TryParse(rangeText, out _))
			throw new ResolutionException($"{requester} requests '{idText}' with an unparseable version range '{rangeText}'");

		if (!constraints.TryGetValue(id.Value, out var list))
			constraints[id.Value] = list = [];

		if (list.Any(r => r.Text == rangeText && r.Requester == requester))
			return;

		list.Add(new Requirement(rangeText, requester));
		queue.Enqueue(id.Value);
	}

	private ResolvedPackage Select(PackageId id, List<Requirement> requirements, ResolveContext context)
	{
		var package = index.Find(id)
					  ?? throw new ResolutionException($"no package '{id}' in the registry (requested by {Requesters(requirements)})");
		var installable = package.Installable().ToList();

		if (installable.Count == 0)
			throw new ResolutionException($"'{id}' has no installable releases (every version is yanked)");

		// Each filter is applied separately so the failure can say which one emptied the set — the
		// difference between "no such version", "not ported to your engine" and "not built for your
		// platform" is the whole of what the user needs to know.
		var prerelease = !RangeMatcher.HasStableRelease(installable.Select(v => v.Version));
		var matching = installable
					   .Where(v => requirements.All(r => RangeMatcher.Matches(r.Text, v.Version, prerelease)))
					   .ToList();

		if (matching.Count == 0)
			throw new ResolutionException(
				$"no release of '{id}' satisfies {Describe(requirements)}\n"
				+ $"  available: {string.Join(", ", installable.Select(v => v.Version).Distinct())}");

		var forEngine = matching.Where(v => SupportsEngine(v, context)).ToList();

		if (forEngine.Count == 0)
		{
			throw new ResolutionException(
				$"no release of '{id}' matching {Describe(requirements)} supports {context.Engine} {context.EngineVersion}\n"
				+ $"  candidates declare: {string.Join(", ", matching.Select(EngineSummary).Distinct())}"
				+ SuggestPort(id, context));
		}

		var forPlatform = forEngine.Where(v => Platforms.Select(v.Artifacts.Keys, context.Platform) is not null).ToList();

		if (forPlatform.Count == 0)
			throw new ResolutionException(
				$"no release of '{id}' matching {Describe(requirements)} ships an artifact for {context.Platform}\n"
				+ $"  candidates build for: {string.Join(", ", forEngine.SelectMany(v => v.Artifacts.Keys).Distinct())}");

		var best = forPlatform.MaxBy(v => v.Release)!;
		var platform = Platforms.Select(best.Artifacts.Keys, context.Platform)!;
		return new ResolvedPackage(best, platform, best.Artifacts[platform]);
	}

	/// <summary>
	/// A release must claim the engine being resolved for. An unclaimed engine is not a silent
	/// "probably fine": most of the imported corpus is AutoHotkey code that has never been checked
	/// against Keysharp, and offering it as installable would make the catalog mostly broken.
	/// </summary>
	private static bool SupportsEngine(VersionManifest version, ResolveContext context) =>
		version.Engines.TryGetValue(context.Engine, out var rangeText)
		&& EngineRange.TryParse(rangeText, out var range, out _)
		&& range.Includes(context.EngineVersion);

	/// <summary>
	/// Names a port of this package that does support the engine being resolved for.
	///
	/// A port is a separate package because it versions and releases independently of the original,
	/// but a user asking for the original on the wrong engine is asking for the library, not the
	/// packaging. <c>derivedFrom</c> already records the relationship, so the answer is knowable
	/// rather than something the user has to go looking for.
	/// </summary>
	private string SuggestPort(PackageId id, ResolveContext context) =>
		DescribePorts(index.PortsOf(id, context.Engine), context.Engine);

	/// <summary>Formats a port suggestion, shared by every path that turns a user away on engine.</summary>
	public static string DescribePorts(IReadOnlyList<PackageId> ports, string engine) =>
		ports.Count == 0
		? ""
		: $"\n  {string.Join(" and ", ports)} "
		  + $"{(ports.Count == 1 ? "is a port of it that supports" : "are ports of it that support")} "
		  + $"{engine}; install that instead.";

	private static string EngineSummary(VersionManifest v) =>
		v.Engines.Count == 0 ? "no engine" : string.Join(" ", v.Engines.Select(e => $"{e.Key} {e.Value}"));

	private static string Describe(List<Requirement> requirements) =>
		string.Join(" and ", requirements.Select(r => $"'{r.Text}' (from {r.Requester})"));

	private static string Requesters(List<Requirement> requirements) =>
		string.Join(", ", requirements.Select(r => r.Requester).Distinct());

	private static void DetectCycles(Dictionary<PackageId, ResolvedPackage> selected)
	{
		var state = new Dictionary<PackageId, int>();   // 1 = on the current path, 2 = finished
		var path = new List<PackageId>();

		foreach (var id in selected.Keys)
			Visit(id);

		void Visit(PackageId id)
		{
			if (state.TryGetValue(id, out var mark))
			{
				if (mark == 1)
				{
					var cycle = path.SkipWhile(p => p != id).Append(id);
					throw new ResolutionException("dependency cycle: " + string.Join(" -> ", cycle));
				}

				return;
			}

			state[id] = 1;
			path.Add(id);

			if (selected.TryGetValue(id, out var resolved))
			{
				foreach (var dep in resolved.Manifest.Dependencies.Keys)
				{
					if (PackageId.TryParse(dep, out var depId, out _))
						Visit(depId.Value);
				}
			}

			path.RemoveAt(path.Count - 1);
			state[id] = 2;
		}
	}
}
