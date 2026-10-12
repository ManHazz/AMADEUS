"""Shared voice-pipeline primitives: config, STT, TTS, brain client, the frame
protocol, and the in-memory conversation state. Both agents (chat_agent,
search_agent) build on this.

TUTOR / SEGMENT PROTOCOL (full spec: PROTOCOL_SEGMENTS.md)
  The brain may tag every line of its reply: [ja] spoken Japanese, [en] spoken
  English, [tr] silent English translation of the preceding [ja] line. Tags are
  parsed incrementally (TagStream) across streamed tokens and never spoken.
  Untagged text is treated as [en]. For each spoken segment chat_agent emits
    FRAME_EVENT {"kind":"segment","id":n,"lang":"ja|en","text":"...",
                 "ruby":[{"t":"日本語","r":"にほんご"},{"t":"を","r":""}],
                 "tr":null,"spoken":true}
  immediately BEFORE that segment's FRAME_AUDIO (WAV; Japanese Kokoro voice for
  ja, TTS_VOICE for en). When a [tr] line follows a ja segment it is sent (and
  never synthesized) as FRAME_EVENT {"kind":"segment_tr","id":<ja id>,"tr":"..."}.
  "ruby" is only present for ja ("r" is "" for kana/punctuation, hiragana for
  kanji). ids increment per reply starting at 0.
"""
import io
import os
import re
import json
import time
import struct
import threading

# Windows: make pip-installed CUDA 12 DLLs (cublas/cudnn) findable by ctranslate2.
if os.name == "nt":
    import site
    for _sp in site.getsitepackages():
        for _d in ("cublas", "cudnn", "cuda_nvrtc"):
            _bin = os.path.join(_sp, "nvidia", _d, "bin")
            if os.path.isdir(_bin):
                os.add_dll_directory(_bin)
                os.environ["PATH"] = _bin + os.pathsep + os.environ["PATH"]

import numpy as np
import soundfile as sf
from faster_whisper import WhisperModel
from kokoro import KPipeline
from openai import OpenAI, BadRequestError

# --------------------------------------------------------------------------- config
BRAIN_URL = os.environ.get("BRAIN_URL", "http://127.0.0.1:8081/v1")
BRAIN_MODEL = os.environ.get("BRAIN_MODEL", "local-model")
BRAIN_MAX_TOKENS = int(os.environ.get("BRAIN_MAX_TOKENS", "220"))
HISTORY_TURNS = int(os.environ.get("HISTORY_TURNS", "8"))
SESSION_TTL_SECONDS = int(os.environ.get("SESSION_TTL_SECONDS", "3600"))

STT_DEVICE = os.environ.get("STT_DEVICE", "cpu")
STT_MODEL = os.environ.get("STT_MODEL", "small")
STT_COMPUTE = os.environ.get("STT_COMPUTE_TYPE") or ("int8" if STT_DEVICE == "cpu" else "float16")
# STT_MODEL defaults to "small"; "medium" recognises Japanese noticeably better (more VRAM/latency).

TTS_DEVICE = os.environ.get("TTS_DEVICE", "cpu")
TTS_VOICE = os.environ.get("TTS_VOICE", "af_heart")
TTS_LANG = os.environ.get("TTS_LANG", "a")  # 'a' = American English
TTS_VOICE_JA = os.environ.get("TTS_VOICE_JA", "jf_alpha")  # jf_alpha|jf_nezumi|jf_tebukuro|jf_gongitsune
# 1: English ([en] / untagged) lines are never synthesized, only shown as silent tips
ENGLISH_FREE = os.environ.get("ENGLISH_FREE", "1") not in ("0", "false", "no", "")
TTS_JA_WARMUP = os.environ.get("TTS_JA_WARMUP", "1") not in ("0", "false", "no", "")

_DEFAULT_PROMPT_FILE = os.path.join(os.path.dirname(os.path.abspath(__file__)), "prompts", "tutor_neutral.txt")


def _read_prompt_file(path: str) -> str:
    with open(path, encoding="utf-8") as f:
        return f.read().strip()


