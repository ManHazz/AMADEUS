using System;
using System.Collections;
using System.IO;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

/// <summary>
/// Runs the repo scene WITHOUT a VRM model and checks the PlaceholderAvatar fallback.
/// Logic test runs headless (tools/unity.ps1 test-play). The screenshot test needs graphics (test-play-gfx), otherwise it is ignored.
/// Scripts live in Assembly-CSharp, which a test asmdef cannot reference, so types are reached by name (same pattern as RoomVerifyTests).
/// </summary>
public class PlaceholderFallbackTests
{
    const string ScenePath = "Assets/Scenes/Room.unity";

    static Type T(string n) { var t = Type.GetType(n + ", Assembly-CSharp"); Assert.NotNull(t, "type " + n); return t; }
    static UnityEngine.Object Find(string n) { return UnityEngine.Object.FindFirstObjectByType(T(n), FindObjectsInactive.Include); }
    static object Get(object o, string prop) { return o.GetType().GetProperty(prop).GetValue(o, null); }
    static float F(object o, string prop) { return Convert.ToSingle(Get(o, prop)); }

    static IEnumerator LoadRoom()
    {
#if UNITY_EDITOR
        var op = UnityEditor.SceneManagement.EditorSceneManager.LoadSceneAsyncInPlayMode(ScenePath, new LoadSceneParameters(LoadSceneMode.Single));
#else
        var op = SceneManager.LoadSceneAsync(ScenePath, LoadSceneMode.Single);
#endif
        Assert.NotNull(op, "scene load");
        while (!op.isDone) yield return null;
        for (int i = 0; i < 5; i++) yield return null;
    }

    [UnityTest, Timeout(120000)]
    public IEnumerator NoVrm_UsesPlaceholderAndDriversWork()
    {
        LogAssert.Expect(LogType.Warning, "AgentController: no VRM avatar found, using PlaceholderAvatar");
        yield return LoadRoom();

        var agent = Find("AgentController") as MonoBehaviour;
        Assert.NotNull(agent, "AgentController exists");
        Assert.IsNull(Find("VrmAvatar"), "no VrmAvatar in scene");
        var vrmType = Type.GetType("UniVRM10.Vrm10Instance, VRM10");
        if (vrmType != null) Assert.IsNull(agent.GetComponentInChildren(vrmType, true), "no Vrm10Instance");
        var ph = Find("PlaceholderAvatar") as MonoBehaviour;
        Assert.NotNull(ph, "PlaceholderAvatar exists");
        Assert.IsTrue(ph.gameObject.activeInHierarchy && ph.enabled, "PlaceholderAvatar active");
        Assert.AreSame(ph, Get(agent, "Avatar"), "agent avatar is the placeholder");
        var bodyT = ph.transform.Find("PlaceholderBody");
        Assert.NotNull(bodyT, "placeholder built its body");

        // drive it like the agent would
        var lip = Get(agent, "LipSync"); var emo = Get(agent, "Emotion");
        Assert.NotNull(lip); Assert.NotNull(emo);
        emo.GetType().GetMethod("Set").Invoke(emo, new object[] { "happy", 1f });
        lip.GetType().GetProperty("ManualMouth").SetValue(lip, 0.8f, null);
        var keep = emo.GetType().GetMethod("KeepAlive");
        // headless frames are very short, so wait in game time (smoothing is time based)
        float t0 = Time.time;
        while (Time.time - t0 < 1.5f) { keep.Invoke(emo, null); yield return null; }
        Assert.Greater(F(lip, "Mouth"), 0.3f, "mouth value driven");
        Assert.Greater(F(emo, "Weight"), 0.3f, "emotion weight rose");
        Assert.AreEqual("happy", (string)Get(emo, "Current"));
        Assert.Greater(bodyT.Find("Mouth").localScale.y, 0.1f, "placeholder mouth opened");
        Assert.AreNotEqual(new Color(0.85f, 0.8f, 0.75f), bodyT.GetComponent<Renderer>().material.color, "body tinted by emotion");

        lip.GetType().GetProperty("ManualMouth").SetValue(lip, -1f, null);
        emo.GetType().GetMethod("Set").Invoke(emo, new object[] { "neutral", 0f });
        for (int i = 0; i < 10; i++) yield return null;

        var cam = Camera.main;
        Assert.NotNull(cam, "main camera");
        Assert.NotNull(Find("CameraRig"), "CameraRig");
        var rend = bodyT.GetComponent<Renderer>();
        // default camera is the Face close-up: both eyes and the mouth must be inside the frame
        foreach (var part in new[] { "EyeL", "EyeR", "Mouth" })
        {
            var vp = cam.WorldToViewportPoint(bodyT.Find(part).position);
            Assert.IsTrue(vp.z > 0 && vp.x > 0 && vp.x < 1 && vp.y > 0 && vp.y < 1, part + " in camera frame, vp=" + vp);
        }
    }

    [UnityTest, Timeout(120000)]
    public IEnumerator NoVrm_ScreenshotWithGraphics()
    {
        if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null) { Assert.Ignore("needs graphics (tools/unity.ps1 test-play-gfx)"); yield break; }
        LogAssert.ignoreFailingMessages = true;
        yield return LoadRoom();
        var agent = Find("AgentController");
        var lip = Get(agent, "LipSync"); var emo = Get(agent, "Emotion");
        emo.GetType().GetMethod("Set").Invoke(emo, new object[] { "happy", 1f });
        lip.GetType().GetProperty("ManualMouth").SetValue(lip, 0.7f, null);
        var keep = emo.GetType().GetMethod("KeepAlive");
        float t0 = Time.time;
        while (Time.time - t0 < 1.5f) { keep.Invoke(emo, null); yield return null; }
        yield return new WaitForEndOfFrame();
        var tex = ScreenCapture.CaptureScreenshotAsTexture();
        Directory.CreateDirectory("Logs");
        File.WriteAllBytes("Logs/placeholder_fallback.png", tex.EncodeToPNG());
        UnityEngine.Object.Destroy(tex);
        Assert.IsTrue(File.Exists("Logs/placeholder_fallback.png"));
    }
}
