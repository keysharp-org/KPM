using Kpm.Model;
using NUnit.Framework;

namespace Kpm.Tests;

[TestFixture]
public sealed class PackageIdTests
{
	[TestCase("Descolada/OCR")]
	[TestCase("descolada/findtext")]
	[TestCase("a/b")]
	[TestCase("0w0Demonic/AquaHotkey")]
	[TestCase("thqby/child_process")]
	[TestCase("some-owner/some.package-2")]
	public void ValidIdsParse(string text)
	{
		Assert.That(PackageId.TryParse(text, out var id, out _), Is.True);
		// The author's own casing survives: these are people's names.
		Assert.That(id!.Value.ToString(), Is.EqualTo(text));
	}

	// A package name is not a file name: "Owner/Foo.ks" would want the same path as the forwarder
	// script generated for "Owner/Foo".
	[TestCase("Owner/Foo.ks", TestName = "name ends in .ks")]
	[TestCase("Owner/Foo.ahk", TestName = "name ends in .ahk")]
	[TestCase("Owner/Foo.AHK", TestName = "name ends in .AHK")]
	[TestCase("findtext", TestName = "no owner")]
	[TestCase("a/b/c", TestName = "two slashes")]
	[TestCase("-lead/x", TestName = "leading hyphen")]
	[TestCase("trail-/x", TestName = "trailing hyphen")]
	[TestCase("_lead/x", TestName = "leading underscore")]
	[TestCase("a/b c", TestName = "space")]
	[TestCase("a/b:c", TestName = "colon")]
	[TestCase("", TestName = "empty")]
	public void InvalidIdsAreRejected(string text) =>
		Assert.That(PackageId.TryParse(text, out _, out _), Is.False);

	/// <summary>
	/// Casing is kept for display but never for comparison: two ids differing only by case would be
	/// one directory on Windows and macOS, so they have to be the same id everywhere else.
	/// </summary>
	[Test]
	public void IdsCompareWithoutRegardToCase()
	{
		var canonical = PackageId.Parse("Descolada/OCR");
		var typed = PackageId.Parse("descolada/ocr");
		Assert.That(typed, Is.EqualTo(canonical));
		Assert.That(typed.GetHashCode(), Is.EqualTo(canonical.GetHashCode()));
		Assert.That(canonical.ToComparisonKey(), Is.EqualTo("descolada/ocr"));
		// ...while each keeps the spelling it was given.
		Assert.That(canonical.ToString(), Is.EqualTo("Descolada/OCR"));
	}

	[Test]
	public void TheRegistryPathIsAlwaysForwardSlashedAndKeepsItsCasing() =>
		Assert.That(PackageId.Parse("Descolada/OCR").RegistryPath, Is.EqualTo("packages/Descolada/OCR"));

	/// <summary>A dot is legal inside a name; only a trailing script extension is not.</summary>
	[Test]
	public void ADotInsideANameIsStillAllowed() =>
		Assert.That(PackageId.TryParse("Owner/Foo.Bar", out _, out _), Is.True);
}

[TestFixture]
public sealed class ValidatorLimitTests
{
	/// <summary>
	/// The schemas CI enforces and the validator a contributor runs locally must agree, or the
	/// answer before pushing differs from the answer after — which is how the registry's first pull
	/// request failed on a package that `kpm validate` had called fine.
	/// </summary>
	[Test]
	public void TheDescriptionLimitMatchesTheSchema()
	{
		var schema = File.ReadAllText(Path.Combine(RepositoryRoot(), "schemas", "package.schema.json"));
		using var document = System.Text.Json.JsonDocument.Parse(schema);
		var limit = document.RootElement.GetProperty("properties").GetProperty("description")
					.GetProperty("maxLength").GetInt32();
		Assert.That(Kpm.Registry.RegistryValidator.MaxDescription, Is.EqualTo(limit));
	}

	/// <summary>The registry repository sits beside this one; skip rather than fail when it does not.</summary>
	private static string RepositoryRoot()
	{
		var candidate = Path.GetFullPath(Path.Combine(TestContext.CurrentContext.TestDirectory,
													  "..", "..", "..", "..", "..", "..", "Packages"));

		if (!Directory.Exists(Path.Combine(candidate, "schemas")))
			Assert.Ignore($"the registry repository is not checked out beside this one ({candidate})");

		return candidate;
	}
}

[TestFixture]
public sealed class PlatformTests
{
	[Test]
	public void FallbackRunsFromArchitectureThroughOperatingSystemToPortable() =>
		Assert.That(Platforms.FallbackChain("linux-arm64"),
					Is.EqualTo(new[] { "linux-arm64", "linux", "any" }).AsCollection);

