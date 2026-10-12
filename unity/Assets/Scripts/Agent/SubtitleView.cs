using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Bottom-center subtitles: the whole turn as stacked lines (max 4, oldest dropped), each Japanese line with
/// furigana + its own English translation; the line whose audio is playing is bright, the others dimmer.
/// A silent English tip sits on top. The stack is cleared when a new turn starts (recording / new request).
/// Uses IMGUI because the project has no ugui/TextMeshPro package.
/// </summary>
public class SubtitleView : MonoBehaviour
{
    [SerializeField] float fadeInSeconds = 0.25f;
    [SerializeField] float holdAfterSpeechSeconds = 4f;
    [SerializeField] float fadeOutSeconds = 0.6f;
    [SerializeField] int maxLines = 4;

    class Line
    {
        public SegmentEvent seg; public string tr;
        public List<List<RubyLayout.Cell>> rows; public List<float> rowW;
        public bool hasRuby; public float w, h, trW, trH, alpha = 0.85f;
    }

    const float MaxPanelFrac = 0.31f;
    AgentController _ctrl;
    AgentHttpClient _client;
    readonly List<Line> _stack = new List<Line>();
    string _tip;
    float _tipHold;
    float _alpha, _idleTime;
    float _debugHold; int _debugPlay = int.MinValue; float _debugClock;
    bool _dirty = true, _newTurn;
    float _layoutScale, _fit = 1f;
    bool _sub, _inline;
    float _colJ, _colT, _colGap;

    GUIStyle _base, _ruby, _trStyle, _tipStyle;
    float _baseH, _rubyH, _tipH, _tipW, _contentW, _contentH, _gap;

    public int LineCount { get { return _stack.Count; } }
    public int PlayingId { get; private set; } = -1;
    /// <summary>Last drawn panel in screen pixels (top-left origin); for tests.</summary>
    public Rect LastPanel { get; private set; }
    public float Fit { get { return _fit; } }

    public void Init(AgentHttpClient client, AgentController ctrl)
    {
        Unsubscribe();
        _client = client; _ctrl = ctrl;
        Subscribe();
    }

    void OnEnable() { Subscribe(); }
    void OnDisable() { Unsubscribe(); }
    void Subscribe()
    {
        if (_sub || _client == null || !isActiveAndEnabled) return;
        _client.OnSegment += HandleSegment; _client.OnSegmentTranslation += HandleTr; _client.OnRequestStart += HandleRequestStart;
        _sub = true;
    }
    void Unsubscribe()
    {
        if (!_sub) return;
        _client.OnSegment -= HandleSegment; _client.OnSegmentTranslation -= HandleTr; _client.OnRequestStart -= HandleRequestStart;
        _sub = false;
    }

    void HandleRequestStart() { _newTurn = true; }

    /// <summary>Drop the previous turn (lazily, when the first frame of the next turn arrives).</summary>
    void BeginTurnIfNeeded()
    {
        if (!_newTurn) return;
        _newTurn = false; _stack.Clear(); _tip = null; _tipHold = 0f; _dirty = true;
    }

    void HandleSegment(SegmentEvent s)
    {
        if (s == null) return;
        BeginTurnIfNeeded();
        if (s.lang == "en" && !s.spoken)
        {
            // Silent English tip: separate line on top, never touches the Japanese lines / translations.
            if (string.IsNullOrEmpty(s.text)) return;
            _tip = s.text; _tipHold = Mathf.Clamp(4f + s.text.Length * 0.04f, 4f, 6f);
            _idleTime = 0f; _dirty = true;
            return;
        }
        Line ln = _stack.Find(l => l.seg.id == s.id);
        if (ln == null)
        {
            ln = new Line();
            _stack.Add(ln);
            while (_stack.Count > Mathf.Max(1, maxLines)) _stack.RemoveAt(0);
        }
        ln.seg = s; ln.tr = s.tr;
        _idleTime = 0f; _dirty = true;
    }

    void HandleTr(SegmentTranslationEvent e)
    {
        if (e == null) return;
        var ln = _stack.Find(l => l.seg.id == e.id);
        if (ln == null) return;
        ln.tr = e.tr; _dirty = true;
    }

