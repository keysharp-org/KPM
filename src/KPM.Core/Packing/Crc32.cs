namespace Kpm.Packing;

/// <summary>CRC-32 (IEEE 802.3), the checksum ZIP entries carry.</summary>
internal static class Crc32
{
	private static readonly uint[] table = BuildTable();

	private static uint[] BuildTable()
	{
		var t = new uint[256];

		for (uint i = 0; i < 256; i++)
		{
			var c = i;

			for (var k = 0; k < 8; k++)
				c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;

			t[i] = c;
		}

		return t;
	}

	public static uint Compute(ReadOnlySpan<byte> data)
	{
		var crc = 0xFFFFFFFFu;

		foreach (var b in data)
			crc = table[(crc ^ b) & 0xFF] ^ (crc >> 8);

		return crc ^ 0xFFFFFFFFu;
	}
}