# Load order: SYSTEM_PROMPT_FILE (env, path to a text file), then SYSTEM_PROMPT (env, inline text),
# then the bundled default prompts/tutor_neutral.txt (a neutral Japanese tutor prompt).
_PROMPT_FILE = os.environ.get("SYSTEM_PROMPT_FILE")
_PROMPT_FROM_FILE = _read_prompt_file(_PROMPT_FILE) if _PROMPT_FILE else ""

SYSTEM_PROMPT = (
    _PROMPT_FROM_FILE
    or os.environ.get("SYSTEM_PROMPT")
    or _read_prompt_file(_DEFAULT_PROMPT_FILE)
)

# --------------------------------------------------------------------------- timing
DEBUG_TIMING = os.environ.get("DEBUG_TIMING", "") not in ("", "0", "false", "no")


class Timer:
    """Opt-in per-request stage timer (DEBUG_TIMING=1). mark() prints ms since start."""
    def __init__(self):
        self.t0 = time.perf_counter()
        self.seen: set[str] = set()

    def mark(self, label: str, once: bool = False):
        if not DEBUG_TIMING or (once and label in self.seen):
            return
        self.seen.add(label)
        print(f"[timing] {label:<22} {(time.perf_counter() - self.t0) * 1000:7.0f} ms", flush=True)


# --------------------------------------------------------------------------- STT
_stt = WhisperModel(STT_MODEL, device=STT_DEVICE, compute_type=STT_COMPUTE)
STT_BEAM = int(os.environ.get("STT_BEAM_SIZE", "1"))
_stt_lang_env = os.environ.get("STT_LANGUAGE", "en").strip()
# "" / "auto" -> None -> Whisper auto-detects (mixed English/Japanese learner speech)
STT_LANG = None if _stt_lang_env.lower() in ("", "auto") else _stt_lang_env
STT_INITIAL_PROMPT = os.environ.get("STT_INITIAL_PROMPT") or None
STT_VAD = os.environ.get("STT_VAD", "1") not in ("0", "false", "no", "")


def _stt_kwargs() -> dict:
    kw = dict(language=STT_LANG, beam_size=STT_BEAM, condition_on_previous_text=False)
    if STT_INITIAL_PROMPT:
        kw["initial_prompt"] = STT_INITIAL_PROMPT
    if STT_VAD:
        kw["vad_filter"] = True
        kw["vad_parameters"] = dict(min_silence_duration_ms=500, speech_pad_ms=200)
    return kw


def transcribe(path: str) -> str:
    segments, _ = _stt.transcribe(path, **_stt_kwargs())
    return " ".join(s.text for s in segments).strip()


# --------------------------------------------------------------------------- TTS
_tts = KPipeline(lang_code=TTS_LANG, device=TTS_DEVICE)
_tts_ja = None                      # second KPipeline (lang 'j'), built lazily
_tts_ja_lock = threading.Lock()


def _get_tts_ja():
    global _tts_ja
    with _tts_ja_lock:
        if _tts_ja is None:
            # share the already-loaded Kokoro model (weights are language-agnostic): no 2nd copy in VRAM
            _tts_ja = KPipeline(lang_code="j", model=getattr(_tts, "model", None) or True, device=TTS_DEVICE)
        return _tts_ja

SENTENCE_END = re.compile(r"(?<=[.!?;])[\s\n]+(?=[\"'(\[]?[A-Z0-9])")
MAX_SPEAK_CHARS = 300
MIN_CHUNK_CHARS = 24
FIRST_CHUNK_CHARS = int(os.environ.get("FIRST_CHUNK_CHARS", "20"))  # flush first clause at >= this many chars

_MD = [
    (re.compile(r"```.*?```", re.S), " "),
    (re.compile(r"`([^`]*)`"), r"\1"),
    (re.compile(r"^\s{0,3}#{1,6}\s*", re.M), ""),
    (re.compile(r"^\s*[-*+]\s+", re.M), ""),
    (re.compile(r"^\s*\d+\.\s+", re.M), ""),
    (re.compile(r"(\*\*|__|\*|_|~~)"), ""),
    (re.compile(r"\[([^\]]+)\]\([^)]+\)"), r"\1"),
    (re.compile(r"\s{2,}"), " "),
]


