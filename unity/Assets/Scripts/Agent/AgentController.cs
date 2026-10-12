using UnityEngine;

public class AgentController : MonoBehaviour
{
    [SerializeField] AgentHttpClient client;
    [SerializeField] MicRecorder mic;
    [SerializeField] float pendingPollSeconds = 4f;
    [SerializeField] AudioStreamPlayer audioPlayer;
    [SerializeField] MonoBehaviour avatarBehaviour; // must implement IAvatarExpressions
    [SerializeField] LipSyncDriver lipSync;
    [SerializeField] EmotionDriver emotion;
    [SerializeField] IdleLife idle;

    public void DisablePolling() { pendingPollSeconds = 0f; }
    public AgentHttpClient Client { get { return client; } }
    public AudioStreamPlayer AudioPlayer { get { return audioPlayer; } }
    public LipSyncDriver LipSync { get { return lipSync; } }
    public EmotionDriver Emotion { get { return emotion; } }
    public IAvatarExpressions Avatar { get; private set; }
    public TranscriptHud Hud { get; private set; }
    public CameraRig Rig { get; private set; }
    public string State { get; private set; } = "idle";
    public string LastText { get; private set; } = "";

    static T Get<T>(GameObject g) where T : Component
    {
        var c = g.GetComponent<T>();
        return c != null ? c : g.AddComponent<T>();
    }

    void Awake()
    {
        if (client == null) client = Get<AgentHttpClient>(gameObject);
        if (mic == null) mic = Get<MicRecorder>(gameObject);
        if (audioPlayer == null)
        {
            Get<AudioSource>(gameObject);
            audioPlayer = Get<AudioStreamPlayer>(gameObject);
        }
        IAvatarExpressions avatar = avatarBehaviour as IAvatarExpressions;
        if (avatar == null) avatar = GetComponentInChildren<IAvatarExpressions>(true);
        if (avatar == null) avatar = FindFirstObjectByType<VrmAvatar>();
        if (avatar == null)
        {
            // a VRM somewhere under/near the agent but no driver yet: add one
            var vrm = GetComponentInChildren<UniVRM10.Vrm10Instance>(true);
            if (vrm != null) avatar = gameObject.AddComponent<VrmAvatar>();
        }
        if (avatar == null) { Debug.LogWarning("AgentController: no VRM avatar found, using PlaceholderAvatar"); avatar = gameObject.AddComponent<PlaceholderAvatar>(); }
        else Debug.Log("AgentController: avatar = " + avatar.GetType().Name);
        Avatar = avatar;

        if (lipSync == null) lipSync = Get<LipSyncDriver>(gameObject);
        if (emotion == null) emotion = Get<EmotionDriver>(gameObject);
        if (idle == null) idle = Get<IdleLife>(gameObject);

        var subs = FindFirstObjectByType<SubtitleView>();
        if (subs == null) subs = new GameObject("SubtitleView").AddComponent<SubtitleView>();
        subs.Init(client, this);

        Hud = FindFirstObjectByType<TranscriptHud>();
        if (Hud == null) Hud = new GameObject("TranscriptHud").AddComponent<TranscriptHud>();
        Hud.Init(client, this, mic, lipSync);

        var cam = Camera.main;
        Rig = FindFirstObjectByType<CameraRig>();
        if (Rig == null && cam != null) Rig = cam.gameObject.AddComponent<CameraRig>();
        var dbg = FindFirstObjectByType<AgentDebugPanel>();
        if (dbg == null) dbg = new GameObject("AgentDebugPanel").AddComponent<AgentDebugPanel>();
        dbg.Init(emotion, lipSync, Rig, Hud, mic);

        lipSync.Init(audioPlayer, avatar);
        emotion.Init(avatar);
        idle.Init(avatar);
    }

    bool _awaiting;
    float _nextPoll;
    string _lastEmotionText = "";

