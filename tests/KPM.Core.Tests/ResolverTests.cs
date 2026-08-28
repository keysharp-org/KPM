using Kpm.Model;
using Kpm.Registry;
using Kpm.Resolution;
using NUnit.Framework;

namespace Kpm.Tests;

[TestFixture]
public sealed class ResolverTests
{
	private static readonly Version keysharp = new("0.0.0.17");

	private static VersionManifest Release(string id, string version, string? engine = ">=0.0.0.17",
										   Dictionary<string, string>? dependencies = null,
										   params string[] platforms)
	{
		var parts = id.Split('/');
		var manifest = new VersionManifest
		{
			Owner = parts[0],
			Name = parts[1],
			Version = version,
			Revision = 1,
			Entry = "src/Entry.ks",
			Dependencies = dependencies ?? [],
			Platforms = [.. platforms.Length > 0 ? platforms : [Platforms.Any]]
		};

		if (engine is not null)
			manifest.Engines["keysharp"] = engine;

		foreach (var platform in manifest.Platforms)
			manifest.Artifacts[platform] = new ArtifactRef { Sha256 = new string('a', 64), Size = 1, Sources = ["https://example/x"] };

		return manifest;
	}

	private static RegistryIndex Index(params VersionManifest[] releases)
	{
		var index = new RegistryIndex();

		foreach (var group in releases.GroupBy(r => $"{r.Owner}/{r.Name}"))
		{
			var first = group.First();
			index.Packages.Add(new IndexedPackage
			{
				Package = new PackageManifest { Owner = first.Owner, Name = first.Name, Description = "test" },
				Versions = [.. group]
			});
		}

		return index;
	}

	private static ResolveContext Context(string platform = "win-x64") => new(keysharp, platform);

	[Test]
	public void PicksTheHighestVersionInRange()
	{
		var index = Index(Release("a/json", "1.0.0"), Release("a/json", "1.4.0"), Release("a/json", "2.0.0"));
		var resolution = new Resolver(index).Resolve(new Dictionary<string, string> { ["a/json"] = "^1.0" }, Context());
		Assert.That(resolution.Packages.Single().Manifest.Version, Is.EqualTo("1.4.0"));
	}

	/// <summary>
	/// npm's rule for 0.x: <c>^0.4</c> allows 0.4.x but not 0.5.0. It matters here because the
	/// registry synthesizes 0.x versions for scripts with no upstream versioning, so most of the
	/// corpus lives in exactly this range.
	/// </summary>
	[Test]
	public void CaretOnAZeroMajorDoesNotCrossTheMinor()
	{
		var index = Index(Release("a/x", "0.4.0"), Release("a/x", "0.4.7"), Release("a/x", "0.5.0"));
		var resolution = new Resolver(index).Resolve(new Dictionary<string, string> { ["a/x"] = "^0.4" }, Context());
		Assert.That(resolution.Packages.Single().Manifest.Version, Is.EqualTo("0.4.7"));
	}

	[Test]
	public void ResolvesTransitiveDependencies()
	{
		var index = Index(
						Release("a/top", "1.0.0", dependencies: new() { ["a/mid"] = "^1.0" }),
						Release("a/mid", "1.2.0", dependencies: new() { ["a/leaf"] = "^2.0" }),
						Release("a/leaf", "2.1.0"));
		var resolution = new Resolver(index).Resolve(new Dictionary<string, string> { ["a/top"] = "^1.0" }, Context());
		Assert.That(resolution.Packages.Select(p => p.Id.ToString()),
					Is.EquivalentTo(new[] { "a/top", "a/mid", "a/leaf" }));
	}

	[Test]
	public void UnifiesTwoCompatibleRequestsOnOneVersion()
	{
		var index = Index(
						Release("a/top", "1.0.0", dependencies: new() { ["a/shared"] = ">=1.2" }),
						Release("a/other", "1.0.0", dependencies: new() { ["a/shared"] = "^1.0" }),
						Release("a/shared", "1.0.0"), Release("a/shared", "1.5.0"));
		var resolution = new Resolver(index).Resolve(
							 new Dictionary<string, string> { ["a/top"] = "^1.0", ["a/other"] = "^1.0" }, Context());
		Assert.That(resolution.Packages.Single(p => p.Id.Name == "shared").Manifest.Version, Is.EqualTo("1.5.0"));
	}

	[Test]
	public void ConflictingRangesFailAndNameBothRequesters()
	{
		var index = Index(
						Release("a/top", "1.0.0", dependencies: new() { ["a/shared"] = "^1.0" }),
						Release("a/other", "1.0.0", dependencies: new() { ["a/shared"] = "^2.0" }),
						Release("a/shared", "1.0.0"), Release("a/shared", "2.0.0"));
		var error = Assert.Throws<ResolutionException>(() => new Resolver(index).Resolve(
						new Dictionary<string, string> { ["a/top"] = "^1.0", ["a/other"] = "^1.0" }, Context()));
		Assert.That(error!.Message, Does.Contain("a/top").And.Contain("a/other"));
	}

