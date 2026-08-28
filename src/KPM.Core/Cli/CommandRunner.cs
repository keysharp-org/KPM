using Kpm.Artifacts;
using Kpm.Install;
using Kpm.Model;
using Kpm.Packing;
using Kpm.Registry;
using Kpm.Resolution;

namespace Kpm.Cli;

/// <summary>
/// Every kpm command, and the console output that goes with them. This lives in the library rather
/// than in the kpm executable so that a front end other than that executable runs exactly the same
/// commands and prints exactly the same things: the Keysharp host embeds this to serve its own
/// package switches, and would otherwise have had to reimplement the surface and let it drift.
///
/// The writers are injected rather than taken from <see cref="Console"/> so an embedder can capture
/// or redirect output without touching process-wide state.
/// </summary>
public sealed class CommandRunner(TextWriter output, TextWriter error, TextReader input)
{
	/// <summary>
	/// Runs one command line, the way the kpm executable would. Returns the process exit code.
	/// Every argument is exactly as kpm takes it, starting with the command name.
	/// </summary>
	public static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error, TextReader input)
	{
		var command = args.Length > 0 && !args[0].StartsWith("--", StringComparison.Ordinal) ? args[0] : "help";
		var cli = new CommandLine(args.Skip(1));
		var runner = new CommandRunner(output, error, input);

		try
		{
			return await runner.Run(command, cli);
		}
		catch (ResolutionException ex)
		{
			error.WriteLine($"error: {ex.Message}");
			return 1;
		}
		catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException or IOException
								   or ArgumentException or UnauthorizedAccessException)
		{
			error.WriteLine($"error: {ex.Message}");
			return 1;
		}
	}

	/// <summary>
	/// The blocking form, for a caller that reaches this by reflection and would otherwise have to
	/// await a Task it has no compile-time reference to.
	/// </summary>
	public static int Run(string[] args, TextWriter output, TextWriter error, TextReader input) =>
		RunAsync(args, output, error, input).GetAwaiter().GetResult();

	async Task<int> Run(string command, CommandLine cli)
{
	switch (command.ToLowerInvariant())
	{
		case "add": return await Add(cli);
		case "remove": case "rm": return await Remove(cli);
		case "install": return await Install(cli);
		case "update": return await Update(cli);
		case "search": return await Search(cli);
		case "list": case "ls": return await List(cli);
		case "setup": return await Setup(cli);
		case "pack": return Pack(cli);
		case "manifest": return await BuildManifest(cli);
		case "index": return await BuildIndex(cli);
		case "validate": return await Validate(cli);
		case "probe": return await Probe(cli);
		case "mirror": return await Mirror(cli);
		case "cache": return Cache(cli);
		case "help": case "--help": case "-h": Help(); return 0;
		case "version": case "--version": output.WriteLine(Version()); return 0;
		default:
			error.WriteLine($"error: unknown command '{command}'. Run 'kpm help'.");
			return 1;
	}
}

	string Version() =>
	System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.1.0";

	string ProjectRoot(CommandLine cli) =>
	Project.FindRoot(cli.ValueOrDefault("project", Directory.GetCurrentDirectory()));

/// <summary>
/// Which engine version to resolve for. Explicit flag first, then the environment (how CI and the
/// Keysharp host pass it), then the version this build was cut against.
/// </summary>
	ResolveContext Context(CommandLine cli)
{
	var text = cli.Value("engine-version")
			   ?? Environment.GetEnvironmentVariable("KPM_ENGINE_VERSION")
			   ?? "0.0.0.17";

	if (!System.Version.TryParse(text, out var version))
		throw new ArgumentException($"'{text}' is not a version");

	return new ResolveContext(version, cli.ValueOrDefault("platform", Platforms.Current),
							  cli.ValueOrDefault("engine", Engines.Keysharp));
}

	async Task<int> Add(CommandLine cli)
{
	if (cli.Positionals.Count == 0)
	{
		error.WriteLine("usage: kpm add <owner/name>[@range]");
		return 1;
	}

	var project = await Project.LoadAsync(ProjectRoot(cli));
	var service = new KpmService();
	var context = Context(cli);
	// Fetched once, and honouring --offline: resolving each argument separately used to refetch,
	// which both ignored the flag and let a network fetch overwrite the cache mid-command.
	var index = (await service.GetIndexAsync(refresh: !cli.Has("offline"))).Index;

	foreach (var request in cli.Positionals)
	{
		var at = request.LastIndexOf('@');
		var idText = at > 0 ? request[..at] : request;
		var range = at > 0 ? request[(at + 1)..] : null;
		var id = ResolveId(index, idText, context.Engine);

		if (id is null)
			return 1;

		// Without an explicit range, pin the caret range of whatever is newest now: a bare add
		// should not mean "always newest", which would change what a project builds over time.
		range ??= CaretRangeForLatest(index, id.Value, context);
		project.SetDependency(id.Value, range);
		output.WriteLine($"added {id} {range}");
	}

	await project.SaveManifestAsync();
	return await Resolve(project, service, cli);
}

