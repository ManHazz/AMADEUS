using System;
using NUnit.Framework;

public class AgentMessageTests
{
    [Test] public void ParsesAudio()
    {
        var pcm = Convert.ToBase64String(new byte[] { 0x00, 0x40, 0x00, 0xC0 }); // 16384, -16384
        var m = AgentMessage.Parse("{\"type\":\"audio\",\"sampleRate\":24000,\"pcm16\":\"" + pcm + "\"}");
        Assert.AreEqual("audio", m.type);
        Assert.AreEqual(24000, m.sampleRate);
        var f = AgentMessage.DecodePcm16(m.pcm16);
        Assert.AreEqual(2, f.Length);
        Assert.AreEqual(0.5f, f[0], 1e-4f);
        Assert.AreEqual(-0.5f, f[1], 1e-4f);
    }
    [Test] public void ParsesEmotion()
    {
        var m = AgentMessage.Parse("{\"type\":\"emotion\",\"name\":\"happy\",\"intensity\":0.7}");
        Assert.AreEqual("happy", m.name); Assert.AreEqual(0.7f, m.intensity, 1e-5f);
    }
    [Test] public void ParsesStateTextEnd()
    {
        Assert.AreEqual("speaking", AgentMessage.Parse("{\"type\":\"state\",\"value\":\"speaking\"}").value);
        Assert.AreEqual("hi", AgentMessage.Parse("{\"type\":\"text\",\"text\":\"hi\"}").text);
        Assert.AreEqual("end", AgentMessage.Parse("{\"type\":\"end\"}").type);
    }
    [Test] public void InvalidReturnsNull()
    {
        Assert.IsNull(AgentMessage.Parse("not json"));
        Assert.IsNull(AgentMessage.Parse(""));
        Assert.IsNull(AgentMessage.Parse("{}"));
    }
    [Test] public void BadBase64Empty() { Assert.AreEqual(0, AgentMessage.DecodePcm16("!!!").Length); }
    [Test] public void HelloHasToken()
    {
        var m = AgentMessage.Parse(AgentMessage.BuildHello("abc"));
        Assert.AreEqual("hello", m.type); Assert.AreEqual("abc", m.token);
    }
}

public class RingBufferTests
{
    [Test] public void WriteRead()
    {
        var r = new PcmRingBuffer(8);
        r.Write(new float[] { 1, 2, 3 });
        var d = new float[3];
        Assert.AreEqual(3, r.Read(d));
        CollectionAssert.AreEqual(new float[] { 1, 2, 3 }, d);
        Assert.AreEqual(0, r.Count);
    }
    [Test] public void UnderrunPadsSilence()
    {
        var r = new PcmRingBuffer(8);
        r.Write(new float[] { 1, 2 });
        var d = new float[4] { 9, 9, 9, 9 };
        Assert.AreEqual(2, r.Read(d));
        CollectionAssert.AreEqual(new float[] { 1, 2, 0, 0 }, d);
    }
    [Test] public void OverflowDropsOldest()
    {
        var r = new PcmRingBuffer(4);
        r.Write(new float[] { 1, 2, 3 });
        r.Write(new float[] { 4, 5, 6 });
        var d = new float[4];
        r.Read(d);
        CollectionAssert.AreEqual(new float[] { 3, 4, 5, 6 }, d);
    }
    [Test] public void WrapAround()
    {
        var r = new PcmRingBuffer(4);
        var d = new float[3];
        r.Write(new float[] { 1, 2, 3 }); r.Read(d);
        r.Write(new float[] { 4, 5, 6 }); r.Read(d);
        CollectionAssert.AreEqual(new float[] { 4, 5, 6 }, d);
    }
    [Test] public void OversizedWriteKeepsTail()
    {
        var r = new PcmRingBuffer(2);
        r.Write(new float[] { 1, 2, 3, 4 });
        var d = new float[2]; r.Read(d);
        CollectionAssert.AreEqual(new float[] { 3, 4 }, d);
    }
}
