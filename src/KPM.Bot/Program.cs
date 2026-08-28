using Kpm.Bot;
using Kpm;

// The migration bot. Runs server-side with a token, unlike the package manager itself, which never
// touches an API — see GitHub.cs.
var command = args.Length > 0 && !args[0].StartsWith("--", StringComparison.Ordinal) ? args[0] : "help";
var cli = new CommandLine(args.Skip(1));

try
{
	switch (command.ToLowerInvariant())
	{
		case "import-aris": return await ImportAris(cli);
		case "publish": return await Publish(cli);
		case "help": case "--help": case "-h": Help(); return 0;
		default:
			Console.Error.WriteLine($"error: unknown command '{command}'. Run 'kpm-bot help'.");
			return 1;
	}
}
catch (Exception ex) when (ex is InvalidOperationException or IOException or HttpRequestException or ArgumentException)
{
	Console.Error.WriteLine($"error: {ex.Message}");
	return 1;
}

static async Task<int> ImportAris(CommandLine cli)
{
	var registry = Path.GetFullPath(cli.ValueOrDefault("registry", Directory.GetCurrentDirectory()));
	var source = cli.ValueOrDefault("index",
					 "https://raw.githubusercontent.com/Descolada/Aris/main/assets/index.json");
	string json;

	if (File.Exists(source))
		json = await File.ReadAllTextAsync(source);
	else
	{
		using var http = new HttpClient();
		http.DefaultRequestHeaders.UserAgent.ParseAdd("kpm-bot/0.1");
		json = await http.GetStringAsync(source);
	}

	var entries = ArisIndex.Read(json);
	Console.WriteLine($"{entries.Count} package(s) in the Aris index");
	var options = new ImportOptions
	{
		RegistryRoot = registry,
		MaxVersions = int.Parse(cli.ValueOrDefault("max-versions", "10")),
		Limit = cli.Value("limit") is { } limit ? int.Parse(limit) : null,
		DryRun = cli.Has("dry-run"),
		Only = cli.Value("only"),
		ArtifactOutput = cli.Value("artifacts"),
		ArtifactRepository = cli.ValueOrDefault("repository", "keysharp-org/Packages")
	};
	using var github = new GitHub(cli.Value("token"));
	var importer = new Importer(github, options);
	var progress = new Progress<string>(Console.WriteLine);
	var outcomes = await importer.ImportAsync(entries, progress);
	Console.WriteLine();

	foreach (var group in outcomes.GroupBy(o => o.Status).OrderBy(g => g.Key, StringComparer.Ordinal))
		Console.WriteLine($"{group.Key,-10} {group.Count()}");

	Console.WriteLine($"{outcomes.Sum(o => o.Releases)} release(s) {(options.DryRun ? "would be" : "")} written");
	var failures = outcomes.Where(o => o.Status == "failed").ToList();

	if (failures.Count > 0)
	{
		Console.WriteLine("\nfailures:");

		foreach (var failure in failures)
			Console.WriteLine($"  {failure.Key}: {failure.Note}");
	}

	return 0;
}

static async Task<int> Publish(CommandLine cli)
{
	var registry = Path.GetFullPath(cli.ValueOrDefault("registry", Directory.GetCurrentDirectory()));
	var artifacts = cli.Value("artifacts")
					?? throw new ArgumentException("--artifacts is required: it is the directory 'import-aris' wrote to");
	var publisher = new Publisher(cli.ValueOrDefault("repository", "keysharp-org/Packages"), cli.Has("dry-run"));
	var count = await publisher.PublishAsync(registry, Path.GetFullPath(artifacts),
											 new Progress<string>(Console.WriteLine));
	Console.WriteLine($"{count} release(s) {(cli.Has("dry-run") ? "would be " : "")}published");
	return 0;
}

static void Help() => Console.WriteLine("""
	kpm-bot — registry ingestion

	  kpm-bot import-aris --registry <dir> [options]
	      Import the Aris package index. Entries hosted on ahkscript/ScriptHub are imported with
	      a synthesized 0.x history from their file's commits; repositories that tag releases keep
	      their own version numbers. Every import declares AutoHotkey only — a Keysharp claim has
	      to be earned by engine validation.

	  kpm-bot publish --registry <dir> --artifacts <dir> [--repository <o/r>] [--dry-run]
	      Upload packed artifacts to the registry's releases. Imported packages have no source in
	      the registry to rebuild from, so their artifacts must exist before their manifests merge.
	      Existing releases are never re-uploaded: a published release is immutable.

	Options:
	  --registry <dir>       the registry repository to write into (default: working directory)
	  --index <path|url>     the Aris index (default: the published one)
	  --only <text>          import only entries whose key contains this
	  --limit <n>            import at most this many packages
	  --max-versions <n>     keep at most this many releases per package (default 10, newest)
	  --artifacts <dir>      also write the packed .kspkg files here, for publishing
	  --repository <o/r>     the repository whose releases artifact URLs point at
	  --token <token>        GitHub token (default: $GITHUB_TOKEN or $GH_TOKEN)
	  --dry-run              report what would be imported and write nothing
	""");