    void OnEnable()
    {
        client.OnAudio += HandleAudio;
        client.OnRequestStart += HandleRequestStart;
        client.OnMeta += HandleMeta;
        client.OnSegment += HandleSegment;
        client.OnSegmentTranslation += HandleSegmentTr;
        client.OnError += HandleError;
        client.OnDone += HandleDone;
        mic.OnRecordStart += HandleRecordStart;
        mic.OnRecorded += HandleRecorded;
        mic.OnTooShort += HandleTooShort;
    }

    void OnDisable()
    {
        client.OnAudio -= HandleAudio;
        client.OnRequestStart -= HandleRequestStart;
        client.OnMeta -= HandleMeta;
        client.OnSegment -= HandleSegment;
        client.OnSegmentTranslation -= HandleSegmentTr;
        client.OnError -= HandleError;
        client.OnDone -= HandleDone;
        mic.OnRecordStart -= HandleRecordStart;
        mic.OnRecorded -= HandleRecorded;
        mic.OnTooShort -= HandleTooShort;
    }

    readonly SegmentAudioTracker _tracker = new SegmentAudioTracker();
    /// <summary>Id of the spoken segment whose audio is playing now, or -1.</summary>
    public int PlayingSegmentId { get { return audioPlayer != null && audioPlayer.IsAudible ? _tracker.Current(audioPlayer.ConsumedSamples) : -1; } }

    void HandleAudio(float[] samples, int rate)
    {
        _tracker.OnAudioChunk(audioPlayer.WrittenSamples);
        audioPlayer.Enqueue(samples, rate);
    }

    void HandleRequestStart() { _tracker.Clear(); }

    void HandleRecordStart()
    {
        _tracker.Clear();
        audioPlayer.Flush(); // barge-in
        State = "listening";
    }

    void HandleRecorded(byte[] wav)
    {
        State = "thinking";
        _awaiting = true;
        mic.Enabled = false;
        client.Converse(wav);
    }

    void HandleTooShort() { State = "idle"; }

    void HandleMeta(AgentMeta m)
    {
        if (!string.IsNullOrEmpty(m.reply)) LastText = m.reply;
        if (!string.IsNullOrEmpty(m.reply) && m.reply != _lastEmotionText)
        {
            _lastEmotionText = m.reply;
            ApplyEmotion(KeywordEmotionClassifier.Classify(m.reply));
        }
    }

    // The server sends no emotion events and the JA text rarely carries keywords, so also classify
    // the English translation (segment_tr), the segment text itself and silent tips.
    void HandleSegment(SegmentEvent s)
    {
        if (s == null) return;
        _tracker.OnSegment(s.id, s.spoken && s.lang != null && !(s.lang == "en" && !s.spoken));
        ApplyEmotion(KeywordEmotionClassifier.ClassifyAny(s.text, s.tr));
    }

    void HandleSegmentTr(SegmentTranslationEvent e)
    {
        if (e == null) return;
        ApplyEmotion(KeywordEmotionClassifier.Classify(e.tr));
    }

    void ApplyEmotion(KeywordEmotionClassifier.Result r)
    {
        if (r.Intensity <= 0f || r.Name == "neutral") return; // neutral never overrides; emotion fades after speech
        emotion.Set(r.Name, r.Intensity);
    }

    void HandleError(string msg) { Debug.LogWarning("Agent error: " + msg); }

    void HandleDone()
    {
        if (client.IsBusy) return; // a queued request is starting
        _awaiting = false;
        mic.Enabled = true;
    }

    void Update()
    {
        if (mic.IsRecording) { State = "listening"; return; }
        bool playing = audioPlayer.Buffered > 0 || audioPlayer.IsAudible;
        if (playing) { State = "speaking"; }
        else if (_awaiting) State = "thinking";
        else State = "idle";

        if (State == "speaking" || lipSync.Speaking > 0.05f) emotion.KeepAlive();

        if (State == "idle" && pendingPollSeconds > 0 && Time.time >= _nextPoll)
        {
            _nextPoll = Time.time + pendingPollSeconds;
            if (client.IsConfigured) client.Pending();
        }
    }
}
