using System;
using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

/// <summary>End-to-end lip sync / emotion / camera / HUD / subtitle check. Needs graphics: run via tools/rungfx.ps1 test-play-gfx.</summary>
public class RoomVerifyTests
{
    [UnityTest, Timeout(120000)]
    public IEnumerator RoomLipSyncEmotionCameraHud()
    {
        if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null) { Assert.Ignore("needs graphics (tools/unity.ps1 test-play-gfx)"); yield break; }
        var op = SceneManager.LoadSceneAsync("Assets/Scenes/Room.unity", LoadSceneMode.Single);
        if (op == null) { Assert.Ignore("Room scene not loadable (not in build settings)"); yield break; }
        while (!op.isDone) yield return null;
        var type = Type.GetType("VerifyHarness, Assembly-CSharp");
        Assert.NotNull(type, "VerifyHarness type");
        var go = new GameObject("VerifyHarness");
        var h = go.AddComponent(type);
        var done = type.GetField("Done");
        float t = Time.realtimeSinceStartup;
        while (!(bool)done.GetValue(h) && Time.realtimeSinceStartup - t < 100f) yield return null;
        var fails = (System.Collections.Generic.List<string>)type.GetField("Failures").GetValue(h);
        var rep = (System.Collections.Generic.List<string>)type.GetField("Report").GetValue(h);
        Debug.Log("[VERIFY] REPORT\n" + string.Join("\n", rep.ToArray()));
        Assert.IsTrue((bool)done.GetValue(h), "harness timed out");
        Assert.IsEmpty(fails, string.Join("\n", fails.ToArray()));
    }
}
