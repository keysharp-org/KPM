using System.IO.Compression;
using System.Net;
using System.Text;
using Kpm.Artifacts;
using Kpm.Install;
using Kpm.Model;
using Kpm.Packing;
using Kpm.Registry;
using Kpm.Resolution;
using NUnit.Framework;

namespace Kpm.Tests;

/// <summary>
/// Serves a directory over HTTP so the registry and artifact code can be exercised exactly as it
/// runs in production — same client, same URLs, same verification — without a network.
/// </summary>
internal sealed class DirectoryHandler(string root) : HttpMessageHandler
{
	public int Requests { get; private set; }
	public Func<string, byte[]?>? Intercept { get; set; }

	protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
	{
		Requests++;
		var relative = request.RequestUri!.AbsolutePath.TrimStart('/');
		var replaced = Intercept?.Invoke(relative);

		if (replaced is not null)
			return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(replaced) });

		var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));

		if (!File.Exists(path))
			return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));

		return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
		{
			Content = new ByteArrayContent(File.ReadAllBytes(path))
		});
	}
}

[TestFixture]
public sealed class EndToEndTests
{
	private string root = "";
	private string registryRoot = "";
	private string projectRoot = "";
	private DirectoryHandler handler = null!;
	private HttpClient http = null!;

	[SetUp]
	public void SetUp()
	{
		root = Path.Combine(Path.GetTempPath(), "kpm-e2e", Guid.NewGuid().ToString("N"));
		registryRoot = Path.Combine(root, "registry");
		projectRoot = Path.Combine(root, "project");
		_ = Directory.CreateDirectory(registryRoot);
		_ = Directory.CreateDirectory(projectRoot);
		// Point the whole of KPM's machine state at the temp tree, so tests never touch the real cache.
		KpmPaths.Override = Path.Combine(root, "home");
		handler = new DirectoryHandler(registryRoot);
		http = new HttpClient(handler);
	}

	[TearDown]
	public void TearDown()
	{
		http.Dispose();
		KpmPaths.Override = null;

		if (Directory.Exists(root))
			Directory.Delete(root, recursive: true);
	}

	private RegistrySource Source => new("test", ["http://registry.test"]);

	/// <summary>Builds a package, publishes it into the fake registry, and returns its manifest.</summary>
	private VersionManifest Publish(string owner, string name, string version, string entry = "src/Entry.ks",
									Dictionary<string, string>? dependencies = null, string engine = ">=0.0.0.17")
	{
		var source = Path.Combine(root, "src", name);
		_ = Directory.CreateDirectory(Path.Combine(source, "src"));
		File.WriteAllText(Path.Combine(source, Path.GetFileName(entry) == entry ? entry : entry.Replace('/', Path.DirectorySeparatorChar)),
						  $"; {name} {version}\n{name}_Marker() {{\n}}\n");
		var embedded = new EmbeddedManifest
		{
			Name = name,
			Owner = owner,
			Version = version,
			Revision = 1,
			Entry = entry,
			Engines = { ["keysharp"] = engine },
			Dependencies = dependencies ?? []
		};
		var packed = Packer.Pack(source, embedded, Platforms.Any);
		var artifactDirectory = Path.Combine(registryRoot, "artifacts");
		_ = Directory.CreateDirectory(artifactDirectory);
		File.WriteAllBytes(Path.Combine(artifactDirectory, packed.FileName), packed.Bytes);
		return new VersionManifest
		{
			Owner = owner,
			Name = name,
			Version = version,
			Revision = 1,
			Entry = entry,
			Published = DateTimeOffset.UnixEpoch,
			Engines = { ["keysharp"] = engine },
			Platforms = [Platforms.Any],
			Dependencies = dependencies ?? [],
			Artifacts =
			{
				[Platforms.Any] = new ArtifactRef
				{
					Sha256 = packed.Sha256,
					Size = packed.Size,
					Sources = [$"http://registry.test/artifacts/{packed.FileName}"]
				}
			}
		};
	}