	[Test]
	public void ThePortableBuildFallsBackToNothing() =>
		Assert.That(Platforms.FallbackChain(Platforms.Any), Is.EqualTo(new[] { "any" }).AsCollection);

	[Test]
	public void SelectionTakesTheMostSpecificAvailable()
	{
		Assert.That(Platforms.Select(["any", "linux", "linux-x64"], "linux-x64"), Is.EqualTo("linux-x64"));
		Assert.That(Platforms.Select(["any", "linux"], "linux-x64"), Is.EqualTo("linux"));
		Assert.That(Platforms.Select(["any"], "linux-x64"), Is.EqualTo("any"));
		Assert.That(Platforms.Select(["win"], "linux-x64"), Is.Null);
	}

	[Test]
	public void OperatingSystemTiersAreValidPlatforms()
	{
		foreach (var os in Platforms.OperatingSystems)
			Assert.That(Platforms.IsValid(os), Is.True, os);

		Assert.That(Platforms.IsValid("windows"), Is.False, "the tier is spelled like the rid prefix");
	}
}

[TestFixture]
public sealed class PackageVersionTests
{
	[Test]
	public void ParsesVersionAndRevision()
	{
		var version = PackageVersion.Parse("0.4.0-r2");
		Assert.That(version.Version.ToString(), Is.EqualTo("0.4.0"));
		Assert.That(version.Revision, Is.EqualTo(2));
		Assert.That(version.ToString(), Is.EqualTo("0.4.0-r2"));
		Assert.That(version.ToDisplayString(), Is.EqualTo("0.4.0"));
	}

	/// <summary>A pre-release tag may itself contain "-r", so only the final suffix is the revision.</summary>
	[Test]
	public void ThePrereleaseTagIsNotMistakenForTheRevision()
	{
		var version = PackageVersion.Parse("1.0.0-rc1-r2");
		Assert.That(version.Version.ToString(), Is.EqualTo("1.0.0-rc1"));
		Assert.That(version.Revision, Is.EqualTo(2));
	}

	[TestCase("1.0.0")]
	[TestCase("1.0.0-r0")]
	[TestCase("1.0.0-rx")]
	[TestCase("notaversion-r1")]
	public void MalformedReleasesAreRejected(string text) =>
		Assert.That(PackageVersion.TryParse(text, out _, out _), Is.False);

	[Test]
	public void OrdersByVersionThenRevision()
	{
		var sorted = new[] { "1.0.0-r2", "0.9.0-r1", "1.0.0-r1", "1.1.0-r1" }
					 .Select(PackageVersion.Parse).OrderBy(v => v).Select(v => v.ToString());
		Assert.That(sorted, Is.EqualTo(new[] { "0.9.0-r1", "1.0.0-r1", "1.0.0-r2", "1.1.0-r1" }).AsCollection);
	}
}

[TestFixture]
public sealed class EngineRangeTests
{
	private static bool Includes(string range, string version)
	{
		Assert.That(EngineRange.TryParse(range, out var parsed, out var error), Is.True, error);
		return parsed!.Includes(Version.Parse(version));
	}

	[Test]
	public void ComparesFourPartEngineVersions()
	{
		Assert.That(Includes(">=0.0.0.17", "0.0.0.17"), Is.True);
		Assert.That(Includes(">=0.0.0.17", "0.0.0.18"), Is.True);
		Assert.That(Includes(">=0.0.0.17", "0.0.0.16"), Is.False);
	}

	/// <summary>
	/// A requirement is compared only as precisely as it was written: ">=2.0" is about the 2.0 line,
	/// so 2.0.0.17 satisfies it. Treating the unwritten components as zero would be the same answer
	/// here but the wrong one for "&lt;3.0" style bounds.
	/// </summary>
	[Test]
	public void ComparisonUsesOnlyTheComponentsTheRequirementNames()
	{
		Assert.That(Includes(">=2.0", "2.0.0.17"), Is.True);
		Assert.That(Includes("<3.0", "2.9.9.9"), Is.True);
		Assert.That(Includes("<3.0", "3.0.0.1"), Is.False);
	}

	[Test]
	public void BoundedRangesAndAutoHotkeyStyleVersionsParse()
	{
		Assert.That(Includes(">=2.0 <3.0", "2.1"), Is.True);
		Assert.That(Includes(">=2.0 <3.0", "3.1"), Is.False);
		Assert.That(Includes("v2.0", "2.0"), Is.True);
	}

	[Test]
	public void ABareVersionMeansAtLeast() => Assert.That(Includes("2.0", "2.5"), Is.True);

	[TestCase("")]
	[TestCase("not-a-version")]
	[TestCase(">=")]
	public void MalformedRangesAreRejected(string text) =>
		Assert.That(EngineRange.TryParse(text, out _, out _), Is.False);
}
