using System;
using System.Buffers.Binary;

namespace Tiger;

/// <summary>Lightweight Wwise RIFF/WAVE header parser — pulls metadata without decoding audio.</summary>
public static class WemInfo
{
    public sealed class Info
    {
        public bool Valid;
        public ushort Codec;
        public ushort Channels;
        public uint SampleRate;
        public uint AvgBytesPerSec;
        public uint TotalSamples;   // from 'vorb' or estimate; 0 if unknown
        public string CodecName => CodecNameOf(Codec);
        public double DurationSeconds =>
            (TotalSamples > 0 && SampleRate > 0) ? (double)TotalSamples / SampleRate :
            (AvgBytesPerSec > 0 ? 0 : 0);
    }

    public static string CodecNameOf(ushort codec) => codec switch
    {
        0xFFFF => "Wwise Vorbis",
        0x0166 => "XMA2",
        0x0002 => "Wwise ADPCM",
        0x0001 => "PCM",
        0x3039 => "Wwise Opus",
        0xAAC0 => "AAC",
        _ => $"0x{codec:X4}",
    };

    public static Info Parse(byte[] data)
    {
        var info = new Info();
        if (data.Length < 12) return info;
        bool isRiff = data[0] == 'R' && data[1] == 'I' && data[2] == 'F' && data[3] == 'F';
        bool isRifx = data[0] == 'R' && data[1] == 'I' && data[2] == 'F' && data[3] == 'X';
        if (!isRiff && !isRifx) return info;
        if (!(data[8] == 'W' && data[9] == 'A' && data[10] == 'V' && data[11] == 'E')) return info;
        info.Valid = true;

        int pos = 12;
        while (pos + 8 <= data.Length)
        {
            string cid = System.Text.Encoding.ASCII.GetString(data, pos, 4);
            uint csz = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos + 4));
            int body = pos + 8;
            if (cid == "fmt " && body + 16 <= data.Length)
            {
                info.Codec = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(body));
                info.Channels = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(body + 2));
                info.SampleRate = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(body + 4));
                info.AvgBytesPerSec = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(body + 8));
            }
            else if (cid == "vorb" && body + 4 <= data.Length)
            {
                info.TotalSamples = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(body));
            }
            if (csz == 0 || csz > data.Length) break;
            pos = body + (int)csz + ((int)csz & 1);
        }
        return info;
    }
}
