using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace Kpm.Packing;

/// <summary>
/// Writes a ZIP container whose bytes depend only on the entries given to it.
///
/// This exists instead of <see cref="ZipArchive"/> because that class stamps each entry's
/// "version made by" field with the host platform, so the same input produces different bytes on
/// Windows and Linux. Artifact identity here is the SHA-256 of these bytes and the registry's CI
/// re-packs a submission to check it reproduces, so a platform-dependent byte would break
/// verification for no benefit. Every other varying field — timestamps, external attributes, entry
/// order — is pinned to a constant below.
///
/// The deflate stream itself comes from the runtime, so byte-identity across machines also assumes
/// the same .NET runtime version; CI pins the SDK for that reason.
/// </summary>
internal static class DeterministicZip
{
	// 2000-01-01T00:00:00Z in MS-DOS date/time. Any fixed instant works; this one is unambiguously
	// inside the representable range (which starts in 1980) and obviously not a real timestamp.
	private const ushort DosDate = (20 << 9) | (1 << 5) | 1;
	private const ushort DosTime = 0;

	// Spec version 2.0 (deflate), platform 0 (MS-DOS/FAT). Platform 0 is the point: it is the same
	// on every host, and it makes external attributes DOS-style, so we do not leak a Unix file mode.
	private const ushort VersionMadeBy = 0x0014;
	private const ushort VersionNeeded = 20;
	private const ushort FlagUtf8 = 0x0800;
	private const ushort MethodDeflate = 8;

	internal readonly record struct Entry(string Path, byte[] Content);

	/// <summary>
	/// Writes <paramref name="entries"/> as a ZIP. Callers pass paths already normalized and sorted
	/// (<see cref="Packer"/> owns those rules); this method does not reorder them, so the caller's
	/// order is the archive's order.
	/// </summary>
	public static byte[] Write(IReadOnlyList<Entry> entries)
	{
		if (entries.Count > ushort.MaxValue)
			throw new InvalidOperationException($"a package may hold at most {ushort.MaxValue} files");

		using var output = new MemoryStream();
		var central = new MemoryStream();
		var offsets = new List<(Entry Entry, uint Crc, int Compressed, int Uncompressed, uint Offset)>(entries.Count);
		Span<byte> header = stackalloc byte[46];

		foreach (var entry in entries)
		{
			var nameBytes = Encoding.UTF8.GetBytes(entry.Path);

			if (nameBytes.Length > ushort.MaxValue)
				throw new InvalidOperationException($"path too long in archive: {entry.Path}");

			var crc = Crc32.Compute(entry.Content);
			var deflated = Deflate(entry.Content);

			if (entry.Content.LongLength > uint.MaxValue || deflated.LongLength > uint.MaxValue)
				throw new InvalidOperationException($"file too large for a package: {entry.Path}");

			var offset = (uint)output.Position;
			offsets.Add((entry, crc, deflated.Length, entry.Content.Length, offset));

			var local = header[..30];
			BinaryPrimitives.WriteUInt32LittleEndian(local[0..], 0x04034B50);
			BinaryPrimitives.WriteUInt16LittleEndian(local[4..], VersionNeeded);
			BinaryPrimitives.WriteUInt16LittleEndian(local[6..], FlagUtf8);
			BinaryPrimitives.WriteUInt16LittleEndian(local[8..], MethodDeflate);
			BinaryPrimitives.WriteUInt16LittleEndian(local[10..], DosTime);
			BinaryPrimitives.WriteUInt16LittleEndian(local[12..], DosDate);
			BinaryPrimitives.WriteUInt32LittleEndian(local[14..], crc);
			BinaryPrimitives.WriteUInt32LittleEndian(local[18..], (uint)deflated.Length);
			BinaryPrimitives.WriteUInt32LittleEndian(local[22..], (uint)entry.Content.Length);
			BinaryPrimitives.WriteUInt16LittleEndian(local[26..], (ushort)nameBytes.Length);
			BinaryPrimitives.WriteUInt16LittleEndian(local[28..], 0);
			output.Write(local);
			output.Write(nameBytes);
			output.Write(deflated);
		}

		foreach (var (entry, crc, compressed, uncompressed, offset) in offsets)
		{
			var nameBytes = Encoding.UTF8.GetBytes(entry.Path);
			BinaryPrimitives.WriteUInt32LittleEndian(header[0..], 0x02014B50);
			BinaryPrimitives.WriteUInt16LittleEndian(header[4..], VersionMadeBy);
			BinaryPrimitives.WriteUInt16LittleEndian(header[6..], VersionNeeded);
			BinaryPrimitives.WriteUInt16LittleEndian(header[8..], FlagUtf8);
			BinaryPrimitives.WriteUInt16LittleEndian(header[10..], MethodDeflate);
			BinaryPrimitives.WriteUInt16LittleEndian(header[12..], DosTime);
			BinaryPrimitives.WriteUInt16LittleEndian(header[14..], DosDate);
			BinaryPrimitives.WriteUInt32LittleEndian(header[16..], crc);
			BinaryPrimitives.WriteUInt32LittleEndian(header[20..], (uint)compressed);
			BinaryPrimitives.WriteUInt32LittleEndian(header[24..], (uint)uncompressed);
			BinaryPrimitives.WriteUInt16LittleEndian(header[28..], (ushort)nameBytes.Length);
			BinaryPrimitives.WriteUInt16LittleEndian(header[30..], 0);   // extra
			BinaryPrimitives.WriteUInt16LittleEndian(header[32..], 0);   // comment
			BinaryPrimitives.WriteUInt16LittleEndian(header[34..], 0);   // disk
			BinaryPrimitives.WriteUInt16LittleEndian(header[36..], 0);   // internal attributes
			BinaryPrimitives.WriteUInt32LittleEndian(header[38..], 0);   // external attributes: no host file mode
			BinaryPrimitives.WriteUInt32LittleEndian(header[42..], offset);
			central.Write(header);
			central.Write(nameBytes);
		}

		var centralOffset = (uint)output.Position;
		var centralBytes = central.ToArray();
		output.Write(centralBytes);
		Span<byte> eocd = stackalloc byte[22];
		BinaryPrimitives.WriteUInt32LittleEndian(eocd[0..], 0x06054B50);
		BinaryPrimitives.WriteUInt16LittleEndian(eocd[4..], 0);
		BinaryPrimitives.WriteUInt16LittleEndian(eocd[6..], 0);
		BinaryPrimitives.WriteUInt16LittleEndian(eocd[8..], (ushort)entries.Count);
		BinaryPrimitives.WriteUInt16LittleEndian(eocd[10..], (ushort)entries.Count);
		BinaryPrimitives.WriteUInt32LittleEndian(eocd[12..], (uint)centralBytes.Length);
		BinaryPrimitives.WriteUInt32LittleEndian(eocd[16..], centralOffset);
		BinaryPrimitives.WriteUInt16LittleEndian(eocd[20..], 0);
		output.Write(eocd);
		return output.ToArray();
	}

	private static byte[] Deflate(byte[] content)
	{
		using var buffer = new MemoryStream();

		using (var deflate = new DeflateStream(buffer, CompressionLevel.Optimal, leaveOpen: true))
			deflate.Write(content);

		return buffer.ToArray();
	}
}