_TAG_ANY = re.compile(r"[\[【]\s*(?:ja|en|tr)\s*[\]】]", re.I)


def strip_tags(text: str) -> str:
    return _TAG_ANY.sub("", text)


def clean_for_speech(text: str) -> str:
    text = strip_tags(text)
    for pat, repl in _MD:
        text = pat.sub(repl, text)
    return text.strip()


def synth(text: str, lang: str = "en") -> bytes:
    """One text chunk -> WAV bytes (24 kHz mono 16-bit). lang 'ja' uses the
    Japanese Kokoro pipeline/voice, anything else the English one."""
    if lang == "ja":
        pipe, voice = _get_tts_ja(), TTS_VOICE_JA
    else:
        pipe, voice = _tts, TTS_VOICE
    parts = [a for _, _, a in pipe(text, voice=voice) if a is not None]
    audio = np.concatenate(parts) if parts else np.zeros(1, dtype="float32")
    buf = io.BytesIO()
    sf.write(buf, audio, 24000, format="WAV")
    return buf.getvalue()


def speak_chunks(text: str) -> list[str]:
    """Split a finished reply into natural spoken chunks (whole sentences; tiny
    fragments merged into a neighbour)."""
    text = clean_for_speech(text)
    if not text:
        return []
    parts = [p.strip() for p in SENTENCE_END.split(text) if p.strip()]
    merged: list[str] = []
    for p in parts:
        if merged and (len(p) < MIN_CHUNK_CHARS or len(merged[-1]) < MIN_CHUNK_CHARS):
            merged[-1] = f"{merged[-1]} {p}"
        else:
            merged.append(p)
    return merged or [text]


_CLAUSE_END = re.compile(r"[,:—]\s+(?=\S)")


def split_ready(buffer: str, first: bool = False):
    """Streaming variant: (complete_chunks, remaining_buffer). first=True also lets
    the very first chunk flush at a clause break (>= FIRST_CHUNK_CHARS) so audio
    starts sooner."""
    out: list[str] = []
    if first and FIRST_CHUNK_CHARS > 0 and not SENTENCE_END.search(buffer):
        for m in _CLAUSE_END.finditer(buffer):
            if m.start() + 1 >= FIRST_CHUNK_CHARS:
                return [buffer[: m.start() + 1].strip()], buffer[m.end():]
    while True:
        m = next(SENTENCE_END.finditer(buffer), None)
        if m:
            out.append(buffer[: m.start()].strip())
            buffer = buffer[m.end():]
            continue
        if len(buffer) >= MAX_SPEAK_CHARS:
            cut = buffer.rfind(", ", 0, MAX_SPEAK_CHARS)
            if cut < 60:
                cut = buffer.rfind(" ", 0, MAX_SPEAK_CHARS)
            if cut > 40:
                out.append(buffer[:cut].strip())
                buffer = buffer[cut + 1:]
                continue
        break
    return [c for c in out if c], buffer


# --------------------------------------------------------------------------- brain
_brain = OpenAI(base_url=BRAIN_URL, api_key=os.environ.get("BRAIN_API_KEY") or "not-needed")
# Gemini "thinking" off by default for latency: BRAIN_REASONING_EFFORT=none|minimal|low|medium|high, "" to omit
BRAIN_REASONING_EFFORT = os.environ.get("BRAIN_REASONING_EFFORT", "none")


def brain_call(messages, tools=None, tool_choice="auto", stream=False):
    kw = dict(model=BRAIN_MODEL, messages=messages, max_tokens=BRAIN_MAX_TOKENS, stream=stream)
    global BRAIN_REASONING_EFFORT
    if BRAIN_REASONING_EFFORT:
        kw["reasoning_effort"] = BRAIN_REASONING_EFFORT
    if tools:
        kw.update(tools=tools, tool_choice=tool_choice)
    try:
        return _brain.chat.completions.create(**kw)
    except BadRequestError:
        if not kw.pop("reasoning_effort", None):
            raise
        BRAIN_REASONING_EFFORT = ""  # model rejects the param (e.g. flash-lite): stop sending it
        return _brain.chat.completions.create(**kw)