/// <summary>
/// Turns what the user typed into a package id. A bare name is accepted when exactly one package
/// has it, and reported with the candidates when several do; a full id is matched regardless of
/// casing, and the registry's own spelling is what gets written to kpm.json.
/// </summary>
	PackageId? ResolveId(RegistryIndex index, string text, string engine)
{
	if (!text.Contains('/'))
	{
		var byName = index.FindByName(text, engine);

		if (byName is null)
		{
			error.WriteLine($"error: no package named '{text}' in the registry");
			return null;
		}

		return PackageId.Parse($"{byName.Package.Owner}/{byName.Package.Name}");
	}

	if (!PackageId.TryParse(text, out var parsed, out var problem))
	{
		error.WriteLine($"error: {problem}");
		return null;
	}

	var found = index.Find(parsed.Value);

	if (found is null)
	{
		error.WriteLine($"error: no package '{parsed}' in the registry");
		return null;
	}

	// Record the registry's spelling, not the user's: ids keep their author's casing.
	return PackageId.Parse($"{found.Package.Owner}/{found.Package.Name}");
}

	string CaretRangeForLatest(RegistryIndex index, PackageId id, ResolveContext context)
{
	var package = index.Find(id) ?? throw new InvalidOperationException($"no package '{id}' in the registry");
	var newest = package.Installable()
				 .Where(v => v.Engines.ContainsKey(context.Engine))
				 .MaxBy(v => v.Release)
				 ?? throw new InvalidOperationException(
						$"'{id}' has no release for {context.Engine}"
						+ Resolver.DescribePorts(index.PortsOf(id, context.Engine), context.Engine));
	return $"^{newest.Version}";
}

	async Task<int> Remove(CommandLine cli)
{
	if (cli.Positionals.Count == 0)
	{
		error.WriteLine("usage: kpm remove <owner/name>");
		return 1;
	}

	var project = await Project.LoadAsync(ProjectRoot(cli));

	foreach (var request in cli.Positionals)
	{
		if (!PackageId.TryParse(request, out var id, out var problem))
		{
			error.WriteLine($"error: {problem}");
			return 1;
		}

		output.WriteLine(project.RemoveDependency(id.Value) ? $"removed {id}" : $"{id} was not a dependency");
	}

	await project.SaveManifestAsync();
	return await Resolve(project, new KpmService(), cli);
}

	async Task<int> Install(CommandLine cli)
{
	var project = await Project.LoadAsync(ProjectRoot(cli));
	var service = new KpmService();

	// With a lockfile this is the offline, reproducible path; without one there is nothing to
	// reproduce yet, so fall through to a resolve.
	if (project.Lock is not null)
	{
		var report = await service.InstallLockedAsync(project);
		Report(report, project);
		return 0;
	}

	return await Resolve(project, service, cli);
}

	Task<int> Update(CommandLine cli) => UpdateAsync(cli);

	async Task<int> UpdateAsync(CommandLine cli)
{
	var project = await Project.LoadAsync(ProjectRoot(cli));
	return await Resolve(project, new KpmService(), cli);
}

	async Task<int> Resolve(Project project, KpmService service, CommandLine cli)
{
	if (project.Manifest.Dependencies.Count == 0)
	{
		output.WriteLine("no dependencies");
		return 0;
	}

	var report = await service.UpdateAsync(project, Context(cli), refresh: !cli.Has("offline"));
	Report(report, project);
	return 0;
}

	void Report(InstallReport report, Project project)
{
	if (report.Warning is not null)
		error.WriteLine($"warning: {report.Warning}");

	foreach (var package in report.Installed)
		output.WriteLine($"  {package.Id} {package.Version.ToDisplayString()}  ->  #Include <{package.IncludePath}>");

	output.WriteLine($"{report.Installed.Count} package(s) installed into {Path.Combine(project.Directory, "Lib", "KPM")}");

	// Printed last so it is the thing still on screen. kpm never performs these itself.
	if (report.Setup is { Count: > 0 })
	{
		output.WriteLine();
		output.WriteLine("These packages need a step kpm does not perform for you:");

		var runnable = false;

		foreach (var (id, note) in report.Setup)
		{
			output.WriteLine($"  {id}: {note.Message}");
			runnable |= note.IsRunnable;

			if (note.Url is not null)
				output.WriteLine($"    {note.Url}");
		}

		// Naming the command is the whole difference between a note and something the user can act
		// on; `kpm setup` still shows what it will run and asks before running it.
		output.WriteLine(runnable
						  ? "\nRun 'kpm setup' to perform these; it shows each command and asks first."
						  : "\nThese have to be done by hand; 'kpm setup' lists them.");
	}
}

	async Task<int> Search(CommandLine cli)
{
	var service = new KpmService();
	var fetch = await service.GetIndexAsync(refresh: !cli.Has("offline"));

	if (fetch.Warning is not null)
		error.WriteLine($"warning: {fetch.Warning}");

	var context = Context(cli);
	var all = cli.Has("all-engines");
	var matches = fetch.Index.Search(cli.Positionals.FirstOrDefault() ?? "").ToList();

	if (matches.Count == 0)
	{
		output.WriteLine("no matching packages");
		return 0;
	}

	foreach (var match in matches.OrderBy(m => $"{m.Package.Owner}/{m.Package.Name}", StringComparer.Ordinal))
	{
		var newest = match.Installable().MaxBy(v => v.Release);

		if (newest is null)
			continue;

		var supported = newest.Engines.ContainsKey(context.Engine);

		if (!supported && !all)
			continue;

		var engines = newest.Engines.Count == 0 ? "no engine declared" : string.Join(", ", newest.Engines.Keys);
		var platforms = string.Join(", ", newest.Platforms);
		output.WriteLine($"{match.Package.Owner}/{match.Package.Name}  {newest.Version}");
		output.WriteLine($"    {match.Package.Description}");

		// Shown next to the id, because the id names the maintainer and people read it as the author.
		if (match.Package.Authors is { Count: > 0 } authors)
		{
			var by = $"    by {string.Join(", ", authors)}";
			output.WriteLine(match.Package.DerivedFrom is { } from
							  ? $"{by}; packaged by {match.Package.Owner}, derived from {from}"
							  : by);
		}

		output.WriteLine($"    engines: {engines}   platforms: {platforms}");
	}

	return 0;
}

	async Task<int> List(CommandLine cli)
{
	var project = await Project.LoadAsync(ProjectRoot(cli));

	if (project.Lock is null || project.Lock.Packages.Count == 0)
	{
		output.WriteLine("no packages installed");
		return 0;
	}

	foreach (var package in project.Lock.Packages)
		output.WriteLine($"{package.Id}  {package.Version}  (r{package.Revision}, {package.Platform})");

	return 0;
}

