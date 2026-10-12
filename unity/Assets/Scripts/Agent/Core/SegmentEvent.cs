using System;
using UnityEngine;

[Serializable] public class RubyToken { public string t = ""; public string r = ""; }

/// <summary>EVENT kind "segment": one subtitle line (Japanese/English) with optional furigana.</summary>
[Serializable]
public class SegmentEvent
{
    public string kind;
    public int id;
    public string lang;
    public string text;
    public RubyToken[] ruby;
    public string tr;
    public bool spoken;
}

/// <summary>EVENT kind "segment_tr": late translation for an earlier segment.</summary>
[Serializable]
public class SegmentTranslationEvent
{
    public string kind;
    public int id;
    public string tr;
}

public static class SegmentParser
{
    /// <summary>Returns null on bad json or wrong kind. ruby/text/lang/tr never throw downstream (ruby is never null).</summary>
    public static SegmentEvent ParseSegment(string json)
    {
        if (string.IsNullOrEmpty(json)) return null;
        try
        {
            var s = JsonUtility.FromJson<SegmentEvent>(json);
            if (s == null || s.kind != "segment") return null;
            if (s.ruby == null) s.ruby = new RubyToken[0];
            for (int i = 0; i < s.ruby.Length; i++)
            {
                if (s.ruby[i] == null) s.ruby[i] = new RubyToken();
                if (s.ruby[i].t == null) s.ruby[i].t = "";
                if (s.ruby[i].r == null) s.ruby[i].r = "";
            }
            if (s.text == null) s.text = "";
            if (string.IsNullOrEmpty(s.lang)) s.lang = "ja";
            return s;
        }
        catch (Exception) { return null; }
    }

    public static SegmentTranslationEvent ParseTranslation(string json)
    {
        if (string.IsNullOrEmpty(json)) return null;
        try
        {
            var s = JsonUtility.FromJson<SegmentTranslationEvent>(json);
            return s != null && s.kind == "segment_tr" ? s : null;
        }
        catch (Exception) { return null; }
    }
}