def warmup():
    """Pay one-time CUDA/model/connection costs at startup instead of on the first turn."""
    t = time.perf_counter()
    try:
        synth("Ready.")
        if TTS_JA_WARMUP:
            try:
                synth("はい。", "ja")
            except Exception as e:  # noqa: BLE001
                print(f"[warmup] ja tts unavailable: {type(e).__name__}: {e}")
        list(_stt.transcribe(np.zeros(16000, dtype="float32"), **_stt_kwargs())[0])
        brain_call([{"role": "user", "content": "hi"}])
    except Exception as e:  # noqa: BLE001
        print(f"[warmup] failed: {type(e).__name__}: {e}")
    print(f"[init] warmup     : {(time.perf_counter() - t) * 1000:.0f} ms")


# --------------------------------------------------------------------------- tags / ruby
_KAKASI = None
_KANJI = re.compile(r"[\u3400-\u4dbf\u4e00-\u9fff\uf900-\ufaff\u3005]")


def ruby_for(text: str) -> list[dict]:
    """[{"t": token, "r": hiragana reading or ""}]; r is set only for tokens containing kanji."""
    global _KAKASI
    try:
        if _KAKASI is None:
            import pykakasi
            _KAKASI = pykakasi.kakasi()
        out = []
        for tok in _KAKASI.convert(text):
            t = tok["orig"]
            if not _KANJI.search(t):
                out.append({"t": t, "r": ""})
                continue
            hira = tok["hira"]
            k = len(t)
            while k > 0 and not _KANJI.search(t[k - 1]) and hira.endswith(t[k - 1:]):
                k -= 1                       # peel okurigana (食べ -> 食 + べ) off the reading
            stem, tail = t[:k], t[k:]
            out.append({"t": stem, "r": hira[:len(hira) - len(tail)]})
            if tail:
                out.append({"t": tail, "r": ""})
        return out or [{"t": text, "r": ""}]
    except Exception:  # noqa: BLE001  (pykakasi missing/broken: plain text still works)
        return [{"t": text, "r": ""}]


_TAG_RE = re.compile(r"[\[【]\s*(ja|en|tr)\s*[\]】]", re.I)
_TAG_PART = re.compile(r"[\[【]\s*(?:j|ja|e|en|t|tr)?\s*$", re.I)    # could still become a tag
_OPEN = re.compile(r"[\[【]")
# a run of Japanese script (kana, kanji, long mark, JP punctuation); used to route text to the ja voice
_JA_RUN = re.compile(
    r"[぀-ヿ㐀-䶿一-鿿ｦ-ﾟ々〆ー]"
    r"[、-〃〈-】぀-ヿ㐀-䶿一-鿿！？ｦ-ﾟ々〆ー…・]*")


def split_scripts(text: str) -> list[tuple[str, str]]:
    """[(lang, text)] splitting Japanese-script runs (-> 'ja') from the rest (-> 'en')."""
    out, last = [], 0
    for m in _JA_RUN.finditer(text):
        if text[last:m.start()].strip():
            out.append(("en", text[last:m.start()].strip()))
        out.append(("ja", m.group()))
        last = m.end()
    if text[last:].strip():
        out.append(("en", text[last:].strip()))
    return out
_JA_END = re.compile(r"[。！？!?]+")


def _split_ja(text: str) -> list[str]:
    """Split Japanese text after each sentence-final mark (mark stays with its sentence)."""
    out, last = [], 0
    for m in _JA_END.finditer(text):
        out.append(text[last:m.end()])
        last = m.end()
    out.append(text[last:])
    return [p.strip() for p in out if p.strip()]


