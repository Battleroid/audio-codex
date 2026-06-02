using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace Tiger;

public sealed class Entry
{
    public int Index;
    public uint Reference;
    public byte FileType;
    public byte FileSubType;
    public uint StartingBlock;
    public uint StartingBlockOffset;
    public uint FileSize;
    public bool IsWem => FileType == 26 && FileSubType == 7;
    public bool IsSoundbank => FileType == 26 && FileSubType == 6;
}

public sealed class Block
{
    public long Offset;
    public uint Size;
    public ushort PatchId;
    public ushort Flags;
    public byte[] GcmTag = Array.Empty<byte>();
}

/// <summary>Reads a Bungie Tiger Engine .pkg (header version 53 / Marathon retail).</summary>
public sealed class TigerPackage
{
    public const int BLOCK_SIZE = 0x40000;

    private static readonly byte[] AES_KEY_0 = {
        0xD6,0x2A,0xB2,0xC1,0x0C,0xC0,0x1B,0xC5,0x35,0xDB,0x7B,0x86,0x55,0xC7,0xDC,0x3B };
    private static readonly byte[] AES_KEY_1 = {
        0x3A,0x4A,0x5D,0x36,0x73,0xA6,0x60,0x58,0x7E,0x63,0xE6,0x76,0xE4,0x08,0x92,0xB5 };
    private static readonly byte[] AES_NONCE_BASE = {
        0x84,0xDF,0x11,0xC0,0xAC,0xAB,0xFA,0x20,0x33,0x11,0x26,0x99 };

    private const int F_COMPRESSED = 0x1;
    private const int F_ENCRYPTED = 0x2;
    private const int F_ALT_CIPHER = 0x4;

    public string Path { get; }
    public string PathBase { get; }
    public string Name { get; }
    public ushort Version { get; private set; }
    public ushort PkgId { get; private set; }
    public ushort PatchId { get; private set; }
    public List<Entry> Entries { get; } = new();
    public List<Block> Blocks { get; } = new();

    private readonly object _ioLock = new();
    private readonly Dictionary<ushort, FileStream> _streams = new();
    private uint _entryTableSize, _entryTableOffset, _blockTableSize, _blockTableOffset;

    // Small LRU cache of decoded blocks — entries frequently share a 256 KB block,
    // so this avoids re-decrypting/decompressing the same block repeatedly.
    private const int BlockCacheCap = 64;
    private readonly object _cacheLock = new();
    private readonly Dictionary<int, LinkedListNode<(int idx, byte[] data)>> _blockCache = new();
    private readonly LinkedList<(int idx, byte[] data)> _blockLru = new();

    public TigerPackage(string path)
    {
        Path = path;
        string fname = System.IO.Path.GetFileName(path);
        var m = Regex.Match(fname, @"^(.*)_(\d+)\.pkg$");
        if (!m.Success) throw new ArgumentException($"Unexpected package filename: {path}");
        PathBase = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(path)!, m.Groups[1].Value);
        Name = m.Groups[1].Value;

