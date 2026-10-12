using System;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

public class AgentSocket : MonoBehaviour
{
    [SerializeField] string url = "ws://localhost:8765";
    [SerializeField] string token = "";
    [SerializeField] float minBackoff = 0.5f;
    [SerializeField] float maxBackoff = 10f;

    public event Action<float[], int> OnAudio;
    public event Action<string> OnText;
    public event Action<string, float> OnEmotion;
    public event Action<string> OnState;
    public event Action OnEnd;
    public event Action OnConnected;
    public event Action OnDisconnected;

    public bool IsConnected { get; private set; }

    enum Evt { Connected, Disconnected, Message }
    struct Item { public Evt kind; public string json; }

    readonly ConcurrentQueue<Item> _queue = new ConcurrentQueue<Item>();
    CancellationTokenSource _cts;
    ClientWebSocket _ws;

    void OnEnable()
    {
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        string u = url, tk = token;
        Task.Run(() => Loop(u, tk, ct));
    }

    void OnDisable()
    {
        if (_cts != null) _cts.Cancel();
        try { if (_ws != null) _ws.Abort(); } catch { }
        _cts = null;
    }

    async Task Loop(string u, string tk, CancellationToken ct)
    {
        float backoff = minBackoff;
        while (!ct.IsCancellationRequested)
        {
            var ws = new ClientWebSocket();
            _ws = ws;
            bool wasConnected = false;
            try
            {
                await ws.ConnectAsync(new Uri(u), ct);
                wasConnected = true;
                backoff = minBackoff;
                if (!string.IsNullOrEmpty(tk))
                    await SendRaw(ws, AgentMessage.BuildHello(tk), ct);
                _queue.Enqueue(new Item { kind = Evt.Connected });
                await ReceiveLoop(ws, ct);
            }
            catch (Exception)
            {
                // swallowed; reconnect below
            }
            finally
            {
                try { ws.Abort(); ws.Dispose(); } catch { }
            }
            if (wasConnected) _queue.Enqueue(new Item { kind = Evt.Disconnected });
            if (ct.IsCancellationRequested) break;
            try { await Task.Delay(TimeSpan.FromSeconds(backoff), ct); } catch { break; }
            backoff = Mathf.Min(backoff * 2f, maxBackoff);
        }
    }

    async Task ReceiveLoop(ClientWebSocket ws, CancellationToken ct)
    {
        var buf = new byte[64 * 1024];
        var sb = new StringBuilder();
        while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            sb.Length = 0;
            WebSocketReceiveResult r;
            do
            {
                r = await ws.ReceiveAsync(new ArraySegment<byte>(buf), ct);
                if (r.MessageType == WebSocketMessageType.Close) return;
                sb.Append(Encoding.UTF8.GetString(buf, 0, r.Count));
            } while (!r.EndOfMessage);
            _queue.Enqueue(new Item { kind = Evt.Message, json = sb.ToString() });
        }
    }

    static Task SendRaw(ClientWebSocket ws, string json, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        return ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct);
    }

    /// <summary>Send a raw JSON text message (fire and forget).</summary>
    public void Send(string json)
    {
        var ws = _ws;
        if (ws == null || ws.State != WebSocketState.Open || _cts == null) return;
        var ct = _cts.Token;
        Task.Run(async () => { try { await SendRaw(ws, json, ct); } catch { } });
    }

    void Update()
    {
        int budget = 64;
        while (budget-- > 0 && _queue.TryDequeue(out var it))
        {
            switch (it.kind)
            {
                case Evt.Connected: IsConnected = true; OnConnected?.Invoke(); break;
                case Evt.Disconnected: IsConnected = false; OnDisconnected?.Invoke(); break;
                case Evt.Message: Dispatch(it.json); break;
            }
        }
    }

    void Dispatch(string json)
    {
        var m = AgentMessage.Parse(json);
        if (m == null) return;
        switch (m.type)
        {
            case "audio": OnAudio?.Invoke(AgentMessage.DecodePcm16(m.pcm16), m.sampleRate > 0 ? m.sampleRate : 24000); break;
            case "text": OnText?.Invoke(m.text); break;
            case "emotion": OnEmotion?.Invoke(m.name, Mathf.Clamp01(m.intensity)); break;
            case "state": OnState?.Invoke(m.value); break;
            case "end": OnEnd?.Invoke(); break;
        }
    }
}
