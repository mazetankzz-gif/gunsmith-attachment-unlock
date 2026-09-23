using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace AttachmentUnlock
{
    /// <summary>Minimal read-only RPF7 reader: enough to pull unencrypted files out of an OpenIV ("OPEN") archive.</summary>
    internal sealed class Rpf : IDisposable
    {
        public sealed class Entry
        {
            public string Path;
            public long Offset;
            public long CompressedSize;
            public uint UncompressedSize;
            public bool Encrypted;
            public bool IsResource;
        }

        readonly Stream fs;
        readonly BinaryReader br;
        public readonly List<Entry> Entries = new List<Entry>();
        public readonly bool IsOpenArchive;

        public Rpf(string path) : this(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)) { }

        /// <summary>For an archive stored inside another archive.</summary>
        public Rpf(byte[] data) : this(new MemoryStream(data)) { }

        Rpf(Stream stream)
        {
            fs = stream;
            br = new BinaryReader(fs);
            uint magic = br.ReadUInt32(), count = br.ReadUInt32(), namesLength = br.ReadUInt32(), encryption = br.ReadUInt32();
            IsOpenArchive = magic == 0x52504637 && (encryption == 0x4E45504F || encryption == 0);
            if (!IsOpenArchive) return;

            byte[] entries = br.ReadBytes((int)(count * 16));
            byte[] names = br.ReadBytes((int)namesLength);
            var paths = new string[count];
            paths[0] = "";
            for (int i = 0; i < count; i++)
            {
                int o = i * 16;
                if (BitConverter.ToUInt32(entries, o + 4) != 0x7FFFFF00) continue;
                uint first = BitConverter.ToUInt32(entries, o + 8), n = BitConverter.ToUInt32(entries, o + 12);
                for (uint c = first; c < first + n && c < count; c++)
                {
                    int co = (int)c * 16;
                    ulong q = BitConverter.ToUInt64(entries, co);
                    bool dir = BitConverter.ToUInt32(entries, co + 4) == 0x7FFFFF00;
                    int nameOffset = dir ? (int)BitConverter.ToUInt32(entries, co) : (int)(q & 0xFFFF);
                    int end = Array.IndexOf(names, (byte)0, nameOffset);
                    if (end < 0) end = names.Length;
                    paths[c] = (paths[i].Length == 0 ? "" : paths[i] + "/") + Encoding.ASCII.GetString(names, nameOffset, end - nameOffset);
                }
            }
            for (int i = 0; i < count; i++)
            {
                int o = i * 16;
                if (BitConverter.ToUInt32(entries, o + 4) == 0x7FFFFF00 || paths[i] == null) continue;
                ulong q = BitConverter.ToUInt64(entries, o);
                long cs = (long)((q >> 16) & 0xFFFFFF), raw = (long)((q >> 40) & 0xFFFFFF);
                Entries.Add(new Entry
                {
                    Path = paths[i],
                    IsResource = (raw & 0x800000) != 0,
                    Offset = (raw & 0x7FFFFF) * 512,
                    CompressedSize = cs,
                    UncompressedSize = BitConverter.ToUInt32(entries, o + 8),
                    Encrypted = BitConverter.ToUInt32(entries, o + 12) != 0,
                });
            }
        }

        public byte[] Read(Entry e)
        {
            if (e.Encrypted || e.IsResource) return null;
            fs.Position = e.Offset;
            if (e.CompressedSize == 0) return br.ReadBytes((int)e.UncompressedSize);
            byte[] raw = br.ReadBytes((int)e.CompressedSize);
            using (var ds = new DeflateStream(new MemoryStream(raw), CompressionMode.Decompress))
            using (var ms = new MemoryStream())
            {
                ds.CopyTo(ms);
                return ms.ToArray();
            }
        }

        public void Dispose()
        {
            br?.Dispose();
            fs?.Dispose();
        }
    }
}
