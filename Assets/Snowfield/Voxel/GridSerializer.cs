using System;
using System.IO;
using Unity.Collections;

namespace Snowfield.Voxel
{
    /// <summary>
    /// RLE codec for density grids. Format: repeated [value:byte][count:ushort LE] runs; runs longer than 65535
    /// are split. Density fields are mostly-empty or mostly-full, so this compresses brutally well.
    /// This exact byte stream becomes the Phase 3 network blob — change it only with a version bump.
    /// </summary>
    public static class GridSerializer
    {
        public const byte Version = 1;

        public static byte[] Encode(NativeArray<byte> density)
        {
            using var ms = new MemoryStream();
            ms.WriteByte(Version);
            int i = 0, n = density.Length;
            while (i < n)
            {
                byte v = density[i];
                int run = 1;
                while (i + run < n && density[i + run] == v && run < ushort.MaxValue) run++;
                ms.WriteByte(v);
                ms.WriteByte((byte)(run & 0xFF));
                ms.WriteByte((byte)(run >> 8));
                i += run;
            }
            return ms.ToArray();
        }

        /// <summary>Decode into an existing array; throws if sizes don't match.</summary>
        public static void Decode(byte[] data, NativeArray<byte> into)
        {
            if (data == null || data.Length < 1) throw new ArgumentException("empty blob");
            if (data[0] != Version) throw new ArgumentException($"unknown blob version {data[0]}");
            if ((data.Length - 1) % 3 != 0) throw new ArgumentException("truncated blob");
            int write = 0;
            for (int i = 1; i + 3 <= data.Length; i += 3)
            {
                byte v = data[i];
                int run = data[i + 1] | (data[i + 2] << 8);
                if (write + run > into.Length) throw new ArgumentException("blob longer than grid");
                for (int k = 0; k < run; k++) into[write + k] = v;
                write += run;
            }
            if (write != into.Length) throw new ArgumentException($"blob decoded {write} voxels, grid holds {into.Length}");
        }
    }
}