	private void WriteIndex(params VersionManifest[] releases)
	{
		var index = new RegistryIndex { Generated = DateTimeOffset.UnixEpoch };

		foreach (var group in releases.GroupBy(r => $"{r.Owner}/{r.Name}"))
		{
			var first = group.First();
			index.Packages.Add(new IndexedPackage
			{
				Package = new PackageManifest
				{
					Owner = first.Owner,
					Name = first.Name,
					Description = $"{first.Name} test package"
				},
				Versions = [.. group]
			});
		}

		var json = ManifestJson.Write(index);
		using var file = File.Create(Path.Combine(registryRoot, "index.json.gz"));
		using var gzip = new GZipStream(file, CompressionLevel.Optimal);
		gzip.Write(Encoding.UTF8.GetBytes(json));
	}

	private KpmService Service() => new(new RegistryClient(http, Source), new ArtifactStore(http));

	private static ResolveContext Context() => new(new Version("0.0.0.17"), Platforms.Any);

	[Test]
	public async Task AddResolvesInstallsAndWritesAForwarderTheIncludeSyntaxCanFind()
	{
		WriteIndex(Publish("tester", "demo", "1.0.0"));
		var project = await Project.LoadAsync(projectRoot);
		project.SetDependency(PackageId.Parse("tester/demo"), "^1.0");
		await project.SaveManifestAsync();
		var report = await Service().UpdateAsync(project, Context());
		Assert.That(report.Installed, Has.Count.EqualTo(1));
		var installed = report.Installed[0];
		Assert.That(installed.IncludePath, Is.EqualTo("KPM/tester/demo"));
		Assert.That(File.Exists(Path.Combine(installed.Directory, "src", "Entry.ks")), Is.True);
		// #Include <KPM/tester/demo> resolves to Lib/KPM/tester/demo.ks, which forwards to the entry.
		var forwarder = Path.Combine(projectRoot, "Lib", "KPM", "tester", "demo.ks");
		Assert.That(File.Exists(forwarder), Is.True);
		Assert.That(await File.ReadAllTextAsync(forwarder), Does.Contain("#Include %A_LineFile%/../demo/src/Entry.ks"));
		Assert.That(File.Exists(project.LockPath), Is.True);
	}

	[Test]
	public async Task InstallFromALockfileNeedsNoNetwork()
	{
		WriteIndex(Publish("tester", "demo", "1.0.0"));
		var project = await Project.LoadAsync(projectRoot);
		project.SetDependency(PackageId.Parse("tester/demo"), "^1.0");
		await project.SaveManifestAsync();
		_ = await Service().UpdateAsync(project, Context());
		Directory.Delete(Path.Combine(projectRoot, "Lib"), recursive: true);
		// A client with no reachable registry and no artifact source at all: everything must come
		// from the content-addressed cache the first install populated.
		var offlineHandler = new DirectoryHandler(Path.Combine(root, "nothing"));
		using var offlineHttp = new HttpClient(offlineHandler);
		var offline = new KpmService(new RegistryClient(offlineHttp, Source), new ArtifactStore(offlineHttp));
		var reloaded = await Project.LoadAsync(projectRoot);
		var report = await offline.InstallLockedAsync(reloaded);
		Assert.That(report.Installed, Has.Count.EqualTo(1));
		Assert.That(offlineHandler.Requests, Is.Zero);
		Assert.That(File.Exists(Path.Combine(projectRoot, "Lib", "KPM", "tester", "demo.ks")), Is.True);
	}

	[Test]
	public async Task ATamperedArtifactIsRejectedBeforeItIsOpened()
	{
		var release = Publish("tester", "demo", "1.0.0");
		WriteIndex(release);
		// The registry serves different bytes than the manifest's hash promises.
		handler.Intercept = path => path.StartsWith("artifacts/", StringComparison.Ordinal)
									? Encoding.UTF8.GetBytes("not the package you asked for")
									: null;
		var project = await Project.LoadAsync(projectRoot);
		project.SetDependency(PackageId.Parse("tester/demo"), "^1.0");
		await project.SaveManifestAsync();
		var error = Assert.ThrowsAsync<InvalidOperationException>(() => Service().UpdateAsync(project, Context()));
		Assert.That(error!.Message, Does.Contain("could not download"));
		Assert.That(Directory.Exists(Path.Combine(projectRoot, "Lib")), Is.False);
	}

