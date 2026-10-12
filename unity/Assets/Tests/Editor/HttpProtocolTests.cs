using System.Collections.Generic;
using System.Text;
using NUnit.Framework;

public class FrameParserTests
{
    static byte[] Frame(byte t, byte[] p)
    {
        var o = new byte[5 + p.Length];
        o[0] = t; o[1] = (byte)(p.Length >> 24); o[2] = (byte)(p.Length >> 16); o[3] = (byte)(p.Length >> 8); o[4] = (byte)p.Length;
        System.Buffer.BlockCopy(p, 0, o, 5, p.Length);
        return o;
    }
    static byte[] Cat(params byte[][] a)
    {
        var l = new List<byte>(); foreach (var x in a) l.AddRange(x); return l.ToArray();
    }

    [Test] public void SingleFrame()
    {
        var types = new List<byte>(); var texts = new List<string>();
        var p = new FrameParser(); p.OnFrame += (t, d) => { types.Add(t); texts.Add(Encoding.UTF8.GetString(d)); };
        p.Feed(Frame(1, Encoding.UTF8.GetBytes("{\"a\":1}")));
        Assert.AreEqual(1, types.Count); Assert.AreEqual((byte)1, types[0]); Assert.AreEqual("{\"a\":1}", texts[0]);
    }
    [Test] public void MultipleFramesOneChunk()
    {
        int n = 0; var p = new FrameParser(); p.OnFrame += (t, d) => n++;
        p.Feed(Cat(Frame(1, new byte[3]), Frame(2, new byte[0]), Frame(3, new byte[10])));
        Assert.AreEqual(3, n); Assert.AreEqual(0, p.Pending);
    }
    [Test] public void SplitAtEveryByte()
    {
        var all = Cat(Frame(1, new byte[] { 1, 2, 3 }), Frame(2, new byte[300]), Frame(3, new byte[] { 9 }));
        var types = new List<byte>(); var lens = new List<int>();
        var p = new FrameParser(); p.OnFrame += (t, d) => { types.Add(t); lens.Add(d.Length); };
        foreach (var b in all) p.Feed(new[] { b });
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, types);
        CollectionAssert.AreEqual(new[] { 3, 300, 1 }, lens);
    }
    [Test] public void SplitInsideHeaderAndPayload()
    {
        var all = Frame(2, new byte[100]);
        int n = 0; var p = new FrameParser(); p.OnFrame += (t, d) => { n++; Assert.AreEqual(100, d.Length); };
        p.Feed(all, 0, 3); Assert.AreEqual(0, n);
        p.Feed(all, 3, 50); Assert.AreEqual(0, n);
        p.Feed(all, 53, all.Length - 53); Assert.AreEqual(1, n);
    }
    [Test] public void LargeLengthBigEndian()
    {
        var payload = new byte[70000]; payload[69999] = 7;
        byte[] got = null; var p = new FrameParser(); p.OnFrame += (t, d) => got = d;
        var f = Frame(2, payload);
        p.Feed(f, 0, 40000); p.Feed(f, 40000, f.Length - 40000);
        Assert.AreEqual(70000, got.Length); Assert.AreEqual(7, got[69999]);
    }
    [Test] public void AbsurdLengthThrows()
    {
        var p = new FrameParser();
        Assert.Throws<System.InvalidOperationException>(() => p.Feed(new byte[] { 2, 0xFF, 0xFF, 0xFF, 0xFF }));
    }
}

public class WavDecoderTests
{
    [Test] public void RoundTrip()
    {
        var src = new[] { 0f, 0.5f, -0.5f, 1f, -1f };
        var wav = WavDecoder.EncodePcm16(src, src.Length, 24000);
        float[] s; int r;
        Assert.IsTrue(WavDecoder.TryDecode(wav, out s, out r));
        Assert.AreEqual(24000, r); Assert.AreEqual(5, s.Length);
        for (int i = 0; i < 5; i++) Assert.AreEqual(src[i], s[i], 1e-3f);
    }
    [Test] public void SkipsExtraChunksAndStreamingSize()
    {
        var wav = WavDecoder.EncodePcm16(new[] { 0.25f, -0.25f }, 2, 16000);
        // insert a LIST chunk before data and set data size to 0xFFFFFFFF
        var list = new byte[] { (byte)'L', (byte)'I', (byte)'S', (byte)'T', 4, 0, 0, 0, 1, 2, 3, 4 };
        var o = new List<byte>(wav.Length + list.Length);
        for (int i = 0; i < 36; i++) o.Add(wav[i]);
        o.AddRange(list);
        for (int i = 36; i < wav.Length; i++) o.Add(wav[i]);
        int dsz = 36 + list.Length + 4;
        o[dsz] = o[dsz + 1] = o[dsz + 2] = o[dsz + 3] = 0xFF;
        float[] s; int r;
        Assert.IsTrue(WavDecoder.TryDecode(o.ToArray(), out s, out r));
        Assert.AreEqual(16000, r); Assert.AreEqual(2, s.Length); Assert.AreEqual(0.25f, s[0], 1e-3f);
    }
    [Test] public void RejectsGarbage()
    {
        float[] s; int r;
        Assert.IsFalse(WavDecoder.TryDecode(new byte[] { 1, 2, 3 }, out s, out r));
        Assert.IsFalse(WavDecoder.TryDecode(new byte[64], out s, out r));
        Assert.IsFalse(WavDecoder.TryDecode(null, out s, out r));
    }
}

