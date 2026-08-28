using System.Runtime.InteropServices;

namespace Kpm.Model;

/// <summary>
/// The platform axis of an artifact. These are the identifiers Keysharp's own release artifacts
/// already use, so a package author reads them without a translation table.
/// </summary>
public static class Platforms
{
	public const string Any = "any";

	public static readonly IReadOnlyList<string> All =
	[
		Any, "win-x64", "win-arm64", "linux-x64", "linux-arm64", "osx-x64", "osx-arm64"
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
	/// Which artifact of <paramref name="available"/> a <paramref name="target"/> machine should
	/// install: its exact RID if the package ships one, else the portable <c>any</c> build.
	///
	/// The target is a parameter rather than <see cref="Current"/> because resolving for another
	/// platform is a supported thing to do — CI validates a package for every platform it claims,
	/// and a user can ask what would be installed elsewhere.
	/// </summary>
	public static string? Select(IEnumerable<string> available, string target)
	{
		var set = available as IReadOnlySet<string> ?? available.ToHashSet(StringComparer.Ordinal);
		return set.Contains(target) ? target : set.Contains(Any) ? Any : null;
	}
}
