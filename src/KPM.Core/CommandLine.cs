namespace Kpm;

/// <summary>
/// A small argument reader: options are <c>--name</c> or <c>--name value</c>, everything else is a
/// positional. Hand-rolled because the CLI is a shell over <see cref="KpmService"/> and a parsing
/// library would be the project's largest dependency for the least of its behaviour.
/// </summary>
public sealed class CommandLine
{
	private readonly Dictionary<string, string?> options = new(StringComparer.OrdinalIgnoreCase);
	private readonly List<string> positionals = [];

	public CommandLine(IEnumerable<string> arguments)
	{
		string? pending = null;

		foreach (var argument in arguments)
		{
			if (argument.StartsWith("--", StringComparison.Ordinal))
			{
				Flush();
				var text = argument[2..];
				var equals = text.IndexOf('=');

				if (equals >= 0)
					options[text[..equals]] = text[(equals + 1)..];
				else
					pending = text;
			}
			else if (pending is not null)
			{
				options[pending] = argument;
				pending = null;
			}
			else
				positionals.Add(argument);
		}

		Flush();

		void Flush()
		{
			if (pending is not null)
			{
				options[pending] = null;   // a flag given without a value
				pending = null;
			}
		}
	}

	public IReadOnlyList<string> Positionals => positionals;

	public string? Value(string name) => options.GetValueOrDefault(name);

	public bool Has(string name) => options.ContainsKey(name);

	public string ValueOrDefault(string name, string fallback) =>
		options.TryGetValue(name, out var value) && !string.IsNullOrEmpty(value) ? value : fallback;
}
