using System.IO.Compression;
using Kpm.Model;
using Kpm.Packing;
using NUnit.Framework;

namespace Kpm.Tests;

[TestFixture]
public sealed class PackerTests
{
	private string root = "";

	[SetUp]
	public void SetUp()
	{
		root = Path.Combine(Path.GetTempPath(), "kpm-tests", Guid.NewGuid().ToString("N"));
		_ = Directory.CreateDirectory(root);
	}

	[TearDown]
	public void TearDown()
	{
		if (Directory.Exists(root))
			Directory.Delete(root, recursive: true);
	}

	private string MakePackage(string name = "demo")
	{
		var directory = Path.Combine(root, name);
		_ = Directory.CreateDirectory(Path.Combine(directory, "src", "inner"));
		File.WriteAllText(Path.Combine(directory, "src", "Demo.ks"), "; entry\nDemo() {\n}\n");
		File.WriteAllText(Path.Combine(directory, "src", "inner", "Helper.ks"), "; helper\n");
		File.WriteAllText(Path.Combine(directory, "LICENSE"), "MIT\n");
		File.WriteAllText(Path.Combine(directory, "README.md"), "# Demo\n");
		File.WriteAllText(Path.Combine(directory, "port.json"), "{}");
		return directory;
	}

	private static EmbeddedManifest Manifest() => new()
	{
		Name = "demo",
		Owner = "tester",
		Version = "1.0.0",
		Revision = 1,
		Entry = "src/Demo.ks",
		Engines = { ["keysharp"] = ">=0.0.0.17" }
	};

	[Test]
	public void PackingTheSameTreeTwiceProducesIdenticalBytes()
	{
		var directory = MakePackage();
		var first = Packer.Pack(directory, Manifest(), Platforms.Any);
		var second = Packer.Pack(directory, Manifest(), Platforms.Any);
		Assert.That(second.Sha256, Is.EqualTo(first.Sha256));
		Assert.That(second.Bytes, Is.EqualTo(first.Bytes).AsCollection);
	}

	/// <summary>
	/// File timestamps must not reach the archive: a checkout gives every machine different mtimes,
	/// so if they leaked in, the registry could never reproduce a submitter's hash.
	/// </summary>
	[Test]
	public void FileTimestampsDoNotAffectTheArchive()
	{
		var directory = MakePackage();
		var before = Packer.Pack(directory, Manifest(), Platforms.Any);
		File.SetLastWriteTimeUtc(Path.Combine(directory, "src", "Demo.ks"), new DateTime(2011, 3, 4, 5, 6, 7, DateTimeKind.Utc));
		var after = Packer.Pack(directory, Manifest(), Platforms.Any);
		Assert.That(after.Sha256, Is.EqualTo(before.Sha256));
	}

	[Test]
	public void RegistryFilesAreExcludedAndTheEmbeddedManifestIsAdded()
	{
		var result = Packer.Pack(MakePackage(), Manifest(), Platforms.Any);
		Assert.That(result.Paths, Does.Contain("package.json"));
		Assert.That(result.Paths, Does.Contain("src/Demo.ks"));
		Assert.That(result.Paths, Does.Contain("src/inner/Helper.ks"));
		Assert.That(result.Paths, Does.Contain("LICENSE"));
		Assert.That(result.Paths, Does.Contain("README.md"));
		// port.json describes the package to the registry; it is not part of the package.
		Assert.That(result.Paths.Count(p => p == "port.json"), Is.Zero);
	}

	[Test]
	public void EntriesAreSortedIndependentlyOfEnumerationOrder()
	{
		var result = Packer.Pack(MakePackage(), Manifest(), Platforms.Any);
		Assert.That(result.Paths, Is.Ordered.Using<string>(StringComparer.Ordinal));
	}