/// <summary>
/// Performs the manual steps installed packages declare. Separate from installing on purpose: this
/// is the only command that runs anything from a package, and only after showing what it will run.
/// </summary>
	async Task<int> Setup(CommandLine cli)
{
	var project = await Project.LoadAsync(ProjectRoot(cli));

	if (project.Lock is null)
	{
		error.WriteLine("error: nothing is installed; run 'kpm install' first");
		return 1;
	}

	var context = Context(cli);
	var index = (await new KpmService().GetIndexAsync(refresh: !cli.Has("offline"))).Index;
	var wanted = cli.Positionals.Count > 0 ? cli.Positionals : null;
	var notes = new List<(PackageId, SetupNote)>();

	foreach (var locked in project.Lock.Packages)
	{
		var id = PackageId.Parse(locked.Id);

		if (wanted is not null && !wanted.Any(w => w.Equals(locked.Id, StringComparison.OrdinalIgnoreCase)
												   || w.Equals(id.Name, StringComparison.OrdinalIgnoreCase)))
			continue;

		var release = index.Find(id)?.Versions
					  .FirstOrDefault(v => v.Version == locked.Version && v.Revision == locked.Revision);

		if (release?.Setup is { } note)
			notes.Add((id, note));
	}

	var steps = SetupRunner.Plan(notes, project.Directory, context.Platform);

	if (steps.Count == 0)
	{
		output.WriteLine("nothing installed declares a setup step for this platform");
		return 0;
	}

	var assumeYes = cli.Has("yes");
	var ran = 0;

	foreach (var step in steps)
	{
		output.WriteLine();
		output.WriteLine($"{step.Id}: {step.Note.Message}");

		if (step.Note.Url is not null)
			output.WriteLine($"  {step.Note.Url}");

		if (step.ScriptPath is null)
		{
			// Either the package ships nothing to run, or it does but not for this platform's build.
			output.WriteLine(step.Note.IsRunnable
							  ? $"  '{step.Note.Script}' is not in the installed package; follow the instructions above"
							  : "  nothing to run automatically; follow the instructions above");
			continue;
		}

		output.WriteLine($"  will run: {step.Describe()}");

		if (step.Note.Elevate)
			output.WriteLine("  as administrator");

		if (step.Note.Reboot)
			output.WriteLine("  a restart is needed afterwards");

		if (!assumeYes)
		{
			output.Write("  run it now? [y/N] ");

			if (input.ReadLine()?.Trim() is not ("y" or "Y" or "yes"))
			{
				output.WriteLine("  skipped");
				continue;
			}
		}

		if (await SetupRunner.RunAsync(step, _ => true))
		{
			ran++;
			output.WriteLine($"  done{(step.Note.Reboot ? "; restart to finish" : "")}");
		}
	}

	output.WriteLine();
	output.WriteLine($"{ran} setup step(s) run");
	return 0;
}

	int Pack(CommandLine cli)
{
	var directory = Path.GetFullPath(cli.Positionals.FirstOrDefault() ?? Directory.GetCurrentDirectory());
	var portPath = Path.Combine(directory, "port.json");
	var packagePath = Path.Combine(directory, "package.json");

	if (!File.Exists(portPath) || !File.Exists(packagePath))
	{
		error.WriteLine($"error: {directory} needs both package.json and port.json to be packed");
		return 1;
	}

	var package = ManifestJson.Read<PackageManifest>(File.ReadAllText(packagePath));
	var port = ManifestJson.Read<PortManifest>(File.ReadAllText(portPath));
	var revision = int.Parse(cli.ValueOrDefault("revision", "1"));
	var outputDirectory = KpmPaths.EnsureDirectory(cli.ValueOrDefault("out", Path.Combine(directory, "dist")));

	foreach (var platform in port.Platforms)
	{
		var manifest = new EmbeddedManifest
		{
			Name = package.Name,
			Owner = package.Owner,
			Version = port.Version,
			Revision = revision,
			Entry = port.Entry,
			Engines = port.Engines,
			Capabilities = port.Capabilities,
			Dependencies = port.Dependencies
		};
		var result = Packer.Pack(directory, manifest, platform);
		var path = Path.Combine(outputDirectory, result.FileName);
		File.WriteAllBytes(path, result.Bytes);
		output.WriteLine($"{result.FileName}");
		output.WriteLine($"  sha256 {result.Sha256}");
		output.WriteLine($"  {result.Size} bytes, {result.Paths.Count} files -> {path}");
	}

	return 0;
}

