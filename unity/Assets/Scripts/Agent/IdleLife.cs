using UnityEngine;

public class IdleLife : MonoBehaviour
{
    [SerializeField] Transform lookTarget; // defaults to main camera
    [SerializeField] float breathAmount = 0.004f;
    [SerializeField] float breathSpeed = 1.2f;
    [SerializeField] float swayDegrees = 0.4f;
    [SerializeField] float lookSpeed = 4f;

    /// <summary>Test hook: NaN = normal; else -1..1 forces the sway/breath extreme.</summary>
    public float ForceSway = float.NaN;
    IAvatarExpressions _avatar;
    Vector3 _basePos;
    float _nextBlink, _blinkT = -1f;
    const float BlinkDuration = 0.15f;

    public void Init(IAvatarExpressions avatar) { _avatar = avatar; }

    void Start() { _basePos = transform.localPosition; ScheduleBlink(); }

    void ScheduleBlink() { _nextBlink = Time.time + Random.Range(2f, 6f); }

    void Update()
    {
        float t = Time.time;
        transform.localPosition = _basePos + Vector3.up * ((float.IsNaN(ForceSway) ? Mathf.Sin(t * breathSpeed) : ForceSway) * breathAmount);

        var target = lookTarget != null ? lookTarget : (Camera.main != null ? Camera.main.transform : null);
        Quaternion rot = transform.rotation;
        if (target != null)
        {
            var d = target.position - transform.position; d.y = 0f;
            if (d.sqrMagnitude > 1e-4f)
                rot = Quaternion.Slerp(transform.rotation, Quaternion.LookRotation(d), 1f - Mathf.Exp(-lookSpeed * Time.deltaTime));
        }
        var e = rot.eulerAngles; e.z = (float.IsNaN(ForceSway) ? Mathf.Sin(t * 0.7f) : ForceSway) * swayDegrees;
        transform.rotation = Quaternion.Euler(e);

        if (_avatar == null) return;
        if (_blinkT < 0f && t >= _nextBlink) _blinkT = 0f;
        if (_blinkT >= 0f)
        {
            _blinkT += Time.deltaTime;
            float p = _blinkT / BlinkDuration;
            _avatar.SetBlink(p >= 1f ? 0f : 1f - Mathf.Abs(2f * p - 1f));
            if (p >= 1f) { _blinkT = -1f; ScheduleBlink(); }
        }
    }
}
