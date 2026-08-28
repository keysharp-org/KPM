using Kpm;
using Kpm.Artifacts;
using Kpm.Install;
using Kpm.Model;
using Kpm.Packing;
using Kpm.Registry;
using Kpm.Resolution;

var command = args.Length > 0 && !args[0].StartsWith("--", StringComparison.Ordinal) ? args[0] : "help";
var cli = new CommandLine(args.Skip(1));

try
{
	return await Run(command, cli);
}
catch (ResolutionException ex)
{
	Console.Error.WriteLine($"error: {ex.Message}");
	return 1;
}
catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException or IOException
						   or ArgumentException or UnauthorizedAccessException)
{
	Console.Error.WriteLine($"error: {ex.Message}");
	return 1;
}

static async Task<int> Run(string command, CommandLine cli)
{
	switch (command.ToLowerInvariant())
	{
		case "add": return await Add(cli);
		case "remove": case "rm": return await Remove(cli);
		case "install": return await Install(cli);
		case "update": return await Update(cli);
		case "search": return await Search(cli);
		case "list": case "ls": return await List(cli);
		case "pack": return Pack(cli);
		case "manifest": return await BuildManifest(cli);
		case "index": return await BuildIndex(cli);
		case "validate": return await Validate(cli);
		case "probe": return await Probe(cli);
		case "mirror": return await Mirror(cli);
		case "cache": return Cache(cli);
		case "help": case "--help": case "-h": Help(); return 0;
		case "version": case "--version": Console.WriteLine(Version()); return 0;
		default:
			Console.Error.WriteLine($"error: unknown command '{command}'. Run 'kpm help'.");
			return 1;
	}
}

static string Version() =>
	System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.1.0";

static string ProjectRoot(CommandLine cli) =>
	Project.FindRoot(cli.ValueOrDefault("project", Directory.GetCurrentDirectory()));

/// <summary>
/// Which engine version to resolve for. Explicit flag first, then the environment (how CI and the
/// Keysharp host pass it), then the version this build was cut against.
/// </summary>
static ResolveContext Context(CommandLine cli)
{
	var text = cli.Value("engine-version")
			   ?? Environment.GetEnvironmentVariable("KPM_ENGINE_VERSION")
			   ?? "0.0.0.17";

	if (!System.Version.TryParse(text, out var version))
		throw new ArgumentException($"'{text}' is not a version");

	return new ResolveContext(version, cli.ValueOrDefault("platform", Platforms.Current),
							  cli.ValueOrDefault("engine", Engines.Keysharp));
}

static async Task<int> Add(CommandLine cli)
{
	if (cli.Positionals.Count == 0)
	{
		Console.Error.WriteLine("usage: kpm add <owner/name>[@range]");
		return 1;
	}

	var project = await Project.LoadAsync(ProjectRoot(cli));
	var service = new KpmService();

	foreach (var request in cli.Positionals)
	{
		var at = request.LastIndexOf('@');
		var idText = at > 0 ? request[..at] : request;
		var range = at > 0 ? request[(at + 1)..] : null;

		if (!PackageId.TryParse(idText, out var id, out var error))
		{
			Console.Error.WriteLine($"error: {error}");
			return 1;
		}

		// Without an explicit range, pin the caret range of whatever is newest now: a bare add
		// should not mean "always newest", which would change what a project builds over time.
		range ??= await CaretRangeForLatest(service, id.Value, Context(cli));
		project.SetDependency(id.Value, range);
		Console.WriteLine($"added {id} {range}");
	}

	await project.SaveManifestAsync();
	return await Resolve(project, service, cli);
}

static async Task<string> CaretRangeForLatest(KpmService service, PackageId id, ResolveContext context)
{
	var index = (await service.GetIndexAsync()).Index;
	var package = index.Find(id) ?? throw new InvalidOperationException($"no package '{id}' in the registry");
	var newest = package.Installable()
				 .Where(v => v.Engines.ContainsKey(context.Engine))
				 .MaxBy(v => v.Release)
				 ?? throw new InvalidOperationException($"'{id}' has no release for {context.Engine}");
	return $"^{newest.Version}";
}

static async Task<int> Remove(CommandLine cli)
{
	if (cli.Positionals.Count == 0)
	{
		Console.Error.WriteLine("usage: kpm remove <owner/name>");
		return 1;
	}

	var project = await Project.LoadAsync(ProjectRoot(cli));

	foreach (var request in cli.Positionals)
	{
		if (!PackageId.TryParse(request, out var id, out var error))
		{
			Console.Error.WriteLine($"error: {error}");
			return 1;
		}

		Console.WriteLine(project.RemoveDependency(id.Value) ? $"removed {id}" : $"{id} was not a dependency");
	}

	await project.SaveManifestAsync();
	return await Resolve(project, new KpmService(), cli);
}

