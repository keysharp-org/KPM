using Semver;

namespace Kpm.Model;

/// <summary>
/// Matches release versions against a dependency range.
///
/// The rule that needs explaining is prereleases. npm ranges exclude them, which is right when a
/// package has stable releases: <c>^1.0</c> should not quietly install <c>2.0.0-alpha</c>. But a
/// good part of this registry's corpus has *only* prereleases — libraries that have sat at
/// <c>9.0.0-alpha</c> for years — and excluding them there means the package can never be resolved
/// at all. So prereleases are admitted exactly when the package offers nothing else.
/// </summary>
public static class RangeMatcher
{
	public static bool TryParse(string text, out SemVersionRange range) =>
		SemVersionRange.TryParseNpm(text, out range!);

	public static bool HasStableRelease(IEnumerable<string> versions) =>
		versions.Any(v => SemVersion.TryParse(v, SemVersionStyles.Strict, out var parsed) && !parsed.IsPrerelease);

	/// <summary>
	/// Whether <paramref name="version"/> satisfies <paramref name="rangeText"/>.
	/// <paramref name="allowPrerelease"/> should be true only when the package has no stable
	/// release; callers get it from <see cref="HasStableRelease"/>.
	/// </summary>
	public static bool Matches(string rangeText, string version, bool allowPrerelease)
	{
		if (!SemVersion.TryParse(version, SemVersionStyles.Strict, out var parsed))
			return false;

		if (SemVersionRange.TryParseNpm(rangeText, allowPrerelease, out var range))
			return range.Contains(parsed);

		return false;
	}
}
