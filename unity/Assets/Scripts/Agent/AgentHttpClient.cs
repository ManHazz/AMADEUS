using System;
using System.Collections;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

[Serializable]
public class AgentConfig
{
    public string url = "";
    public string token = "";

    public static string FilePath { get { return Path.GetFullPath(Path.Combine(Application.dataPath, "..", "agent.local.json")); } }

    public static AgentConfig Load()
    {
        try
        {
            var p = FilePath;
            if (File.Exists(p))
            {
                var c = JsonUtility.FromJson<AgentConfig>(File.ReadAllText(p));
                if (c != null) return c;
            }
        }
        catch (Exception e) { Debug.LogWarning("agent.local.json unreadable: " + e.Message); }
        return new AgentConfig();
    }
}

[Serializable] public class AgentMeta { public string user; public string reply; public bool done; }
[Serializable] public class AgentEvent { public string kind; public string question; }

/// <summary>HTTP client for the Voice-Agent /api/converse frame-stream protocol.</summary>
public class AgentHttpClient : MonoBehaviour
{
    [Tooltip("Overrides agent.local.json when non-empty")]
    [SerializeField] string urlOverride = "";
    [Tooltip("Overrides agent.local.json when non-empty. Prefer the file; never commit a token.")]
    [SerializeField] string tokenOverride = "";
    [SerializeField] int timeoutSeconds = 120;

    public event Action<AgentMeta> OnMeta;
    public event Action<float[], int> OnAudio;
    public event Action<AgentEvent> OnEvent;
    public event Action<SegmentEvent> OnSegment;
    public event Action<SegmentTranslationEvent> OnSegmentTranslation;
    public event Action<string> OnError;
    public event Action OnDone;
    public event Action OnRequestStart; // fired when a request (converse or pending) begins

    public bool IsBusy { get; private set; }
    public string SessionId { get; private set; }
    public bool IsConfigured { get { return !string.IsNullOrEmpty(_url); } }

    string _url = "", _token = "";
    byte[] _queued;

    void Awake()
    {
        SessionId = Guid.NewGuid().ToString();
        var c = AgentConfig.Load();
        _url = (string.IsNullOrEmpty(urlOverride) ? c.url : urlOverride) ?? "";
        _token = (string.IsNullOrEmpty(tokenOverride) ? c.token : tokenOverride) ?? "";
        _url = _url.Trim().TrimEnd('/');
        if (!IsConfigured) Debug.LogWarning("AgentHttpClient: no url (set agent.local.json or Inspector override)");
    }

    // Test / debug injection: runs the same event path as a server frame.
    public void InjectMeta(AgentMeta m) { OnMeta?.Invoke(m); }
    public void InjectAudio(float[] samples, int rate) { OnAudio?.Invoke(samples, rate); }
    public void InjectSegment(SegmentEvent e) { OnSegment?.Invoke(e); }
    public void InjectSegmentTranslation(SegmentTranslationEvent e) { OnSegmentTranslation?.Invoke(e); }

    public void Converse(byte[] wav)
    {
        if (IsBusy) { _queued = wav; return; } // sent when current request ends
        if (!IsConfigured) { OnError?.Invoke("agent url not configured"); OnDone?.Invoke(); return; }
        var form = new WWWForm();
        form.AddBinaryData("audio", wav, "clip.wav", "audio/wav");
        StartCoroutine(Run(UnityWebRequest.Post(_url + "/api/converse", form)));
    }

    /// <summary>Fetch a finished background answer (204 -> OnDone with no frames).</summary>
    public void Pending()
    {
        if (IsBusy || !IsConfigured) return;
        StartCoroutine(Run(UnityWebRequest.Get(_url + "/api/pending")));
    }

    public void ResetConversation()
    {
        if (!IsConfigured) return;
        StartCoroutine(RunReset());
    }

    IEnumerator RunReset()
    {
        using (var req = UnityWebRequest.PostWwwForm(_url + "/api/reset", ""))
        {
            Headers(req);
            yield return req.SendWebRequest();
            if (req.result != UnityWebRequest.Result.Success) OnError?.Invoke("reset failed: " + req.error);
        }
    }

    void Headers(UnityWebRequest req)
    {
        if (!string.IsNullOrEmpty(_token)) req.SetRequestHeader("X-Access-Token", _token);
        req.SetRequestHeader("X-Session-Id", SessionId);
        req.timeout = timeoutSeconds;
    }

