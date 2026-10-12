using System;

/// <summary>
/// Incremental parser for the Voice-Agent frame stream:
/// 1 byte type | uint32 big-endian length | payload.
/// Feed arbitrary byte chunks; complete frames are emitted via OnFrame.
/// </summary>
public class FrameParser
{
    public const byte Meta = 0x01, Audio = 0x02, Event = 0x03;
    public const int MaxPayload = 64 * 1024 * 1024;

    public event Action<byte, byte[]> OnFrame;

    byte[] _buf = new byte[4096];
    int _len;

    /// <summary>Bytes buffered but not yet forming a complete frame.</summary>
    public int Pending { get { return _len; } }

    public void Reset() { _len = 0; }

    public void Feed(byte[] data) { if (data != null) Feed(data, 0, data.Length); }

    /// <summary>Throws InvalidOperationException if a frame declares an absurd length.</summary>
    public void Feed(byte[] data, int offset, int count)
    {
        if (data == null || count <= 0) return;
        if (_len + count > _buf.Length)
            Array.Resize(ref _buf, Math.Max(_buf.Length * 2, _len + count));
        Buffer.BlockCopy(data, offset, _buf, _len, count);
        _len += count;

        int pos = 0;
        while (_len - pos >= 5)
        {
            uint plen = ((uint)_buf[pos + 1] << 24) | ((uint)_buf[pos + 2] << 16)
                      | ((uint)_buf[pos + 3] << 8) | _buf[pos + 4];
            if (plen > MaxPayload)
            {
                _len = 0;
                throw new InvalidOperationException("frame too large: " + plen);
            }
            if (_len - pos < 5 + (int)plen) break;
            byte type = _buf[pos];
            var payload = new byte[plen];
            Buffer.BlockCopy(_buf, pos + 5, payload, 0, (int)plen);
            pos += 5 + (int)plen;
            var h = OnFrame;
            if (h != null) h(type, payload);
        }
        if (pos > 0)
        {
            _len -= pos;
            if (_len > 0) Buffer.BlockCopy(_buf, pos, _buf, 0, _len);
        }
    }
}
