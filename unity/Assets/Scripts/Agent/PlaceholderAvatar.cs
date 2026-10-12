using UnityEngine;

/// <summary>Capsule-based stand-in avatar. Builds its own body, mouth and eyes.</summary>
public class PlaceholderAvatar : MonoBehaviour, IAvatarExpressions
{
    Renderer _body;
    Transform _mouth, _eyeL, _eyeR;
    readonly Color _baseColor = new Color(0.85f, 0.8f, 0.75f);
    float _mouthOpen, _blink, _emoIntensity;
    string _emo = "neutral";

    static readonly string[] Names = { "neutral", "happy", "sad", "angry", "surprised", "embarrassed" };
    static readonly Color[] Colors =
    {
        new Color(0.85f,0.8f,0.75f), new Color(1f,0.9f,0.4f), new Color(0.5f,0.6f,0.95f),
        new Color(0.95f,0.35f,0.3f), new Color(0.95f,0.7f,1f), new Color(1f,0.6f,0.7f)
    };

    void Awake()
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Capsule);
        go.name = "PlaceholderBody";
        Destroy(go.GetComponent<Collider>());
        go.transform.SetParent(transform, false);
        _body = go.GetComponent<Renderer>();
        _body.material = new Material(_body.sharedMaterial);
        _mouth = MakePart(go.transform, "Mouth", new Vector3(0f, 0.45f, 0.48f), new Vector3(0.3f, 0.05f, 0.05f), Color.black);
        _eyeL = MakePart(go.transform, "EyeL", new Vector3(-0.17f, 0.7f, 0.43f), new Vector3(0.1f, 0.1f, 0.05f), Color.black);
        _eyeR = MakePart(go.transform, "EyeR", new Vector3(0.17f, 0.7f, 0.43f), new Vector3(0.1f, 0.1f, 0.05f), Color.black);
        Apply();
    }

    static Transform MakePart(Transform parent, string n, Vector3 pos, Vector3 scale, Color c)
    {
        var p = GameObject.CreatePrimitive(PrimitiveType.Cube);
        p.name = n;
        Destroy(p.GetComponent<Collider>());
        p.transform.SetParent(parent, false);
        p.transform.localPosition = pos;
        p.transform.localScale = scale;
        var r = p.GetComponent<Renderer>();
        r.material = new Material(r.sharedMaterial) { color = c };
        return p.transform;
    }

    public void SetEmotion(string name, float intensity) { _emo = name; _emoIntensity = Mathf.Clamp01(intensity); Apply(); }
    public void SetMouth(float open) { _mouthOpen = Mathf.Clamp01(open); Apply(); }
    public void SetSpeaking(float energy) { }
    public void SetBlink(float amount) { _blink = Mathf.Clamp01(amount); Apply(); }

    void Apply()
    {
        if (_body == null) return;
        int idx = System.Array.IndexOf(Names, _emo);
        var target = Colors[idx < 0 ? 0 : idx];
        _body.material.color = Color.Lerp(_baseColor, target, _emoIntensity);
        var ms = _mouth.localScale; ms.y = Mathf.Lerp(0.05f, 0.25f, _mouthOpen); _mouth.localScale = ms;
        float ey = Mathf.Lerp(0.1f, 0.01f, _blink);
        var l = _eyeL.localScale; l.y = ey; _eyeL.localScale = l;
        var r = _eyeR.localScale; r.y = ey; _eyeR.localScale = r;
    }
}
