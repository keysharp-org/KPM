using System.Diagnostics;
using Kpm.Model;

namespace Kpm.Install;

public sealed record SetupStep(PackageId Id, SetupNote Note, string? ScriptPath)
{
	/// <summary>The command as the user will see it before confirming — never a shell string.</summary>
	public string Describe() =>
		ScriptPath is null
		? "(nothing to run; follow the instructions above)"
		: string.Join(" ", new[] { Quote(ScriptPath) }.Concat(Note.Arguments.Select(Quote)));

	private static string Quote(string value) => value.Contains(' ') ? $"\"{value}\"" : value;
}

/// <summary>
/// Runs the manual steps packages declare — a driver installer, a one-time registration.
///
/// Deliberately reachable only from <c>kpm setup</c>, never from installing. Install hooks are the
/// most exploited position in a software supply chain, and the reason they are so dangerous is that
/// they run without anyone deciding to run them: a build resolves a dependency and code executes.
/// Here nothing executes unless a person typed a command whose only purpose is to execute it, saw
/// the exact program and arguments, and confirmed.
///
/// What that buys over printing a path is real, though: the step usually needs elevation and a
/// reboot, and a user copying a path is a user guessing at arguments.
/// </summary>
public sealed class SetupRunner
{
    /// <summary>
    /// The steps declared by what is installed, resolved against the project so each script path
    /// points at a real file inside that package's own directory.
    /// </summary>
    public static IReadOnlyList<SetupStep> Plan(IEnumerable<(PackageId Id, SetupNote Note)> notes,
											    string projectDirectory, string platform)
	{
		var steps = new List<SetupStep>();

		foreach (var (id, note) in notes)
		{
			if (!note.AppliesTo(platform))
				continue;

			string? script = null;

			if (note.IsRunnable)
			{
				var root = Path.GetFullPath(Installer.PackageDirectory(projectDirectory, id));
				var candidate = Path.GetFullPath(Path.Combine(root, note.Script!.Replace('/', Path.DirectorySeparatorChar)));

				// The path comes from a manifest, so it is treated as untrusted input: it must land
				// inside the package it belongs to, and the file must actually be there.
				if (!candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
					throw new InvalidOperationException($"{id}: setup script '{note.Script}' escapes the package directory");

				script = File.Exists(candidate) ? candidate : null;
			}

			steps.Add(new SetupStep(id, note, script));
		}

		return steps;
	}

	/// <summary>
	/// Runs one step. <paramref name="confirm"/> is asked before anything starts and is what makes
	/// this different from an install hook; a caller that passes a function returning true has
	/// decided that for itself.
	/// </summary>
	public static async Task<bool> RunAsync(SetupStep step, Func<SetupStep, bool> confirm,
											CancellationToken ct = default)
	{
		if (step.ScriptPath is null || !confirm(step))
			return false;

		var info = new ProcessStartInfo(step.ScriptPath)
		{
			// Elevation needs the shell verb, so this cannot redirect output; the installer's own
			// window is what the user watches, which is also what they would see running it by hand.
			UseShellExecute = step.Note.Elevate,
			WorkingDirectory = Path.GetDirectoryName(step.ScriptPath)
		};

		if (step.Note.Elevate && OperatingSystem.IsWindows())
			info.Verb = "runas";

		foreach (var argument in step.Note.Arguments)
			info.ArgumentList.Add(argument);

		using var process = Process.Start(info)
							?? throw new InvalidOperationException($"{step.Id}: could not start {step.ScriptPath}");
		await process.WaitForExitAsync(ct);

		if (process.ExitCode != 0)
			throw new InvalidOperationException($"{step.Id}: setup exited with code {process.ExitCode}");

		return true;
	}
}
