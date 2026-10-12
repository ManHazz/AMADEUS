using NUnit.Framework;

public class SegmentEventTests
{
    [Test] public void ParsesSegment()
    {
        var s = SegmentParser.ParseSegment("{\"kind\":\"segment\",\"id\":3,\"lang\":\"ja\",\"text\":\"日本語を\",\"ruby\":[{\"t\":\"日本語\",\"r\":\"にほんご\"},{\"t\":\"を\",\"r\":\"\"}],\"tr\":\"Japanese\",\"spoken\":true}");
        Assert.NotNull(s);
        Assert.AreEqual(3, s.id); Assert.AreEqual("ja", s.lang); Assert.AreEqual("日本語を", s.text);
        Assert.AreEqual(2, s.ruby.Length); Assert.AreEqual("にほんご", s.ruby[0].r); Assert.AreEqual("", s.ruby[1].r);
        Assert.AreEqual("Japanese", s.tr); Assert.IsTrue(s.spoken);
    }
    [Test] public void NullTranslationAndMissingRuby()
    {
        var s = SegmentParser.ParseSegment("{\"kind\":\"segment\",\"id\":1,\"lang\":\"en\",\"text\":\"Hi\",\"tr\":null,\"spoken\":false}");
        Assert.NotNull(s); Assert.NotNull(s.ruby); Assert.AreEqual(0, s.ruby.Length);
        Assert.IsTrue(string.IsNullOrEmpty(s.tr)); Assert.IsFalse(s.spoken);
    }
    [Test] public void ParsesSilentEnglishTip()
    {
        var s = SegmentParser.ParseSegment("{\"kind\":\"segment\",\"id\":0,\"lang\":\"en\",\"text\":\"Close. Use the past tense.\",\"ruby\":[],\"tr\":null,\"spoken\":false}");
        Assert.NotNull(s); Assert.AreEqual("en", s.lang); Assert.IsFalse(s.spoken);
        Assert.AreEqual(0, s.ruby.Length); Assert.IsTrue(string.IsNullOrEmpty(s.tr));
        Assert.AreEqual("Close. Use the past tense.", s.text);
    }
    [Test] public void ParsesTranslation()
    {
        var t = SegmentParser.ParseTranslation("{\"kind\":\"segment_tr\",\"id\":7,\"tr\":\"Hello\"}");
        Assert.NotNull(t); Assert.AreEqual(7, t.id); Assert.AreEqual("Hello", t.tr);
    }
    [Test] public void BadJsonAndWrongKindReturnNull()
    {
        Assert.IsNull(SegmentParser.ParseSegment("not json"));
        Assert.IsNull(SegmentParser.ParseSegment(""));
        Assert.IsNull(SegmentParser.ParseSegment("{\"kind\":\"ask\",\"id\":1}"));
        Assert.IsNull(SegmentParser.ParseTranslation("{{"));
        Assert.IsNull(SegmentParser.ParseTranslation("{\"kind\":\"segment\",\"id\":1}"));
    }
}

public class RubyLayoutTests
{
    static RubyToken T(string t, string r) { return new RubyToken { t = t, r = r }; }
    static float B(string s) { return s.Length * 46f; }
    static float R(string s) { return s.Length * 22f; }

    [Test] public void CellWidthIsMaxOfBaseAndRuby()
    {
        // 日本語 base 138, にほんご ruby 88 -> base wins; 毎日 base 92, まいにち ruby 88 -> base wins; 勉 base 46 ruby べん 44
        var res = RubyLayout.Break(new[] { T("毎日", "まいにち"), T("日本語", "にほんご"), T("を", ""), T("勉強", "べんきょう"), T("します", "") }, B, R, 10000f);
        Assert.AreEqual(1, res.rows.Count); Assert.IsTrue(res.hasRuby);
        Assert.AreEqual(92f, res.rows[0][0].w); Assert.AreEqual(138f, res.rows[0][1].w);
        Assert.AreEqual(46f, res.rows[0][2].w); Assert.AreEqual(0f, res.rows[0][2].rubyW);
        Assert.AreEqual(92f + 138f + 46f + 110f + 138f, res.rowW[0]);
    }
    [Test] public void LongRubyWidensNarrowBase()
    {
        var res = RubyLayout.Break(new[] { T("私", "わたくし") }, B, R, 1000f);
        Assert.AreEqual(88f, res.rows[0][0].w);
        Assert.AreEqual(21f, RubyLayout.BaseOffset(res.rows[0][0]));  // base centred under wide ruby
        Assert.AreEqual(0f, RubyLayout.RubyOffset(res.rows[0][0]));
    }
    [Test] public void WrapsWhenTooWide()
    {
        var res = RubyLayout.Break(new[] { T("日本語", "にほんご"), T("勉強", "べんきょう"), T("します", "") }, B, R, 250f);
        Assert.AreEqual(2, res.rows.Count);
        Assert.AreEqual(138f + 110f, res.rowW[0]); Assert.AreEqual(138f, res.rowW[1]);
        Assert.AreEqual(248f, res.maxRowW);
    }
    [Test] public void NoRubyMeansNoRubyRow()
    {
        var res = RubyLayout.Break(new[] { T("です", "") }, B, R, 500f);
        Assert.IsFalse(res.hasRuby);
        Assert.AreEqual(60f, RubyLayout.RowHeight(60f, 30f, res.hasRuby));
        Assert.AreEqual(90f, RubyLayout.RowHeight(60f, 30f, true));
    }
    [Test] public void SkipsEmptyAndNullTokens()
    {
        var res = RubyLayout.Break(new[] { T("", "x"), null, T("あ", null) }, B, R, 500f);
        Assert.AreEqual(1, res.rows[0].Count); Assert.IsFalse(res.hasRuby);
    }
}