    /// <summary>Show a segment directly (debug / tests).</summary>
    public void Show(SegmentEvent s, float holdSeconds)
    {
        HandleSegment(s); _debugHold = holdSeconds;
    }

    /// <summary>Simulate the start of a new turn (debug / tests).</summary>
    public void NewTurn() { _newTurn = true; }

    void Update()
    {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
        if (Input.GetKeyDown(KeyCode.F9))
        {
            NewTurn();
            Show(Sample(901, "私は学生です", "I am a student.", new[] { "私", "わたし", "は", "", "学生", "がくせい", "です", "" }), 7f);
            Show(Sample(902, "毎日日本語を勉強します", "I study Japanese every day.", RubySample), 7f);
            Show(Sample(903, "あなたは何を勉強しますか", "What do you study?", new[] { "あなた", "", "は", "", "何", "なに", "を", "", "勉強", "べんきょう", "しますか", "" }), 7f);
            _debugPlay = 901; _debugClock = 0f;
        }
        if (Input.GetKeyDown(KeyCode.F10))
        {
            Show(new SegmentEvent
            {
                kind = "segment", id = -2, lang = "en", text = "Close. Use the past tense for yesterday.",
                spoken = false, tr = null, ruby = new RubyToken[0]
            }, 7f);
        }
#endif
        if (_stack.Count == 0 && _tip == null) { _alpha = 0f; return; }
        string st = _ctrl != null ? _ctrl.State : "idle";
        if (st == "listening") _newTurn = true;
        bool hold = st == "speaking" || st == "thinking" || _debugHold > 0f || _tipHold > 0f;
        if (_tipHold > 0f) _tipHold -= Time.unscaledDeltaTime;
        if (_debugHold > 0f) _debugHold -= Time.unscaledDeltaTime;
        if (hold) _idleTime = 0f; else _idleTime += Time.unscaledDeltaTime;

        // which line is playing
        int pid = _ctrl != null ? _ctrl.PlayingSegmentId : -1;
        if (_debugHold > 0f && _debugPlay != int.MinValue)
        {
            _debugClock += Time.unscaledDeltaTime;
            pid = 901 + Mathf.Min(2, (int)(_debugClock / 1.8f));
        }
        PlayingId = pid;
        foreach (var l in _stack)
        {
            float target = pid < 0 ? 0.92f : (l.seg.id == pid ? 1f : 0.42f);
            l.alpha = Mathf.MoveTowards(l.alpha, target, Time.unscaledDeltaTime * 4f);
        }

        if (_idleTime < holdAfterSpeechSeconds)
            _alpha = Mathf.MoveTowards(_alpha, 1f, Time.unscaledDeltaTime / Mathf.Max(0.01f, fadeInSeconds));
        else
        {
            _alpha = Mathf.MoveTowards(_alpha, 0f, Time.unscaledDeltaTime / Mathf.Max(0.01f, fadeOutSeconds));
            if (_alpha <= 0f) { _stack.Clear(); _tip = null; _tipHold = 0f; }
        }
    }

    public static readonly string[] RubySample = { "毎日", "まいにち", "日本語", "にほんご", "を", "", "勉強", "べんきょう", "します", "" };

    public static SegmentEvent Sample(int id, string text, string tr, string[] pairs)
    {
        var ruby = new List<RubyToken>();
        for (int i = 0; i + 1 < pairs.Length; i += 2) ruby.Add(new RubyToken { t = pairs[i], r = pairs[i + 1] });
        return new SegmentEvent { kind = "segment", id = id, lang = "ja", text = text, spoken = true, tr = tr, ruby = ruby.ToArray() };
    }

    // ---- fonts / styles -------------------------------------------------

    GUIStyle _panel;
    void EnsureStyles()
    {
        if (_base != null) return;
        _base = JpFont.Style(46, Color.white);
        _ruby = JpFont.Style(22, new Color(1f, 1f, 1f, 0.85f));
        _trStyle = JpFont.Style(28, new Color(1f, 1f, 1f, 0.65f), true);
        _tipStyle = JpFont.Style(24, new Color(1f, 0.82f, 0.45f, 0.8f), true, FontStyle.Italic);
        _tipStyle.alignment = TextAnchor.UpperCenter;
        _panel = JpFont.Panel();
    }

