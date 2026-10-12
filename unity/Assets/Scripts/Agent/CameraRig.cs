using UnityEngine;
using UniVRM10;

/// <summary>Camera presets: 1 = full body, 2 = face close-up (default), 3 = bust. Tab cycles. Static pose computed once; smooth lerp only when switching preset. followHead is opt-in.</summary>
[RequireComponent(typeof(Camera))]
public class CameraRig : MonoBehaviour
{
    public enum Preset { FullBody = 0, Face = 1, Bust = 2 }

    [SerializeField] Preset startPreset = Preset.Face;
    [SerializeField] Transform avatarRoot;      // optional; found at runtime
    [SerializeField] float transitionSpeed = 5f;
    [SerializeField] float followSpeed = 6f;    // only used when followHead
    [SerializeField] bool followHead = false;   // default: camera is fully static

    Camera _cam;
    Transform _head, _eyeL, _eyeR, _root;
    Preset _preset;
    Vector3 _focus; bool _hasFocus, _snapped;

    public Preset Current { get { return _preset; } }
    public bool FollowHead { get { return followHead; } set { followHead = value; } }

    void Awake()
    {
        _cam = GetComponent<Camera>();
        _preset = startPreset;
    }

    public void SetPreset(Preset p) { _preset = p; }
    public void SetPreset(int p) { _preset = (Preset)Mathf.Clamp(p, 0, 2); }

    /// <summary>Instantly jump to the current preset (tests/screenshots).</summary>
    public void Snap()
    {
        if (_cam == null) _cam = GetComponent<Camera>();
        if (!Resolve()) return;
        Compute(out var pos, out var rot, out var fov);
        transform.SetPositionAndRotation(pos, rot); _cam.fieldOfView = fov; _cam.nearClipPlane = 0.03f;
    }

    bool Resolve()
    {
        if (_root == null)
        {
            if (avatarRoot != null) _root = avatarRoot;
            else { var va = FindFirstObjectByType<VrmAvatar>(); if (va != null) _root = va.transform; }
            if (_root == null) { var go = GameObject.Find("AgentRoot"); if (go != null) _root = go.transform; }
        }
        if (_root == null) return false;
        if (_head == null)
        {
            var vrm = _root.GetComponentInChildren<Vrm10Instance>();
            if (vrm != null)
            {
                vrm.TryGetBoneTransform(HumanBodyBones.Head, out _head);
                vrm.TryGetBoneTransform(HumanBodyBones.LeftEye, out _eyeL);
                vrm.TryGetBoneTransform(HumanBodyBones.RightEye, out _eyeR);
            }
            if (_head == null)
            {
                var an = _root.GetComponentInChildren<Animator>();
                if (an != null && an.isHuman)
                {
                    _head = an.GetBoneTransform(HumanBodyBones.Head);
                    _eyeL = an.GetBoneTransform(HumanBodyBones.LeftEye);
                    _eyeR = an.GetBoneTransform(HumanBodyBones.RightEye);
                }
            }
        }
        return true;
    }

    Vector3 FaceCenter()
    {
        if (_eyeL != null && _eyeR != null) return (_eyeL.position + _eyeR.position) * 0.5f;
        if (_head != null) return _head.position + _root.up * 0.07f;
        return _root.position + _root.up * 1.45f;
    }

    void Compute(out Vector3 pos, out Quaternion rot, out float fov)
    {
        Vector3 fwd = _root.forward; fwd.y = 0f;
        if (fwd.sqrMagnitude < 1e-4f) fwd = Vector3.forward;
        fwd.Normalize();
        if (!_hasFocus) { _focus = FaceCenter(); _hasFocus = true; }
        Vector3 f;
        switch (_preset)
        {
            case Preset.FullBody:
                f = _root.position + Vector3.up * 0.9f;
                pos = f + fwd * 3.2f + Vector3.up * 0.15f; fov = 40f;
                break;
            case Preset.Bust:
                f = _focus + Vector3.down * 0.2f;
                pos = f + fwd * 1.15f + Vector3.up * 0.08f; fov = 32f;
                break;
            default:
                // eyes sit ~30% from the top so the mouth/chin stay well above the bottom subtitle panel (<= 28% of screen)
                f = _focus + Vector3.down * 0.075f;
                pos = _focus + fwd * 0.68f + Vector3.up * 0.02f; fov = 30f;
                break;
        }
        rot = Quaternion.LookRotation(f - pos, Vector3.up);
    }

    void Update()
    {
        if (Input.GetKeyDown(KeyCode.Alpha1)) _preset = Preset.FullBody;
        else if (Input.GetKeyDown(KeyCode.Alpha2)) _preset = Preset.Face;
        else if (Input.GetKeyDown(KeyCode.Alpha3)) _preset = Preset.Bust;
        else if (Input.GetKeyDown(KeyCode.Tab)) _preset = (Preset)(((int)_preset + 1) % 3);
    }

    void LateUpdate()
    {
        if (!Resolve()) return;
        if (!_hasFocus) { _focus = FaceCenter(); _hasFocus = true; }
        if (!_snapped) { _snapped = true; Snap(); return; }
        if (followHead) _focus = Vector3.Lerp(_focus, FaceCenter(), 1f - Mathf.Exp(-followSpeed * Time.deltaTime));
        Compute(out var pos, out var rot, out var fov);
        float k = 1f - Mathf.Exp(-transitionSpeed * Time.deltaTime);
        transform.position = Vector3.Lerp(transform.position, pos, k);
        transform.rotation = Quaternion.Slerp(transform.rotation, rot, k);
        _cam.fieldOfView = Mathf.Lerp(_cam.fieldOfView, fov, k);
        _cam.nearClipPlane = 0.03f;
    }
}