/// <summary>
/// Regenerates a source-hosted package's version manifest from its current source. Hashes come from
/// packing rather than from the author, which is what CI later re-checks.
/// </summary>
	async Task<int> BuildManifest(CommandLine cli)
{
	var directory = Path.GetFullPath(cli.Positionals.FirstOrDefault() ?? Directory.GetCurrentDirectory());
	var package = await RegistryTree.ReadPackageAsync(directory);

	if (!package.IsSourceHosted)
	{
		error.WriteLine($"error: {directory} has no port.json (only source-hosted packages are packed here)");
		return 1;
	}

	var repository = cli.ValueOrDefault("repository", "keysharp-org/Packages");
	var revision = int.Parse(cli.ValueOrDefault("revision", "1"));
	var manifest = RegistryTree.BuildVersionManifest(package, revision,
					   (fileName, release) => RegistryTree.ReleaseAssetUrl(repository, package.Id, release, fileName));
	var path = Path.Combine(directory, "versions", $"{manifest.Version}-r{revision}.json");

	// A published release is immutable, so regenerating over one would rewrite history rather than
	// describe it. Correcting a mistake means a new revision.
	if (File.Exists(path) && !cli.Has("force"))
	{
		error.WriteLine($"error: versions/{Path.GetFileName(path)} already exists. "
								+ "Published releases are immutable — bump --revision, or pass --force if it is unpublished.");
		return 1;
	}

	await ManifestJson.WriteFileAsync(path, manifest);
	output.WriteLine($"wrote {path}");

	foreach (var (platform, artifact) in manifest.Artifacts)
		output.WriteLine($"  {platform}: {artifact.Sha256} ({artifact.Size} bytes)");

	return 0;
}

