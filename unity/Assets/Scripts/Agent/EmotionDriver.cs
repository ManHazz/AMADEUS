using UnityEngine;

public class EmotionDriver : MonoBehaviour
{
    [SerializeField] float smoothing = 7f;
    [SerializeField] float tailSeconds = 2f; // stay on the emotion this long after speech ends, then fade to neutral

    IAvatarExpressions _avatar;
    string _name = "neutral", _shown = "neutral";
    float _target, _current, _active;

    public string Current { get { return _shown; } }
    public float Weight { get { return _current; } }

    public void Init(IAvatarExpressions avatar) { _avatar = avatar; }

    public void Set(string name, float intensity)
    {
        _name = string.IsNullOrEmpty(name) ? "neutral" : name;
        _target = _name == "neutral" ? 0f : Mathf.Clamp01(Mathf.Max(intensity, KeywordEmotionClassifier.MinIntensity));
        _active = Time.time + tailSeconds;
    }

    /// <summary>Call every frame while the character is speaking so the emotion is held until speech ends.</summary>
    public void KeepAlive() { _active = Mathf.Max(_active, Time.time + tailSeconds); }

    void Update()
    {
        if (_avatar == null) return;
        float t = Time.time <= _active ? _target : 0f;
        if (_name != _shown)
        {
            // fade out the old emotion before showing the new one
            _current = Mathf.MoveTowards(_current, 0f, 2f * smoothing * Time.deltaTime);
            if (_current <= 0.01f) { _shown = _name; _current = 0f; }
        }
        else _current = Mathf.Lerp(_current, t, 1f - Mathf.Exp(-smoothing * Time.deltaTime));
        _avatar.SetEmotion(_shown, _current);
    }
}