class TagStream:
    """Incremental parser for tagged brain output. feed(delta) -> list of items:
         {"kind":"seg","id":n,"lang":"ja"|"en","text":...,"spoken":bool}  speak this
                                                  (en is spoken=False when ENGLISH_FREE)
         {"kind":"tr","id":<ja id>,"text":...}                subtitle only, never spoken
                                                  (one per ja segment still untranslated)
       Tags may be split across deltas ("[" then "ja]"). Untagged text is English."""

    def __init__(self):
        self.tag = None          # None (-> en) | 'ja' | 'en' | 'tr'
        self.pend = ""           # undecided tail (possible partial tag)
        self.buf = ""            # text of the current, unfinished segment/line
        self.next_id = 0
        self.last_ja = None
        self.untr: list[int] = []   # ids of ja segments still waiting for a [tr]
        self.n_seg = 0

    def feed(self, delta: str) -> list[dict]:
        self.pend += delta
        items: list[dict] = []
        while self.pend:
            mo = _OPEN.search(self.pend)
            i = mo.start() if mo else -1
            if i < 0:
                self.buf += self.pend
                self.pend = ""
                break
            self.buf += self.pend[:i]
            self.pend = self.pend[i:]
            m = _TAG_RE.match(self.pend)
            if m:
                items += self._flush_all()
                self.tag = m.group(1).lower()
                self.pend = self.pend[m.end():]
            elif _TAG_PART.match(self.pend):
                break            # may still become a tag: wait for more tokens
            else:
                self.buf += self.pend[0]
                self.pend = self.pend[1:]
        return items + self._ready()

    def finish(self) -> list[dict]:
        self.buf += self.pend
        self.pend = ""
        return self._flush_all()

    def _seg(self, text: str) -> list[dict]:
        text = clean_for_speech(text)
        if not text or not re.search(r"\w", text):
            return []
        # safety net: Japanese script must never reach the English voice (it reads
        # "Japanese letter, ..."), whatever tag (or none) the brain used.
        runs = [("ja", text)] if self.tag == "ja" else split_scripts(text)
        out = []
        for lang, t in runs:
            if not re.search(r"\w", t):
                continue
            out.append({"kind": "seg", "id": self.next_id, "lang": lang, "text": t,
                        "spoken": lang == "ja" or not ENGLISH_FREE})
            if lang == "ja":
                self.last_ja = self.next_id
                self.untr.append(self.next_id)
            self.next_id += 1
            self.n_seg += 1
        return out

    def _flush_all(self) -> list[dict]:
        """Treat the whole buffer as finished text."""
        text, self.buf = self.buf, ""
        if self.tag == "tr":
            text = clean_for_speech(text)
            if not text:
                return []
            # one [tr] covers every ja segment still untranslated (the brain sometimes
            # emits [ja][ja][tr]): the same text goes to each of their segment ids.
            ids = self.untr or ([self.last_ja] if self.last_ja is not None else [])
            self.untr = []
            return [{"kind": "tr", "id": i, "text": text} for i in ids]
        parts = _split_ja(text) if self.tag == "ja" else speak_chunks(text)
        out: list[dict] = []
        for p in parts:
            out += self._seg(p)
        return out

    def _ready(self) -> list[dict]:
        out: list[dict] = []
        while "\n" in self.buf:
            line, rest = self.buf.split("\n", 1)
            self.buf = line
            out += self._flush_all()
            self.buf = rest
        if self.tag == "tr" or not self.buf.strip():
            return out
        if self.tag == "ja":
            ends = list(_JA_END.finditer(self.buf))
            if ends:
                cut = ends[-1].end()
                done, self.buf = self.buf[:cut], self.buf[cut:]
                for p in _split_ja(done):
                    out += self._seg(p)
        else:
            ready, self.buf = split_ready(self.buf, first=(self.n_seg == 0))
            for r in ready:
                out += self._seg(r)
        return out


# --------------------------------------------------------------------------- frames
# wire format:  1 byte type | 4 bytes uint32 big-endian length | payload
FRAME_META = 0x01   # JSON {"user","reply","done"}
FRAME_AUDIO = 0x02  # WAV bytes for one spoken chunk
FRAME_EVENT = 0x03  # JSON {"kind", ...}


def frame(ftype: int, payload: bytes) -> bytes:
    return bytes([ftype]) + struct.pack(">I", len(payload)) + payload