	/// <summary>
	/// A package needing a driver looks identical to one that works, right up until it fails, so the
	/// note has to reach the user. kpm reports it and never acts on it.
	/// </summary>
	[Test]
	public async Task AManualSetupStepIsReportedAfterInstalling()
	{
		var release = Publish("tester", "needsdriver", "1.0.0");
		release.Setup = new SetupNote
		{
			Message = "Requires the Interception driver, installed separately with administrator rights.",
			Url = "https://example/install",
			Script = "src/install.exe"
		};
		WriteIndex(release);
		var project = await Project.LoadAsync(projectRoot);
		project.SetDependency(PackageId.Parse("tester/needsdriver"), "^1.0");
		await project.SaveManifestAsync();
		var report = await Service().UpdateAsync(project, Context());
		Assert.That(report.Setup, Has.Count.EqualTo(1));
		Assert.That(report.Setup![0].Note.Message, Does.Contain("Interception driver"));
		Assert.That(report.Setup[0].Id.ToString(), Is.EqualTo("tester/needsdriver"));
	}

	/// <summary>
	/// A setup script path comes from a manifest, so it is untrusted input: it must land inside the
	/// package that declared it, or a registry entry could point kpm at any executable on the disk.
	/// </summary>
	[Test]
	public void ASetupScriptCannotPointOutsideItsOwnPackage()
	{
		var note = new SetupNote { Message = "x", Script = "../../../evil.exe" };
		var id = PackageId.Parse("tester/demo");
		var error = Assert.Throws<InvalidOperationException>(
						() => SetupRunner.Plan([(id, note)], projectRoot, Platforms.Any));
		Assert.That(error!.Message, Does.Contain("escapes the package directory"));
	}

	[Test]
	public void ASetupStepIsPlannedOnlyForThePlatformsItDeclares()
	{
		var id = PackageId.Parse("tester/demo");
		var windowsOnly = new SetupNote { Message = "install driver", Platforms = ["win"] };
		Assert.That(SetupRunner.Plan([(id, windowsOnly)], projectRoot, "win-x64"), Has.Count.EqualTo(1));
		Assert.That(SetupRunner.Plan([(id, windowsOnly)], projectRoot, "linux-x64"), Is.Empty);
		// No platforms declared means it applies everywhere.
		var anywhere = new SetupNote { Message = "do a thing" };
		Assert.That(SetupRunner.Plan([(id, anywhere)], projectRoot, "linux-x64"), Has.Count.EqualTo(1));
	}

	/// <summary>Nothing runs without a confirmation, which is what separates this from an install hook.</summary>
	[Test]
	public async Task NothingRunsWhenTheStepIsDeclined()
	{
		var id = PackageId.Parse("tester/demo");
		var directory = Installer.PackageDirectory(projectRoot, id);
		_ = Directory.CreateDirectory(directory);
		var script = Path.Combine(directory, "setup.cmd");
		await File.WriteAllTextAsync(script, "@echo should not run\n");
		var step = SetupRunner.Plan([(id, new SetupNote { Message = "x", Script = "setup.cmd" })],
									projectRoot, Platforms.Any).Single();
		Assert.That(step.ScriptPath, Is.Not.Null);
		Assert.That(await SetupRunner.RunAsync(step, _ => false), Is.False);
	}