/// <summary>
/// Lays out a throwaway project containing one package and a script that includes it, for an engine
/// to compile-check. A library often does not parse standalone and needs its dependencies present,
/// so validation has to happen through a real include from a real install rather than by handing
/// the engine a source file.
/// </summary>
	async Task<int> Probe(CommandLine cli)
{
	var directory = Path.GetFullPath(cli.Positionals.FirstOrDefault() ?? Directory.GetCurrentDirectory());
	var destination = Path.GetFullPath(cli.ValueOrDefault("out", Path.Combine(Path.GetTempPath(), "kpm-probe")));
	var package = await RegistryTree.ReadPackageAsync(directory);

	if (!package.IsSourceHosted)
	{
		error.WriteLine($"error: {directory} has no port.json");
		return 1;
	}

	var port = package.Port!;
	// The caller names the machine being checked; which artifact that machine gets is the registry's
	// decision, so resolve it the same way an install would rather than packing the name literally.
	var target = cli.ValueOrDefault("platform", Platforms.Current);
	var platform = Platforms.Select(port.Platforms, target);

	if (platform is null)
	{
		error.WriteLine($"error: {package.Id} ships nothing for {target} "
								+ $"(it builds for {string.Join(", ", port.Platforms)})");
		return 1;
	}

	if (Directory.Exists(destination))
		Directory.Delete(destination, recursive: true);

	_ = Directory.CreateDirectory(destination);

	// Install the package's own dependencies from the registry tree being validated, so a probe
	// tests the package as a consumer would get it rather than in isolation.
	if (port.Dependencies.Count > 0)
	{
		var registryRoot = cli.Value("registry")
						   ?? Path.GetFullPath(Path.Combine(directory, "..", "..", ".."));
		var index = IndexBuilder.BuildIndex(await RegistryTree.ReadAsync(registryRoot));
		var resolution = new Resolver(index).Resolve(port.Dependencies,
						 new ResolveContext(System.Version.Parse(cli.ValueOrDefault("engine-version", "0.0.0.17")),
											target,
											cli.ValueOrDefault("engine", Engines.Keysharp)));
		_ = await new Installer(new ArtifactStore()).InstallAsync(resolution, destination);
	}

	var embedded = new EmbeddedManifest
	{
		Name = package.Package.Name,
		Owner = package.Package.Owner,
		Version = port.Version,
		Revision = 1,
		Entry = port.Entry,
		Engines = new Dictionary<string, string>(port.Engines),
		Capabilities = [.. port.Capabilities],
		Dependencies = new Dictionary<string, string>(port.Dependencies)
	};
	var packed = Packer.Pack(directory, embedded, platform);
	Unpacker.Extract(packed.Bytes, Installer.PackageDirectory(destination, package.Id));
	// Written by hand rather than through Installer so the probe works before anything is published.
	var forwarder = Installer.ForwarderPath(destination, package.Id);
	_ = Directory.CreateDirectory(Path.GetDirectoryName(forwarder)!);
	await File.WriteAllTextAsync(forwarder,
								 $"; generated probe forwarder\n#Include %A_LineFile%/../{package.Id.Name}/{port.Entry}\n");
	var probe = Path.Combine(destination, "probe.ks");
	await File.WriteAllTextAsync(probe, $"""
		; Generated by 'kpm probe'. Compile-checks {package.Id} {port.Version} through a real include.
		#Include <KPM/{package.Id.Owner}/{package.Id.Name}>
		ExitApp()

		""");
	output.WriteLine(probe);
	return 0;
}

	async Task<int> BuildIndex(CommandLine cli)
{
	var registryRoot = Path.GetFullPath(cli.Positionals.FirstOrDefault() ?? Directory.GetCurrentDirectory());
	var destination = Path.GetFullPath(cli.ValueOrDefault("out", Path.Combine(registryRoot, "dist")));
	var packages = await RegistryTree.ReadAsync(registryRoot);
	var index = IndexBuilder.BuildIndex(packages);
	var catalog = IndexBuilder.BuildCatalog(index);
	await IndexBuilder.WriteAsync(destination, index, catalog);
	var releases = index.Packages.Sum(p => p.Versions.Count);
	output.WriteLine($"{index.Packages.Count} package(s), {releases} release(s) -> {destination}");
	return 0;
}

	async Task<int> Validate(CommandLine cli)
{
	var registryRoot = Path.GetFullPath(cli.Positionals.FirstOrDefault() ?? Directory.GetCurrentDirectory());
	var problems = await RegistryValidator.ValidateAsync(registryRoot);

	foreach (var problem in problems)
		error.WriteLine($"  {problem}");

	if (problems.Count > 0)
	{
		error.WriteLine($"{problems.Count} problem(s)");
		return 1;
	}

	output.WriteLine("registry is valid");
	return 0;
}

	async Task<int> Mirror(CommandLine cli)
{
	var service = new KpmService();
	var progress = new Progress<string>(line => output.WriteLine($"  {line}"));
	var count = await service.MirrorAsync(progress);
	output.WriteLine($"{count} artifact(s) downloaded; cache is at {KpmPaths.CacheRoot}");
	return 0;
}

	int Cache(CommandLine cli)
{
	var action = cli.Positionals.FirstOrDefault() ?? "dir";

	switch (action)
	{
		case "dir":
			output.WriteLine(KpmPaths.CacheRoot);
			return 0;
		case "clear":
			new ArtifactStore().Clear();
			output.WriteLine("cache cleared");
			return 0;
		default:
			error.WriteLine("usage: kpm cache [dir|clear]");
			return 1;
	}
}

	void Help() => output.WriteLine("""
	kpm — Keysharp package manager

	  kpm add <owner/name>[@range]   add a dependency, then install
	  kpm remove <owner/name>        drop a dependency, then install
	  kpm install                    install exactly what kpm.lock.json names (works offline)
	  kpm update [--offline]         re-resolve within kpm.json's ranges and rewrite the lockfile
	  kpm search <text>              search the registry
	  kpm list                       show installed packages
	  kpm setup [pkg]                perform a package's manual setup step, after showing what it runs
	  kpm pack [dir]                 build .kspkg files from a package directory
	  kpm mirror                     download every artifact in the registry into the local cache
	  kpm cache [dir|clear]          inspect or empty the artifact cache

	Registry maintenance (run inside the registry repository):
	  kpm manifest [dir]             regenerate a source-hosted package's version manifest
	  kpm index [registry]           build index.json.gz and catalog.json
	  kpm validate [registry]        check every manifest, artifact hash and dependency
	  kpm probe [dir] --out <dir>    lay out a project + probe script for an engine to compile-check

	Options:
	  --project <dir>          act on this project instead of the working directory
	  --engine-version <v>     resolve for this engine version (default: $KPM_ENGINE_VERSION)
	  --engine <name>          keysharp (default) or autohotkey
	  --platform <rid>         resolve for another platform, e.g. linux-x64
	  --offline                do not contact the registry
	  --all-engines            in search, also list packages that do not support your engine
	""");
}
