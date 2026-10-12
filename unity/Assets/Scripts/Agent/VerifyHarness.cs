using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

/// <summary>
/// Automated end-to-end check (run by the PlayMode test "RoomVerifyTests"): feeds synthetic speech + events through the
/// real AgentController path and writes screenshots to Logs/. Not used at runtime in the shipped app.
/// </summary>
public class VerifyHarness : MonoBehaviour
{
    public bool Done;
    public readonly List<string> Failures = new List<string>();
    public readonly List<string> Report = new List<string>();

    static string LogDir { get { var d = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Logs")); Directory.CreateDirectory(d); return d; } }

    void Fail(string m) { Failures.Add(m); Debug.LogError("[VERIFY] FAIL " + m); }
    void Note(string m) { Report.Add(m); Debug.Log("[VERIFY] " + m); }

    public static float[] Speech(float seconds, int rate, float hz = 2.6f)
    {
        var d = new float[(int)(seconds * rate)];
        for (int i = 0; i < d.Length; i++)
        {
            float t = i / (float)rate;
            float env = Mathf.Pow(Mathf.Max(0f, Mathf.Sin(2f * Mathf.PI * hz * t)), 0.6f); // syllables with silent gaps
            float v = Mathf.Sin(2f * Mathf.PI * 160f * t) + 0.5f * Mathf.Sin(2f * Mathf.PI * 320f * t) + 0.25f * Mathf.Sin(2f * Mathf.PI * 640f * t);
            d[i] = 0.3f * env * v;
        }
        return d;
    }

    static float[] Tone(float seconds, int rate)
    {
        var d = new float[(int)(seconds * rate)];
        for (int i = 0; i < d.Length; i++) d[i] = 0.25f * Mathf.Sin(2f * Mathf.PI * 200f * i / rate) * (0.7f + 0.3f * Mathf.Sin(i * 0.002f));
        return d;
    }

    void Start() { StartCoroutine(Run()); }

    void SaveCam(Camera cam, string file)
    {
        const int w = 1024, h = 768;
        var rt = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32);
        var prev = cam.targetTexture;
        cam.targetTexture = rt; cam.aspect = w / (float)h;
        cam.Render();
        RenderTexture.active = rt;
        var tex = new Texture2D(w, h, TextureFormat.RGB24, false);
        tex.ReadPixels(new Rect(0, 0, w, h), 0, 0); tex.Apply();
        RenderTexture.active = null; cam.targetTexture = prev; cam.ResetAspect();
        File.WriteAllBytes(Path.Combine(LogDir, file), tex.EncodeToPNG());
        Destroy(tex); rt.Release(); Destroy(rt);
        Note("saved " + file);
    }

    IEnumerator SaveScreen(string file)
    {
        yield return new WaitForEndOfFrame();
        var tex = ScreenCapture.CaptureScreenshotAsTexture();
        File.WriteAllBytes(Path.Combine(LogDir, file), tex.EncodeToPNG());
        Destroy(tex);
        Note("saved " + file + " (full screen incl. IMGUI)");
    }

    IEnumerator SaveCrop(string file, Rect topLeftRect)
    {
        yield return new WaitForEndOfFrame();
        var tex = ScreenCapture.CaptureScreenshotAsTexture();
        int x = Mathf.Clamp(Mathf.FloorToInt(topLeftRect.x) - 12, 0, tex.width - 1), w = Mathf.Min(tex.width - x, Mathf.CeilToInt(topLeftRect.width) + 24);
        int yTop = Mathf.Clamp(Mathf.FloorToInt(topLeftRect.y) - 12, 0, tex.height - 1), h = Mathf.Min(tex.height - yTop, Mathf.CeilToInt(topLeftRect.height) + 24);
        var crop = new Texture2D(w, h, TextureFormat.RGB24, false);
        crop.SetPixels(tex.GetPixels(x, tex.height - yTop - h, w, h)); crop.Apply();
        File.WriteAllBytes(Path.Combine(LogDir, file), crop.EncodeToPNG());
        Destroy(crop); Destroy(tex);
        Note("saved " + file + " (crop " + w + "x" + h + ")");
    }

    static RubyToken[] Rb(params string[] p)
    {
        var l = new List<RubyToken>();
        for (int i = 0; i + 1 < p.Length; i += 2) l.Add(new RubyToken { t = p[i], r = p[i + 1] });
        return l.ToArray();
    }

    float MaxBlend(int idx)
    {
        float m = 0f;
        foreach (var r in FindObjectsByType<SkinnedMeshRenderer>(FindObjectsSortMode.None))
            if (r.sharedMesh != null && r.sharedMesh.blendShapeCount > idx) m = Mathf.Max(m, r.GetBlendShapeWeight(idx));
        return m;
    }

    IEnumerator Run()
    {
        for (int i = 0; i < 20; i++) yield return null;
        var ctrl = FindFirstObjectByType<AgentController>();
        if (ctrl == null) { Fail("no AgentController in scene"); Done = true; yield break; }
        Screen.SetResolution(1920, 1080, false);
        for (int i = 0; i < 10; i++) yield return null;
        Note("screen=" + Screen.width + "x" + Screen.height);
        ctrl.DisablePolling();
        var cam = Camera.main;
        var rig = FindFirstObjectByType<CameraRig>();
        var va = FindFirstObjectByType<VrmAvatar>();
        Note("avatar=" + (ctrl.Avatar == null ? "null" : ctrl.Avatar.GetType().Name) + " vrmAvatarFound=" + (va != null) + " rig=" + (rig != null));
        if (!(ctrl.Avatar is VrmAvatar)) Fail("AgentController is not using VrmAvatar: " + (ctrl.Avatar == null ? "null" : ctrl.Avatar.GetType().Name));
        if (rig == null || cam == null) { Fail("no CameraRig/main camera"); Done = true; yield break; }
        for (int i = 0; i < 40; i++) yield return null; // let the camera settle
        var client = ctrl.Client;
        var hud = ctrl.Hud;
        var subs = FindFirstObjectByType<SubtitleView>();

        // camera check
        if (va != null && va.Head != null)
        {
            float dist = Vector3.Distance(cam.transform.position, va.Head.position);
            Note("camera preset=" + rig.Current + " dist-to-head=" + dist.ToString("0.00") + " fov=" + cam.fieldOfView.ToString("0.0") + " near=" + cam.nearClipPlane);
            if (rig.Current != CameraRig.Preset.Face) Fail("default preset is not Face");
            if (dist < 0.4f || dist > 0.9f) Fail("camera not a face close-up: dist " + dist);
            if (cam.fieldOfView > 36f) Fail("fov too wide " + cam.fieldOfView);
        }
        else Fail("avatar head bone not found");

        // ---- phase 1: speech + happy + HUD ----
        const int rate = 24000;
        client.InjectMeta(new AgentMeta { user = "ありがとう", reply = "どういたしまして", done = false });
        client.InjectSegment(new SegmentEvent { kind = "segment", id = 1, lang = "ja", text = "どういたしまして", spoken = true, tr = null, ruby = new RubyToken[0] });
        client.InjectAudio(Speech(3f, rate), rate);
        float t0 = Time.time, maxMouth = 0f, minAfterOpen = 1f;
        bool happySpeakShot = false, openShot = false, closedShot = false, happyShot = false, hudShot = false, trSent = false;
        float maxAa = 0f, maxHappyBlend = 0f;
        while (Time.time - t0 < 4.6f)
        {
            float t = Time.time - t0;
            if (!trSent && t > 1.9f) { client.InjectSegmentTranslation(new SegmentTranslationEvent { kind = "segment_tr", id = 1, tr = "Thank you, that was great!" }); trSent = true; }
            float m = ctrl.LipSync.Mouth;
            if (Time.frameCount % 120 == 0) Note("t=" + t.ToString("0.00") + " frame=" + Time.frameCount + " mouth=" + m.ToString("0.00") + " buffered=" + ctrl.AudioPlayer.Buffered + " consumed=" + ctrl.AudioPlayer.ConsumedSamples + " audible=" + ctrl.AudioPlayer.IsAudible + " state=" + ctrl.State + " emo=" + ctrl.Emotion.Current);
            maxMouth = Mathf.Max(maxMouth, m);
            if (maxMouth > 0.4f) minAfterOpen = Mathf.Min(minAfterOpen, m);
            maxAa = Mathf.Max(maxAa, MaxBlend(39)); maxHappyBlend = Mathf.Max(maxHappyBlend, MaxBlend(3));
            yield return null; // after LateUpdate of previous frame
            if (!openShot && t > 0.6f && m > 0.45f) { openShot = true; SaveCam(cam, "face_open.png"); }
            if (openShot && !closedShot && m < 0.05f) { closedShot = true; SaveCam(cam, "face_closed.png"); }
            if (!hudShot && t > 3.0f) { hudShot = true; yield return SaveScreen("hud.png"); }
            if (!happyShot && t > 2.8f && ctrl.Emotion.Current == "happy" && ctrl.Emotion.Weight > 0.5f) { happyShot = true; SaveCam(cam, "face_happy.png"); }
            if (!happySpeakShot && t > 0.8f && ctrl.Emotion.Current == "happy" && ctrl.Emotion.Weight > 0.5f && m > 0.5f) { happySpeakShot = true; Note("happy speaking mouth=" + m.ToString("0.00") + " emo=" + ctrl.Emotion.Weight.ToString("0.00")); yield return SaveScreen("face_happy_speaking.png"); }
        }
        Note("mouth max=" + maxMouth.ToString("0.00") + " minAfterOpen=" + minAfterOpen.ToString("0.00") + " blend aa(max)=" + maxAa.ToString("0.0") + " happy(max)=" + maxHappyBlend.ToString("0.0") + " emotion=" + ctrl.Emotion.Current + ":" + ctrl.Emotion.Weight.ToString("0.00"));
        if (maxMouth < 0.4f) Fail("mouth never opened (max " + maxMouth + ")");
        if (!openShot) Fail("no mouth-open moment captured");
        if (!closedShot) Fail("no mouth-closed moment captured");
        if (maxAa < 20f) Fail("aa blendshape never driven (max " + maxAa + ")");
        if (!happySpeakShot) Fail("no happy+mouth-open screenshot");
        if (!happyShot) Fail("happy expression never reached (thank-you translation)");
        if (maxHappyBlend < 30f) Fail("happy blendshape weak (max " + maxHappyBlend + ")");
        if (hud != null && hud.LastEntry != "ありがとう") Fail("HUD last entry = '" + (hud != null ? hud.LastEntry : "null") + "'");

        // happy while idle (speech over, emotion still held)
        while (ctrl.AudioPlayer.IsAudible) yield return null;
        for (int i = 0; i < 40; i++) yield return null;
        Note("idle emotion=" + ctrl.Emotion.Current + ":" + ctrl.Emotion.Weight.ToString("0.00") + " mouth=" + ctrl.LipSync.Mouth.ToString("0.00"));
        if (ctrl.Emotion.Current != "happy") Fail("happy not held at idle: " + ctrl.Emotion.Current);
        yield return SaveScreen("face_happy_idle.png");

        // angry while speaking
        client.InjectSegment(new SegmentEvent { kind = "segment", id = 5, lang = "ja", text = "これは本です", spoken = true, tr = null, ruby = new RubyToken[0] });
        client.InjectAudio(Speech(2.5f, rate), rate);
        float ta = Time.time; bool angryShot = false;
        while (Time.time - ta < 3f)
        {
            ctrl.Emotion.Set("angry", 0.9f); ctrl.Emotion.KeepAlive();
            yield return null;
            if (!angryShot && Time.time - ta > 1.0f && ctrl.Emotion.Current == "angry" && ctrl.Emotion.Weight > 0.5f && ctrl.LipSync.Mouth > 0.5f) { angryShot = true; yield return SaveScreen("face_angry_speaking.png"); }
        }
        if (!angryShot) Fail("no angry+mouth-open screenshot");
        ctrl.Emotion.Set("neutral", 0f);

        // ---- phase 2: stacked subtitles, highlight follows audio ----
        while (ctrl.AudioPlayer.IsAudible) yield return null;
        if (subs == null) { Fail("no SubtitleView"); Done = true; yield break; }
        subs.NewTurn();
        string[] ja = { "私は学生です", "毎日日本語を勉強します", "あなたは何を勉強しますか", "日本語はとても面白いです" };
        string[] en = { "I am a student.", "I study Japanese every day.", "What do you study?", "Japanese is very interesting." };
        var rubies = new[]
        {
            Rb("私", "わたし", "は", "", "学生", "がくせい", "です", ""),
            Rb(SubtitleView.RubySample),
            Rb("あなた", "", "は", "", "何", "なに", "を", "", "勉強", "べんきょう", "しますか", ""),
            Rb("日本語", "にほんご", "は", "", "とても", "", "面白", "おもしろ", "いです", ""),
        };
        client.InjectSegment(new SegmentEvent { kind = "segment", id = 10, lang = "en", text = "Tip: use the polite form.", spoken = false, tr = null, ruby = new RubyToken[0] });
        for (int i = 0; i < 4; i++)
            client.InjectSegment(new SegmentEvent { kind = "segment", id = 11 + i, lang = "ja", text = ja[i], spoken = true, tr = null, ruby = rubies[i] });
        for (int i = 0; i < 4; i++) client.InjectSegmentTranslation(new SegmentTranslationEvent { kind = "segment_tr", id = 11 + i, tr = en[i] });
        client.InjectMeta(new AgentMeta { user = "自己紹介をしてください", reply = "", done = false });
        for (int i = 0; i < 4; i++) client.InjectAudio(Tone(1.4f, rate), rate);
        float t1 = Time.time; bool shot = false; var seen = new HashSet<int>();
        while (Time.time - t1 < 5f && (ctrl.AudioPlayer.IsAudible || Time.time - t1 < 0.5f))
        {
            yield return null;
            int pid = ctrl.PlayingSegmentId; if (pid > 0) seen.Add(pid);
            if (!shot && Time.time - t1 > 2.1f && pid == 12)
            {
                shot = true;
                yield return SaveScreen("subtitles_stack.png"); yield return SaveScreen("face_with_subs.png");
                var pr = subs.LastPanel; Note("panel rect=" + pr + " screen=" + Screen.width + "x" + Screen.height + " panelFrac=" + (pr.height / Screen.height).ToString("0.000") + " fit=" + subs.Fit);
                if (pr.height / Screen.height > 0.315f) Fail("subtitle panel too tall: " + pr.height / Screen.height);
                // mouth / chin must be above the panel: chin ~ 0.12 m below the eye line
                if (va != null && va.Head != null)
                {
                    Vector3 chinW = va.Head.position + Vector3.down * 0.07f;
                    float chinY = Screen.height - cam.WorldToScreenPoint(chinW).y;
                    Note("approx chin screen y=" + chinY.ToString("0") + " panel top=" + pr.y.ToString("0"));
                    if (chinY > pr.y) Fail("chin overlaps subtitle panel");
                }
                yield return SaveCrop("furigana.png", pr);
            }
        }
        Note("subtitle lines=" + subs.LineCount + " playing ids seen=" + string.Join(",", new List<int>(seen).ConvertAll(x => x.ToString()).ToArray()) + " fit=" + subs.Fit);
        if (subs.LineCount != 4) Fail("subtitle stack has " + subs.LineCount + " lines, expected 4");
        if (!seen.Contains(11) || !seen.Contains(12) || !seen.Contains(13) || !seen.Contains(14)) Fail("highlight did not step through all 4 lines");
        if (!shot) Fail("subtitle stack screenshot not taken");
        if (hud != null && hud.LastEntry != "自己紹介をしてください") Fail("HUD second entry missing");

        // static camera: face must stay inside the frame at both sway extremes
        var idleL = FindFirstObjectByType<IdleLife>();
        if (idleL != null && va != null && va.Head != null)
        {
            float[] ext = { 1f, -1f }; string[] nm = { "face_static_a.png", "face_static_b.png" };
            for (int i = 0; i < 2; i++)
            {
                idleL.ForceSway = ext[i];
                yield return null; yield return null;
                Vector3 e = cam.WorldToScreenPoint(va.Head.position + Vector3.up * 0.07f), c = cam.WorldToScreenPoint(va.Head.position + Vector3.down * 0.07f);
                float ey = 1f - e.y / Screen.height, cy = 1f - c.y / Screen.height, ex = e.x / Screen.width;
                Note(nm[i] + " eyeY=" + ey.ToString("0.00") + " chinY=" + cy.ToString("0.00") + " eyeX=" + ex.ToString("0.00"));
                if (ey < 0.12f || cy > 0.68f || ex < 0.35f || ex > 0.65f) Fail("face leaves safe frame at sway extreme " + i);
                yield return SaveScreen(nm[i]);
            }
            idleL.ForceSway = float.NaN;
        }
        Done = true;
    }
}
