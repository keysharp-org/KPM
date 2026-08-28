using Kpm.Cli;

// The whole command surface lives in KPM.Core (see CommandRunner), so that the Keysharp host — which
// embeds the library and has no executable to call — offers exactly the same commands with exactly
// the same output. This executable is one front end onto it, and the console is its only difference.
return await CommandRunner.RunAsync(args, Console.Out, Console.Error, Console.In);
