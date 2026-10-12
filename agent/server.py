"""
Voice agent web backend — FastAPI wiring only.

Routing:
    POST /api/converse
        STT -> if web search enabled AND the question looks time-sensitive:
                   search_agent  (background job + spoken "keep talking" note)
               else:
                   chat_agent    (fast, no tools, token-streamed sentence-by-sentence)
    GET  /api/pending    deliver a finished background-search answer (page polls it)
    POST /api/reset      clear history + pending deliveries
    GET  /               the push-to-talk page (access token + session id injected)
    GET  /health

Each browser session gets its own pipeline.State (history, delivery queue) —
GET / mints a fresh session id per page load, baked into the page the same way
the access token is; the page sends it back as X-Session-Id on every call, so
concurrent users/tabs/devices never see each other's conversation or web-search
answers. See pipeline.SessionStore.

Response body of /api/converse and /api/pending is a stream of length-prefixed
frames:  1 byte type | 4 bytes uint32 BE length | payload
    0x01 JSON {"user","reply","done"}   0x02 WAV chunk   0x03 JSON event
"""
import os
import uuid
import asyncio
import secrets
import tempfile
import pathlib

from fastapi import FastAPI, UploadFile, File, Header, HTTPException, Response
from fastapi.responses import StreamingResponse, HTMLResponse, JSONResponse

import pipeline
import chat_agent
import search_agent

HERE = pathlib.Path(__file__).parent
INDEX_HTML = (HERE / "static" / "index.html").read_text()

ACCESS_TOKEN = os.environ.get("VOICE_AGENT_TOKEN") or secrets.token_urlsafe(16)
ENABLE_WEB_SEARCH = os.environ.get("ENABLE_WEB_SEARCH", "1") not in ("0", "false", "no", "")

app = FastAPI()
SESSIONS = pipeline.SessionStore()
LOOP: asyncio.AbstractEventLoop | None = None


@app.on_event("startup")
async def _startup():
    global LOOP
    LOOP = asyncio.get_running_loop()
    if os.environ.get("WARMUP", "1") not in ("0", "false", "no", ""):
        await asyncio.to_thread(pipeline.warmup)


def check_token(tok: str):
    if not secrets.compare_digest(tok, ACCESS_TOKEN):
        raise HTTPException(status_code=401, detail="invalid or missing access token")


print(f"[init] brain      : {pipeline.BRAIN_URL}  ({pipeline.BRAIN_MODEL})")
print(f"[init] stt        : faster-whisper '{pipeline.STT_MODEL}' on {pipeline.STT_DEVICE}")
print(f"[init] tts        : kokoro '{pipeline.TTS_VOICE}' on {pipeline.TTS_DEVICE}")
print(f"[init] web search : {'on' if ENABLE_WEB_SEARCH else 'OFF (chat_agent only)'}")
print(f"[init] token      : {ACCESS_TOKEN}")


@app.get("/", response_class=HTMLResponse)
def index():
    # a fresh session id per page load, injected the same way as the access
    # token — the page sends it back as X-Session-Id so concurrent users (or
    # tabs/devices) each get an isolated conversation instead of sharing one.
    session_id = str(uuid.uuid4())
    return (INDEX_HTML
            .replace("__ACCESS_TOKEN__", ACCESS_TOKEN)
            .replace("__SESSION_ID__", session_id))


@app.get("/health")
def health():
    return {
        "ok": True, "brain": pipeline.BRAIN_URL, "model": pipeline.BRAIN_MODEL,
        "stt": f"{pipeline.STT_MODEL}/{pipeline.STT_DEVICE}",
        "tts": f"kokoro:{pipeline.TTS_VOICE}/{pipeline.TTS_DEVICE}",
        "web_search": ENABLE_WEB_SEARCH,
        **SESSIONS.stats(),
    }


@app.post("/api/converse")
async def converse(audio: UploadFile = File(...), x_access_token: str = Header(default=""),
                    x_session_id: str = Header(default="")):
    tm = pipeline.Timer()
    tm.mark("request received")
    check_token(x_access_token)
    state = SESSIONS.get(x_session_id or "legacy")

    suffix = os.path.splitext(audio.filename or "")[1] or ".webm"
    with tempfile.NamedTemporaryFile(suffix=suffix, delete=False) as tmp:
        tmp.write(await audio.read())
        tmp_path = tmp.name
    size = os.path.getsize(tmp_path)
    try:
        if size < 2048:
            raise ValueError("clip too short")
        user_text = await asyncio.to_thread(pipeline.transcribe, tmp_path)
    except Exception as e:  # noqa: BLE001
        print(f"[stt] decode failed ({size} B): {type(e).__name__}: {e}")
        return JSONResponse({"error": "didn't catch that — try again"}, status_code=422)
    finally:
        os.unlink(tmp_path)

    tm.mark("STT done")
    if not user_text:
        return JSONResponse({"error": "no speech detected"}, status_code=422)

    snapshot = state.add_user(user_text)

    if ENABLE_WEB_SEARCH and search_agent.wants_search(user_text):
        frames = search_agent.handle(user_text, snapshot, state, LOOP)
    else:
        frames = chat_agent.handle(user_text, snapshot, state, tm)

    return StreamingResponse(frames, media_type="application/octet-stream")


@app.get("/api/pending")
def pending(x_access_token: str = Header(default=""), x_session_id: str = Header(default="")):
    check_token(x_access_token)
    state = SESSIONS.get(x_session_id or "legacy")
    item = state.pop_delivery()
    hdr = {"X-Searches-Active": str(state.searches_active)}
    if item is None:
        return Response(status_code=204, headers=hdr)

    def gen():
        yield pipeline.event_frame("delivery", question=item["question"])
        yield pipeline.meta_frame(item["question"], item["text"])
        for w in item["wavs"]:
            yield pipeline.frame(pipeline.FRAME_AUDIO, w)
        yield pipeline.meta_frame(item["question"], item["text"], done=True)

    return StreamingResponse(gen(), media_type="application/octet-stream", headers=hdr)


@app.post("/api/reset")
def reset(x_access_token: str = Header(default=""), x_session_id: str = Header(default="")):
    check_token(x_access_token)
    SESSIONS.get(x_session_id or "legacy").reset()
    return {"status": "reset"}