static async Task<int> Install(CommandLine cli)
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

static Task<int> Update(CommandLine cli) => UpdateAsync(cli);

static async Task<int> UpdateAsync(CommandLine cli)
{
	var project = await Project.LoadAsync(ProjectRoot(cli));
	return await Resolve(project, new KpmService(), cli);
}

static async Task<int> Resolve(Project project, KpmService service, CommandLine cli)
{
	if (project.Manifest.Dependencies.Count == 0)
	{
		Console.WriteLine("no dependencies");
		return 0;
	}

	var report = await service.UpdateAsync(project, Context(cli), refresh: !cli.Has("offline"));
	Report(report, project);
	return 0;
}

static void Report(InstallReport report, Project project)
{
	if (report.Warning is not null)
		Console.Error.WriteLine($"warning: {report.Warning}");

	foreach (var package in report.Installed)
		Console.WriteLine($"  {package.Id} {package.Version.ToDisplayString()}  ->  #Include <{package.IncludePath}>");

	Console.WriteLine($"{report.Installed.Count} package(s) installed into {Path.Combine(project.Directory, "Lib", "KPM")}");
}

static async Task<int> Search(CommandLine cli)
{
	var service = new KpmService();
	var fetch = await service.GetIndexAsync(refresh: !cli.Has("offline"));

	if (fetch.Warning is not null)
		Console.Error.WriteLine($"warning: {fetch.Warning}");

	var context = Context(cli);
	var all = cli.Has("all-engines");
	var matches = fetch.Index.Search(cli.Positionals.FirstOrDefault() ?? "").ToList();

	if (matches.Count == 0)
	{
		Console.WriteLine("no matching packages");
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
		Console.WriteLine($"{match.Package.Owner}/{match.Package.Name}  {newest.Version}");
		Console.WriteLine($"    {match.Package.Description}");
		Console.WriteLine($"    engines: {engines}   platforms: {platforms}");
	}

	return 0;
}

static async Task<int> List(CommandLine cli)
{
	var project = await Project.LoadAsync(ProjectRoot(cli));

	if (project.Lock is null || project.Lock.Packages.Count == 0)
	{
		Console.WriteLine("no packages installed");
		return 0;
	}

	foreach (var package in project.Lock.Packages)
		Console.WriteLine($"{package.Id}  {package.Version}  (r{package.Revision}, {package.Platform})");

	return 0;
}

static int Pack(CommandLine cli)
{
	var directory = Path.GetFullPath(cli.Positionals.FirstOrDefault() ?? Directory.GetCurrentDirectory());
	var portPath = Path.Combine(directory, "port.json");
	var packagePath = Path.Combine(directory, "package.json");

	if (!File.Exists(portPath) || !File.Exists(packagePath))
	{
		Console.Error.WriteLine($"error: {directory} needs both package.json and port.json to be packed");
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
		Console.WriteLine($"{result.FileName}");
		Console.WriteLine($"  sha256 {result.Sha256}");
		Console.WriteLine($"  {result.Size} bytes, {result.Paths.Count} files -> {path}");
	}

	return 0;
}

/// <summary>
/// Regenerates a source-hosted package's version manifest from its current source. Hashes come from
/// packing rather than from the author, which is what CI later re-checks.
/// </summary>
static async Task<int> BuildManifest(CommandLine cli)
{
	var directory = Path.GetFullPath(cli.Positionals.FirstOrDefault() ?? Directory.GetCurrentDirectory());
	var package = await RegistryTree.ReadPackageAsync(directory);

	if (!package.IsSourceHosted)
	{
		Console.Error.WriteLine($"error: {directory} has no port.json (only source-hosted packages are packed here)");
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
		Console.Error.WriteLine($"error: versions/{Path.GetFileName(path)} already exists. "
								+ "Published releases are immutable — bump --revision, or pass --force if it is unpublished.");
		return 1;
	}

	await ManifestJson.WriteFileAsync(path, manifest);
	Console.WriteLine($"wrote {path}");

	foreach (var (platform, artifact) in manifest.Artifacts)
		Console.WriteLine($"  {platform}: {artifact.Sha256} ({artifact.Size} bytes)");

	return 0;
}

