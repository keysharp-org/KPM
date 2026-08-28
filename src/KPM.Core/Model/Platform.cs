using System.Runtime.InteropServices;

namespace Kpm.Model;

/// <summary>
/// The platform axis of an artifact. These are the identifiers Keysharp's own release artifacts
/// already use, so a package author reads them without a translation table.
/// </summary>
public static class Platforms
{
	public const string Any = "any";

	/// <summary>
	/// The operating-system tier, between a specific architecture and <see cref="Any"/>.
	///
	/// It exists because script packages almost always vary by OS and almost never by architecture:
	/// a Windows OCR engine is the same code on x64 and arm64. Without this tier an author had two
	/// bad options — enumerate every architecture, or press <c>any</c> into service as "the Windows
	/// build", which silently hands Windows code to a linux-arm64 user whose architecture was not
	/// listed. Native payloads, which really do vary by architecture, still use the specific ids.
	/// </summary>
	public static readonly IReadOnlyList<string> OperatingSystems = ["win", "linux", "osx"];

	public static readonly IReadOnlyList<string> All =
	[
		Any, "win", "linux", "osx",
		"win-x64", "win-arm64", "linux-x64", "linux-arm64", "osx-x64", "osx-arm64"
	];

	public static bool IsValid(string? platform) => platform is not null && All.Contains(platform);

	/// <summary>The RID of the machine this process is running on, e.g. <c>win-x64</c>.</summary>
	public static string Current
	{
		get
		{
			var os = OperatingSystem.IsWindows() ? "win"
					 : OperatingSystem.IsMacOS() ? "osx"
					 : OperatingSystem.IsLinux() ? "linux"
					 : throw new PlatformNotSupportedException("unrecognized operating system");
			var arch = RuntimeInformation.ProcessArchitecture switch
			{
				Architecture.X64 => "x64",
				Architecture.Arm64 => "arm64",
				var other => throw new PlatformNotSupportedException($"unsupported architecture {other}")
			};
			return $"{os}-{arch}";
		}
	}

	/// <summary>
	/// What a <paramref name="target"/> machine falls back through, most specific first:
	/// <c>linux-arm64</c>, then <c>linux</c>, then <c>any</c>.
	///
	/// The middle step is the important one. Without it, a package shipping <c>any</c> and
	/// <c>linux-x64</c> would hand its <c>any</c> build to a linux-arm64 machine — and if that
	/// <c>any</c> build was really the Windows one, as authors are tempted to make it, the Linux
	/// user silently receives Windows code.
	/// </summary>
	public static IEnumerable<string> FallbackChain(string target)
	{
		yield return target;

		var dash = target.IndexOf('-');

		if (dash > 0)
			yield return target[..dash];

		if (target != Any)
			yield return Any;
	}

	/// <summary>
	/// Which artifact of <paramref name="available"/> a <paramref name="target"/> machine should
	/// install: the first entry of its <see cref="FallbackChain(string)"/> the package ships.
	///
	/// The target is a parameter rather than <see cref="Current"/> because resolving for another
	/// platform is a supported thing to do — CI validates a package for every platform it claims,
	/// and a user can ask what would be installed elsewhere.
	/// </summary>
	public static string? Select(IEnumerable<string> available, string target)
	{
		var set = available as IReadOnlySet<string> ?? available.ToHashSet(StringComparer.Ordinal);
		return FallbackChain(target).FirstOrDefault(set.Contains);
	}
}
