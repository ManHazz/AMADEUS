using UnityEngine;
using UniVRM10;

/// <summary>VRM 1.0 avatar driven through Vrm10Instance expressions. Implements IAvatarExpressions.</summary>
[DefaultExecutionOrder(-50)]
public class VrmAvatar : MonoBehaviour, IAvatarExpressions
{
    [SerializeField] float smoothing = 14f;
    [SerializeField] bool lookAtCamera = true;
    [SerializeField] bool armsDown = true;
    [SerializeField, Range(0f, 60f)] float armAngleFromDown = 20f;

    Vrm10Instance _vrm;
    string _emo = "neutral";
    float _emoTarget, _emoCur, _mouthTarget, _mouthCur, _blinkTarget, _blinkCur;
    string _shownEmo = "neutral";
    float _speakTarget, _speakCur;
    Transform _head, _neck;
    Quaternion _headBase, _neckBase;
    int _vowel;
    bool _mouthWasOpen;
    readonly float[] _vowelW = new float[5]; // aa ih ou ee oh
    float _seed;

    public Transform Head { get { return _head; } }
    public Vrm10Instance Vrm { get { return _vrm; } }

    void Awake()
    {
        _vrm = GetComponentInChildren<Vrm10Instance>();
        if (_vrm == null) { Debug.LogWarning("VrmAvatar: no Vrm10Instance found"); enabled = false; return; }
        if (armsDown) ApplyArmsDown(_vrm, armAngleFromDown);
        _seed = Random.value * 100f;
        if (_vrm.TryGetBoneTransform(HumanBodyBones.Head, out _head)) _headBase = _head.localRotation;
        if (_vrm.TryGetBoneTransform(HumanBodyBones.Neck, out _neck)) _neckBase = _neck.localRotation;
    }

    void Start()
    {
        if (_vrm == null) return;
        if (lookAtCamera && Camera.main != null)
        {
            _vrm.LookAtTargetType = VRM10ObjectLookAt.LookAtTargetTypes.SpecifiedTransform;
            _vrm.LookAtTarget = Camera.main.transform;
        }
    }

    /// <summary>Idempotent: rotates both upper arms so they hang down, angled outward by angleFromDown degrees.</summary>
    public static void ApplyArmsDown(Vrm10Instance vrm, float angleFromDown = 20f)
    {
        if (vrm == null) return;
        Pose(vrm, HumanBodyBones.LeftUpperArm, HumanBodyBones.LeftLowerArm, angleFromDown);
        Pose(vrm, HumanBodyBones.RightUpperArm, HumanBodyBones.RightLowerArm, angleFromDown);
    }

    static void Pose(Vrm10Instance vrm, HumanBodyBones upper, HumanBodyBones lower, float angle)
    {
        if (!vrm.TryGetBoneTransform(upper, out var u) || !vrm.TryGetBoneTransform(lower, out var l)) return;
        Vector3 dir = (l.position - u.position).normalized;
        float side = Vector3.Dot(dir, vrm.transform.right) >= 0 ? 1f : -1f;
        float a = angle * Mathf.Deg2Rad;
        Vector3 target = (vrm.transform.right * (side * Mathf.Sin(a)) - vrm.transform.up * Mathf.Cos(a)).normalized;
        u.rotation = Quaternion.FromToRotation(dir, target) * u.rotation;
    }

