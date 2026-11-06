using System.IO.Compression;
using System.Text;

namespace Hodozip.Core.Tests;

internal sealed class TestZip
{
    private readonly List<RawEntry> _entries = new();

    public void AddStored(
        string name,
        string content,
        Encoding? nameEncoding = null,
        bool utf8Flag = false,
        int externalAttributes = 0)
    {
        var encoding = nameEncoding ?? Encoding.UTF8;
        _entries.Add(new RawEntry(
            encoding.GetBytes(name),
            Encoding.UTF8.GetBytes(content),
            utf8Flag,
            externalAttributes));
    }

    public byte[] Build()
    {
        using var localStream = new MemoryStream();
        var offsets = new List<uint>(_entries.Count);

        foreach (var entry in _entries)
        {
            offsets.Add((uint)localStream.Length);
            WriteLocalEntry(localStream, entry);
        }

        var centralOffset = (uint)localStream.Length;
        using var centralStream = new MemoryStream();

        for (var i = 0; i < _entries.Count; i++)
        {
            WriteCentralEntry(centralStream, _entries[i], offsets[i]);
        }

        var centralSize = (uint)centralStream.Length;
        using var result = new MemoryStream();
        localStream.Position = 0;
        localStream.CopyTo(result);
        centralStream.Position = 0;
        centralStream.CopyTo(result);

        using var writer = new BinaryWriter(result, Encoding.UTF8, leaveOpen: true);
        writer.Write(0x06054b50u);
        writer.Write((ushort)0);
        writer.Write((ushort)0);
        writer.Write((ushort)_entries.Count);
        writer.Write((ushort)_entries.Count);
        writer.Write(centralSize);
        writer.Write(centralOffset);
        writer.Write((ushort)0);
        writer.Flush();

        return result.ToArray();
    }

    private static void WriteLocalEntry(Stream stream, RawEntry entry)
    {
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        var crc = Crc32.Compute(entry.Content);
        writer.Write(0x04034b50u);
        writer.Write((ushort)20);
        writer.Write(entry.Utf8Flag ? (ushort)0x0800 : (ushort)0);
        writer.Write((ushort)0); // stored
        writer.Write(DosTime);
        writer.Write(DosDate);
        writer.Write(crc);
        writer.Write((uint)entry.Content.Length);
        writer.Write((uint)entry.Content.Length);
        writer.Write((ushort)entry.NameBytes.Length);
        writer.Write((ushort)0);
        writer.Write(entry.NameBytes);
        writer.Write(entry.Content);
    }

    private static void WriteCentralEntry(Stream stream, RawEntry entry, uint localOffset)
    {
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        var crc = Crc32.Compute(entry.Content);
        writer.Write(0x02014b50u);
        writer.Write((ushort)0x031E); // made by Unix, version 3.0
        writer.Write((ushort)20);
        writer.Write(entry.Utf8Flag ? (ushort)0x0800 : (ushort)0);
        writer.Write((ushort)0);
        writer.Write(DosTime);
        writer.Write(DosDate);
        writer.Write(crc);
        writer.Write((uint)entry.Content.Length);
        writer.Write((uint)entry.Content.Length);
        writer.Write((ushort)entry.NameBytes.Length);
        writer.Write((ushort)0);
        writer.Write((ushort)0);
        writer.Write((ushort)0);
        writer.Write((ushort)0);
        writer.Write(entry.ExternalAttributes);
        writer.Write(localOffset);
        writer.Write(entry.NameBytes);
    }

    private static ushort DosTime => (ushort)((3 << 11) | (4 << 5) | (6 / 2));

    private static ushort DosDate => (ushort)(((2024 - 1980) << 9) | (1 << 5) | 2);

    private sealed record RawEntry(byte[] NameBytes, byte[] Content, bool Utf8Flag, int ExternalAttributes);

    private static class Crc32
    {
        private static readonly uint[] Table = CreateTable();

        public static uint Compute(ReadOnlySpan<byte> data)
        {
            var crc = 0xFFFFFFFFu;
            foreach (var value in data)
            {
                crc = Table[(crc ^ value) & 0xFF] ^ (crc >> 8);
            }

            return ~crc;
        }

        private static uint[] CreateTable()
        {
            var table = new uint[256];
            for (uint i = 0; i < table.Length; i++)
            {
                var value = i;
                for (var bit = 0; bit < 8; bit++)
                {
                    value = (value & 1) != 0 ? 0xEDB88320u ^ (value >> 1) : value >> 1;
                }

                table[i] = value;
            }

            return table;
        }
    }
}

internal static class ZipFactory
{
    public static string Create(
        string directory,
        string fileName,
        params (string Name, string? Content)[] entries)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, fileName);
        using var stream = File.Create(path);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);
        foreach (var (name, content) in entries)
        {
            var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
            if (content is null)
            {
                continue;
            }

            using var writer = new StreamWriter(entry.Open());
            writer.Write(content);
        }

        return path;
    }

    public static string CreateWithBytes(string directory, string fileName, string entryName, byte[] data)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, fileName);
        using var stream = File.Create(path);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);
        var entry = archive.CreateEntry(entryName, CompressionLevel.SmallestSize);
        using var entryStream = entry.Open();
        entryStream.Write(data);
        return path;
    }
}
