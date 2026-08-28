using System.Diagnostics.CodeAnalysis;
using Semver;

namespace Kpm.Model;

/// <summary>
/// A release identity: the human-facing SemVer plus the packaging revision.
///
/// The split is the one Linux distributions make between an upstream version and a package
/// release. The version tracks the library's own source; the revision counts repackagings of
/// that same source (a wrong dependency, a missing file, a bad archive). Only the version is
/// normally shown to users — bumping it for a packaging mistake would claim the library changed
/// when it did not.
/// </summary>
public readonly record struct PackageVersion : IComparable<PackageVersion>
{
	public SemVersion Version { get; }
	public int Revision { get; }

	public PackageVersion(SemVersion version, int revision)
	{
		ArgumentNullException.ThrowIfNull(version);

		if (revision < 1)
			throw new ArgumentOutOfRangeException(nameof(revision), "revisions start at 1");

		Version = version;
		Revision = revision;
	}

	/// <summary>Parses the <c>1.2.3-r4</c> spelling used by manifest file names and release tags.</summary>
	public static bool TryParse(string? text, [NotNullWhen(true)] out PackageVersion? value, out string? error)
	{
		value = null;
		error = null;

		if (string.IsNullOrWhiteSpace(text))
		{
			error = "version is empty";
			return false;
		}

		// The revision suffix is found from the END: a SemVer pre-release/build part may itself
		// contain "-r" (1.0.0-rc1-r2), and only the last one is the packaging revision.
		var dash = text.LastIndexOf("-r", StringComparison.Ordinal);

		if (dash < 0)
		{
			error = $"'{text}' has no revision suffix (expected e.g. '1.2.3-r1')";
			return false;
		}

		var revisionText = text[(dash + 2)..];

		if (!int.TryParse(revisionText, System.Globalization.NumberStyles.None,
						  System.Globalization.CultureInfo.InvariantCulture, out var revision) || revision < 1)
		{
			error = $"'{revisionText}' is not a revision number (expected a positive integer)";
			return false;
		}

		if (!SemVersion.TryParse(text[..dash], SemVersionStyles.Strict, out var semver))
		{
			error = $"'{text[..dash]}' is not a valid version";
			return false;
		}

		value = new PackageVersion(semver, revision);
		return true;
	}

	public static PackageVersion Parse(string text) =>
		TryParse(text, out var v, out var error) ? v.Value : throw new ArgumentException(error, nameof(text));

	public int CompareTo(PackageVersion other)
	{
		var byVersion = SemVersion.ComparePrecedence(Version, other.Version);
		return byVersion != 0 ? byVersion : Revision.CompareTo(other.Revision);
	}

	/// <summary>The file-name and tag spelling, <c>1.2.3-r4</c>.</summary>
	public override string ToString() => $"{Version}-r{Revision}";

	/// <summary>What a user is shown: the revision is packaging detail.</summary>
	public string ToDisplayString() => Version.ToString();
}