        // Read only what's needed: header, then the entry + block tables. The full
        // package body is read block-by-block on demand (packages can be ~700 MB).
        using var fs = File.OpenRead(path);
        byte[] header = ReadRegion(fs, 0, 0x130);
        ParseHeader(header);
        ParseEntries(ReadRegion(fs, _entryTableOffset, (int)_entryTableSize * 16));
        ParseBlocks(ReadRegion(fs, _blockTableOffset, (int)_blockTableSize * 0x30));
    }

    private static byte[] ReadRegion(FileStream fs, long offset, int length)
    {
        byte[] buf = new byte[length];
        fs.Seek(offset, SeekOrigin.Begin);
        int read = 0;
        while (read < length)
        {
            int n = fs.Read(buf, read, length - read);
            if (n == 0) break;
            read += n;
        }
        return buf;
    }

    private void ParseHeader(byte[] h)
    {
        Version = BinaryPrimitives.ReadUInt16LittleEndian(h.AsSpan(0x00));
        if (Version != 53) throw new InvalidDataException($"Unsupported package version {Version}");
        PkgId = BinaryPrimitives.ReadUInt16LittleEndian(h.AsSpan(0x10));
        PatchId = BinaryPrimitives.ReadUInt16LittleEndian(h.AsSpan(0x30));
        _entryTableSize = BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(0x60));
        _entryTableOffset = BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(0x64));
        _blockTableSize = BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(0x68));
        _blockTableOffset = BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(0x6C));
    }

    private void ParseEntries(byte[] t)
    {
        for (int i = 0; i < _entryTableSize; i++)
        {
            int off = i * 16;
            uint reference = BinaryPrimitives.ReadUInt32LittleEndian(t.AsSpan(off));
            uint ti = BinaryPrimitives.ReadUInt32LittleEndian(t.AsSpan(off + 4));
            ulong bi = BinaryPrimitives.ReadUInt64LittleEndian(t.AsSpan(off + 8));
            Entries.Add(new Entry
            {
                Index = i,
                Reference = reference,
                FileType = (byte)((ti >> 9) & 0x7F),
                FileSubType = (byte)((ti >> 6) & 0x7),
                StartingBlock = (uint)(bi & 0x3FFF),
                StartingBlockOffset = (uint)(((bi >> 14) & 0x3FFF) << 4),
                FileSize = (uint)(bi >> 28),
            });
        }
    }

    private void ParseBlocks(byte[] t)
    {
        for (int i = 0; i < _blockTableSize; i++)
        {
            int off = i * 0x30;
            Blocks.Add(new Block
            {
                Offset = BinaryPrimitives.ReadUInt32LittleEndian(t.AsSpan(off)),
                Size = BinaryPrimitives.ReadUInt32LittleEndian(t.AsSpan(off + 4)),
                PatchId = BinaryPrimitives.ReadUInt16LittleEndian(t.AsSpan(off + 8)),
                Flags = BinaryPrimitives.ReadUInt16LittleEndian(t.AsSpan(off + 0x0A)),
                GcmTag = t.AsSpan(off + 0x20, 16).ToArray(),
            });
        }
    }

    private byte[] Nonce()
    {
        var n = (byte[])AES_NONCE_BASE.Clone();
        n[0] ^= (byte)((PkgId >> 8) & 0xFF);
        n[1] = 0xEA;
        n[11] ^= (byte)(PkgId & 0xFF);
        return n;
    }

    private FileStream GetStream(ushort patchId)
    {
        if (_streams.TryGetValue(patchId, out var s)) return s;
        string p = patchId == PatchId ? Path : $"{PathBase}_{patchId}.pkg";
        s = new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.Read);
        _streams[patchId] = s;
        return s;
    }

    private byte[] ReadBlock(int index)
    {
        lock (_cacheLock)
        {
            if (_blockCache.TryGetValue(index, out var node))
            {
                _blockLru.Remove(node);
                _blockLru.AddFirst(node);
                return node.Value.data;
            }
        }

        Block bh = Blocks[index];
        byte[] data = new byte[bh.Size];
        lock (_ioLock)
        {
            FileStream src = GetStream(bh.PatchId);
            src.Seek(bh.Offset, SeekOrigin.Begin);
            int read = 0;
            while (read < data.Length)
            {
                int n = src.Read(data, read, data.Length - read);
                if (n == 0) break;
                read += n;
            }
        }

        if ((bh.Flags & F_ENCRYPTED) != 0)
        {
            byte[] key = (bh.Flags & F_ALT_CIPHER) != 0 ? AES_KEY_1 : AES_KEY_0;
            data = DecryptGcm(key, Nonce(), data, bh.GcmTag);
        }
        if ((bh.Flags & F_COMPRESSED) != 0)
            data = Oodle.Decompress(data, BLOCK_SIZE);

        lock (_cacheLock)
        {
            if (!_blockCache.ContainsKey(index))
            {
                var node = _blockLru.AddFirst((index, data));
                _blockCache[index] = node;
                while (_blockLru.Count > BlockCacheCap)
                {
                    var last = _blockLru.Last!;
                    _blockLru.RemoveLast();
                    _blockCache.Remove(last.Value.idx);
                }
            }
        }
        return data;
    }

    private static byte[] DecryptGcm(byte[] key, byte[] nonce, byte[] ciphertext, byte[] tag)
    {
        byte[] plain = new byte[ciphertext.Length];
        try
        {
            using var gcm = new AesGcm(key, 16);
            gcm.Decrypt(nonce, ciphertext, tag, plain);
            return plain;
        }
        catch (CryptographicException)
        {
            // tag mismatch on some blocks: fall back to unverified GCM-CTR decryption (matches reference tools)
            return DecryptGcmCtrNoVerify(key, nonce, ciphertext);
        }
    }

    private static byte[] DecryptGcmCtrNoVerify(byte[] key, byte[] nonce, byte[] ciphertext)
    {
        using var aes = Aes.Create();
        aes.Key = key;
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;
        using var enc = aes.CreateEncryptor();

        // J0 = nonce(12) || 0x00000001 ; first data counter = inc32(J0)
        byte[] counter = new byte[16];
        Array.Copy(nonce, 0, counter, 0, 12);
        counter[15] = 1;
        Inc32(counter);

        byte[] outBuf = new byte[ciphertext.Length];
        byte[] keystream = new byte[16];
        for (int i = 0; i < ciphertext.Length; i += 16)
        {
            enc.TransformBlock(counter, 0, 16, keystream, 0);
            int n = Math.Min(16, ciphertext.Length - i);
            for (int j = 0; j < n; j++) outBuf[i + j] = (byte)(ciphertext[i + j] ^ keystream[j]);
            Inc32(counter);
        }
        return outBuf;
    }

    private static void Inc32(byte[] counter)
    {
        for (int i = 15; i >= 12; i--) { if (++counter[i] != 0) break; }
    }

    public byte[] ReadEntry(int index)
    {
        Entry e = Entries[index];
        using var ms = new MemoryStream((int)e.FileSize + BLOCK_SIZE);
        int blockI = (int)e.StartingBlock;
        byte[] first = ReadBlock(blockI);
        ms.Write(first, (int)e.StartingBlockOffset, first.Length - (int)e.StartingBlockOffset);
        while (ms.Length < e.FileSize)
        {
            blockI++;
            byte[] b = ReadBlock(blockI);
            ms.Write(b, 0, b.Length);
        }
        byte[] all = ms.GetBuffer();
        byte[] result = new byte[e.FileSize];
        Array.Copy(all, result, (int)e.FileSize);
        return result;
    }

    public uint TagHash(int index) => (uint)(0x80800000u + ((uint)PkgId << 13) + (uint)index);

    public void CloseStreams()
    {
        lock (_ioLock)
        {
            foreach (var s in _streams.Values) s.Dispose();
            _streams.Clear();
        }
    }
}
