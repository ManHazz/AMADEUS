using System;

/// <summary>Decodes RIFF/WAVE (PCM 8/16/24/32-bit or float32) to mono float samples.</summary>
public static class WavDecoder
{
    public static bool TryDecode(byte[] wav, out float[] samples, out int rate)
    {
        samples = new float[0]; rate = 0;
        if (wav == null || wav.Length < 12) return false;
        if (!Tag(wav, 0, "RIFF") || !Tag(wav, 8, "WAVE")) return false;

        int fmtTag = 0, channels = 0, bits = 0, dataOff = -1, dataLen = 0;
        int pos = 12;
        while (pos + 8 <= wav.Length)
        {
            long size = U32(wav, pos + 4);
            int body = pos + 8;
            if (Tag(wav, pos, "fmt "))
            {
                if (size < 16 || body + 16 > wav.Length) return false;
                fmtTag = U16(wav, body);
                channels = U16(wav, body + 2);
                rate = (int)U32(wav, body + 4);
                bits = U16(wav, body + 14);
                if (fmtTag == 0xFFFE && size >= 26 && body + 26 <= wav.Length) fmtTag = U16(wav, body + 24);
            }
            else if (Tag(wav, pos, "data"))
            {
                dataOff = body;
                // streaming writers may leave 0 / 0xFFFFFFFF; clamp to available bytes
                long avail = wav.Length - body;
                dataLen = (int)((size == 0 || size > avail) ? avail : size);
                break;
            }
            long next = body + size + (size & 1);
            if (next > wav.Length) break;
            pos = (int)next;
        }
        if (dataOff < 0 || channels < 1 || rate <= 0) return false;
        bool isFloat = fmtTag == 3;
        if (fmtTag != 1 && !isFloat) return false;
        int bps = bits / 8;
        if (!(isFloat ? bits == 32 : (bits == 8 || bits == 16 || bits == 24 || bits == 32))) return false;

        int frameBytes = bps * channels;
        int frames = dataLen / frameBytes;
        var outS = new float[frames];
        for (int i = 0; i < frames; i++)
        {
            float sum = 0;
            for (int c = 0; c < channels; c++)
                sum += Sample(wav, dataOff + i * frameBytes + c * bps, bits, isFloat);
            outS[i] = sum / channels;
        }
        samples = outS;
        return true;
    }

    static float Sample(byte[] b, int p, int bits, bool isFloat)
    {
        switch (bits)
        {
            case 8: return (b[p] - 128) / 128f;
            case 16: return (short)(b[p] | (b[p + 1] << 8)) / 32768f;
            case 24: return ((b[p] << 8 | b[p + 1] << 16 | b[p + 2] << 24) >> 8) / 8388608f;
            default:
                if (isFloat) return BitConverter.ToSingle(b, p);
                return (b[p] | (b[p + 1] << 8) | (b[p + 2] << 16) | (b[p + 3] << 24)) / 2147483648f;
        }
    }

    /// <summary>Encodes mono float samples as a 16-bit PCM WAV file.</summary>
    public static byte[] EncodePcm16(float[] samples, int count, int rate)
    {
        int dataLen = count * 2;
        var o = new byte[44 + dataLen];
        W(o, 0, "RIFF"); P32(o, 4, 36 + dataLen); W(o, 8, "WAVE");
        W(o, 12, "fmt "); P32(o, 16, 16); P16(o, 20, 1); P16(o, 22, 1);
        P32(o, 24, rate); P32(o, 28, rate * 2); P16(o, 32, 2); P16(o, 34, 16);
        W(o, 36, "data"); P32(o, 40, dataLen);
        for (int i = 0; i < count; i++)
        {
            float f = samples[i];
            if (f > 1f) f = 1f; else if (f < -1f) f = -1f;
            short s = (short)Math.Round(f * 32767f);
            o[44 + 2 * i] = (byte)(s & 0xFF);
            o[45 + 2 * i] = (byte)((s >> 8) & 0xFF);
        }
        return o;
    }

    static bool Tag(byte[] b, int p, string t)
    {
        for (int i = 0; i < 4; i++) if (b[p + i] != t[i]) return false;
        return true;
    }
    static int U16(byte[] b, int p) { return b[p] | (b[p + 1] << 8); }
    static long U32(byte[] b, int p) { return (uint)(b[p] | (b[p + 1] << 8) | (b[p + 2] << 16) | (b[p + 3] << 24)); }
    static void W(byte[] o, int p, string t) { for (int i = 0; i < 4; i++) o[p + i] = (byte)t[i]; }
    static void P16(byte[] o, int p, int v) { o[p] = (byte)v; o[p + 1] = (byte)(v >> 8); }
    static void P32(byte[] o, int p, int v) { P16(o, p, v & 0xFFFF); P16(o, p + 2, (v >> 16) & 0xFFFF); }
}
