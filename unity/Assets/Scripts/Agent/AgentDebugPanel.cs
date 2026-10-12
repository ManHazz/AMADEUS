using UnityEngine;

/// <summary>F2 toggles: emotion buttons, mouth slider, camera presets. Editor and development builds only.</summary>
public class AgentDebugPanel : MonoBehaviour
{
    MicRecorder _mic; EmotionDriver _emotion; LipSyncDriver _lip; CameraRig _rig; TranscriptHud _hud;
    bool _open; float _mouth; bool _manual;
    GUIStyle _label;

    static readonly string[] Emotions = { "neutral", "happy", "sad", "angry", "surprised", "embarrassed" };

    public bool Open { get { return _open; } set { _open = value; if (_hud != null) _hud.ShowDebug = value; } }

    public void Init(EmotionDriver emotion, LipSyncDriver lip, CameraRig rig, TranscriptHud hud, MicRecorder mic = null)
    {
        _mic = mic;
        _emotion = emotion; _lip = lip; _rig = rig; _hud = hud;
    }

    void Update()
    {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
        if (Input.GetKeyDown(KeyCode.F2)) Open = !_open;
#endif
    }

    void OnGUI()
    {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
        if (!_open) return;
        float s = Mathf.Max(0.7f, Screen.height / 1080f);
        if (_label == null) { _label = JpFont.Style(14, Color.white); }
        _label.fontSize = Mathf.RoundToInt(14 * s);
        float w = 230f * s, bh = 28f * s, x = Screen.width - w - 12f * s, y = 12f * s;
        float h = (Emotions.Length + 8 + (_mic != null ? _mic.Devices.Length + 2 : 0)) * (bh + 4f * s) + 20f * s;
        GUI.color = new Color(0.04f, 0.05f, 0.08f, 0.75f);
        JpFont.Panel().Draw(new Rect(x, y, w, h), GUIContent.none, false, false, false, false);
        GUI.color = Color.white;
        x += 12f * s; w -= 24f * s; y += 10f * s;
        GUI.Label(new Rect(x, y, w, bh), "Debug (F2)", _label); y += bh;
        foreach (var e in Emotions)
        {
            if (GUI.Button(new Rect(x, y, w, bh), e) && _emotion != null) _emotion.Set(e, e == "neutral" ? 0f : 1f);
            y += bh + 4f * s;
        }
        GUI.Label(new Rect(x, y, w, bh), "mouth " + _mouth.ToString("0.00") + (_manual ? " (manual)" : " (audio)"), _label); y += bh;
        float nm = GUI.HorizontalSlider(new Rect(x, y + 6f * s, w, bh), _mouth, 0f, 1f);
        if (!Mathf.Approximately(nm, _mouth)) { _mouth = nm; _manual = true; }
        y += bh + 4f * s;
        if (GUI.Button(new Rect(x, y, w, bh), _manual ? "mouth: back to audio" : "mouth: audio")) _manual = false;
        if (_lip != null) _lip.ManualMouth = _manual ? _mouth : -1f;
        y += bh + 4f * s;
        if (_rig != null)
        {
            _rig.FollowHead = GUI.Toggle(new Rect(x, y, w, bh), _rig.FollowHead, " camera follows head", _label); y += bh + 4f * s;
            float bw = (w - 8f * s) / 3f;
            if (GUI.Button(new Rect(x, y, bw, bh), "Body")) _rig.SetPreset(CameraRig.Preset.FullBody);
            if (GUI.Button(new Rect(x + bw + 4f * s, y, bw, bh), "Face")) _rig.SetPreset(CameraRig.Preset.Face);
            if (GUI.Button(new Rect(x + 2 * (bw + 4f * s), y, bw, bh), "Bust")) _rig.SetPreset(CameraRig.Preset.Bust);
            y += bh + 4f * s;
        }
        if (_mic != null)
        {
            GUI.Label(new Rect(x, y, w, bh), "Microphone", _label); y += bh + 4f * s;
            for (int i = 0; i < _mic.Devices.Length; i++)
            {
                if (GUI.Button(new Rect(x, y, w, bh), (i == _mic.DeviceIndex ? "> " : "   ") + _mic.Devices[i])) _mic.SetDevice(i);
                y += bh + 4f * s;
            }
            if (GUI.Button(new Rect(x, y, w, bh), "Refresh")) _mic.RefreshDevices();
        }
#endif
    }
}