	[Test]
	public void DependencyCyclesAreReported()
	{
		var index = Index(
						Release("a/one", "1.0.0", dependencies: new() { ["a/two"] = "^1.0" }),
						Release("a/two", "1.0.0", dependencies: new() { ["a/one"] = "^1.0" }));
		var error = Assert.Throws<ResolutionException>(() => new Resolver(index).Resolve(
						new Dictionary<string, string> { ["a/one"] = "^1.0" }, Context()));
		Assert.That(error!.Message, Does.Contain("cycle"));
	}

	[Test]
	public void YankedReleasesAreNotSelected()
	{
		var index = Index(Release("a/x", "1.0.0"), Release("a/x", "1.1.0"));
		index.Packages[0].Package.Yanked.Add("1.1.0-r1");
		var resolution = new Resolver(index).Resolve(new Dictionary<string, string> { ["a/x"] = "^1.0" }, Context());
		Assert.That(resolution.Packages.Single().Manifest.Version, Is.EqualTo("1.0.0"));
	}

	[Test]
	public void TheHighestRevisionOfAVersionWins()
	{
		var first = Release("a/x", "1.0.0");
		var second = Release("a/x", "1.0.0");
		second.Revision = 3;
		var resolution = new Resolver(Index(first, second)).Resolve(
							 new Dictionary<string, string> { ["a/x"] = "1.0.0" }, Context());
		Assert.That(resolution.Packages.Single().Release.Revision, Is.EqualTo(3));
	}

	/// <summary>
	/// The imported corpus is AutoHotkey code that has never been checked against Keysharp, so a
	/// release that does not claim the engine must not be offered as installable.
	/// </summary>
	[Test]
	public void ReleasesThatDoNotClaimTheEngineAreExcluded()
	{
		var autohotkeyOnly = Release("a/x", "1.0.0", engine: null);
		autohotkeyOnly.Engines["autohotkey"] = ">=2.0";
		var error = Assert.Throws<ResolutionException>(() => new Resolver(Index(autohotkeyOnly)).Resolve(
						new Dictionary<string, string> { ["a/x"] = "^1.0" }, Context()));
		Assert.That(error!.Message, Does.Contain("keysharp"));
	}

	/// <summary>
	/// A library and a port of it share a name but never an engine, so the short form is
	/// unambiguous to any actual user — only a lookup that ignores which engine they are on sees a
	/// clash. This is what keeps `kpm add FindText` working once a port exists.
	/// </summary>
	[Test]
	public void ABareNameResolvesPerEngineWhenALibraryAndItsPortShareIt()
	{
		var original = Release("feiyue/FindText", "10.2.0", engine: null);
		original.Engines["autohotkey"] = ">=2.0";
		var index = Index(original, Release("Keysharp/FindText", "0.1.0"));
		Assert.That(index.FindByName("FindText", Engines.Keysharp)!.Package.Owner, Is.EqualTo("Keysharp"));
		Assert.That(index.FindByName("FindText", Engines.AutoHotkey)!.Package.Owner, Is.EqualTo("feiyue"));
		// Ignoring the engine, they really are ambiguous — which is why the engine has to be passed.
		Assert.Throws<InvalidOperationException>(() => index.FindByName("FindText"));
	}

	/// <summary>Two packages the same user could install is a real clash, and still reported.</summary>
	[Test]
	public void ABareNameIsStillAmbiguousBetweenTwoPackagesForTheSameEngine()
	{
		var index = Index(Release("a/Json", "1.0.0"), Release("b/Json", "1.0.0"));
		var error = Assert.Throws<InvalidOperationException>(() => index.FindByName("Json", Engines.Keysharp));
		Assert.That(error!.Message, Does.Contain("a/Json").And.Contain("b/Json").And.Contain("matches more than one"));
	}

	/// <summary>
	/// A user asking for the original on an engine it does not support is asking for the library,
	/// not the packaging — so the port that does support them is named rather than left to be found.
	/// </summary>
	[Test]
	public void APortIsSuggestedWhenTheOriginalDoesNotSupportTheEngine()
	{
		var original = Release("feiyue/FindText", "10.2.0", engine: null);
		original.Engines["autohotkey"] = ">=2.0";
		var index = Index(original, Release("Keysharp/FindText", "0.1.0"));
		index.Packages.Single(p => p.Package.Owner == "Keysharp").Package.DerivedFrom = "feiyue/FindText";
		var error = Assert.Throws<ResolutionException>(() => new Resolver(index).Resolve(
						new Dictionary<string, string> { ["feiyue/FindText"] = "^10.0" }, Context()));
		Assert.That(error!.Message, Does.Contain("Keysharp/FindText").And.Contain("port"));
	}