def meta_frame(user: str, reply: str, done: bool = False) -> bytes:
    return frame(FRAME_META, json.dumps({"user": user, "reply": reply, "done": done}).encode())


def event_frame(kind: str, **extra) -> bytes:
    return frame(FRAME_EVENT, json.dumps({"kind": kind, **extra}).encode())


def audio_frame(text: str, lang: str = "en") -> bytes:
    return frame(FRAME_AUDIO, synth(text, lang))


def segment_frame(seg: dict) -> bytes:
    """seg: a 'seg' item from TagStream."""
    spoken = seg.get("spoken", True)
    ev = {"id": seg["id"], "lang": seg["lang"], "text": seg["text"], "tr": None, "spoken": spoken}
    if seg["lang"] == "ja":
        ev["ruby"] = ruby_for(seg["text"])
    elif not spoken:
        ev["ruby"] = []
    return event_frame("segment", **ev)


def segment_tr_frame(item: dict) -> bytes:
    """item: a 'tr' item from TagStream."""
    return event_frame("segment_tr", id=item["id"], tr=item["text"])


# --------------------------------------------------------------------------- state
class State:
    """In-memory single-user conversation + background-delivery queue.
    threading.Lock because request-handler threadpool workers and the search
    background job both touch it."""

    def __init__(self):
        self.lock = threading.Lock()
        self.history = [{"role": "system", "content": SYSTEM_PROMPT}]
        self.deliveries: list[dict] = []
        self.searches_active = 0

    def add_user(self, text):
        with self.lock:
            self.history.append({"role": "user", "content": text})
            self._trim()
            return list(self.history)

    def add_assistant(self, text):
        with self.lock:
            self.history.append({"role": "assistant", "content": text})
            self._trim()

    def drop_last_user(self, text):
        with self.lock:
            if self.history and self.history[-1].get("content") == text \
               and self.history[-1].get("role") == "user":
                self.history.pop()

    def add_qa(self, question, answer):
        with self.lock:
            self.history.append({"role": "user", "content": f"[earlier] {question}"})
            self.history.append({"role": "assistant", "content": answer})
            self._trim()

    def _trim(self):
        if len(self.history) > 1 + HISTORY_TURNS:
            self.history[:] = self.history[:1] + self.history[-HISTORY_TURNS:]

    def reset(self):
        with self.lock:
            self.history[:] = [{"role": "system", "content": SYSTEM_PROMPT}]
            self.deliveries.clear()

    # background deliveries
    def search_begin(self):
        with self.lock:
            self.searches_active += 1

    def search_end(self):
        with self.lock:
            self.searches_active = max(0, self.searches_active - 1)

    def push_delivery(self, item):
        with self.lock:
            self.deliveries.append(item)

    def pop_delivery(self):
        with self.lock:
            return self.deliveries.pop(0) if self.deliveries else None


class SessionStore:
    """One State per browser session, keyed by a server-minted session id (see
    server.py's GET / — same pattern as the ACCESS_TOKEN injection). Idle
    sessions (no request in SESSION_TTL_SECONDS) are evicted lazily on the next
    get() so memory doesn't grow unbounded from abandoned tabs."""

    def __init__(self):
        self._lock = threading.Lock()
        self._sessions: dict[str, tuple[State, float]] = {}

    def get(self, session_id: str) -> State:
        now = time.time()
        with self._lock:
            self._evict_expired(now)
            entry = self._sessions.get(session_id)
            if entry is None:
                st = State()
                self._sessions[session_id] = (st, now)
                return st
            st, _ = entry
            self._sessions[session_id] = (st, now)
            return st

    def _evict_expired(self, now):
        dead = [sid for sid, (_, last) in self._sessions.items()
                if now - last > SESSION_TTL_SECONDS]
        for sid in dead:
            del self._sessions[sid]

    def stats(self) -> dict:
        with self._lock:
            return {
                "active_sessions": len(self._sessions),
                "searches_active": sum(st.searches_active for st, _ in self._sessions.values()),
                "deliveries_waiting": sum(len(st.deliveries) for st, _ in self._sessions.values()),
            }
