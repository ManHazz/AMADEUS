using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

public static class BuildRoom
{
    const int CharLayer = 8; // MToon characters skip warm point lights (they blow out the face)
    const string ScenePath = "Assets/Scenes/Room.unity";
    const string MatDir = "Assets/Materials/Room";
    const float W = 4.5f, D = 3.5f, H = 2.7f;

    static Shader _sh;
    static Shader Sh()
    {
        if (_sh != null) return _sh;
        var rp = GraphicsSettings.defaultRenderPipeline;
        string[] names = rp != null
            ? new[] { "Universal Render Pipeline/Lit", "Standard" }
            : new[] { "Standard", "Universal Render Pipeline/Lit", "Diffuse" };
        foreach (var n in names) { _sh = Shader.Find(n); if (_sh != null) break; }
        return _sh;
    }

    static Material Mat(string name, Color c, float smooth = 0.2f, Color? emit = null)
    {
        Directory.CreateDirectory(MatDir);
        string path = MatDir + "/" + name + ".mat";
        var m = new Material(Sh()) { name = name };
        m.color = c;
        if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", c);
        if (m.HasProperty("_Glossiness")) m.SetFloat("_Glossiness", smooth);
        if (m.HasProperty("_Smoothness")) m.SetFloat("_Smoothness", smooth);
        if (emit.HasValue)
        {
            m.EnableKeyword("_EMISSION");
            m.globalIlluminationFlags = MaterialGlobalIlluminationFlags.None;
            m.SetColor("_EmissionColor", emit.Value);
        }
        AssetDatabase.CreateAsset(m, path);
        return AssetDatabase.LoadAssetAtPath<Material>(path);
    }

    static GameObject P(PrimitiveType t, string name, Vector3 pos, Vector3 scale, Material m, Transform parent)
    {
        var g = GameObject.CreatePrimitive(t);
        g.name = name;
        g.transform.SetParent(parent, false);
        g.transform.localPosition = pos;
        g.transform.localScale = scale;
        g.GetComponent<Renderer>().sharedMaterial = m;
        Object.DestroyImmediate(g.GetComponent<Collider>());
        return g;
    }
    static GameObject Cube(string n, Vector3 pos, Vector3 s, Material m, Transform p) { return P(PrimitiveType.Cube, n, pos, s, m, p); }

    static void PointLight(string n, Vector3 pos, Color c, float intensity, float range, Transform p)
    {
        var g = new GameObject(n); g.transform.SetParent(p, false); g.transform.position = pos;
        var l = g.AddComponent<Light>(); l.type = LightType.Point; l.color = c; l.intensity = intensity; l.range = range; l.cullingMask = ~(1 << CharLayer);
    }

    static Texture2D MakeScreenTex()
    {
        const int w = 128, h = 72;
        var t = new Texture2D(w, h, TextureFormat.RGB24, false);
        for (int y = 0; y < h; y++) for (int x = 0; x < w; x++)
        {
            float v = y / (float)h;
            Color c = Color.Lerp(new Color(0.12f, 0.2f, 0.45f), new Color(0.35f, 0.3f, 0.6f), v);
            if (y < 5) c = new Color(0.1f, 0.1f, 0.15f);
            else if (x > 8 && x < 40 && y > 20 && y < 56 && (x < 10 || y > 52 || x > 38 || y < 22)) c = new Color(0.8f, 0.85f, 0.95f) * 0.7f;
            else if (x > 10 && x < 38 && y > 52 && y < 55) c = new Color(0.3f, 0.5f, 0.8f);
            else if (x > 50 && x < 110 && y > 30 && y < 60 && (y % 8) < 2) c = new Color(0.7f, 0.75f, 0.85f) * 0.7f;
            t.SetPixel(x, y, c);
        }
        t.Apply();
        Directory.CreateDirectory(MatDir);
        string path = MatDir + "/ScreenTex.png";
        File.WriteAllBytes(path, t.EncodeToPNG());
        AssetDatabase.ImportAsset(path);
        return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
    }

