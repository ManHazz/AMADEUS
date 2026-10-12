using UnityEngine;

/// <summary>
/// Mouth driver. Level comes from the samples fed to the mixer (AudioStreamPlayer.ConsumeLevel),
/// not AudioSource.GetOutputData (zeros when the source/listener config is unusual or in batch mode).
/// </summary>
public class LipSyncDriver : MonoBehaviour
{
    [SerializeField] float gain = 7f;
    [SerializeField] float gate = 0.012f;
    [SerializeField] float attack = 30f;
    [SerializeField] float release = 14f;

    IAvatarExpressions _avatar;
    AudioStreamPlayer _player;
    float _mouth, _speaking, _manual = -1f;

    /// <summary>Last mouth value sent to the avatar (HUD/debug).</summary>
    public float Mouth { get { return _mouth; } }
    /// <summary>Set 0..1 to force the mouth (debug); negative = audio driven.</summary>
    public float ManualMouth { get { return _manual; } set { _manual = value; } }

    public void Init(AudioStreamPlayer player, IAvatarExpressions avatar) { _player = player; _avatar = avatar; }

    void Update()
    {
        if (_avatar == null) return;
        float dt = Time.deltaTime;
        float target = 0f;
        if (_player != null)
        {
            bool had;
            float rms = _player.ConsumeLevel(dt, out had);
            if (rms > gate) target = Mathf.Clamp01((rms - gate) * gain);
        }
        if (_manual >= 0f) target = _manual;
        float k = target > _mouth ? attack : release;
        _mouth = Mathf.Lerp(_mouth, target, 1f - Mathf.Exp(-k * dt));
        // speaking energy: fast up, slow down (about 0.6 s tail) so micro-expressions don't flicker between words
        float e = Mathf.Clamp01(target * 1.5f);
        _speaking = Mathf.Lerp(_speaking, e, 1f - Mathf.Exp(-(e > _speaking ? 10f : 1.6f) * dt));
        _avatar.SetMouth(_mouth);
        _avatar.SetSpeaking(_speaking);
    }

    public float Speaking { get { return _speaking; } }
}
