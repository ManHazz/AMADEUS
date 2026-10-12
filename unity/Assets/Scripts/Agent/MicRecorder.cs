using System;
using UnityEngine;

/// <summary>Push-to-talk: hold Space (or a UI button wired to BeginRecord/EndRecord). Emits 16-bit mono WAV.
/// Device is chosen by name (PlayerPrefs AMADEUS_MIC), list is re-scanned for hot-plug.</summary>
public class MicRecorder : MonoBehaviour
{
    [SerializeField] KeyCode talkKey = KeyCode.Space;
    [SerializeField] int sampleRate = 16000;
    [SerializeField] int maxSeconds = 30;
    [SerializeField] float minSeconds = 0.4f;
    [SerializeField] bool showOnGuiButton = true;

    public event Action OnRecordStart;
    public event Action<byte[]> OnRecorded;   // finished WAV
    public event Action OnTooShort;
    public event Action<string> OnDeviceChanged;

    public bool IsRecording { get; private set; }
    public bool Enabled = true;                // controller can disable while busy

    AudioClip _clip;
    string _device;
    string[] _devices = new string[0];
    int _deviceIndex = -1;
    float _nextScan;
    bool _testing; float _testEnd;
    float _level;
    const string PrefKey = "AMADEUS_MIC";

    public string[] Devices { get { return _devices; } }
    public int DeviceIndex { get { return _deviceIndex; } }
    public string CurrentDevice { get { return _deviceIndex >= 0 && _deviceIndex < _devices.Length ? _devices[_deviceIndex] : ""; } }
    public bool IsTesting { get { return _testing; } }
    /// <summary>Live input peak 0..1 while recording or testing, else decays to 0.</summary>
    public float Level { get { return _level; } }

    void Awake()
    {
        RefreshDevices();
        var sb = new System.Text.StringBuilder("MicRecorder: devices [").Append(_devices.Length).Append("]");
        for (int i = 0; i < _devices.Length; i++) sb.Append("\n  ").Append(i == _deviceIndex ? "* " : "- ").Append(_devices[i]);
        Debug.Log(sb.ToString());
    }

    /// <summary>Re-read the OS device list (headsets are hot-plugged). Keeps the saved choice by name. Ignored while recording.</summary>
    public void RefreshDevices()
    {
        if (IsRecording) return;
        string[] d;
        try { d = Microphone.devices; } catch (Exception) { d = new string[0]; }
        string saved = PlayerPrefs.GetString(PrefKey, "");
        string keep = _deviceIndex >= 0 && _deviceIndex < _devices.Length ? _devices[_deviceIndex] : null;
        _devices = d;
        _deviceIndex = MicDeviceChooser.Choose(d, !string.IsNullOrEmpty(saved) ? saved : keep);
    }

    public bool SetDevice(int index)
    {
        if (IsRecording || index < 0 || index >= _devices.Length) return false;
        bool changed = index != _deviceIndex;
        _deviceIndex = index;
        PlayerPrefs.SetString(PrefKey, _devices[index]); PlayerPrefs.Save();
        if (changed) { Debug.Log("MicRecorder: device = " + _devices[index]); OnDeviceChanged?.Invoke(_devices[index]); }
        return true;
    }

    public bool SetDevice(string deviceName)
    {
        for (int i = 0; i < _devices.Length; i++) if (_devices[i] == deviceName) return SetDevice(i);
        return false;
    }

    public void CycleDevice()
    {
        if (IsRecording) return;
        RefreshDevices();
        int n = MicDeviceChooser.Next(_deviceIndex, _devices.Length);
        if (n >= 0) SetDevice(n);
    }

    /// <summary>Record 2 s only to drive the level meter; nothing is uploaded.</summary>
    public void TestMic()
    {
        if (IsRecording || !Enabled) return;
        RefreshDevices();
        if (_devices.Length == 0) return;
        _device = _devices[_deviceIndex];
        _clip = Microphone.Start(_device, false, 3, sampleRate);
        IsRecording = true; _testing = true; _testEnd = Time.unscaledTime + 2f;
    }

    void UpdateLevel()
    {
        float peak = 0f;
        if (IsRecording && _clip != null)
        {
            int pos = Microphone.GetPosition(_device);
            int n = Mathf.Min(1024, pos);
            if (n > 0)
            {
                var buf = new float[n * _clip.channels];
                if (_clip.GetData(buf, pos - n))
                    for (int i = 0; i < buf.Length; i++) { float a = Mathf.Abs(buf[i]); if (a > peak) peak = a; }
            }
        }
        _level = peak > _level ? peak : Mathf.Lerp(_level, peak, 0.25f);
    }

    void Update()
    {
        if (!IsRecording && Time.unscaledTime >= _nextScan)
        {
            _nextScan = Time.unscaledTime + 3f;
            string[] d; try { d = Microphone.devices; } catch (Exception) { d = _devices; }
            if (!MicDeviceChooser.SameList(d, _devices)) { RefreshDevices(); Debug.Log("MicRecorder: device list changed, using " + CurrentDevice); }
        }
        UpdateLevel();
        if (_testing)
        {
            if (Time.unscaledTime >= _testEnd) { _testing = false; IsRecording = false; Microphone.End(_device); }
            return;
        }
        if (!Enabled) return;
        if (Input.GetKeyDown(talkKey)) BeginRecord();
        else if (Input.GetKeyUp(talkKey)) EndRecord();
        else if (IsRecording && Microphone.GetPosition(_device) >= maxSeconds * sampleRate - 1) EndRecord();
    }

    void OnGUI()
    {
        if (!showOnGuiButton || !Enabled || _testing) return;
        var r = new Rect(10, Screen.height - 60, 160, 50);
        bool down = GUI.RepeatButton(r, IsRecording ? "Recording..." : "Hold to talk");
        if (Event.current.type != EventType.Repaint) return;
        if (down && !IsRecording) BeginRecord();
        else if (!down && IsRecording && !Input.GetKey(talkKey) && !Input.GetMouseButton(0)) EndRecord();
    }

    void OnDisable() { _testing = false; if (IsRecording) { Microphone.End(_device); IsRecording = false; } }

    public void BeginRecord()
    {
        if (IsRecording || !Enabled) return;
        if (_devices.Length == 0) RefreshDevices();
        if (_devices.Length == 0) { Debug.LogWarning("MicRecorder: no microphone"); return; }
        _device = _devices[_deviceIndex];
        _clip = Microphone.Start(_device, false, maxSeconds, sampleRate);
        IsRecording = true;
        OnRecordStart?.Invoke();
    }

    public void EndRecord()
    {
        if (!IsRecording || _testing) return;
        IsRecording = false;
        int pos = Microphone.GetPosition(_device);
        Microphone.End(_device);
        if (_clip == null || pos <= 0 || pos < minSeconds * sampleRate) { OnTooShort?.Invoke(); return; }
        int ch = _clip.channels;
        var data = new float[pos * ch];
        _clip.GetData(data, 0);
        var mono = new float[pos];
        for (int i = 0; i < pos; i++)
        {
            float s = 0;
            for (int c = 0; c < ch; c++) s += data[i * ch + c];
            mono[i] = s / ch;
        }
        OnRecorded?.Invoke(WavDecoder.EncodePcm16(mono, mono.Length, _clip.frequency));
    }
}