/// <summary>
/// Lays out a throwaway project containing one package and a script that includes it, for an engine
/// to compile-check. A library often does not parse standalone and needs its dependencies present,
/// so validation has to happen through a real include from a real install rather than by handing
/// the engine a source file.
/// </summary>
static async Task<int> Probe(CommandLine cli)
{
	var directory = Path.GetFullPath(cli.Positionals.FirstOrDefault() ?? Directory.GetCurrentDirectory());
	var output = Path.GetFullPath(cli.ValueOrDefault("out", Path.Combine(Path.GetTempPath(), "kpm-probe")));
	var platform = cli.ValueOrDefault("platform", Platforms.Any);
	var package = await RegistryTree.ReadPackageAsync(directory);

	if (!package.IsSourceHosted)
	{
		Console.Error.WriteLine($"error: {directory} has no port.json");
		return 1;
	}

	var port = package.Port!;

	if (Directory.Exists(output))
		Directory.Delete(output, recursive: true);

	_ = Directory.CreateDirectory(output);

	// Install the package's own dependencies from the registry tree being validated, so a probe
	// tests the package as a consumer would get it rather than in isolation.
	if (port.Dependencies.Count > 0)
	{
		var registryRoot = cli.Value("registry")
						   ?? Path.GetFullPath(Path.Combine(directory, "..", "..", ".."));
		var index = IndexBuilder.BuildIndex(await RegistryTree.ReadAsync(registryRoot));
		var resolution = new Resolver(index).Resolve(port.Dependencies,
						 new ResolveContext(System.Version.Parse(cli.ValueOrDefault("engine-version", "0.0.0.17")),
											platform == Platforms.Any ? Platforms.Current : platform,
											cli.ValueOrDefault("engine", Engines.Keysharp)));
		_ = await new Installer(new ArtifactStore()).InstallAsync(resolution, output);
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
	var target = Installer.PackageDirectory(output, package.Id);
	Unpacker.Extract(packed.Bytes, target);
	// Written by hand rather than through Installer so the probe works before anything is published.
	var forwarder = Installer.ForwarderPath(output, package.Id);
	_ = Directory.CreateDirectory(Path.GetDirectoryName(forwarder)!);
	await File.WriteAllTextAsync(forwarder,
								 $"; generated probe forwarder\n#Include %A_LineFile%/../{package.Id.Name}/{port.Entry}\n");
	var probe = Path.Combine(output, "probe.ks");
	await File.WriteAllTextAsync(probe, $"""
		; Generated by 'kpm probe'. Compile-checks {package.Id} {port.Version} through a real include.
		#Include <KPM/{package.Id.Owner}/{package.Id.Name}>
		ExitApp()

		""");
	Console.WriteLine(probe);
	return 0;
}

static async Task<int> BuildIndex(CommandLine cli)
{
	var registryRoot = Path.GetFullPath(cli.Positionals.FirstOrDefault() ?? Directory.GetCurrentDirectory());
	var output = Path.GetFullPath(cli.ValueOrDefault("out", Path.Combine(registryRoot, "dist")));
	var packages = await RegistryTree.ReadAsync(registryRoot);
	var index = IndexBuilder.BuildIndex(packages);
	var catalog = IndexBuilder.BuildCatalog(index);
	await IndexBuilder.WriteAsync(output, index, catalog);
	var releases = index.Packages.Sum(p => p.Versions.Count);
	Console.WriteLine($"{index.Packages.Count} package(s), {releases} release(s) -> {output}");
	return 0;
}

static async Task<int> Validate(CommandLine cli)
{
	var registryRoot = Path.GetFullPath(cli.Positionals.FirstOrDefault() ?? Directory.GetCurrentDirectory());
	var problems = await RegistryValidator.ValidateAsync(registryRoot);

	foreach (var problem in problems)
		Console.Error.WriteLine($"  {problem}");

	if (problems.Count > 0)
	{
		Console.Error.WriteLine($"{problems.Count} problem(s)");
		return 1;
	}

	Console.WriteLine("registry is valid");
	return 0;
}

static async Task<int> Mirror(CommandLine cli)
{
	var service = new KpmService();
	var progress = new Progress<string>(line => Console.WriteLine($"  {line}"));
	var count = await service.MirrorAsync(progress);
	Console.WriteLine($"{count} artifact(s) downloaded; cache is at {KpmPaths.CacheRoot}");
	return 0;
}

static int Cache(CommandLine cli)
{
	var action = cli.Positionals.FirstOrDefault() ?? "dir";

	switch (action)
	{
		case "dir":
			Console.WriteLine(KpmPaths.CacheRoot);
			return 0;
		case "clear":
			new ArtifactStore().Clear();
			Console.WriteLine("cache cleared");
			return 0;
		default:
			Console.Error.WriteLine("usage: kpm cache [dir|clear]");
			return 1;
	}
}

static void Help() => Console.WriteLine("""
	kpm — Keysharp package manager

	  kpm add <owner/name>[@range]   add a dependency, then install
	  kpm remove <owner/name>        drop a dependency, then install
	  kpm install                    install exactly what kpm.lock.json names (works offline)
	  kpm update [--offline]         re-resolve within kpm.json's ranges and rewrite the lockfile
	  kpm search <text>              search the registry
	  kpm list                       show installed packages
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
