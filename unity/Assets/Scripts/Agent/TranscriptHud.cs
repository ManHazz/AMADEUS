using System.Collections.Generic;
using UnityEngine;

/// <summary>Top-left IMGUI panel: what the speech recogniser heard (META.user), recording/transcribing state, status line.</summary>
public class TranscriptHud : MonoBehaviour
{
    [SerializeField] int maxEntries = 3;
    [SerializeField] float fadeAfterSeconds = 20f;
    [SerializeField] float fadeSeconds = 2f;
    [SerializeField] float panelWidth = 420f;

    struct Entry { public string text; public float time; }
    readonly List<Entry> _entries = new List<Entry>();

    AgentController _ctrl; AgentHttpClient _client; MicRecorder _mic; LipSyncDriver _lip;
    bool _awaitingTurn;       // an utterance was sent and its META.user is not in yet
    bool _turnTextAdded;
    string _lastError = "";
    float _errorTime;
    bool _sub;

    /// <summary>F2 toggles extra status detail (shared with AgentDebugPanel).</summary>
    public bool ShowDebug;
    /// <summary>Test hook: force the "transcribing" indicator.</summary>
    public bool ForceTranscribing { set { _awaitingTurn = value; } }

    public int EntryCount { get { return _entries.Count; } }
    public string LastEntry { get { return _entries.Count > 0 ? _entries[_entries.Count - 1].text : ""; } }

    public void Init(AgentHttpClient client, AgentController ctrl, MicRecorder mic, LipSyncDriver lip)
    {
        Unsubscribe();
        _client = client; _ctrl = ctrl; _mic = mic; _lip = lip;
        Subscribe();
    }

    void OnEnable() { Subscribe(); }
    void OnDisable() { Unsubscribe(); }

    void Subscribe()
    {
        if (_sub || _client == null || !isActiveAndEnabled) return;
        _client.OnMeta += HandleMeta; _client.OnError += HandleError;
        if (_mic != null) { _mic.OnRecordStart += HandleStart; _mic.OnRecorded += HandleRecorded; _mic.OnDeviceChanged += HandleDevice; }
        _sub = true;
    }

    void Unsubscribe()
    {
        if (!_sub) return;
        if (_client != null) { _client.OnMeta -= HandleMeta; _client.OnError -= HandleError; }
        if (_mic != null) { _mic.OnRecordStart -= HandleStart; _mic.OnRecorded -= HandleRecorded; _mic.OnDeviceChanged -= HandleDevice; }
        _sub = false;
    }

    void HandleStart() { _awaitingTurn = false; }
    void HandleRecorded(byte[] wav) { _awaitingTurn = true; _turnTextAdded = false; }
    void HandleError(string m) { _lastError = m; _errorTime = Time.unscaledTime; _awaitingTurn = false; }

    public void AddUserText(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        text = text.Trim();
        // META can repeat the same user text within one turn: add once per turn
        if (_entries.Count > 0 && _entries[_entries.Count - 1].text == text && (_turnTextAdded || !_awaitingTurn)) return;
        _entries.Add(new Entry { text = text, time = Time.unscaledTime });
        while (_entries.Count > maxEntries) _entries.RemoveAt(0);
        _turnTextAdded = true; _awaitingTurn = false;
    }

    void HandleMeta(AgentMeta m) { if (m != null) AddUserText(m.user); }

    string _toast = ""; float _toastTime;
    void HandleDevice(string n) { _toast = "Mic: " + n; _toastTime = Time.unscaledTime; }

    string StatusLine()
    {
        var sb = new System.Text.StringBuilder();
        string conn = _client == null ? "no client" : (!_client.IsConfigured ? "not configured" : (_client.IsBusy ? "busy" : "connected"));
        sb.Append("link: ").Append(conn);
        if (_ctrl != null) sb.Append("  |  ").Append(_ctrl.State);
        if (ShowDebug && _lip != null)
            sb.Append("\nmouth ").Append(_lip.Mouth.ToString("0.00")).Append("  speaking ").Append(_lip.Speaking.ToString("0.00"));
        if (_lastError.Length > 0 && Time.unscaledTime - _errorTime < 10f) sb.Append("\nerr: ").Append(_lastError);
        return sb.ToString();
    }

    GUIStyle _head, _line, _lineNew, _status, _state;

