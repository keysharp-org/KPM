using Kpm.Model;
using NUnit.Framework;

namespace Kpm.Tests;

[TestFixture]
public sealed class PackageIdTests
{
	[TestCase("descolada/findtext")]
	[TestCase("a/b")]
	[TestCase("some-owner/some-package-2")]
	public void ValidIdsParse(string text)
	{
		Assert.That(PackageId.TryParse(text, out var id, out _), Is.True);
		Assert.That(id!.Value.ToString(), Is.EqualTo(text));
	}

	// Uppercase is rejected rather than lowercased: two ids differing only by case would collide on
	// a case-insensitive filesystem, and silently rewriting what the user typed hides that.
	[TestCase("Descolada/FindText", TestName = "uppercase")]
	[TestCase("findtext", TestName = "no owner")]
	[TestCase("a/b/c", TestName = "two slashes")]
	[TestCase("-lead/x", TestName = "leading hyphen")]
	[TestCase("trail-/x", TestName = "trailing hyphen")]
	[TestCase("under_score/x", TestName = "underscore")]
	[TestCase("a/b c", TestName = "space")]
	[TestCase("", TestName = "empty")]
	public void InvalidIdsAreRejected(string text) =>
		Assert.That(PackageId.TryParse(text, out _, out _), Is.False);

	[Test]
	public void TheRegistryPathIsAlwaysForwardSlashed() =>
		Assert.That(PackageId.Parse("a/b").RegistryPath, Is.EqualTo("packages/a/b"));
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
