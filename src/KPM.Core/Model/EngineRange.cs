using System.Diagnostics.CodeAnalysis;

namespace Kpm.Model;

/// <summary>
/// A requirement on an engine's version, written the way <c>#Requires</c> writes one:
/// <c>&gt;=0.0.0.17</c>, <c>&gt;=2.0 &lt;3.0</c>, or a bare <c>2.0</c> meaning "at least".
///
/// Engine versions are not SemVer — Keysharp's are four-part (<c>0.0.0.17</c>) — so this is
/// deliberately not the same grammar as a dependency range, and the two never share a parser.
/// Terms are ANDed, as in the directive.
/// </summary>
public sealed class EngineRange
{
	private readonly List<(string Operator, Version Version)> terms;
	private readonly string text;

	private EngineRange(string text, List<(string, Version)> terms)
	{
		this.text = text;
		this.terms = terms;
	}

	public static bool TryParse(string? input, [NotNullWhen(true)] out EngineRange? range, out string? error)
	{
		range = null;
		error = null;

		if (string.IsNullOrWhiteSpace(input))
		{
			error = "engine range is empty";
			return false;
		}

		var terms = new List<(string, Version)>();

		foreach (var token in input.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
		{
			var rest = token;
			var op = ">=";

			foreach (var candidate in (string[])[">=", "<=", ">", "<", "="])
			{
				if (rest.StartsWith(candidate, StringComparison.Ordinal))
				{
					op = candidate;
					rest = rest[candidate.Length..];
					break;
				}
			}

			// "v2.0" is how #Requires spells an AutoHotkey version; accept it so a manifest can be
			// copied straight from a script's directive.
			if (rest.StartsWith('v') || rest.StartsWith('V'))
				rest = rest[1..];

			if (!Version.TryParse(Normalize(rest), out var version))
			{
				error = $"'{token}' is not a version comparison";
				return false;
			}

			terms.Add((op, version));
		}

		if (terms.Count == 0)
		{
			error = "engine range is empty";
			return false;
		}

		range = new EngineRange(input.Trim(), terms);
		return true;
	}

	/// <summary><see cref="Version"/> needs at least two components, so "2" becomes "2.0".</summary>
	private static string Normalize(string s) => s.Contains('.') ? s : $"{s}.0";

	public bool Includes(Version version)
	{
		foreach (var (op, bound) in terms)
		{
			// Compare only as precisely as the requirement was written: ">=2.0" must accept 2.0.0.17,
			// which it would not if the unwritten components were treated as zero.
			var comparison = Truncate(version, bound).CompareTo(bound);
			var satisfied = op switch
			{
				">=" => comparison >= 0,
				">" => comparison > 0,
				"<=" => comparison <= 0,
				"<" => comparison < 0,
				_ => comparison == 0
			};

			if (!satisfied)
				return false;
		}

		return true;
	}

	private static Version Truncate(Version version, Version bound)
	{
		var components = bound.Revision >= 0 ? 4 : bound.Build >= 0 ? 3 : 2;
		return components switch
		{
			2 => new Version(version.Major, version.Minor),
			3 => new Version(version.Major, version.Minor, Math.Max(version.Build, 0)),
			_ => new Version(version.Major, version.Minor, Math.Max(version.Build, 0), Math.Max(version.Revision, 0))
		};
	}

	public override string ToString() => text;
}