    void OnGUI()
    {
        float s = Mathf.Max(0.6f, Screen.height / 1080f);
        if (_head == null)
        {
            _head = JpFont.Style(15, new Color(1f, 1f, 1f, 0.7f));
            _line = JpFont.Style(18, new Color(1f, 1f, 1f, 0.6f), true);
            _lineNew = JpFont.Style(20, new Color(1f, 0.93f, 0.55f, 1f), true);
            _status = JpFont.Style(13, new Color(1f, 1f, 1f, 0.55f), true);
            _state = JpFont.Style(15, Color.white);
        }
        _head.fontSize = Mathf.RoundToInt(15 * s); _line.fontSize = Mathf.RoundToInt(18 * s);
        _lineNew.fontSize = Mathf.RoundToInt(20 * s); _status.fontSize = Mathf.RoundToInt(13 * s); _state.fontSize = Mathf.RoundToInt(15 * s);

        float now = Time.unscaledTime, w = panelWidth * s, pad = 14f * s, inner = w - 2 * pad;
        bool rec = _mic != null && _mic.IsRecording;
        bool transcribing = !rec && _awaitingTurn;
        string status = StatusLine();

        var vis = new List<int>(); var al = new List<float>();
        for (int i = 0; i < _entries.Count; i++)
        {
            float age = now - _entries[i].time;
            float a = age < fadeAfterSeconds ? 1f : 1f - (age - fadeAfterSeconds) / Mathf.Max(0.1f, fadeSeconds);
            if (a > 0.02f) { vis.Add(i); al.Add(a); }
        }

        float hd = _head.CalcHeight(new GUIContent("You (heard):"), inner);
        float stH = _state.CalcHeight(new GUIContent("listening"), inner);
        float h = pad + hd + 4f * s;
        var hs = new float[vis.Count];
        for (int j = 0; j < vis.Count; j++)
        {
            var st = vis[j] == _entries.Count - 1 ? _lineNew : _line;
            hs[j] = st.CalcHeight(new GUIContent(_entries[vis[j]].text), inner) + 3f * s;
            h += hs[j];
        }
        if (rec || transcribing) h += stH + 4f * s;
        float statH = _status.CalcHeight(new GUIContent(status), inner);
        float micH = _status.CalcHeight(new GUIContent("mic"), inner) + 4f * s;
        float barH = 6f * s;
        h += statH + 6f * s + micH + barH + 6f * s + pad;

        float x = 12f * s, y = 12f * s;
        var old = GUI.color;
        GUI.color = new Color(0.04f, 0.05f, 0.08f, 0.72f);
        if (Event.current.type == EventType.Repaint) JpFont.Panel().Draw(new Rect(x, y, w, h), GUIContent.none, false, false, false, false);
        GUI.color = Color.white;
        float cy = y + pad;
        GUI.Label(new Rect(x + pad, cy, inner, hd), "You (heard):", _head);
        cy += hd + 4f * s;
        for (int j = 0; j < vis.Count; j++)
        {
            bool newest = vis[j] == _entries.Count - 1;
            GUI.color = new Color(1, 1, 1, al[j]);
            GUI.Label(new Rect(x + pad, cy, inner, hs[j]), _entries[vis[j]].text, newest ? _lineNew : _line);
            cy += hs[j];
        }
        GUI.color = Color.white;
        if (rec || transcribing)
        {
            if (rec)
            {
                float pulse = 0.6f + 0.4f * Mathf.Sin(now * 8f);
                GUI.color = new Color(1f, 0.15f, 0.15f, pulse);
                float d = 11f * s;
                GUI.DrawTexture(new Rect(x + pad, cy + (stH - d) * 0.5f, d, d), Texture2D.whiteTexture);
                GUI.color = Color.white;
                GUI.Label(new Rect(x + pad + d + 8f * s, cy, inner, stH), "listening", _state);
            }
            else
            {
                string dots = new string('.', 1 + (int)(now * 3f) % 3);
                GUI.Label(new Rect(x + pad, cy, inner, stH), "transcribing" + dots, _state);
            }
            cy += stH + 4f * s;
        }
        GUI.Label(new Rect(x + pad, cy + 2f * s, inner, statH), status, _status);
        cy += statH + 6f * s;
        DrawMic(x + pad, cy, inner, micH, barH, s, now);
        GUI.color = old;
    }

    GUIStyle _micBtn;
    void DrawMic(float x, float y, float w, float lh, float barH, float s, float now)
    {
        if (_mic == null) return;
        if (_micBtn == null)
        {
            _micBtn = new GUIStyle(_status); _micBtn.clipping = TextClipping.Clip;
            _micBtn.normal.textColor = new Color(0.7f, 0.9f, 1f, 0.9f); _micBtn.hover.textColor = Color.white;
        }
        _micBtn.fontSize = _status.fontSize;
        string nm = _mic.CurrentDevice.Length > 0 ? _mic.CurrentDevice : "none";
        float testW = 74f * s;
        bool toast = _toast.Length > 0 && now - _toastTime < 2.5f;
        string label = (toast ? _toast : "mic: " + nm) + (_mic.Devices.Length > 1 ? "  >" : "");
        if (GUI.Button(new Rect(x, y, w - testW - 6f * s, lh), label, _micBtn)) _mic.CycleDevice();
        if (GUI.Button(new Rect(x + w - testW, y, testW, lh), _mic.IsTesting ? "testing" : "Test mic")) _mic.TestMic();
        float by = y + lh + 2f * s;
        var c = GUI.color;
        GUI.color = new Color(1, 1, 1, 0.15f);
        GUI.DrawTexture(new Rect(x, by, w, barH), Texture2D.whiteTexture);
        float lvl = Mathf.Clamp01(Mathf.Sqrt(_mic.Level));
        GUI.color = lvl > 0.9f ? new Color(1f, 0.3f, 0.3f, 0.95f) : new Color(0.4f, 1f, 0.55f, 0.95f);
        GUI.DrawTexture(new Rect(x, by, w * lvl, barH), Texture2D.whiteTexture);
        GUI.color = c;
    }
}