	[Test]
	public void NoPortIsSuggestedWhenNoneSupportsTheEngineEither()
	{
		var original = Release("feiyue/FindText", "10.2.0", engine: null);
		original.Engines["autohotkey"] = ">=2.0";
		var error = Assert.Throws<ResolutionException>(() => new Resolver(Index(original)).Resolve(
						new Dictionary<string, string> { ["feiyue/FindText"] = "^10.0" }, Context()));
		Assert.That(error!.Message, Does.Not.Contain("install that instead"));
	}

	[Test]
	public void AnEngineVersionBelowTheRequirementIsExcluded()
	{
		var index = Index(Release("a/x", "1.0.0", engine: ">=0.0.0.20"));
		var error = Assert.Throws<ResolutionException>(() => new Resolver(index).Resolve(
						new Dictionary<string, string> { ["a/x"] = "^1.0" }, Context()));
		Assert.That(error!.Message, Does.Contain("supports keysharp"));
	}

	/// <summary>
	/// The failure this tier exists to prevent: a package shipping "any" plus linux-x64, where the
	/// "any" build is really the Windows one, must not hand Windows code to a linux-arm64 machine.
	/// </summary>
	[Test]
	public void AnArchitectureFallsBackToItsOperatingSystemBeforeThePortableBuild()
	{
		var index = Index(Release("a/x", "1.0.0", platforms: [Platforms.Any, "linux", "win"]));
		var resolution = new Resolver(index).Resolve(new Dictionary<string, string> { ["a/x"] = "^1.0" },
													new ResolveContext(keysharp, "linux-arm64"));
		Assert.That(resolution.Packages.Single().Platform, Is.EqualTo("linux"));
	}

	[Test]
	public void ThePortableBuildIsUsedOnlyWhenNothingMoreSpecificExists()
	{
		var index = Index(Release("a/x", "1.0.0", platforms: [Platforms.Any, "linux"]));
		var resolution = new Resolver(index).Resolve(new Dictionary<string, string> { ["a/x"] = "^1.0" },
													new ResolveContext(keysharp, "osx-arm64"));
		Assert.That(resolution.Packages.Single().Platform, Is.EqualTo(Platforms.Any));
	}

	[Test]
	public void TheMostSpecificArtifactWinsOverBothFallbacks()
	{
		var index = Index(Release("a/x", "1.0.0", platforms: [Platforms.Any, "linux", "linux-arm64"]));
		var resolution = new Resolver(index).Resolve(new Dictionary<string, string> { ["a/x"] = "^1.0" },
													new ResolveContext(keysharp, "linux-arm64"));
		Assert.That(resolution.Packages.Single().Platform, Is.EqualTo("linux-arm64"));
	}

	[Test]
	public void AnExactPlatformArtifactBeatsThePortableOne()
	{
		var index = Index(Release("a/x", "1.0.0", platforms: [Platforms.Any, "win-x64"]));
		var resolution = new Resolver(index).Resolve(new Dictionary<string, string> { ["a/x"] = "^1.0" }, Context("win-x64"));
		Assert.That(resolution.Packages.Single().Platform, Is.EqualTo("win-x64"));
	}

	[Test]
	public void APackageWithNoArtifactForThisPlatformFailsWithTheOnesItHas()
	{
		var index = Index(Release("a/x", "1.0.0", platforms: ["win-x64"]));
		var error = Assert.Throws<ResolutionException>(() => new Resolver(index).Resolve(
						new Dictionary<string, string> { ["a/x"] = "^1.0" }, new ResolveContext(keysharp, "linux-x64")));
		Assert.That(error!.Message, Does.Contain("win-x64"));
	}

	/// <summary>
	/// A package whose every release is a prerelease would be unresolvable under npm's rule that
	/// ranges exclude prereleases — and a good part of the imported corpus is exactly that, sitting
	/// at some x.0.0-alpha for years.
	/// </summary>
	[Test]
	public void APackageWithOnlyPrereleasesStillResolves()
	{
		var index = Index(Release("a/x", "9.0.0-alpha"), Release("a/x", "8.0.0-alpha"));
		var resolution = new Resolver(index).Resolve(new Dictionary<string, string> { ["a/x"] = "*" }, Context());
		Assert.That(resolution.Packages.Single().Manifest.Version, Is.EqualTo("9.0.0-alpha"));
	}

	/// <summary>But where a stable release exists, a range must not quietly pick a prerelease.</summary>
	[Test]
	public void APrereleaseIsNotChosenOverAStableRelease()
	{
		var index = Index(Release("a/x", "1.0.0"), Release("a/x", "2.0.0-alpha"));
		var resolution = new Resolver(index).Resolve(new Dictionary<string, string> { ["a/x"] = "*" }, Context());
		Assert.That(resolution.Packages.Single().Manifest.Version, Is.EqualTo("1.0.0"));
	}

	[Test]
	public void AnUnknownPackageIsReportedWithItsRequester()
	{
		var error = Assert.Throws<ResolutionException>(() => new Resolver(Index()).Resolve(
						new Dictionary<string, string> { ["a/missing"] = "^1.0" }, Context()));
		Assert.That(error!.Message, Does.Contain("a/missing").And.Contain("kpm.json"));
	}
}
