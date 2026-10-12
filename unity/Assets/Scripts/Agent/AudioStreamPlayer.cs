using UnityEngine;

[RequireComponent(typeof(AudioSource))]
public class AudioStreamPlayer : MonoBehaviour
{
    [SerializeField] float prebufferSeconds = 0.15f;
    [SerializeField] float bufferSeconds = 20f;

    AudioSource _src;
    PcmRingBuffer _ring;
    int _rate;
    volatile bool _playing; // false = prebuffering (silence)
    int _prebufferSamples;

    public int SampleRate { get { return _rate; } }
    // 10 ms RMS windows of the samples actually handed to the mixer (audio thread -> main thread).
    // Lip sync reads these instead of AudioSource.GetOutputData, which returns zeros on many setups.
    readonly System.Collections.Generic.Queue<float> _levels = new System.Collections.Generic.Queue<float>();
    const int MaxQueuedWindows = 200;
    float _acc;

    /// <summary>Main thread: consume windows for the given elapsed seconds, return their peak RMS (0 when silent).</summary>
    public float ConsumeLevel(float dt, out bool hadWindows)
    {
        int backlog; lock (_levels) backlog = _levels.Count;
        // normally ~100 windows/s; if big chunks pile up (null audio device, stalls) drain faster instead of lagging
        _acc += Mathf.Max(dt * 100f, backlog * dt / 0.25f);
        int n = (int)_acc; _acc -= n;
        if (_acc > 3f) _acc = 0f;
        float peak = 0f; hadWindows = false;
        lock (_levels)
        {
            while (_levels.Count > MaxQueuedWindows) _levels.Dequeue(); // resync if main thread stalled
            for (int i = 0; i < n && _levels.Count > 0; i++)
            {
                hadWindows = true;
                float v = _levels.Dequeue();
                if (v > peak) peak = v;
            }
        }
        if (peak > 0.004f) _lastAudible = Time.unscaledTime;
        return peak;
    }

    float _lastAudible = -10f;
    /// <summary>True while samples are buffered or were audible in the last 0.4 s (the looping stream clip is always "playing").</summary>
    public bool IsAudible { get { return Buffered > 0 || Time.unscaledTime - _lastAudible < 0.4f; } }

    void PushLevels(float[] data, int real)
    {
        int win = Mathf.Max(1, _rate / 100);
        lock (_levels)
        {
            for (int o = 0; o < data.Length; o += win)
            {
                int e = Mathf.Min(data.Length, o + win);
                double sum = 0;
                for (int i = o; i < e; i++) sum += data[i] * data[i];
                _levels.Enqueue((float)System.Math.Sqrt(sum / (e - o)));
            }
        }
    }

    long _written, _consumed; // absolute sample counters (stream position bookkeeping for segment highlighting)
    public long WrittenSamples { get { return System.Threading.Interlocked.Read(ref _written); } }
    public long ConsumedSamples { get { return System.Threading.Interlocked.Read(ref _consumed); } }

    public int Buffered { get { return _ring == null ? 0 : _ring.Count; } }

    void Awake() { _src = GetComponent<AudioSource>(); }

    void Setup(int rate)
    {
        if (_src == null) _src = GetComponent<AudioSource>();
        _rate = rate;
        _ring = new PcmRingBuffer(Mathf.CeilToInt(rate * bufferSeconds));
        _prebufferSamples = Mathf.CeilToInt(rate * prebufferSeconds);
        _playing = false;
        var clip = AudioClip.Create("AgentStream", rate, 1, rate, true, OnRead);
        _src.Stop();
        _src.clip = clip;
        _src.loop = true;
        _src.Play();
    }

    public void Enqueue(float[] samples, int rate)
    {
        if (samples == null || samples.Length == 0 || rate <= 0) return;
        if (_ring == null || rate != _rate)
        {
            Setup(rate);
            System.Threading.Interlocked.Exchange(ref _consumed, System.Threading.Interlocked.Read(ref _written));
        }
        _ring.Write(samples);
        System.Threading.Interlocked.Add(ref _written, samples.Length);
    }

    public void Flush() { if (_ring != null) _ring.Clear(); _playing = false; System.Threading.Interlocked.Exchange(ref _consumed, System.Threading.Interlocked.Read(ref _written)); lock (_levels) _levels.Clear(); }

    // Runs on the audio thread.
    void OnRead(float[] data)
    {
        var ring = _ring;
        if (ring == null) { System.Array.Clear(data, 0, data.Length); return; }
        if (!_playing)
        {
            if (ring.Count >= _prebufferSamples) _playing = true;
            else { System.Array.Clear(data, 0, data.Length); PushLevels(data, 0); return; }
        }
        int got = ring.Read(data); // pads remainder with silence
        PushLevels(data, got);
        System.Threading.Interlocked.Add(ref _consumed, got);
        if (got < data.Length) _playing = false; // underrun: re-prebuffer
    }
}
