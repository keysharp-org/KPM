using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kpm.Model;

/// <summary>
/// The registry's <c>packages/&lt;owner&gt;/&lt;name&gt;/package.json</c>: the package-level record,
/// and the only file in a package directory that may be edited after publication.
/// </summary>
public sealed class PackageManifest
{
	public int Schema { get; set; } = 1;
	public string Name { get; set; } = "";
	public string Owner { get; set; } = "";
	public string? DisplayName { get; set; }
	public string Description { get; set; } = "";
	public string? License { get; set; }
	public string? Homepage { get; set; }
	public List<string> Categories { get; set; } = [];

	/// <summary>
	/// <c>upstream</c> when the author publishes their own versions, <c>registry</c> when the registry
	/// synthesizes 0.x versions for source that has none (see <see cref="VersioningKind"/>).
	/// </summary>
	public string Versioning { get; set; } = VersioningKind.Upstream;

	/// <summary>
	/// Releases withdrawn from new resolution, as <c>1.2.3-r1</c> strings. A lockfile that already
	/// names one still installs — yanking is not deletion, and breaking existing builds is not the point.
	/// </summary>
	public List<string> Yanked { get; set; } = [];
}

public static class VersioningKind
{
	public const string Upstream = "upstream";
	public const string Registry = "registry";

	public static bool IsValid(string? s) => s is Upstream or Registry;
}

/// <summary>
/// One immutable release: <c>versions/&lt;version&gt;-r&lt;rev&gt;.json</c>. Once merged, this file is
/// never edited or deleted — a mistake is corrected by publishing a new revision.
/// </summary>
public sealed class VersionManifest
{
	public int Schema { get; set; } = 1;
	public string Name { get; set; } = "";
	public string Owner { get; set; } = "";
	public string Version { get; set; } = "";
	public int Revision { get; set; } = 1;
	public DateTimeOffset Published { get; set; }

	/// <summary>Archive-relative path of the file a consumer includes, e.g. <c>src/FindText.ks</c>.</summary>
	public string Entry { get; set; } = "";

	/// <summary>
	/// Engine requirements, keyed by <see cref="Engines"/>. Values use the comparison grammar
	/// <c>#Requires</c> accepts — NOT the SemVer ranges <see cref="Dependencies"/> uses. The two
	/// grammars are deliberately different: this half mirrors the language directive, that half
	/// mirrors package ecosystems.
	/// </summary>
	public Dictionary<string, string> Engines { get; set; } = [];

	public List<string> Platforms { get; set; } = [];

	/// <summary>
	/// Capability names as <c>#Requires capability</c> spells them. Informational: tooling displays
	/// them so a user knows what a package will ask the OS for; nothing enforces them yet.
	/// </summary>
	public List<string> Capabilities { get; set; } = [];

	/// <summary>Package id to SemVer range (<c>^1.4.0</c>).</summary>
	public Dictionary<string, string> Dependencies { get; set; } = [];

	/// <summary>
	/// Something the user must do themselves before the package works — install a driver, obtain a
	/// tool. KPM prints it and never acts on it.
	/// </summary>
	public SetupNote? Setup { get; set; }

	public SourceRef? Source { get; set; }

	/// <summary>Platform (see <see cref="Model.Platforms"/>) to the artifact built for it.</summary>
	public Dictionary<string, ArtifactRef> Artifacts { get; set; } = [];

	[JsonIgnore]
	public PackageId Id => PackageId.Parse($"{Owner}/{Name}");

	[JsonIgnore]
	public PackageVersion Release => PackageVersion.Parse($"{Version}-r{Revision}");
}

public static class Engines
{
	public const string Keysharp = "keysharp";
	public const string AutoHotkey = "autohotkey";

	public static bool IsValid(string? s) => s is Keysharp or AutoHotkey;
}

/// <summary>
/// A manual step a package needs, shown to the user after installing.
///
/// Deliberately inert data rather than a script. A package manager that runs code at install time
/// is the single most exploited position in a software supply chain, and this registry serves a
/// bot-imported corpus whose CI checks that code compiles, not what it does. The steps that
/// genuinely cannot be automated — installing a driver, anything needing administrator rights —
/// require the user to approve an elevation prompt regardless, so executing here would buy almost
/// no convenience in exchange for that exposure.
/// </summary>
public sealed class SetupNote
{
	/// <summary>What the user has to do, in plain language.</summary>
	public string Message { get; set; } = "";

	/// <summary>Where the instructions live.</summary>
	public string? Url { get; set; }

