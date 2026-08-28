namespace Kpm;

/// <summary>
/// Where KPM keeps machine-wide state. This sits under the same per-user root Keysharp itself uses,
/// so a user has one Keysharp data directory rather than two.
/// </summary>
public static class KpmPaths
{
	/// <summary>Overrides the data root; set by tests and by the <c>KPM_HOME</c> environment variable.</summary>
	public static string? Override { get; set; }

	public static string Root
	{
		get
		{
			if (!string.IsNullOrEmpty(Override))
				return Override;

			var fromEnvironment = Environment.GetEnvironmentVariable("KPM_HOME");

			if (!string.IsNullOrEmpty(fromEnvironment))
				return fromEnvironment;

			if (OperatingSystem.IsWindows())
			{
				var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData,
													  Environment.SpecialFolderOption.DoNotVerify);
				return Path.Combine(local, "Keysharp", "kpm");
			}

			var xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");

			if (!string.IsNullOrEmpty(xdg))
				return Path.Combine(xdg, "Keysharp", "kpm");

			var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile,
												 Environment.SpecialFolderOption.DoNotVerify);
			return Path.Combine(home, ".local", "share", "Keysharp", "kpm");
		}
	}

	/// <summary>Content-addressed artifact cache. Keyed by hash, so it never needs invalidating.</summary>
	public static string CacheRoot => Path.Combine(Root, "cache");

	/// <summary>The last index fetched from each registry.</summary>
	public static string RegistryRoot => Path.Combine(Root, "registry");

	public static string EnsureDirectory(string path)
	{
		_ = Directory.CreateDirectory(path);
		return path;
	}
}
