using System;

/// <summary>Thread-safe float ring buffer. Overflow drops oldest samples.</summary>
public class PcmRingBuffer
{
    readonly float[] _buf;
    int _head, _count;
    readonly object _lock = new object();

    public PcmRingBuffer(int capacity) { _buf = new float[Math.Max(1, capacity)]; }

    public int Capacity { get { return _buf.Length; } }
    public int Count { get { lock (_lock) return _count; } }

    public void Write(float[] src, int count = -1)
    {
        if (src == null) return;
        if (count < 0 || count > src.Length) count = src.Length;
        lock (_lock)
        {
            int start = 0;
            if (count > _buf.Length) { start = count - _buf.Length; count = _buf.Length; }
            int overflow = _count + count - _buf.Length;
            if (overflow > 0) { _head = (_head + overflow) % _buf.Length; _count -= overflow; }
            int tail = (_head + _count) % _buf.Length;
            for (int i = 0; i < count; i++)
            {
                _buf[tail] = src[start + i];
                tail = (tail + 1) % _buf.Length;
            }
            _count += count;
        }
    }

    /// <summary>Fills dst; missing samples are zero (silence). Returns real samples read.</summary>
    public int Read(float[] dst)
    {
        lock (_lock)
        {
            int n = Math.Min(dst.Length, _count);
            for (int i = 0; i < n; i++)
            {
                dst[i] = _buf[_head];
                _head = (_head + 1) % _buf.Length;
            }
            _count -= n;
            for (int i = n; i < dst.Length; i++) dst[i] = 0f;
            return n;
        }
    }

    public void Clear() { lock (_lock) { _head = 0; _count = 0; } }
}