	/// <summary>An archive-relative path the user may run themselves, if the package ships one.</summary>
	public string? Script { get; set; }
}

/// <summary>Where a release's content came from — provenance, not a download location.</summary>
public sealed class SourceRef
{
	/// <summary>One of <c>ports</c>, <c>git</c>, <c>scripthub</c>, <c>forum</c>.</summary>
	public string Kind { get; set; } = "";
	public string? Repository { get; set; }
	public string? Commit { get; set; }
	public string? Path { get; set; }
	public string? Url { get; set; }

	/// <summary>For a port or an import: the upstream revision this content was taken from.</summary>
	public SourceRef? Upstream { get; set; }
}

/// <summary>
/// One built artifact. The SHA-256 is the artifact's identity; <see cref="Sources"/> is a list of
/// places the same bytes can be fetched from, tried in order. A mirror therefore needs no trust:
/// bytes that do not hash to <see cref="Sha256"/> are rejected wherever they came from.
/// </summary>
public sealed class ArtifactRef
{
	public string Sha256 { get; set; } = "";
	public long Size { get; set; }
	public List<string> Sources { get; set; } = [];
}

/// <summary>
/// <c>port.json</c> — present only for source-hosted packages, whose maintained source lives in the
/// registry repo beside their manifests. It carries what packing needs; the package-level record
/// stays in <see cref="PackageManifest"/>.
/// </summary>
public sealed class PortManifest
{
	public int Schema { get; set; } = 1;
	public string Version { get; set; } = "";
	public string Entry { get; set; } = "";
	public Dictionary<string, string> Engines { get; set; } = [];
	public List<string> Platforms { get; set; } = [Model.Platforms.Any];
	public List<string> Capabilities { get; set; } = [];
	public Dictionary<string, string> Dependencies { get; set; } = [];
	public SetupNote? Setup { get; set; }
	public SourceRef? Upstream { get; set; }
}

/// <summary>A consumer's <c>kpm.json</c>.</summary>
public sealed class ProjectManifest
{
	public int Schema { get; set; } = 1;
	public Dictionary<string, string> Engines { get; set; } = [];
	public Dictionary<string, string> Dependencies { get; set; } = [];
}

/// <summary>A consumer's <c>kpm.lock.json</c>: the exact identities a resolve settled on.</summary>
public sealed class LockFile
{
	public int Schema { get; set; } = 1;
	public List<LockedPackage> Packages { get; set; } = [];
}

public sealed class LockedPackage
{
	public string Id { get; set; } = "";
	public string Version { get; set; } = "";
	public int Revision { get; set; } = 1;
	public string Platform { get; set; } = Model.Platforms.Any;
	public string Sha256 { get; set; } = "";
	public long Size { get; set; }
	public List<string> Sources { get; set; } = [];
	public string? SourceCommit { get; set; }
	public string Entry { get; set; } = "";
}

/// <summary>
/// One JSON shape for every manifest kind. Property names are camelCase on the wire; writing is
/// indented and deterministically ordered because these files are reviewed in pull requests.
/// </summary>
public static class ManifestJson
{
	public static readonly JsonSerializerOptions Options = new()
	{
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		PropertyNameCaseInsensitive = false,
		DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
		WriteIndented = true,
		NewLine = "\n",
		IndentCharacter = ' ',
		IndentSize = 2,
		// These files are reviewed in pull requests, so ">=0.0.0.17" must not be written as
		// ">=0.0.0.17". The escaping this turns off guards against embedding JSON directly in
		// HTML, which nothing here does — a web consumer escapes on output regardless.
		Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
		Converters = { new JsonStringEnumConverter() }
	};

	public static string Write<T>(T value) => JsonSerializer.Serialize(value, Options) + "\n";

	public static T Read<T>(string json) =>
		JsonSerializer.Deserialize<T>(json, Options) ?? throw new JsonException("manifest is null");

	public static async Task<T> ReadFileAsync<T>(string path, CancellationToken ct = default)
	{
		await using var stream = File.OpenRead(path);
		return await JsonSerializer.DeserializeAsync<T>(stream, Options, ct)
			   ?? throw new JsonException($"{path} is null");
	}

	public static async Task WriteFileAsync<T>(string path, T value, CancellationToken ct = default)
	{
		var dir = Path.GetDirectoryName(path);

		if (!string.IsNullOrEmpty(dir))
			_ = Directory.CreateDirectory(dir);

		await File.WriteAllTextAsync(path, Write(value), ct);
	}
}