    // max VRM preset weight while speaking (idle allows 0.8)
    const float CapHappy = 0.35f, CapSad = 0.4f, CapAngry = 0.35f, CapSurprised = 0.25f, CapRelaxed = 0.5f;
    const string RawEyeFun = "Fcl_EYE_Fun", RawBrwJoy = "Fcl_BRW_Joy", RawEyeAngry = "Fcl_EYE_Angry", RawBrwAngry = "Fcl_BRW_Angry",
        RawEyeSorrow = "Fcl_EYE_Sorrow", RawBrwSorrow = "Fcl_BRW_Sorrow", RawEyeSurp = "Fcl_EYE_Surprised", RawBrwSurp = "Fcl_BRW_Surprised", RawEyeSpread = "Fcl_EYE_Spread";
    SkinnedMeshRenderer _face; readonly System.Collections.Generic.Dictionary<string, int> _rawIdx = new System.Collections.Generic.Dictionary<string, int>();
    void SetRaw(string name, float w01)
    {
        if (_face == null)
        {
            foreach (var r in _vrm.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                if (r.sharedMesh != null && r.sharedMesh.GetBlendShapeIndex("Fcl_EYE_Fun") >= 0) { _face = r; break; }
            if (_face == null) { _face = null; return; }
        }
        if (!_rawIdx.TryGetValue(name, out int i)) { i = _face.sharedMesh.GetBlendShapeIndex(name); _rawIdx[name] = i; }
        if (i >= 0) _face.SetBlendShapeWeight(i, Mathf.Clamp01(w01) * 100f);
    }

    public void SetEmotion(string name, float intensity)
    {
        _emo = string.IsNullOrEmpty(name) ? "neutral" : name.ToLowerInvariant();
        _emoTarget = Mathf.Clamp01(intensity);
    }
    public void SetMouth(float open) { _mouthTarget = Mathf.Clamp01(open); }
    public void SetSpeaking(float energy) { _speakTarget = Mathf.Clamp01(energy); }
    public void SetBlink(float amount) { _blinkTarget = Mathf.Clamp01(amount); }

    void Update()
    {
        if (_vrm == null) return;
        float k = 1f - Mathf.Exp(-smoothing * Time.deltaTime);
        if (_emo != _shownEmo)
        {
            // fade the old emotion out, then switch
            _emoCur = Mathf.Lerp(_emoCur, 0f, k);
            if (_emoCur < 0.02f) { _emoCur = 0f; _shownEmo = _emo; }
        }
        else _emoCur = Mathf.Lerp(_emoCur, _emoTarget, k);
        _mouthCur = Mathf.Lerp(_mouthCur, _mouthTarget, k);
        _blinkCur = Mathf.Lerp(_blinkCur, _blinkTarget, k);
        _speakCur = Mathf.Lerp(_speakCur, _speakTarget, 1f - Mathf.Exp(-8f * Time.deltaTime));

        float t = Time.time;
        float happy = 0, sad = 0, angry = 0, surprised = 0, relaxed = 0;
        switch (_shownEmo)
        {
            case "happy": happy = _emoCur; break;
            case "sad": sad = _emoCur; break;
            case "angry": angry = _emoCur; break;
            case "surprised": surprised = _emoCur; break;
            case "embarrassed": relaxed = _emoCur; happy = 0.35f * _emoCur; break;
        }
        // micro-expressions while talking: slow smile / brow variation (only on top of a weak emotion)
        float n1 = Mathf.PerlinNoise(t * 0.45f, _seed), n2 = Mathf.PerlinNoise(t * 0.7f, _seed + 17f);
        float calm = 1f - Mathf.Clamp01(_emoCur * 1.5f);
        happy = Mathf.Max(happy, _speakCur * calm * (0.10f + 0.28f * n1));
        surprised = Mathf.Max(surprised, _speakCur * calm * 0.14f * Mathf.Clamp01((n2 - 0.45f) * 4f));
        relaxed = Mathf.Max(relaxed, _speakCur * calm * 0.12f * (1f - n1));

        // vowel variation: a new vowel shape at every syllable (mouth closing -> opening edge)
        if (_mouthCur > 0.28f && !_mouthWasOpen) { _mouthWasOpen = true; _vowel = (_vowel + 1 + Mathf.FloorToInt(Random.value * 3f)) % 5; }
        else if (_mouthCur < 0.12f) _mouthWasOpen = false;
        for (int i = 0; i < 5; i++)
        {
            float tw = i == 0 ? (_vowel == 0 ? 1f : 0.45f) : (i == _vowel ? 0.65f : 0f);
            _vowelW[i] = Mathf.Lerp(_vowelW[i], tw, k);
        }
        float m = _mouthCur;

        // This VRM's emotion presets are whole-face morphs (Fcl_ALL_*: eyes + brows + wide mouth). While the mouth is lip-synced
        // cap them so they don't fight the vowel shapes; the eye/brow part is driven separately by raw morphs below (uncapped).
        float sp2 = _speakCur;
        happy = Mathf.Min(happy, Mathf.Lerp(0.6f, CapHappy, sp2)); // Joy preset opens the mouth even at rest
        sad = Mathf.Min(sad, Mathf.Lerp(0.8f, CapSad, sp2));
        angry = Mathf.Min(angry, Mathf.Lerp(0.8f, CapAngry, sp2));
        surprised = Mathf.Min(surprised, Mathf.Lerp(0.8f, CapSurprised, sp2));
        relaxed = Mathf.Min(relaxed, Mathf.Lerp(0.8f, CapRelaxed, sp2));
        float e = _emoCur;
        float eyeFun = 0, brwJoy = 0, eyeAngry = 0, brwAngry = 0, eyeSorrow = 0, brwSorrow = 0, eyeSurp = 0, brwSurp = 0, eyeSpread = 0;
        switch (_shownEmo)
        {
            case "happy": eyeFun = 0f; brwJoy = 0.8f * e; eyeSpread = 1f * e; eyeSurp = 0.7f * e; break; // wide bright eyes: little ^^ squint
            case "embarrassed": eyeFun = 0.4f * e; brwSorrow = 0.3f * e; break;
            case "angry": eyeAngry = 0.9f * e; brwAngry = 1f * e; break;
            case "sad": eyeSorrow = 0.9f * e; brwSorrow = 1f * e; break;
            case "surprised": eyeSurp = 0.8f * e; brwSurp = 1f * e; break;
        }
        SetRaw(RawEyeFun, eyeFun); SetRaw(RawBrwJoy, brwJoy); SetRaw(RawEyeAngry, eyeAngry); SetRaw(RawBrwAngry, brwAngry);
        SetRaw(RawEyeSorrow, eyeSorrow); SetRaw(RawBrwSorrow, brwSorrow); SetRaw(RawEyeSurp, eyeSurp); SetRaw(RawBrwSurp, brwSurp); SetRaw(RawEyeSpread, eyeSpread);

        var ex = _vrm.Runtime.Expression;
        ex.SetWeight(ExpressionKey.Happy, happy);
        ex.SetWeight(ExpressionKey.Sad, sad);
        ex.SetWeight(ExpressionKey.Angry, angry);
        ex.SetWeight(ExpressionKey.Surprised, surprised);
        ex.SetWeight(ExpressionKey.Relaxed, relaxed);
        ex.SetWeight(ExpressionKey.Aa, m * _vowelW[0]);
        ex.SetWeight(ExpressionKey.Ih, m * _vowelW[1]);
        ex.SetWeight(ExpressionKey.Ou, m * _vowelW[2]);
        ex.SetWeight(ExpressionKey.Ee, m * _vowelW[3]);
        ex.SetWeight(ExpressionKey.Oh, m * _vowelW[4]);
        ex.SetWeight(ExpressionKey.Blink, _blinkCur);

        // head / neck: nod with speech energy, tilt with emotion
        if (_head != null)
        {
            float sp = _speakCur;
            float pitch = sp * (Mathf.Sin(t * 2.3f + _seed) * 1.8f + m * 3.5f) + (_shownEmo == "sad" ? 5f * _emoCur : 0f) - (_shownEmo == "surprised" ? 3f * _emoCur : 0f);
            float yaw = sp * Mathf.Sin(t * 0.85f + _seed) * 3f;
            float roll = sp * Mathf.Sin(t * 0.6f) * 2f + (_shownEmo == "happy" ? 3.5f * _emoCur : 0f) + (_shownEmo == "embarrassed" ? -4f * _emoCur : 0f);
            var off = Quaternion.Euler(pitch, yaw, roll);
            _head.localRotation = _headBase * off;
            if (_neck != null) _neck.localRotation = _neckBase * Quaternion.Euler(pitch * 0.4f, yaw * 0.3f, roll * 0.3f);
        }
    }
}
