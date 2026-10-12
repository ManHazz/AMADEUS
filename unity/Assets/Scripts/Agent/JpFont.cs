using UnityEngine;

/// <summary>Shared Japanese-capable IMGUI font (same lookup as SubtitleView) and a rounded panel style.</summary>
public static class JpFont
{
    static Font _font;
    static GUIStyle _panel;
    static Texture2D _tex;

    public static Font Get()
    {
        if (_font != null) return _font;
        _font = Resources.Load<Font>("Fonts/NotoSansJP-Regular");
        if (_font == null)
            _font = Font.CreateDynamicFontFromOSFont(new[] { "Yu Gothic UI", "Meiryo", "MS Gothic", "Noto Sans CJK JP", "Arial Unicode MS" }, 40);
        return _font;
    }

    public static GUIStyle Panel()
    {
        if (_panel != null && _tex != null) return _panel;
        const int S = 64, R = 24;
        _tex = new Texture2D(S, S, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave, wrapMode = TextureWrapMode.Clamp };
        var px = new Color[S * S];
        for (int y = 0; y < S; y++)
            for (int x = 0; x < S; x++)
            {
                float cx = Mathf.Clamp(x + 0.5f, R, S - R), cy = Mathf.Clamp(y + 0.5f, R, S - R);
                float d = Mathf.Sqrt((x + 0.5f - cx) * (x + 0.5f - cx) + (y + 0.5f - cy) * (y + 0.5f - cy));
                px[y * S + x] = new Color(1f, 1f, 1f, Mathf.Clamp01(R - d + 0.5f));
            }
        _tex.SetPixels(px); _tex.Apply();
        _panel = new GUIStyle { border = new RectOffset(R, R, R, R) };
        _panel.normal.background = _tex;
        return _panel;
    }

    public static GUIStyle Style(int size, Color c, bool wrap = false, FontStyle fs = FontStyle.Normal)
    {
        var s = new GUIStyle { font = Get(), fontSize = size, wordWrap = wrap, fontStyle = fs, clipping = TextClipping.Overflow, richText = false };
        s.normal.textColor = c;
        return s;
    }
}
