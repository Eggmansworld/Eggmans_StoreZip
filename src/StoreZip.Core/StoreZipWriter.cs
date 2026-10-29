using System.Buffers.Binary;
using System.IO.Compression;
using System.IO.Hashing;
using System.Security.Cryptography;
using System.Text;
using ZstdSharp;

namespace StoreZip;

/// <summary>One file written into a StoreZip.</summary>
public sealed record StoreZipMember(string Name, long Size, uint Crc32, string Sha1);

/// <summary>A finished StoreZip: its own size and hashes, and the files in it (in zip order).</summary>
public sealed record StoreZipResult(long Size, uint Crc32, string Sha1, IReadOnlyList<StoreZipMember> Members)
{
    public string CrcHex => Crc32.ToString("x8");
}

public sealed class StoreZipException(string message) : Exception(message);

/// <summary>
/// Writes a StoreZip: a zip whose bytes depend only on the names and contents
/// of its files, so anyone rebuilding it from the same files gets the same
/// hash. Every file is stored (no compression), sorted by lower-cased name,
/// with a fixed date (1996-12-24 23:32, as TorrentZip), no folder entries, no
/// extra fields, no attributes and no comment. Zip64 records appear only when
/// a size, offset or count needs them. docs/StoreZip-Format.md is the spec.
/// </summary>
public static class StoreZipWriter
{
    public const ushort DosTime = 0xBC00;   // 23:32:00
    public const ushort DosDate = 0x2198;   // 1996-12-24

    private const uint LocalSig = 0x04034b50, CentralSig = 0x02014b50, EocdSig = 0x06054b50;
    private const uint Eocd64Sig = 0x06064b50, Locator64Sig = 0x07064b50;
    private const int Chunk = 1024 * 1024;

    /// <summary>TorrentZip's order: lower-cased name, then the exact name for names that differ only in case.</summary>
    public static int CompareNames(string a, string b)
    {
        int c = string.CompareOrdinal(a.ToLowerInvariant(), b.ToLowerInvariant());
        return c != 0 ? c : string.CompareOrdinal(a, b);
    }