	[Test]
	public void TheArchiveReadsBackAsAValidZip()
	{
		var result = Packer.Pack(MakePackage(), Manifest(), Platforms.Any);
		using var stream = new MemoryStream(result.Bytes);
		using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
		Assert.That(zip.Entries.Select(e => e.FullName), Is.EquivalentTo(result.Paths));
		var embedded = Unpacker.ReadManifest(result.Bytes);
		Assert.That(embedded.Entry, Is.EqualTo("src/Demo.ks"));
		Assert.That(embedded.Platform, Is.EqualTo(Platforms.Any));
	}

	[Test]
	public void SharedContentReachesEveryArtifact()
	{
		var directory = MakePackage();
		_ = Directory.CreateDirectory(Path.Combine(directory, "native"));
		File.WriteAllText(Path.Combine(directory, "native", "portable.dll"), "managed");
		// A top-level native/ is rare but meaningful: a payload that is the same everywhere.
		foreach (var platform in (string[])[Platforms.Any, "win-x64", "linux-x64"])
			Assert.That(Packer.Pack(directory, Manifest(), platform).Paths, Does.Contain("native/portable.dll"));
	}

	/// <summary>
	/// The mechanism that lets a package ship different code per platform while every file stays
	/// plain, portable script — as opposed to compile-time platform branches, which AutoHotkey
	/// rejects outright, so a package using them could never claim both engines.
	/// </summary>
	private static void WritePlatformFile(string directory, string platform, string kind, string name, string text)
	{
		var target = Path.Combine(directory, "platform", platform, kind);
		_ = Directory.CreateDirectory(target);
		File.WriteAllText(Path.Combine(target, name), text);
	}

	[Test]
	public void PlatformSpecificSourceLandsInThatPlatformsArtifactOnly()
	{
		var directory = MakePackage();
		WritePlatformFile(directory, Platforms.Any, "src", "Engine.ks", "; windows\n");
		WritePlatformFile(directory, "linux-x64", "src", "Engine.ks", "; linux\n");
		var linux = Packer.Pack(directory, Manifest(), "linux-x64");
		var portable = Packer.Pack(directory, Manifest(), Platforms.Any);

		// Platform-specific files land in src/, so a consumer's include never varies by platform.
		Assert.That(ReadEntry(linux, "src/Engine.ks"), Is.EqualTo("; linux\n"));
		Assert.That(ReadEntry(portable, "src/Engine.ks"), Is.EqualTo("; windows\n"));
		// One platform's code never reaches another's artifact, and the rid never leaks into it.
		Assert.That(linux.Paths, Has.None.Contain("platform/"));
		Assert.That(linux.Bytes, Is.Not.EqualTo(portable.Bytes).AsCollection);
		// Shared files are identical everywhere.
		Assert.That(ReadEntry(linux, "src/Demo.ks"), Is.EqualTo(ReadEntry(portable, "src/Demo.ks")));
	}

	/// <summary>
	/// The rid selects the artifact but never appears inside it, so a script loads its library by
	/// the same path everywhere instead of having to work out which rid it is running as.
	/// </summary>
	[Test]
	public void NativePayloadsAreAddressedWithoutTheirPlatform()
	{
		var directory = MakePackage();
		WritePlatformFile(directory, "win-x64", "native", "demo.dll", "windows");
		WritePlatformFile(directory, "linux-x64", "native", "demo.so", "linux");
		var windows = Packer.Pack(directory, Manifest(), "win-x64");
		var linux = Packer.Pack(directory, Manifest(), "linux-x64");
		Assert.That(windows.Paths, Does.Contain("native/demo.dll"));
		Assert.That(linux.Paths, Does.Contain("native/demo.so"));
		Assert.That(windows.Paths, Has.None.EqualTo("native/demo.so"));
		Assert.That(windows.Paths, Has.None.Contain("win-x64"));
		// The portable build carries no native payload at all, not the host's.
		Assert.That(Packer.Pack(directory, Manifest(), Platforms.Any).Paths, Has.None.StartWith("native/"));
	}