    // ---- layout ---------------------------------------------------------

    static List<RubyToken> Tokens(SegmentEvent s)
    {
        var l = new List<RubyToken>();
        if (s.ruby != null && s.ruby.Length > 0) { l.AddRange(s.ruby); return l; }
        string t = s.text ?? "";
        if (s.lang == "ja")
        {
            for (int i = 0; i < t.Length; i++)
            {
                if (char.IsHighSurrogate(t[i]) && i + 1 < t.Length) { l.Add(new RubyToken { t = t.Substring(i, 2) }); i++; }
                else l.Add(new RubyToken { t = t[i].ToString() });
            }
        }
        else
        {
            int start = 0;
            for (int i = 0; i < t.Length; i++)
                if (t[i] == ' ') { l.Add(new RubyToken { t = t.Substring(start, i - start + 1) }); start = i + 1; }
            if (start < t.Length) l.Add(new RubyToken { t = t.Substring(start) });
        }
        return l;
    }

    void LayoutLine(Line ln, float maxW, float s, bool inline)
    {
        float jaW = inline ? maxW * 0.62f : maxW, trMax = inline ? maxW * 0.36f : maxW;
        var res = RubyLayout.Break(Tokens(ln.seg), t => _base.CalcSize(new GUIContent(t)).x, t => _ruby.CalcSize(new GUIContent(t)).x, jaW);
        ln.rows = res.rows; ln.rowW = res.rowW; ln.hasRuby = res.hasRuby; float maxRow = res.maxRowW;
        ln.trW = 0f; ln.trH = 0f;
        if (!string.IsNullOrEmpty(ln.tr))
        {
            var gc = new GUIContent(ln.tr);
            ln.trW = Mathf.Min(_trStyle.CalcSize(gc).x, trMax);
            ln.trH = _trStyle.CalcHeight(gc, trMax);
        }
        float rowH = RubyLayout.RowHeight(_baseH, _rubyH, ln.hasRuby);
        float jaH = ln.rows.Count * rowH;
        if (inline) { ln.w = maxRow; ln.h = Mathf.Max(jaH, ln.trH); }
        else { ln.w = Mathf.Max(maxRow, ln.trW); ln.h = jaH + (ln.trH > 0f ? ln.trH + 4f * s : 0f); }
    }

    void Layout(float s)
    {
        _layoutScale = s; _dirty = false;
        float maxPanel = Screen.height * MaxPanelFrac; // panel (incl. padding) stays <= ~31% of the screen (face is framed in the upper ~60%) so the mouth stays visible
        float[] fits = { 1f, 0.9f, 0.8f, 0.7f, 0.6f, 0.5f, 0.42f, 0.35f, 0.3f };
        for (int fi = 0; fi < fits.Length * 2; fi++)
        {
            float fit = fits[fi / 2]; _inline = (fi & 1) == 1;
            _fit = fit;
            _base.fontSize = Mathf.Max(8, Mathf.RoundToInt(46 * s * fit));
            _ruby.fontSize = Mathf.Max(6, Mathf.RoundToInt(22 * s * fit));
            _trStyle.fontSize = Mathf.Max(8, Mathf.RoundToInt(28 * s * fit));
            _tipStyle.fontSize = Mathf.Max(8, Mathf.RoundToInt(24 * s * fit));
            float maxW = Mathf.Min(1500f * s, Screen.width * 0.9f);
            _gap = 12f * s * fit;
            _baseH = _base.CalcSize(new GUIContent("あ")).y;
            _rubyH = _ruby.CalcSize(new GUIContent("あ")).y;
            _contentW = 0f; _contentH = 0f;
            _tipH = 0f; _tipW = 0f;
            if (!string.IsNullOrEmpty(_tip))
            {
                var gt = new GUIContent(_tip);
                _tipW = Mathf.Min(_tipStyle.CalcSize(gt).x, maxW);
                _tipH = _tipStyle.CalcHeight(gt, maxW);
                _contentW = _tipW; _contentH = _tipH + (_stack.Count > 0 ? 10f * s * fit : 0f);
            }
            for (int i = 0; i < _stack.Count; i++)
            {
                LayoutLine(_stack[i], maxW, s * fit, _inline);
                _contentW = Mathf.Max(_contentW, _stack[i].w);
                _contentH += _stack[i].h + (i > 0 ? _gap : 0f);
            }
            if (_inline)
            {
                // JA column (centred) + gap + translation column (left aligned), shared by all lines
                _colJ = 0f; _colT = 0f;
                foreach (var l in _stack) { _colJ = Mathf.Max(_colJ, l.w); _colT = Mathf.Max(_colT, l.trW); }
                _colGap = 36f * s * fit;
                _contentW = Mathf.Max(_tipW, _colJ + (_colT > 0f ? _colGap + _colT : 0f));
            }
            if (_contentH + 2f * 22f * s * fit <= maxPanel) break;
        }
    }