    public static void Generate()
    {
        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        Directory.CreateDirectory("Assets/Scenes");

        var wood = Mat("Wood", new Color(0.62f, 0.44f, 0.28f), 0.35f);
        var woodDark = Mat("WoodDark", new Color(0.45f, 0.3f, 0.18f), 0.3f);
        var pink = Mat("WallPink", new Color(0.97f, 0.82f, 0.86f));
        var cream = Mat("WallCream", new Color(0.99f, 0.95f, 0.86f));
        var ceil = Mat("Ceiling", new Color(0.98f, 0.97f, 0.95f));
        var white = Mat("White", new Color(0.95f, 0.95f, 0.95f));
        var frame = Mat("WindowFrame", new Color(0.97f, 0.97f, 0.95f));
        var sky = Mat("Daylight", new Color(0.55f, 0.68f, 0.82f), 0f, new Color(0.55f, 0.7f, 0.9f) * 0.5f);
        var blanket = Mat("Blanket", new Color(0.8f, 0.7f, 0.92f));
        var sheet = Mat("Sheet", new Color(1f, 0.96f, 0.96f));
        var pillowA = Mat("PillowPink", new Color(1f, 0.72f, 0.8f));
        var pillowB = Mat("PillowMint", new Color(0.72f, 0.95f, 0.85f));
        var rugM = Mat("Rug", new Color(0.98f, 0.78f, 0.82f));
        var rugM2 = Mat("RugInner", new Color(1f, 0.93f, 0.8f));
        var screen = Mat("Screen", new Color(0.25f, 0.3f, 0.45f), 0.5f, new Color(0.45f, 0.5f, 0.6f) * 0.6f);
        var scrTex = MakeScreenTex();
        screen.mainTexture = scrTex; screen.SetTexture("_EmissionMap", scrTex);
        var black = Mat("Black", new Color(0.1f, 0.1f, 0.12f), 0.5f);
        var keyM = Mat("Keyboard", new Color(0.92f, 0.9f, 0.95f));
        var chairM = Mat("Chair", new Color(0.7f, 0.85f, 0.95f));
        var chairBack = Mat("ChairFabric", new Color(0.78f, 0.7f, 0.93f), 0.1f);
        var fairy = Mat("FairyLight", new Color(1f, 0.9f, 0.6f), 0f, new Color(1f, 0.8f, 0.4f) * 2f);
        var plushA = Mat("PlushBrown", new Color(0.85f, 0.65f, 0.5f), 0f);
        var plushB = Mat("PlushWhite", new Color(1f, 1f, 1f), 0f);
        var plushC = Mat("PlushYellow", new Color(1f, 0.9f, 0.45f), 0f);
        Color[] bookC = { new Color(.9f, .4f, .45f), new Color(.45f, .7f, .9f), new Color(.95f, .8f, .4f), new Color(.5f, .8f, .55f), new Color(.7f, .55f, .85f), new Color(.95f, .6f, .75f) };
        var bookMs = new Material[bookC.Length];
        for (int i = 0; i < bookC.Length; i++) bookMs[i] = Mat("Book" + i, bookC[i]);
        Color[] posterC = { new Color(.95f, .55f, .7f), new Color(.5f, .75f, .95f), new Color(.98f, .85f, .45f), new Color(.6f, .85f, .7f) };

        var room = new GameObject("Room").transform;

        Cube("Floor", new Vector3(0, -0.05f, 0), new Vector3(W, 0.1f, D), wood, room);
        Cube("Ceiling", new Vector3(0, H + 0.05f, 0), new Vector3(W, 0.1f, D), ceil, room);
        Cube("WallLeft", new Vector3(-W / 2 - 0.05f, H / 2, 0), new Vector3(0.1f, H, D), pink, room);
        Cube("WallRight", new Vector3(W / 2 + 0.05f, H / 2, 0), new Vector3(0.1f, H, D), pink, room);
        float bz = D / 2 + 0.05f, wx = -1.0f, ww = 1.3f, wy0 = 1.0f, wy1 = 2.2f;
        float leftEdge = -W / 2, rightEdge = W / 2;
        Cube("BackLeft", new Vector3((leftEdge + wx - ww / 2) / 2, H / 2, bz), new Vector3(wx - ww / 2 - leftEdge, H, 0.1f), cream, room);
        Cube("BackRight", new Vector3((wx + ww / 2 + rightEdge) / 2, H / 2, bz), new Vector3(rightEdge - (wx + ww / 2), H, 0.1f), cream, room);
        Cube("BackBelow", new Vector3(wx, wy0 / 2, bz), new Vector3(ww, wy0, 0.1f), cream, room);
        Cube("BackAbove", new Vector3(wx, (wy1 + H) / 2, bz), new Vector3(ww, H - wy1, 0.1f), cream, room);
        Cube("Skirting", new Vector3(0, 0.05f, D / 2 - 0.02f), new Vector3(W, 0.1f, 0.04f), white, room);
        float fz = D / 2;
        Cube("FrameTop", new Vector3(wx, wy1, fz), new Vector3(ww + 0.1f, 0.06f, 0.12f), frame, room);
        Cube("FrameBottom", new Vector3(wx, wy0, fz), new Vector3(ww + 0.1f, 0.06f, 0.2f), frame, room);
        Cube("FrameL", new Vector3(wx - ww / 2, (wy0 + wy1) / 2, fz), new Vector3(0.06f, wy1 - wy0, 0.12f), frame, room);
        Cube("FrameR", new Vector3(wx + ww / 2, (wy0 + wy1) / 2, fz), new Vector3(0.06f, wy1 - wy0, 0.12f), frame, room);
        Cube("FrameMullionV", new Vector3(wx, (wy0 + wy1) / 2, fz), new Vector3(0.04f, wy1 - wy0, 0.1f), frame, room);
        Cube("FrameMullionH", new Vector3(wx, (wy0 + wy1) / 2, fz), new Vector3(ww, 0.04f, 0.1f), frame, room);
        Cube("DaylightPanel", new Vector3(wx, (wy0 + wy1) / 2, D / 2 + 0.3f), new Vector3(ww + 1.5f, wy1 - wy0 + 1f, 0.05f), sky, room);
        Cube("CurtainL", new Vector3(wx - ww / 2 - 0.12f, 1.65f, fz - 0.1f), new Vector3(0.22f, 1.6f, 0.06f), pillowA, room);
        Cube("CurtainR", new Vector3(wx + ww / 2 + 0.12f, 1.65f, fz - 0.1f), new Vector3(0.22f, 1.6f, 0.06f), pillowA, room);

        var sun = new GameObject("Sun"); sun.transform.SetParent(room, false);
        sun.transform.position = new Vector3(wx, 3f, D / 2 + 2f);
        sun.transform.rotation = Quaternion.Euler(38f, 188f, 0f);
        var sl = sun.AddComponent<Light>(); sl.type = LightType.Directional; sl.color = new Color(1f, 0.96f, 0.88f);
        sl.intensity = 0.8f; sl.shadows = LightShadows.Soft;
        PointLight("WarmLampDesk", new Vector3(1.4f, 1.4f, 1.1f), new Color(1f, 0.8f, 0.55f), 0.9f, 4f, room);
        PointLight("WarmLampBed", new Vector3(-1.6f, 1.7f, 0.2f), new Color(1f, 0.75f, 0.6f), 0.8f, 4f, room);
        PointLight("WarmFill", new Vector3(0f, 2.4f, -0.3f), new Color(1f, 0.88f, 0.75f), 0.6f, 6f, room);
        RenderSettings.ambientMode = AmbientMode.Flat;
        RenderSettings.ambientLight = new Color(0.55f, 0.46f, 0.40f);

        P(PrimitiveType.Cylinder, "Rug", new Vector3(0, 0.01f, -0.1f), new Vector3(2.0f, 0.01f, 1.5f), rugM, room);
        P(PrimitiveType.Cylinder, "RugInner", new Vector3(0, 0.022f, -0.1f), new Vector3(1.4f, 0.01f, 1.0f), rugM2, room);

        var bed = new GameObject("Bed").transform; bed.SetParent(room, false); bed.localPosition = new Vector3(-1.7f, 0, 0.7f);
        Cube("BedFrame", new Vector3(0, 0.2f, 0), new Vector3(1.0f, 0.3f, 2.0f), woodDark, bed);
        Cube("Mattress", new Vector3(0, 0.42f, 0), new Vector3(0.96f, 0.2f, 1.95f), sheet, bed);
        Cube("Blanket", new Vector3(0, 0.54f, -0.3f), new Vector3(1.0f, 0.1f, 1.3f), blanket, bed);
        Cube("Headboard", new Vector3(0, 0.7f, 1.02f), new Vector3(1.04f, 0.8f, 0.06f), woodDark, bed);
        Cube("PillowA", new Vector3(-0.22f, 0.58f, 0.78f), new Vector3(0.42f, 0.12f, 0.3f), pillowA, bed).transform.localRotation = Quaternion.Euler(0, 8, 0);
        Cube("PillowB", new Vector3(0.22f, 0.58f, 0.78f), new Vector3(0.42f, 0.12f, 0.3f), pillowB, bed).transform.localRotation = Quaternion.Euler(0, -6, 0);
        P(PrimitiveType.Sphere, "BedPlush", new Vector3(0.05f, 0.7f, 0.55f), new Vector3(0.28f, 0.26f, 0.26f), plushA, bed);

        var desk = new GameObject("Desk").transform; desk.SetParent(room, false); desk.localPosition = new Vector3(1.3f, 0, 1.4f);
        Cube("DeskTop", new Vector3(0, 0.74f, 0), new Vector3(1.3f, 0.04f, 0.65f), wood, desk);
        foreach (var sx in new[] { -0.6f, 0.6f }) foreach (var sz in new[] { -0.28f, 0.28f })
            Cube("DeskLeg", new Vector3(sx, 0.36f, sz), new Vector3(0.05f, 0.72f, 0.05f), woodDark, desk);
        Cube("MonitorScreen", new Vector3(0, 1.05f, 0.185f), new Vector3(0.6f, 0.36f, 0.03f), screen, desk);
        Cube("MonitorBack", new Vector3(0, 1.05f, 0.2f), new Vector3(0.62f, 0.38f, 0.03f), black, desk);
        Cube("MonitorStand", new Vector3(0, 0.84f, 0.2f), new Vector3(0.06f, 0.16f, 0.04f), black, desk);
        Cube("MonitorBase", new Vector3(0, 0.77f, 0.2f), new Vector3(0.24f, 0.015f, 0.14f), black, desk);
        Cube("Keyboard", new Vector3(0, 0.77f, -0.1f), new Vector3(0.42f, 0.02f, 0.14f), keyM, desk);
        P(PrimitiveType.Sphere, "Mouse", new Vector3(0.32f, 0.775f, -0.1f), new Vector3(0.06f, 0.03f, 0.09f), keyM, desk);
        P(PrimitiveType.Cylinder, "Mug", new Vector3(-0.5f, 0.8f, -0.05f), new Vector3(0.07f, 0.05f, 0.07f), pillowA, desk);
        var chair = new GameObject("Chair").transform; chair.SetParent(room, false); chair.localPosition = new Vector3(1.3f, 0, 0.55f);
        chair.localRotation = Quaternion.Euler(0, 15, 0);
        Cube("Seat", new Vector3(0, 0.45f, 0), new Vector3(0.42f, 0.08f, 0.42f), chairM, chair);
        Cube("Back", new Vector3(0, 0.75f, -0.2f), new Vector3(0.42f, 0.5f, 0.06f), chairBack, chair);
        P(PrimitiveType.Cylinder, "Post", new Vector3(0, 0.22f, 0), new Vector3(0.06f, 0.2f, 0.06f), black, chair);
        P(PrimitiveType.Cylinder, "Base", new Vector3(0, 0.02f, 0), new Vector3(0.45f, 0.02f, 0.45f), black, chair);

        var shelf = new GameObject("Bookshelf").transform; shelf.SetParent(room, false); shelf.localPosition = new Vector3(W / 2 - 0.2f, 0, -0.2f);
        Cube("ShelfL", new Vector3(0, 0.9f, -0.5f), new Vector3(0.32f, 1.8f, 0.04f), wood, shelf);
        Cube("ShelfR", new Vector3(0, 0.9f, 0.5f), new Vector3(0.32f, 1.8f, 0.04f), wood, shelf);
        Cube("ShelfBack", new Vector3(0.15f, 0.9f, 0), new Vector3(0.02f, 1.8f, 1.04f), woodDark, shelf);
        int bi = 0;
        for (int s = 0; s < 5; s++)
        {
            float y = s * 0.4f + 0.02f;
            Cube("Board" + s, new Vector3(0, y, 0), new Vector3(0.32f, 0.04f, 1.04f), wood, shelf);
            if (s == 4) break;
            float z = -0.44f;
            while (z < 0.4f)
            {
                float bw = 0.05f + 0.03f * ((bi * 7) % 3), bh = 0.22f + 0.04f * ((bi * 5) % 4);
                if (s == 3 && bi % 4 == 0) { z += 0.08f; bi++; continue; }
                Cube("Book", new Vector3(0, y + 0.02f + bh / 2, z + bw / 2), new Vector3(0.22f, bh, bw), bookMs[bi % bookMs.Length], shelf);
                z += bw + 0.005f; bi++;
            }
        }
        P(PrimitiveType.Sphere, "ShelfPlush", new Vector3(0, 1.98f, 0.1f), new Vector3(0.22f, 0.22f, 0.22f), plushC, shelf);

        P(PrimitiveType.Sphere, "PlushBodyBear", new Vector3(-0.8f, 0.22f, 1.3f), new Vector3(0.4f, 0.36f, 0.36f), plushA, room);
        P(PrimitiveType.Sphere, "PlushHeadBear", new Vector3(-0.8f, 0.5f, 1.3f), new Vector3(0.28f, 0.26f, 0.26f), plushA, room);
        P(PrimitiveType.Sphere, "PlushEarL", new Vector3(-0.91f, 0.64f, 1.3f), new Vector3(0.1f, 0.1f, 0.06f), plushA, room);
        P(PrimitiveType.Sphere, "PlushEarR", new Vector3(-0.69f, 0.64f, 1.3f), new Vector3(0.1f, 0.1f, 0.06f), plushA, room);
        P(PrimitiveType.Capsule, "PlushBunny", new Vector3(0.4f, 0.25f, 1.5f), new Vector3(0.25f, 0.25f, 0.25f), plushB, room).transform.localRotation = Quaternion.Euler(0, 0, 15);
        P(PrimitiveType.Sphere, "PlushBlob", new Vector3(-1.0f, 0.15f, 1.5f), new Vector3(0.3f, 0.3f, 0.3f), plushC, room);

        float pz = D / 2 - 0.01f;
        var pa = P(PrimitiveType.Quad, "PosterBack1", new Vector3(0.6f, 1.7f, pz), new Vector3(0.5f, 0.7f, 1), Mat("Poster0", posterC[0]), room);
        var pb = P(PrimitiveType.Quad, "PosterBack2", new Vector3(-2.0f, 1.6f, pz), new Vector3(0.4f, 0.55f, 1), Mat("Poster1", posterC[1]), room);
        pa.transform.rotation = pb.transform.rotation = Quaternion.Euler(0, 0, 0);
        var pc = P(PrimitiveType.Quad, "PosterLeft1", new Vector3(-W / 2 + 0.01f, 1.7f, -0.6f), new Vector3(0.6f, 0.8f, 1), Mat("Poster2", posterC[2]), room);
        var pd = P(PrimitiveType.Quad, "PosterLeft2", new Vector3(-W / 2 + 0.01f, 1.5f, 0.7f), new Vector3(0.5f, 0.5f, 1), Mat("Poster3", posterC[3]), room);
        pc.transform.rotation = pd.transform.rotation = Quaternion.Euler(0, -90, 0);
        // quads face -Z by default? Unity quad faces -Z (visible from -Z side): back wall posters need rotation 0 (camera at -Z). Left wall posters face +X: yaw -90 maps -Z to +X.

        var fl = new GameObject("FairyLights").transform; fl.SetParent(room, false);
        for (int i = 0; i < 24; i++)
        {
            float t = i / 23f;
            float x = Mathf.Lerp(-W / 2 + 0.2f, W / 2 - 0.2f, t);
            float y = 2.45f - 0.18f * Mathf.Sin((t * 3f % 1f) * Mathf.PI);
            var g = P(PrimitiveType.Sphere, "Fairy" + i, new Vector3(x, y, D / 2 - 0.08f), Vector3.one * 0.05f, fairy, fl);
            if (i % 6 == 0) PointLight("FairyGlow" + i, g.transform.position, new Color(1f, 0.82f, 0.55f), 0.25f, 1.5f, fl);
        }

        var agent = new GameObject("AgentRoot");
        agent.transform.position = new Vector3(0.2f, 0, 0.5f);
        agent.transform.rotation = Quaternion.Euler(0, 180, 0);
        var vrmPrefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Characters/amadeus.vrm");
        if (vrmPrefab != null)
        {
            var inst = (GameObject)PrefabUtility.InstantiatePrefab(vrmPrefab, agent.transform);
            inst.name = "Amadeus";
            inst.transform.localPosition = Vector3.zero; inst.transform.localRotation = Quaternion.identity;
            foreach (var t in inst.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = CharLayer;
            agent.AddComponent<VrmAvatar>();
            VrmAvatar.ApplyArmsDown(inst.GetComponent<UniVRM10.Vrm10Instance>());
        }
        else Debug.LogWarning("VRM not found; using PlaceholderAvatar");
        var ac = System.Type.GetType("AgentController, Assembly-CSharp");
        if (ac != null) agent.AddComponent(ac); else Debug.LogWarning("AgentController type not found");

        var cam = new GameObject("Main Camera"); cam.tag = "MainCamera";
        var c = cam.AddComponent<Camera>(); c.fieldOfView = 30f; c.nearClipPlane = 0.03f; c.farClipPlane = 50f;
        c.clearFlags = CameraClearFlags.SolidColor; c.backgroundColor = new Color(0.9f, 0.88f, 0.9f);
        cam.AddComponent<AudioListener>();
        // default = face close-up (~0.6 m from the face); CameraRig snaps to the head at runtime. Keys 1 full body / 2 face / 3 bust, Tab cycles.
        cam.transform.position = new Vector3(0.2f, 1.45f, -0.1f);
        cam.transform.rotation = Quaternion.identity;
        cam.AddComponent<CameraRig>();

        EditorSceneManager.SaveScene(scene, ScenePath);
        var list = new System.Collections.Generic.List<EditorBuildSettingsScene>(EditorBuildSettings.scenes);
        if (!list.Exists(s => s.path == ScenePath)) list.Add(new EditorBuildSettingsScene(ScenePath, true));
        EditorBuildSettings.scenes = list.ToArray();
        AssetDatabase.SaveAssets();
        Debug.Log("BuildRoom: saved " + ScenePath + " shader=" + Sh().name);
    }

    static void Shot(string file)
    {
        var cam = Camera.main;
        const int w = 1280, h = 720;
        var rt = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32);
        cam.targetTexture = rt;
        cam.Render();
        RenderTexture.active = rt;
        var tex = new Texture2D(w, h, TextureFormat.RGB24, false);
        tex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
        tex.Apply();
        RenderTexture.active = null; cam.targetTexture = null;
        string dir = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Logs"));
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, file);
        File.WriteAllBytes(path, tex.EncodeToPNG());
        Debug.Log("BuildRoom: screenshot " + path);
    }

    public static void Screenshot()
    {
        EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
        if (Camera.main == null) { Debug.LogError("No main camera"); EditorApplication.Exit(1); return; }
        Shot("room_preview.png");
    }

    const string NL = "\n";
    public static void DumpMaterials()
    {
        EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
        foreach (var r in Object.FindObjectsByType<Renderer>(FindObjectsSortMode.None))
        {
            if (!r.transform.IsChildOf(GameObject.Find("AgentRoot").transform)) continue;
            foreach (var m in r.sharedMaterials)
            {
                if (m == null) continue;
                var sb = new System.Text.StringBuilder("MAT " + r.name + " / " + m.name + " shader=" + m.shader.name + NL);
                int n = m.shader.GetPropertyCount();
                for (int i = 0; i < n; i++)
                {
                    var t = m.shader.GetPropertyType(i); var pn = m.shader.GetPropertyName(i);
                    if (t == UnityEngine.Rendering.ShaderPropertyType.Color) sb.Append("  " + pn + "=" + m.GetColor(pn) + NL);
                    else if (t == UnityEngine.Rendering.ShaderPropertyType.Float || t == UnityEngine.Rendering.ShaderPropertyType.Range) sb.Append("  " + pn + "=" + m.GetFloat(pn) + NL);
                    else if (t == UnityEngine.Rendering.ShaderPropertyType.Texture) sb.Append("  " + pn + "=" + (m.GetTexture(pn) != null ? m.GetTexture(pn).name : "null") + NL);
                }
                Debug.Log(sb.ToString());
            }
        }
    }

    // Simulates VrmAvatar updates in edit mode (play mode is not available in batchmode capture).
    public static void ScreenshotPlay()
    {
        EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
        var av = Object.FindFirstObjectByType<VrmAvatar>();
        if (av == null || Camera.main == null) { Debug.LogError("no avatar/camera"); EditorApplication.Exit(1); return; }
        av.SendMessage("Awake", SendMessageOptions.DontRequireReceiver);
        av.SendMessage("Start", SendMessageOptions.DontRequireReceiver);
        av.SetEmotion("happy", 1f); av.SetMouth(0.7f);
        // Time.deltaTime is 0 outside play mode, so smoothing never advances: seed current values by reflection, then tick Update.
        var bf = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
        typeof(VrmAvatar).GetField("_emoCur", bf).SetValue(av, 1f);
        typeof(VrmAvatar).GetField("_mouthCur", bf).SetValue(av, 0.7f);
        typeof(VrmAvatar).GetField("_shownEmo", bf).SetValue(av, "happy");
        for (int i = 0; i < 5; i++) av.SendMessage("Update", SendMessageOptions.DontRequireReceiver);
        var vrm = av.GetComponentInChildren<UniVRM10.Vrm10Instance>();
        if (vrm != null && vrm.Runtime != null) vrm.Runtime.Process();
        Camera.main.transform.position = new Vector3(0.2f, 1.45f, -1.3f); Camera.main.transform.rotation = Quaternion.Euler(3f, 0, 0);
        Shot("room_happy.png");
    }
}