	/// <summary>
	/// The rule that keeps the layout honest: if the shared tree could be silently shadowed, a
	/// reader looking at src/Engine.ks would have no way to tell it is replaced on Linux.
	/// </summary>
	[Test]
	public void AFileCannotBeProvidedByBothSharedAndPlatformSpecificContent()
	{
		var directory = MakePackage();
		File.WriteAllText(Path.Combine(directory, "src", "Engine.ks"), "; shared\n");
		WritePlatformFile(directory, "linux-x64", "src", "Engine.ks", "; linux\n");
		var error = Assert.Throws<InvalidOperationException>(
						() => Packer.Pack(directory, Manifest(), "linux-x64"));
		Assert.That(error!.Message, Does.Contain("src/Engine.ks").And.Contain("platform/linux-x64/src/Engine.ks"));
		// The unaffected platform still packs: the clash only exists where both would apply.
		Assert.DoesNotThrow(() => Packer.Pack(directory, Manifest(), "win-x64"));
	}

	/// <summary>
	/// Reserved names live at the top of a package, never inside src/, so they cannot collide with a
	/// package's own source. thqby/Native really does ship a src/Native/ directory, and on a
	/// case-insensitive filesystem a reserved src/native/ would have been the same name.
	/// </summary>
	[Test]
	public void APackageMayHaveItsOwnSourceDirectoriesNamedLikeReservedOnes()
	{
		var directory = MakePackage();
		_ = Directory.CreateDirectory(Path.Combine(directory, "src", "Native"));
		_ = Directory.CreateDirectory(Path.Combine(directory, "src", "platform"));
		File.WriteAllText(Path.Combine(directory, "src", "Native", "Native.ks"), "; the package's own\n");
		File.WriteAllText(Path.Combine(directory, "src", "platform", "Detect.ks"), "; also the package's own\n");
		var result = Packer.Pack(directory, Manifest(), "linux-x64");
		Assert.That(result.Paths, Does.Contain("src/Native/Native.ks"));
		Assert.That(result.Paths, Does.Contain("src/platform/Detect.ks"));
	}

	[Test]
	public void OverlayPlatformsAreDiscoverable()
	{
		var directory = MakePackage();
		_ = Directory.CreateDirectory(Path.Combine(directory, "platform", "osx-arm64"));
		File.WriteAllText(Path.Combine(directory, "platform", "osx-arm64", "Engine.ks"), "; mac\n");
		Assert.That(Packer.OverlayPlatforms(directory), Is.EquivalentTo(new[] { "osx-arm64" }));
		Assert.That(Packer.OverlayPlatforms(Path.Combine(root, "nonexistent")), Is.Empty);
	}

	private static string ReadEntry(PackResult result, string path)
	{
		using var stream = new MemoryStream(result.Bytes);
		using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
		using var reader = new StreamReader(zip.GetEntry(path)!.Open());
		return reader.ReadToEnd();
	}

	[Test]
	public void PackingRefusesADirectoryWithNoSources()
	{
		var directory = Path.Combine(root, "empty");
		_ = Directory.CreateDirectory(directory);
		Assert.Throws<InvalidOperationException>(() => Packer.Pack(directory, Manifest(), Platforms.Any));
	}

	[TestCase("../escape.ks")]
	[TestCase("/absolute.ks")]
	[TestCase("C:/absolute.ks")]
	[TestCase("dir/../../escape.ks")]
	[TestCase("back\\slash.ks")]
	public void PathsThatCouldEscapeTheDestinationAreRejected(string path) =>
		Assert.Throws<InvalidOperationException>(() => Packer.ValidateArchivePath(path));

	[TestCase("src/Demo.ks")]
	[TestCase("package.json")]
	[TestCase("native/win-x64/demo.dll")]
	public void OrdinaryPathsAreAccepted(string path) => Packer.ValidateArchivePath(path);
}