public class KeywordEmotionTests
{
    [TestCase("Wow, I am so happy to see you!", "happy")]
    [TestCase("I feel a little sad today.", "sad")]
    [TestCase("That really makes me angry!", "angry")]
    [TestCase("Oh! I did not expect that! Wow!", "surprised")]
    [TestCase("Um, this is a bit embarrassing...", "embarrassed")]
    [TestCase("The capital of France is Paris.", "neutral")]
    [TestCase("", "neutral")]
    public void Classifies(string text, string want)
    {
        Assert.AreEqual(want, KeywordEmotionClassifier.Classify(text).Name);
    }
    [Test] public void IntensityInRangeAndGrowsWithExclaims()
    {
        var a = KeywordEmotionClassifier.Classify("I am happy.");
        var b = KeywordEmotionClassifier.Classify("I am so happy and glad!!!");
        Assert.That(a.Intensity, Is.InRange(0.3f, 1f));
        Assert.That(b.Intensity, Is.InRange(0.3f, 1f));
        Assert.Greater(b.Intensity, a.Intensity);
        Assert.AreEqual(0f, KeywordEmotionClassifier.Classify("plain text").Intensity);
    }
}

public class JapaneseEmotionTests
{
    [TestCase("ありがとう！", "happy")]
    [TestCase("すごいですね", "happy")]
    [TestCase("Thank you, that was great!", "happy")]
    [TestCase("ごめんなさい", "sad")]
    [TestCase("悲しいです", "sad")]
    [TestCase("だめ！違う！", "angry")]
    [TestCase("えっ、まさか", "surprised")]
    [TestCase("恥ずかしい…", "embarrassed")]
    [TestCase("別に…", "embarrassed")]
    [TestCase("今日は水曜日です。", "neutral")]
    public void ClassifiesJapanese(string text, string want)
    {
        Assert.AreEqual(want, KeywordEmotionClassifier.Classify(text).Name);
    }

    [Test] public void NonNeutralHasIntensityFloor()
    {
        foreach (var t in new[] { "I am happy.", "ごめん", "えっ", "ばか", "照れる" })
        {
            var r = KeywordEmotionClassifier.Classify(t);
            Assert.AreNotEqual("neutral", r.Name, t);
            Assert.GreaterOrEqual(r.Intensity, 0.6f, t);
        }
    }

    [Test] public void ClassifyAnyPicksEnglishTranslationWhenJapaneseIsNeutral()
    {
        var r = KeywordEmotionClassifier.ClassifyAny("どういたしまして", "Thank you, that was great!");
        Assert.AreEqual("happy", r.Name);
        Assert.AreEqual("neutral", KeywordEmotionClassifier.ClassifyAny(null, "").Name);
    }
}

public class SegmentAudioTrackerTests
{
    [Test] public void SegmentsFirstThenAudio()
    {
        var t = new SegmentAudioTracker();
        t.OnSegment(1, true); t.OnSegment(2, true); t.OnSegment(3, false); t.OnSegment(4, true);
        t.OnAudioChunk(0); t.OnAudioChunk(1000); t.OnAudioChunk(3000);
        Assert.AreEqual(1, t.Current(0)); Assert.AreEqual(1, t.Current(999));
        Assert.AreEqual(2, t.Current(1000)); Assert.AreEqual(4, t.Current(5000));
    }
    [Test] public void AudioFirstThenSegments()
    {
        var t = new SegmentAudioTracker();
        t.OnAudioChunk(0); t.OnAudioChunk(500);
        t.OnSegment(7, true); t.OnSegment(8, true);
        Assert.AreEqual(7, t.Current(100)); Assert.AreEqual(8, t.Current(600));
    }
    [Test] public void ClearAndSilentSegments()
    {
        var t = new SegmentAudioTracker();
        t.OnSegment(1, false); t.OnAudioChunk(0);
        Assert.AreEqual(-1, t.Current(10)); // chunk is an orphan: no spoken segment yet
        t.Clear(); Assert.AreEqual(0, t.MarkCount);
    }
}
