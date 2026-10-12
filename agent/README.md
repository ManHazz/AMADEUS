# voice-agent

A push-to-talk voice assistant you can reach from a phone browser. Hold a
button, talk, hear a spoken reply. Speech and language models run on your own
hardware; it does **not** need a keyboard or an app install. Each browser
session gets its own isolated conversation, so multiple people (or the same
person on multiple tabs/devices) can use it at once without cross-talk — see
[§2b](#2b-multiple-concurrent-users).

```
 phone browser ──(recorded audio)──▶  FastAPI server
                                        │
              ┌─────────────────────────┼─────────────────────────┐
              ▼                         ▼                         ▼
        faster-whisper            OpenAI-compatible          Kokoro TTS
           (STT)                  chat endpoint            (spoken reply)
                                  e.g. llama.cpp
```

The reply is streamed back **sentence by sentence** as length-prefixed binary
frames, so playback starts while the model is still talking.

## Contents

1. [Quick start](#1-quick-start)
2. [Two agents](#2-two-agents) ([multiple concurrent users](#2b-multiple-concurrent-users))
3. [HTTP API](#3-http-api)
4. [Configuration](#4-configuration)
5. [Files](#5-files)
6. [Troubleshooting](#6-troubleshooting)
7. [Notes & limits](#7-notes--limits)
8. [Future work](#8-future-work)
9. [Testing](#9-testing)

---

## 1. Quick start

**Easiest route: Docker.** It bundles Python, CUDA, cuDNN and all pinned dependencies, so there is no dependency setup. See [DOCKER.md](DOCKER.md) (`cp .env.example .env`, then `docker compose up --build`). The manual install below is the alternative.

**Requires:** Python 3.12, and a running OpenAI-compatible chat endpoint (any
provider works; [llama.cpp](https://github.com/ggml-org/llama.cpp)
`llama-server --jinja` is what this was built against).

```bash
# 1. install (uv recommended — https://docs.astral.sh/uv/)
uv venv .venv --python 3.12
uv pip install --python .venv/bin/python pip -r requirements.txt

# faster-whisper on CUDA also needs the CUDA 12 libs (skip this line for CPU):
uv pip install --python .venv/bin/python nvidia-cublas-cu12 "nvidia-cudnn-cu12>=9,<10"

# 2. configure
cp .env.example .env        # edit BRAIN_URL / BRAIN_MODEL / *_DEVICE as needed

# 3. run
./start.sh                  # detached, waits for /health — use this normally
#  or  ./run.sh              # foreground, for watching logs live
#  or  ./run.sh tunnel        # foreground + a Cloudflare quick tunnel

# 4. (optional) expose it publicly over HTTPS
./tunnel.sh                 # prints a https://*.trycloudflare.com URL
```

No system `ffmpeg` or `espeak-ng` needed — PyAV (bundled) decodes the phone
audio and `espeakng-loader` ships the phoneme lib.

Open the printed URL on your phone. **HTTPS is required** for microphone
access — use the tunnel, or put the server behind your own TLS. The access
token is baked into the page automatically; the server also prints it at
startup (or set `VOICE_AGENT_TOKEN` yourself in `.env`).

**Check it's alive:** `curl http://<host>:5000/health`

**Stop it:**
```bash
pkill -f 'uvicorn server:app'        # voice server
pkill -f 'cloudflared tunnel'        # tunnel, if you started one
```

**If you're also running your own brain** with `brain.sh` (an optional
single-node llama.cpp helper — see [§5](#5-files)), start it *first*:
```bash
MODEL=/path/to/model.gguf ./brain.sh   # then ./start.sh, then ./tunnel.sh
./brain.sh stop
```

---

## 2. Two agents

Routing happens right after speech-to-text, on the transcript:

| | **`chat_agent`** (default) | **`search_agent`** |
|---|---|---|
| when | every turn | you **explicitly ask** — *"search the web for…"*, *"look it up"*, *"google the…"*, *"do a web search for…"* (`search_agent.wants_search()`, one regex) |
| tools | none | `web_search` + `web_fetch` (DuckDuckGo, no API key) |
| shape | one streamed pass, speaks each sentence as it lands (~1 s to first audio for "hello") | returns immediately with a spoken *"…you can keep talking while I search"*; a **background task** runs the tool loop; the answer is delivered later via `GET /api/pending`, which the page polls |

So a plain *"who's the UK prime minister?"* is answered from the model's own
knowledge (possibly stale) — you only get a live web answer when you say so.
Set `ENABLE_WEB_SEARCH=0` to drop `search_agent` entirely.

### 2b. Multiple concurrent users

Each `GET /` mints a fresh session id and bakes it into the page (same mechanism
as the access token); the page sends it back as `X-Session-Id` on every call, and
the server keeps one `pipeline.State` (conversation history + pending web-search
deliveries) per session id, via `pipeline.SessionStore`. Two people — or two
tabs/devices for the same person — talking to the server at once get fully
separate conversations; neither sees the other's transcript, and a search
answer only ever gets delivered back to the session that asked for it. An
idle session is evicted after `SESSION_TTL_SECONDS` (default 1 hour) to keep
memory bounded. Reloading the page always starts a new session — there's no
persistence across reloads by design (see [§8](#8-future-work) for why).

---

## 3. HTTP API

Response bodies for `/api/converse` and `/api/pending` are a stream of frames:

```
frame = 1 byte type | 4 bytes uint32 big-endian length | payload
  0x01  JSON  {"user": str, "reply": str-so-far, "done": bool}
  0x02  WAV bytes for one spoken chunk (24 kHz mono 16-bit PCM)
  0x03  JSON  {"kind": "search_started"|"delivery", ...}
```

| endpoint | |
|---|---|
| `GET /` | the page, with a fresh access token **and session id** injected (each page load gets its own isolated conversation — see [§2b](#2b-multiple-concurrent-users)) |
| `GET /health` | status JSON |
| `POST /api/converse` | multipart `audio` field + `X-Access-Token` + `X-Session-Id` headers → frame stream. Normal turn: meta → audio/meta per sentence → done. Search turn (transcript contained a trigger phrase): `search_started` event → the "keep talking" audio → done; real answer follows via `/api/pending`. |
| `GET /api/pending` | `X-Access-Token` + `X-Session-Id` headers. `204` if nothing ready for *that session* (header `X-Searches-Active`), else the delivered background answer as frames. Poll while idle. |
| `POST /api/reset` | `X-Access-Token` + `X-Session-Id` headers. Clears history + pending deliveries for that session only. |

---

## 4. Configuration

All via env / `.env` — see [`.env.example`](.env.example) for the full list
with defaults. Most relevant:

| Var | What |
|---|---|
| `BRAIN_URL`, `BRAIN_MODEL` | the chat endpoint |
| `STT_DEVICE`, `STT_MODEL` | `cpu`/`cuda`, faster-whisper size |
| `TTS_DEVICE`, `TTS_VOICE` | `cpu`/`cuda`, Kokoro voice |
| `ENABLE_WEB_SEARCH` | `0` to disable `search_agent` |
| `HISTORY_TURNS` | messages kept in memory, per session |
| `SESSION_TTL_SECONDS` | idle session eviction, default 3600 |
| `VOICE_AGENT_TOKEN` | shared secret; random if unset |
| `PORT` | default `5000` |

---

## 5. Files

```
server.py         FastAPI app: routing, auth, the frame endpoints
pipeline.py       shared primitives — config, STT, TTS + sentence splitter,
                  brain client, the frame protocol, in-memory State
chat_agent.py     the fast no-tools conversational path
search_agent.py   the web-search path: tools + background job + /api/pending feed
static/index.html the push-to-talk page (Web-Audio playback, gapless, barge-in)
smoke_test.py     offline TTS→STT→brain round-trip check (live, needs a real deployment)
stream_test.py    times a live /api/converse turn (live, needs a real deployment)
tests/            unit tests — pure logic only, no GPU/brain/network needed,
                  runs in CI on every push (see §9)
pytest.ini        scopes `pytest` to tests/ only (smoke_test.py / stream_test.py
                  match pytest's default *_test.py discovery but aren't unit tests)

run.sh            first-run: writes .env, then foreground uvicorn (`run.sh tunnel` too)
start.sh          detached launch, waits for /health — the one to use normally
tunnel.sh         start/restart a Cloudflare quick tunnel, prints the URL
brain.sh          optional: a local single-node llama.cpp server with MoE
                  expert-offload (`-ot`), for running the brain on the same
                  small GPU as STT/TTS. Needs MODEL=<path to .gguf>.

.github/workflows/ci.yml   syntax check + unit tests on every push/PR
requirements-dev.txt       test-only deps (pytest) — not needed to run the app
```

---

## 6. Troubleshooting

- **Mic works but nothing plays back ("speaking..." then silence).** Almost
  always a stale page — mobile browsers cache hard. Hard-refresh / reopen the
  tab. (Playback uses Web Audio, unlocked on the talk-button press; if that
  unlock never ran, e.g. the page loaded but you never pressed the button
  once, later `.play()` calls are silently blocked.)
- **`422 didn't catch that`.** The recorded clip was too short or the codec
  container was headerless (some `MediaRecorder` implementations do this on a
  very quick tap). Just try again with a slightly longer hold.
- **Deploying to a remote box:** sync the **whole** directory
  (`rsync -az --delete ./ host:/path/ `), never a single file — a stray
  `index.html` landing outside `static/` leaves the server silently serving
  an old page.
- **`nvidia-smi` fails / STT or TTS crash on startup with a CUDA error.**
  The GPU driver isn't loaded for the running kernel (common after a reboot
  or kernel update). Check `dkms status`; if it lists a module, `sudo dkms
  autoinstall`, otherwise `sudo apt install --reinstall nvidia-driver-<ver>`
  and reboot. Until fixed, set `STT_DEVICE=cpu` and `TTS_DEVICE=cpu` in
  `.env` (and drop `-ngl`/`-ot` from `brain.sh` if you're using it) to run
  everything on CPU — slower, but it comes straight back up once the driver
  is fixed, no other config changes needed.
- **`llama-server` refuses to start / version mismatch across nodes.** All
  llama.cpp nodes in a multi-node (RPC) setup must be the exact same build
  commit — check with `git log -1 --format=%H` on each.

---

## 7. Notes & limits

- Per-session in-memory history (last `HISTORY_TURNS` messages per session — see
  [§2b](#2b-multiple-concurrent-users)); all state is lost on restart, nothing
  is persisted to disk.
- Auth is one shared secret in a header, same for every session — fine for
  personal use behind a private URL; add real per-user auth before exposing it
  widely. Session ids isolate *conversations*, they are not an auth boundary —
  anyone with the token can still talk to the agent, just each in their own
  session.
- `web_fetch` refuses private / loopback / link-local addresses (basic SSRF guard).
- Search-answer quality depends on DuckDuckGo snippets; a real search API
  (SearXNG, Brave) would be more reliable.
- Kokoro is fast but a bit flat. `tts_wav_bytes`/`synth` in `pipeline.py` is the
  single seam to swap in e.g. Chatterbox for a more natural voice.
- Nothing here is a system service — a host reboot takes down `brain.sh` /
  `start.sh` / `tunnel.sh` with it. Wrap them in `systemd --user` units (or a
  `tmux`/`screen` session) if you want it to survive that.

---

## 8. Future work

**WebSocket transport, replacing HTTP polling — planned, not yet built.**

Right now the server talks to the page over three separate HTTP calls
(`POST /api/converse`, `GET /api/pending` polled every 4 s, `POST /api/reset`),
and background search results are *pulled* by the page rather than pushed. A
single persistent WebSocket connection per session would let the server push
frames (including a finished search delivery) the instant they're ready instead
of waiting for the next poll, and the connection's own lifetime could replace
the session-id header as the natural scope for a `pipeline.State` — one
connection, one conversation, closed = cleaned up, no idle-session TTL logic
needed either.

Not done now because it's a bigger change to both ends (the frame protocol,
`static/index.html`'s fetch/poll loop, and `server.py`'s routing all move to a
single long-lived connection) than the session-id approach above, which fixes
the immediate cross-talk problem with a small, low-risk change. Worth revisiting
if the 4-second poll latency on search deliveries ever actually matters, or if
session TTL/cleanup logic becomes a maintenance headache.

---

## 9. Testing

Two different kinds, deliberately kept apart:

**Unit tests** (`tests/`, run by `pytest`, and in CI on every push/PR — see the
badge at the top). Cover only pure logic: the `search_agent` trigger regex,
the SSRF guard, markdown stripping, sentence chunking, the frame protocol,
`State`/`SessionStore` (including the exact two-session isolation scenario —
"Alice"/"Bob" not seeing each other's history — that this was built to fix),
and `server.py`'s auth/routing via FastAPI's `TestClient`. `tests/conftest.py`
stubs out `faster_whisper` and `kokoro` **before** anything imports
`pipeline.py`, so none of this needs a GPU, model weights, or even those
packages installed — the whole suite runs in well under a second.

```bash
pip install fastapi uvicorn python-multipart openai httpx soundfile numpy
pip install -r requirements-dev.txt
pytest -v
```

**Live/manual integration checks** (`smoke_test.py`, `stream_test.py`) —
deliberately **not** run in CI, because they need a real deployment: a
running brain, real STT/TTS models loaded (GPU or not), and in `stream_test`'s
case an actual `/api/converse` round trip. Run these by hand against pc4 (or
wherever it's deployed) after any change to the pipeline itself:

```bash
.venv/bin/python smoke_test.py                          # TTS -> STT -> brain, offline
.venv/bin/python stream_test.py <token> /tmp/clip.wav    # times a live turn
```

See `notes/testing-notes` for the exact manual test cases run against this
deployment (multi-session isolation, trigger-phrase routing, the persona
system-prompt check) and their actual output.