	[Test]
	public void AStepWhosePackageShipsNoScriptIsStillReported()
	{
		var id = PackageId.Parse("tester/demo");
		var step = SetupRunner.Plan([(id, new SetupNote { Message = "install it yourself", Script = "absent.exe" })],
									projectRoot, Platforms.Any).Single();
		// Reported so the user sees the instructions, but with nothing to run.
		Assert.That(step.ScriptPath, Is.Null);
		Assert.That(step.Note.IsRunnable, Is.True);
	}

	[Test]
	public async Task APackageWithoutASetupStepReportsNone()
	{
		WriteIndex(Publish("tester", "plain", "1.0.0"));
		var project = await Project.LoadAsync(projectRoot);
		project.SetDependency(PackageId.Parse("tester/plain"), "^1.0");
		await project.SaveManifestAsync();
		var report = await Service().UpdateAsync(project, Context());
		Assert.That(report.Setup, Is.Empty);
	}

	[Test]
	public async Task TransitiveDependenciesAreInstalledToo()
	{
		WriteIndex(
			Publish("tester", "top", "1.0.0", dependencies: new() { ["tester/leaf"] = "^2.0" }),
			Publish("tester", "leaf", "2.1.0"));
		var project = await Project.LoadAsync(projectRoot);
		project.SetDependency(PackageId.Parse("tester/top"), "^1.0");
		await project.SaveManifestAsync();
		var report = await Service().UpdateAsync(project, Context());
		Assert.That(report.Installed.Select(p => p.Id.ToString()), Is.EquivalentTo(new[] { "tester/top", "tester/leaf" }));
	}

	[Test]
	public async Task RemovingADependencyPrunesItsFiles()
	{
		WriteIndex(Publish("tester", "one", "1.0.0"), Publish("tester", "two", "1.0.0"));
		var project = await Project.LoadAsync(projectRoot);
		project.SetDependency(PackageId.Parse("tester/one"), "^1.0");
		project.SetDependency(PackageId.Parse("tester/two"), "^1.0");
		await project.SaveManifestAsync();
		_ = await Service().UpdateAsync(project, Context());
		_ = project.RemoveDependency(PackageId.Parse("tester/two"));
		await project.SaveManifestAsync();
		_ = await Service().UpdateAsync(project, Context());
		Assert.That(Directory.Exists(Path.Combine(projectRoot, "Lib", "KPM", "tester", "one")), Is.True);
		Assert.That(Directory.Exists(Path.Combine(projectRoot, "Lib", "KPM", "tester", "two")), Is.False);
		Assert.That(File.Exists(Path.Combine(projectRoot, "Lib", "KPM", "tester", "two.ks")), Is.False);
	}

	/// <summary>
	/// A stale index is still a usable index: everything except discovering new versions keeps
	/// working when the registry cannot be reached.
	/// </summary>
	[Test]
	public async Task AnUnreachableRegistryFallsBackToTheCachedIndexWithAWarning()
	{
		WriteIndex(Publish("tester", "demo", "1.0.0"));
		var client = new RegistryClient(http, Source);
		_ = await client.GetIndexAsync();
		File.Delete(Path.Combine(registryRoot, "index.json.gz"));
		var result = await client.GetIndexAsync();
		Assert.That(result.FromCache, Is.True);
		Assert.That(result.Warning, Does.Contain("unreachable"));
		Assert.That(result.Index.Find(PackageId.Parse("tester/demo")), Is.Not.Null);
	}

	[Test]
	public void ExtractionRefusesAnArchiveThatWouldEscapeItsDirectory()
	{
		// Built by hand: Packer would refuse to produce this, which is the point of checking the
		// reader separately from the writer.
		using var buffer = new MemoryStream();

		using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
		{
			var entry = zip.CreateEntry("../escaped.ks");
			using var stream = entry.Open();
			stream.Write("pwned"u8);
		}

		var destination = Path.Combine(root, "extract");
		var error = Assert.Throws<InvalidOperationException>(() => Unpacker.Extract(buffer.ToArray(), destination));
		Assert.That(error!.Message, Does.Contain(".."));
		Assert.That(File.Exists(Path.Combine(root, "escaped.ks")), Is.False);
	}
}