    void OnGUI()
    {
        if (Event.current.type != EventType.Repaint) return;
        if ((_stack.Count == 0 && _tip == null) || _alpha <= 0.001f) return;
        EnsureStyles();
        float s = Screen.height / 1080f;
        if (_dirty || !Mathf.Approximately(s, _layoutScale)) Layout(s);
        if (_stack.Count == 0 && _tipH <= 0f) return;

        float padX = 44f * s * _fit, padY = 22f * s * _fit;
        float pw = _contentW + 2 * padX, ph = _contentH + 2 * padY;
        float px = (Screen.width - pw) * 0.5f, py = Screen.height - ph - 20f * s;
        LastPanel = new Rect(px, py, pw, ph);
        var oldColor = GUI.color;

        GUI.color = new Color(0.04f, 0.05f, 0.08f, 0.62f * _alpha);
        _panel.Draw(new Rect(px, py, pw, ph), GUIContent.none, false, false, false, false);

        float y = py + padY;
        if (_tipH > 0f)
        {
            GUI.color = new Color(1f, 1f, 1f, _alpha);
            GUI.Label(new Rect(px + (pw - _tipW) * 0.5f, y, _tipW + 2f, _tipH), _tip, _tipStyle);
            y += _tipH + (_stack.Count > 0 ? 10f * s * _fit : 0f);
        }
        for (int i = 0; i < _stack.Count; i++)
        {
            var ln = _stack[i];
            GUI.color = new Color(1f, 1f, 1f, _alpha * ln.alpha);
            float rowH = RubyLayout.RowHeight(_baseH, _rubyH, ln.hasRuby), y0 = y;
            for (int r = 0; r < ln.rows.Count; r++)
            {
                float x = _inline ? px + padX + (_colJ - ln.rowW[r]) * 0.5f : px + (pw - ln.rowW[r]) * 0.5f;
                foreach (var c in ln.rows[r])
                {
                    if (c.r.Length > 0)
                        GUI.Label(new Rect(x + (c.w - c.rubyW) * 0.5f, y, c.rubyW + 2f, _rubyH), c.r, _ruby);
                    GUI.Label(new Rect(x + (c.w - c.baseW) * 0.5f, y + (ln.hasRuby ? _rubyH : 0f), c.baseW + 2f, _baseH), c.t, _base);
                    x += c.w;
                }
                y += rowH;
            }
            if (_inline) y += ln.h - ln.rows.Count * rowH;
            if (ln.trH > 0f && _inline)
            {
                float ty = y0 + (ln.h - ln.trH) * 0.5f;
                GUI.Label(new Rect(px + padX + _colJ + _colGap, ty, ln.trW + 2f, ln.trH), ln.tr, _trStyle);
            }
            else if (ln.trH > 0f)
            {
                y += 4f * s * _fit;
                GUI.Label(new Rect(px + (pw - ln.trW) * 0.5f, y, ln.trW + 2f, ln.trH), ln.tr, _trStyle);
                y += ln.trH;
            }
            y += _gap;
        }
        GUI.color = oldColor;
    }
}