    /// <summary>Convert a zip file (stored, deflate or zstd members) into a StoreZip file.</summary>
    public static StoreZipResult Convert(string sourceZip, string destination, CancellationToken cancel)
    {
        string temp = destination + ".tmp";
        try
        {
            StoreZipResult result;
            using (var src = new FileStream(sourceZip, FileMode.Open, FileAccess.Read, FileShare.Read, Chunk))
            using (var dst = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, Chunk))
                result = Write(src, dst, cancel);
            File.Move(temp, destination, overwrite: true);
            return result;
        }
        finally
        {
            if (File.Exists(temp))
            {
                try { File.Delete(temp); } catch { /* best effort */ }
            }
        }
    }

    /// <summary>
    /// Whether a zip already is a StoreZip: rebuilding it gives the same bytes.
    /// <paramref name="sha1"/> is the zip's own SHA-1, when the caller already has it.
    /// </summary>
    public static bool IsStoreZip(Stream zip, string? sha1, CancellationToken cancel)
    {
        try
        {
            var entries = ZipCentralDirectory.Read(zip);
            if (entries.Any(e => e.Method != 0 || e.IsDirectory))
                return false;
            if (sha1 is null)
            {
                zip.Seek(0, SeekOrigin.Begin);
                sha1 = System.Convert.ToHexStringLower(SHA1.HashData(zip));
            }
            var rebuilt = Write(zip, Stream.Null, cancel);
            return rebuilt.Size == zip.Length && rebuilt.Sha1.Equals(sha1, StringComparison.OrdinalIgnoreCase);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Write a StoreZip of every file in <paramref name="source"/> (a seekable zip) to <paramref name="output"/>.</summary>
    public static StoreZipResult Write(Stream source, Stream output, CancellationToken cancel)
    {
        var entries = ZipCentralDirectory.Read(source);
        var files = new List<(string Name, byte[] NameBytes, bool Utf8, ZipEntryInfo Entry)>();
        foreach (var e in entries)
        {
            string name = e.FileName.Replace('\\', '/');
            if (name.EndsWith('/'))
                continue;   // folder entries carry no data and aren't written
            if ((e.Flags & 1) != 0)
                throw new StoreZipException($"{name} is encrypted");
            if (e.Method is not (0 or 8 or 93))
                throw new StoreZipException($"{name} uses compression method {e.Method}, which isn't supported (stored, deflate and zstd are)");
            bool ascii = name.All(c => c is >= ' ' and <= '~');
            files.Add((name, ascii ? Encoding.ASCII.GetBytes(name) : Encoding.UTF8.GetBytes(name), !ascii, e));
        }
        var dup = files.GroupBy(f => f.Name, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1);
        if (dup is not null)
            throw new StoreZipException($"\"{dup.Key}\" is in the zip more than once");
        files.Sort((a, b) => CompareNames(a.Name, b.Name));

        using var sink = new HashingStream(output);
        var central = new MemoryStream();
        var members = new List<StoreZipMember>(files.Count);
        byte[] buffer = new byte[Chunk];
        foreach (var (name, nameBytes, utf8, e) in files)
        {
            cancel.ThrowIfCancellationRequested();
            long size = e.UncompressedSize;
            long offset = sink.Position;
            bool bigSize = size >= 0xFFFFFFFF;
            bool bigOffset = offset >= 0xFFFFFFFF;
            ushort version = (ushort)(bigSize || bigOffset ? 45 : 10);
            ushort flags = (ushort)(utf8 ? 0x0800 : 0);

            // Local header — sizes and CRC come from the source, then are checked against the data
            var local = new byte[30];
            var l = local.AsSpan();
            BinaryPrimitives.WriteUInt32LittleEndian(l, LocalSig);
            BinaryPrimitives.WriteUInt16LittleEndian(l[4..], version);
            BinaryPrimitives.WriteUInt16LittleEndian(l[6..], flags);
            BinaryPrimitives.WriteUInt16LittleEndian(l[8..], 0);
            BinaryPrimitives.WriteUInt16LittleEndian(l[10..], DosTime);
            BinaryPrimitives.WriteUInt16LittleEndian(l[12..], DosDate);
            BinaryPrimitives.WriteUInt32LittleEndian(l[14..], e.Crc32);
            BinaryPrimitives.WriteUInt32LittleEndian(l[18..], bigSize ? 0xFFFFFFFF : (uint)size);
            BinaryPrimitives.WriteUInt32LittleEndian(l[22..], bigSize ? 0xFFFFFFFF : (uint)size);
            BinaryPrimitives.WriteUInt16LittleEndian(l[26..], (ushort)nameBytes.Length);
            BinaryPrimitives.WriteUInt16LittleEndian(l[28..], (ushort)(bigSize ? 20 : 0));
            sink.Write(local);
            sink.Write(nameBytes);
            if (bigSize)
            {
                var z = new byte[20];
                BinaryPrimitives.WriteUInt16LittleEndian(z, 0x0001);
                BinaryPrimitives.WriteUInt16LittleEndian(z.AsSpan(2), 16);
                BinaryPrimitives.WriteInt64LittleEndian(z.AsSpan(4), size);
                BinaryPrimitives.WriteInt64LittleEndian(z.AsSpan(12), size);
                sink.Write(z);
            }

            // Data
            var crc = new Crc32();
            using var sha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
            long copied = 0;
            if (size > 0)
            {
                source.Seek(ZipCentralDirectory.GetDataOffset(source, e), SeekOrigin.Begin);
                using var bounded = new BoundedStream(source, e.CompressedSize);
                using var data = e.Method switch
                {
                    0 => (Stream)bounded,
                    8 => new DeflateStream(bounded, CompressionMode.Decompress, leaveOpen: true),
                    _ => new DecompressionStream(bounded, leaveOpen: true),
                };
                int read;
                while ((read = data.Read(buffer, 0, (int)Math.Min(buffer.Length, size - copied + 1))) > 0)
                {
                    cancel.ThrowIfCancellationRequested();
                    if (copied + read > size)
                        throw new StoreZipException($"{name}: holds more data than its zip header says — the zip is damaged");
                    crc.Append(buffer.AsSpan(0, read));
                    sha1.AppendData(buffer, 0, read);
                    sink.Write(buffer, 0, read);
                    copied += read;
                }
            }
            if (copied != size || crc.GetCurrentHashAsUInt32() != e.Crc32)
                throw new StoreZipException($"{name}: data doesn't match its zip header (CRC or size) — the zip is damaged");
            members.Add(new StoreZipMember(name, size, e.Crc32, System.Convert.ToHexStringLower(sha1.GetCurrentHash())));

            // Central header for later
            int extraLen = (bigSize ? 16 : 0) + (bigOffset ? 8 : 0);
            var c = new byte[46];
            var s = c.AsSpan();
            BinaryPrimitives.WriteUInt32LittleEndian(s, CentralSig);
            BinaryPrimitives.WriteUInt16LittleEndian(s[4..], version);   // made by: MS-DOS host, same version
            BinaryPrimitives.WriteUInt16LittleEndian(s[6..], version);
            BinaryPrimitives.WriteUInt16LittleEndian(s[8..], flags);
            BinaryPrimitives.WriteUInt16LittleEndian(s[10..], 0);
            BinaryPrimitives.WriteUInt16LittleEndian(s[12..], DosTime);
            BinaryPrimitives.WriteUInt16LittleEndian(s[14..], DosDate);
            BinaryPrimitives.WriteUInt32LittleEndian(s[16..], e.Crc32);
            BinaryPrimitives.WriteUInt32LittleEndian(s[20..], bigSize ? 0xFFFFFFFF : (uint)size);
            BinaryPrimitives.WriteUInt32LittleEndian(s[24..], bigSize ? 0xFFFFFFFF : (uint)size);
            BinaryPrimitives.WriteUInt16LittleEndian(s[28..], (ushort)nameBytes.Length);
            BinaryPrimitives.WriteUInt16LittleEndian(s[30..], (ushort)(extraLen > 0 ? extraLen + 4 : 0));
            // comment length, disk, internal and external attributes stay 0
            BinaryPrimitives.WriteUInt32LittleEndian(s[42..], bigOffset ? 0xFFFFFFFF : (uint)offset);
            central.Write(c);
            central.Write(nameBytes);
            if (extraLen > 0)
            {
                var z = new byte[4 + extraLen];
                BinaryPrimitives.WriteUInt16LittleEndian(z, 0x0001);
                BinaryPrimitives.WriteUInt16LittleEndian(z.AsSpan(2), (ushort)extraLen);
                int p = 4;
                if (bigSize)
                {
                    BinaryPrimitives.WriteInt64LittleEndian(z.AsSpan(p), size);
                    BinaryPrimitives.WriteInt64LittleEndian(z.AsSpan(p + 8), size);
                    p += 16;
                }
                if (bigOffset)
                    BinaryPrimitives.WriteInt64LittleEndian(z.AsSpan(p), offset);
                central.Write(z);
            }
        }

        // Central directory, then the end records
        long cdOffset = sink.Position;
        long cdSize = central.Length;
        central.Position = 0;
        central.CopyTo(sink);
        long count = files.Count;
        if (count >= 0xFFFF || cdOffset >= 0xFFFFFFFF || cdSize >= 0xFFFFFFFF)
        {
            long eocd64 = sink.Position;
            var z = new byte[56];
            var s = z.AsSpan();
            BinaryPrimitives.WriteUInt32LittleEndian(s, Eocd64Sig);
            BinaryPrimitives.WriteInt64LittleEndian(s[4..], 44);
            BinaryPrimitives.WriteUInt16LittleEndian(s[12..], 45);
            BinaryPrimitives.WriteUInt16LittleEndian(s[14..], 45);
            // disk numbers stay 0
            BinaryPrimitives.WriteInt64LittleEndian(s[24..], count);
            BinaryPrimitives.WriteInt64LittleEndian(s[32..], count);
            BinaryPrimitives.WriteInt64LittleEndian(s[40..], cdSize);
            BinaryPrimitives.WriteInt64LittleEndian(s[48..], cdOffset);
            sink.Write(z);
            var loc = new byte[20];
            BinaryPrimitives.WriteUInt32LittleEndian(loc, Locator64Sig);
            BinaryPrimitives.WriteInt64LittleEndian(loc.AsSpan(8), eocd64);
            BinaryPrimitives.WriteUInt32LittleEndian(loc.AsSpan(16), 1);
            sink.Write(loc);
        }
        var end = new byte[22];
        var t = end.AsSpan();
        BinaryPrimitives.WriteUInt32LittleEndian(t, EocdSig);
        BinaryPrimitives.WriteUInt16LittleEndian(t[8..], (ushort)Math.Min(count, 0xFFFF));
        BinaryPrimitives.WriteUInt16LittleEndian(t[10..], (ushort)Math.Min(count, 0xFFFF));
        BinaryPrimitives.WriteUInt32LittleEndian(t[12..], (uint)Math.Min(cdSize, 0xFFFFFFFF));
        BinaryPrimitives.WriteUInt32LittleEndian(t[16..], (uint)Math.Min(cdOffset, 0xFFFFFFFF));
        sink.Write(end);
        sink.Flush();
        return new StoreZipResult(sink.Position, sink.Crc, sink.Sha1, members);
    }

    /// <summary>Passes writes through while counting bytes and hashing them (CRC32 + SHA-1).</summary>
    private sealed class HashingStream(Stream inner) : Stream
    {
        private readonly Crc32 _crc = new();
        private readonly IncrementalHash _sha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        private long _position;

        public uint Crc => _crc.GetCurrentHashAsUInt32();
        public string Sha1 => System.Convert.ToHexStringLower(_sha1.GetCurrentHash());
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _position;
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            _crc.Append(buffer);
            _sha1.AppendData(buffer);
            inner.Write(buffer);
            _position += buffer.Length;
        }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing)
                _sha1.Dispose();
            base.Dispose(disposing);
        }
    }

    /// <summary>Reads at most <c>limit</c> bytes from the current position of a stream it doesn't own.</summary>
    private sealed class BoundedStream(Stream inner, long limit) : Stream
    {
        private long _left = limit;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            if (_left <= 0)
                return 0;
            int n = inner.Read(buffer[..(int)Math.Min(buffer.Length, _left)]);
            _left -= n;
            return n;
        }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
