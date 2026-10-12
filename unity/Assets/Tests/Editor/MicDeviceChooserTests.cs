using NUnit.Framework;

public class MicDeviceChooserTests
{
    static readonly string[] D = { "Microphone Array (Realtek(R) Audio)", "Headset (USB)", "Line In" };
    [Test] public void EmptyListGivesMinusOne() { Assert.AreEqual(-1, MicDeviceChooser.Choose(new string[0], "x")); Assert.AreEqual(-1, MicDeviceChooser.Choose(null, null)); }
    [Test] public void NoSavedUsesFirst() { Assert.AreEqual(0, MicDeviceChooser.Choose(D, null)); Assert.AreEqual(0, MicDeviceChooser.Choose(D, "")); }
    [Test] public void SavedNameMatches() { Assert.AreEqual(1, MicDeviceChooser.Choose(D, "Headset (USB)")); }
    [Test] public void SavedNameCaseInsensitive() { Assert.AreEqual(2, MicDeviceChooser.Choose(D, "line in")); }
    [Test] public void MissingSavedFallsBackToFirst() { Assert.AreEqual(0, MicDeviceChooser.Choose(D, "Gone")); }
    [Test] public void NextWraps() { Assert.AreEqual(1, MicDeviceChooser.Next(0, 3)); Assert.AreEqual(0, MicDeviceChooser.Next(2, 3)); Assert.AreEqual(-1, MicDeviceChooser.Next(0, 0)); }
    [Test] public void SignatureDetectsChange()
    {
        Assert.IsTrue(MicDeviceChooser.SameList(D, (string[])D.Clone()));
        Assert.IsFalse(MicDeviceChooser.SameList(D, new[] { D[0], D[1] }));
    }
}
