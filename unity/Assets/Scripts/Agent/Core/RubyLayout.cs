using System;
using System.Collections.Generic;

/// <summary>Pure furigana layout maths (no Unity GUI): cell widths = max(base, ruby), greedy row wrapping, row height incl. ruby row.</summary>
public static class RubyLayout
{
    public struct Cell { public string t, r; public float w, baseW, rubyW; }

    public class Result
    {
        public readonly List<List<Cell>> rows = new List<List<Cell>>();
        public readonly List<float> rowW = new List<float>();
        public bool hasRuby; public float maxRowW;
    }

    public static Result Break(IList<RubyToken> tokens, Func<string, float> baseWidth, Func<string, float> rubyWidth, float maxW)
    {
        var res = new Result();
        var cur = new List<Cell>(); float curW = 0f;
        foreach (var tk in tokens)
        {
            if (tk == null || string.IsNullOrEmpty(tk.t)) continue;
            string r = tk.r ?? "";
            var c = new Cell { t = tk.t, r = r, baseW = baseWidth(tk.t), rubyW = r.Length > 0 ? rubyWidth(r) : 0f };
            c.w = Math.Max(c.baseW, c.rubyW);
            if (r.Length > 0) res.hasRuby = true;
            if (curW + c.w > maxW && cur.Count > 0)
            {
                res.rows.Add(cur); res.rowW.Add(curW); res.maxRowW = Math.Max(res.maxRowW, curW);
                cur = new List<Cell>(); curW = 0f;
            }
            cur.Add(c); curW += c.w;
        }
        if (cur.Count > 0) { res.rows.Add(cur); res.rowW.Add(curW); res.maxRowW = Math.Max(res.maxRowW, curW); }
        return res;
    }

    /// <summary>Height of one wrapped row: base row plus the ruby row when any token on the line has a reading.</summary>
    public static float RowHeight(float baseH, float rubyH, bool hasRuby) { return baseH + (hasRuby ? rubyH : 0f); }

    /// <summary>X offset of the ruby text inside its cell so it is centred over the base text.</summary>
    public static float RubyOffset(Cell c) { return (c.w - c.rubyW) * 0.5f; }
    public static float BaseOffset(Cell c) { return (c.w - c.baseW) * 0.5f; }
}