    IEnumerator Run(UnityWebRequest req)
    {
        IsBusy = true;
        OnRequestStart?.Invoke();
        var handler = new FrameDownloadHandler(req);
        handler.Meta += m => OnMeta?.Invoke(m);
        handler.Audio += (s, r) => OnAudio?.Invoke(s, r);
        handler.Evt += e => OnEvent?.Invoke(e);
        handler.Seg += e => OnSegment?.Invoke(e);
        handler.SegTr += e => OnSegmentTranslation?.Invoke(e);
        handler.Error += e => OnError?.Invoke(e);
        req.downloadHandler = handler;
        Headers(req);
        using (req)
        {
            yield return req.SendWebRequest();
            if (req.result != UnityWebRequest.Result.Success && !handler.ParseFailed)
                OnError?.Invoke(handler.ErrorMessage(req.responseCode, req.error));
        }
        IsBusy = false;
        var q = _queued; _queued = null;
        if (q != null) { Converse(q); yield break; }
        OnDone?.Invoke();
    }

    /// <summary>Receives body chunks as they arrive (main thread) and feeds the frame parser.</summary>
    class FrameDownloadHandler : DownloadHandlerScript
    {
        readonly UnityWebRequest _req;
        readonly FrameParser _parser = new FrameParser();
        readonly MemoryStream _errBody = new MemoryStream();
        public bool ParseFailed;
        public event Action<AgentMeta> Meta;
        public event Action<float[], int> Audio;
        public event Action<AgentEvent> Evt;
        public event Action<SegmentEvent> Seg;
        public event Action<SegmentTranslationEvent> SegTr;
        public event Action<string> Error;

        public FrameDownloadHandler(UnityWebRequest req) : base(new byte[64 * 1024])
        {
            _req = req;
            _parser.OnFrame += OnFrame;
        }

        protected override bool ReceiveData(byte[] data, int dataLength)
        {
            if (data == null || dataLength <= 0) return true;
            if (_req.responseCode >= 400)
            {
                if (_errBody.Length < 8192) _errBody.Write(data, 0, dataLength);
                return true;
            }
            try { _parser.Feed(data, 0, dataLength); }
            catch (Exception e)
            {
                ParseFailed = true;
                Error?.Invoke("bad stream: " + e.Message);
                return false;
            }
            return true;
        }

        public string ErrorMessage(long code, string netError)
        {
            if (_errBody.Length > 0)
            {
                try
                {
                    var s = Encoding.UTF8.GetString(_errBody.ToArray());
                    var e = JsonUtility.FromJson<ErrBody>(s);
                    if (e != null && !string.IsNullOrEmpty(e.error)) return e.error;
                    if (e != null && !string.IsNullOrEmpty(e.detail)) return e.detail;
                }
                catch (Exception) { }
            }
            return code > 0 ? "server error " + code : netError;
        }

        [Serializable] class ErrBody { public string error; public string detail; }

        void OnFrame(byte type, byte[] payload)
        {
            try
            {
                switch (type)
                {
                    case FrameParser.Meta:
                        var m = JsonUtility.FromJson<AgentMeta>(Encoding.UTF8.GetString(payload));
                        if (m != null) Meta?.Invoke(m);
                        break;
                    case FrameParser.Audio:
                        float[] s; int r;
                        if (WavDecoder.TryDecode(payload, out s, out r)) Audio?.Invoke(s, r);
                        else Error?.Invoke("undecodable WAV chunk");
                        break;
                    case FrameParser.Event:
                        var json = Encoding.UTF8.GetString(payload);
                        var ev = JsonUtility.FromJson<AgentEvent>(json);
                        if (ev != null) Evt?.Invoke(ev);
                        if (ev != null && ev.kind == "segment")
                        {
                            var sg = SegmentParser.ParseSegment(json);
                            if (sg != null) Seg?.Invoke(sg);
                        }
                        else if (ev != null && ev.kind == "segment_tr")
                        {
                            var st = SegmentParser.ParseTranslation(json);
                            if (st != null) SegTr?.Invoke(st);
                        }
                        break;
                }
            }
            catch (Exception e) { Error?.Invoke("frame error: " + e.Message); }
        }
    }
}
