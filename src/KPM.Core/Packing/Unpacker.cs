using System.IO.Compression;
using System.Security.Cryptography;
using Kpm.Model;

namespace Kpm.Packing;

/// <summary>
/// Reads a <c>.kspkg</c>. Every path is validated before anything is written, because an archive is
/// third-party content: the guards here are what stop a crafted entry from writing outside the
/// destination.
/// </summary>
public static class Unpacker
{
	/// <summary>
	/// Verifies bytes against an expected SHA-256 before they are ever parsed as an archive.
	/// Callers must use this rather than hashing after opening: a malformed archive should be
	/// rejected as the wrong bytes, not handled as a ZIP error.
	/// </summary>
	public static void VerifyHash(ReadOnlySpan<byte> bytes, string expectedSha256, string what)
	{
		var actual = Convert.ToHexStringLower(SHA256.HashData(bytes));

		if (!actual.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
			throw new InvalidDataException(
				$"{what} does not match its expected hash (expected {expectedSha256}, got {actual}). "
				+ "The download was corrupted or the source served different content.");
	}

	public static EmbeddedManifest ReadManifest(byte[] archive)
	{
		using var stream = new MemoryStream(archive, writable: false);
		using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
		var entry = zip.GetEntry("package.json")
					?? throw new InvalidDataException("archive has no package.json");
		using var reader = new StreamReader(entry.Open());
		return ManifestJson.Read<EmbeddedManifest>(reader.ReadToEnd());
	}

	/// <summary>Extracts into <paramref name="destination"/>, which is emptied first.</summary>
	public static void Extract(byte[] archive, string destination)
	{
		using var stream = new MemoryStream(archive, writable: false);
		using var zip = new ZipArchive(stream, ZipArchiveMode.Read);

		foreach (var entry in zip.Entries)
		{
			if (entry.FullName.EndsWith('/'))
				throw new InvalidDataException($"archive contains a directory entry '{entry.FullName}'");

			Packer.ValidateArchivePath(entry.FullName);
		}

		if (Directory.Exists(destination))
			Directory.Delete(destination, recursive: true);

		_ = Directory.CreateDirectory(destination);
		var root = Path.GetFullPath(destination);

		foreach (var entry in zip.Entries)
		{
			var target = Path.GetFullPath(Path.Combine(root, entry.FullName));

			// Belt and braces: the path was validated above, so this can only fire if some platform
			// path rule made a validated name still resolve outside the root.
			if (!target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
				throw new InvalidDataException($"archive entry '{entry.FullName}' escapes the destination");

			_ = Directory.CreateDirectory(Path.GetDirectoryName(target)!);
			using var source = entry.Open();
			using var file = File.Create(target);
			source.CopyTo(file);
		}
	}
}
