"""Pure helpers for the e2e suite: frame parser, text scoring, per-turn checks.

Nothing here needs a GPU, a network or the server, so tests/test_e2e_helpers.py
can exercise all of it offline. Heavy imports (numpy, soundfile, pykakasi) are
done lazily inside the functions that need them.
"""
from __future__ import annotations

import difflib
import io
import json
import re
import struct
import unicodedata
from dataclasses import dataclass, field

# ------------------------------------------------------------------ frame protocol
FRAME_META = 0x01
FRAME_AUDIO = 0x02
FRAME_EVENT = 0x03
KNOWN_FRAMES = (FRAME_META, FRAME_AUDIO, FRAME_EVENT)
MAX_FRAME = 64 * 1024 * 1024


@dataclass
class Frame:
    type: int
    payload: bytes
    t: float = 0.0          # arrival time (seconds, caller's clock), set by the client


class FrameParser:
    """Incremental parser for  1B type | 4B uint32 BE length | payload  frames."""

    def __init__(self):
        self.buf = bytearray()
        self.error: str | None = None

    def feed(self, data: bytes) -> list[Frame]:
        out: list[Frame] = []
        if self.error:
            return out
        self.buf += data
        while len(self.buf) >= 5:
            ftype = self.buf[0]
            (length,) = struct.unpack(">I", bytes(self.buf[1:5]))
            if ftype not in KNOWN_FRAMES:
                self.error = f"unknown frame type 0x{ftype:02x}"
                break
            if length > MAX_FRAME:
                self.error = f"absurd frame length {length}"
                break
            if len(self.buf) < 5 + length:
                break
            out.append(Frame(ftype, bytes(self.buf[5:5 + length])))
            del self.buf[:5 + length]
        return out

    @property
    def leftover(self) -> int:
        return len(self.buf)


def parse_stream(data: bytes) -> tuple[list[Frame], FrameParser]:
    p = FrameParser()
    frames = p.feed(data)
    return frames, p


def encode_frame(ftype: int, payload: bytes) -> bytes:
    return bytes([ftype]) + struct.pack(">I", len(payload)) + payload


# ------------------------------------------------------------------ stream analysis
@dataclass
class Analysis:
    metas: list[dict] = field(default_factory=list)
    segments: list[dict] = field(default_factory=list)     # + "seq"
    trs: list[dict] = field(default_factory=list)          # + "seq"
    audio: list[dict] = field(default_factory=list)        # {"wav","seq","seg_id"}
    problems: list[str] = field(default_factory=list)      # protocol violations
    leftover: int = 0
    parser_error: str | None = None

    @property
    def final_meta(self):
        return self.metas[-1] if self.metas else None

    @property
    def done(self) -> bool:
        return bool(self.final_meta and self.final_meta.get("done"))

    @property
    def user_heard(self) -> str:
        for m in self.metas:
            if m.get("user"):
                return m["user"]
        return ""

    @property
    def reply_text(self) -> str:
        return (self.final_meta or {}).get("reply", "") or ""

    def ja_segments(self):
        return [s for s in self.segments if s.get("lang") == "ja"]

    def spoken_segments(self):
        return [s for s in self.segments if s.get("spoken")]

    def tr_for(self, seg_id):
        for t in self.trs:
            if t.get("id") == seg_id:
                return t.get("tr")
        return None


def analyze(frames: list[Frame], leftover: int = 0, parser_error: str | None = None) -> Analysis:
    a = Analysis(leftover=leftover, parser_error=parser_error)
    last_seg = None                       # most recent segment event not yet given audio
    for seq, fr in enumerate(frames):
        if fr.type == FRAME_META:
            try:
                a.metas.append(json.loads(fr.payload.decode("utf-8")))
            except Exception as e:  # noqa: BLE001
                a.problems.append(f"frame {seq}: bad META json ({type(e).__name__})")
        elif fr.type == FRAME_EVENT:
            try:
                ev = json.loads(fr.payload.decode("utf-8"))
            except Exception as e:  # noqa: BLE001
                a.problems.append(f"frame {seq}: bad EVENT json ({type(e).__name__})")
                continue
            kind = ev.get("kind")
            if kind == "segment":
                ev["seq"] = seq
                a.segments.append(ev)
                last_seg = ev
            elif kind == "segment_tr":
                ev["seq"] = seq
                a.trs.append(ev)
        elif fr.type == FRAME_AUDIO:
            seg_id = None
            if last_seg is not None and last_seg.get("spoken") and not last_seg.get("_has_audio"):
                last_seg["_has_audio"] = True
                seg_id = last_seg.get("id")
            a.audio.append({"wav": fr.payload, "seq": seq, "seg_id": seg_id})
    return a


# ------------------------------------------------------------------ text scoring
_PUNCT = re.compile(r"[\W_]+", re.U)
_JP = re.compile(r"[぀-ヿ㐀-䶿一-鿿ｦ-ﾟ]")
KANJI = re.compile(r"[㐀-䶿一-鿿豈-﫿々]")
_KKS = None


def has_japanese(s: str) -> bool:
    return bool(_JP.search(s or ""))


def has_kanji(s: str) -> bool:
    return bool(KANJI.search(s or ""))


def norm_text(s: str) -> str:
    """NFKC, lowercase, strip all punctuation/whitespace/symbols."""
    s = unicodedata.normalize("NFKC", s or "").lower()
    return _PUNCT.sub("", s)


def to_hira(s: str) -> str:
    """Japanese text -> hiragana via pykakasi (kanji/katakana variants collapse).
    Returns the input unchanged if pykakasi is missing or the text has no Japanese."""
    global _KKS
    if not has_japanese(s):
        return s
    try:
        if _KKS is None:
            import pykakasi
            _KKS = pykakasi.kakasi()
        return "".join(t["hira"] for t in _KKS.convert(s))
    except Exception:  # noqa: BLE001
        return s


def similarity(a: str, b: str, kana_fold: bool = True) -> float:
    """difflib ratio on punctuation-stripped text. For Japanese the best of the
    raw comparison and a hiragana-folded comparison is used, so that a correct
    transcript written with kanji instead of kana (or vice versa) is not penalised."""
    na, nb = norm_text(a), norm_text(b)
    if not na and not nb:
        return 1.0
    if not na or not nb:
        return 0.0
    best = difflib.SequenceMatcher(None, na, nb).ratio()
    if kana_fold and (has_japanese(a) or has_japanese(b)):
        ha, hb = norm_text(to_hira(a)), norm_text(to_hira(b))
        if ha and hb:
            best = max(best, difflib.SequenceMatcher(None, ha, hb).ratio())
    return round(best, 3)


# ------------------------------------------------------------------ audio helpers
def wav_to_float(wav: bytes):
    import soundfile as sf
    data, sr = sf.read(io.BytesIO(wav), dtype="float32", always_2d=False)
    if getattr(data, "ndim", 1) > 1:
        data = data.mean(axis=1)
    return data, sr


def rms(x) -> float:
    import numpy as np
    x = np.asarray(x, dtype="float64")
    return float(np.sqrt(np.mean(x * x))) if x.size else 0.0


def resample(x, sr_in: int, sr_out: int):
    """Band-limited FFT resample (no scipy needed)."""
    import numpy as np
    if sr_in == sr_out or len(x) == 0:
        return np.asarray(x, dtype="float32")
    n_out = int(round(len(x) * sr_out / sr_in))
    spec = np.fft.rfft(x)
    out_spec = np.zeros(n_out // 2 + 1, dtype=spec.dtype)
    m = min(len(spec), len(out_spec))
    out_spec[:m] = spec[:m]
    y = np.fft.irfft(out_spec, n=n_out) * (n_out / len(x))
    return y.astype("float32")


def wav_rms(wav: bytes) -> float:
    data, _ = wav_to_float(wav)
    return rms(data)


def is_wav(b: bytes) -> bool:
    return len(b) > 44 and b[:4] == b"RIFF" and b[8:12] == b"WAVE"


# ------------------------------------------------------------------ stats
def median(xs):
    xs = sorted(x for x in xs if x is not None)
    if not xs:
        return None
    n = len(xs)
    return xs[n // 2] if n % 2 else (xs[n // 2 - 1] + xs[n // 2]) / 2


def percentile(xs, p):
    xs = sorted(x for x in xs if x is not None)
    if not xs:
        return None
    if len(xs) == 1:
        return xs[0]
    k = (len(xs) - 1) * p / 100.0
    lo, hi = int(k), min(int(k) + 1, len(xs) - 1)
    return xs[lo] + (xs[hi] - xs[lo]) * (k - lo)


# ------------------------------------------------------------------ status
PASS, WARN, FAIL, INCONCLUSIVE, SKIP = "PASS", "WARN", "FAIL", "INCONCLUSIVE", "SKIP"


def worst(statuses) -> str:
    """FAIL > INCONCLUSIVE > WARN > PASS (SKIP ignored)."""
    s = set(statuses) - {SKIP}
    for k in (FAIL, INCONCLUSIVE, WARN):
        if k in s:
            return k
    return PASS


def chk(name, status, detail=""):
    return {"name": name, "status": status, "detail": detail}


# ------------------------------------------------------------------ server log
_TIMING = re.compile(r"\[timing\]\s+(.+?)\s+(\d+)\s*ms")
UPSTREAM_PAT = re.compile(
    r"\b429\b|\b503\b|\b500\b.*(?:gemini|google)|RateLimit|RESOURCE_EXHAUSTED|UNAVAILABLE|overloaded|"
    r"InternalServerError|ServiceUnavailable|quota|APIConnectionError|APITimeoutError", re.I)


def parse_timing_lines(lines) -> dict:
    """[timing] label  123 ms -> {label: ms} (first occurrence of each label)."""
    out = {}
    for ln in lines:
        m = _TIMING.search(ln)
        if m and m.group(1) not in out:
            out[m.group(1)] = int(m.group(2))
    return out


def upstream_error_in(lines) -> bool:
    return any(UPSTREAM_PAT.search(ln) for ln in lines)


# ------------------------------------------------------------------ per-turn checks
TAG_LEAK = re.compile(r"[\[【]\s*(?:ja|en|tr)\s*[\]】]", re.I)
TSUNDERE = re.compile(
    r"\bhah\b|\btch\b|\bhmph\b|it'?s not like|\bidiot\b|\bbaka\b|christina|"
    r"ふん|べ、別に|別にあなたのため|バカ|ばか|クリスティーナ", re.I)
CLAIMS_CHARACTER = re.compile(
    r"\bi(?:'m| am)\s+(?:a\s+)?(?:fictional|anime|game)\s+character|"
    r"\bi(?:'m| am)\s+(?:from|in)\s+(?:an?\s+)?(?:anime|game|visual novel)|"
    r"私は\s*(?:アニメ|ゲーム)の(?:キャラクター|キャラ)", re.I)
AI_IDENTITY = re.compile(
    r"\bAI\b|artificial|tutor|partner|conversation|会話|相手|ＡＩ|エーアイ|人工知能|チューター|先生|家庭教師|にほんごの|日本語の", re.I)

MAX_SPOKEN_LINES = 4
FIRST_AUDIO_WARN_S = 6.0
SIM_USER_MIN = 0.6
SIM_ROUNDTRIP_MIN = 0.5
RMS_MIN = 0.01


def check_wellformed(a: Analysis, http_status):
    if http_status != 200:
        return chk("http_200", FAIL, f"HTTP {http_status}")
    probs = list(a.problems)
    if a.parser_error:
        probs.append(a.parser_error)
    if a.leftover:
        probs.append(f"{a.leftover} trailing bytes (truncated frame)")
    if not a.metas:
        probs.append("no META frame")
    elif not a.done:
        probs.append("final META has done=false")
    for au in a.audio:
        if not is_wav(au["wav"]):
            probs.append(f"audio frame {au['seq']} is not a WAV")
            break
    if probs:
        return chk("frame_stream_wellformed", FAIL, "; ".join(probs))
    return chk("frame_stream_wellformed", PASS,
               f"{len(a.metas)} meta, {len(a.segments) + len(a.trs)} event, {len(a.audio)} audio, done=true")


def check_transcript(a: Analysis, input_text: str):
    heard = a.user_heard
    s = similarity(input_text, heard)
    st = PASS if s >= SIM_USER_MIN else WARN
    return chk("stt_transcript_similarity", st, f"ratio {s:.2f} (min {SIM_USER_MIN}); heard {heard!r}"), s


def check_ja_present(a: Analysis):
    n = len(a.ja_segments())
    return chk("ja_segment_present", PASS if n >= 1 else FAIL, f"{n} ja segment(s)")


def check_ruby(a: Analysis):
    ja = a.ja_segments()
    if not ja:
        return chk("ruby_readings", SKIP, "no ja segments")
    problems, kanji_segs = [], 0
    for s in ja:
        ruby = s.get("ruby")
        if not isinstance(ruby, list) or not ruby:
            problems.append(f"seg {s.get('id')}: no ruby list")
            continue
        if "".join(t.get("t", "") for t in ruby) != s.get("text", ""):
            problems.append(f"seg {s.get('id')}: ruby tokens do not rebuild the text")
        if has_kanji(s.get("text", "")):
            kanji_segs += 1
            if not any(t.get("r") for t in ruby):
                problems.append(f"seg {s.get('id')}: kanji but no reading at all")
            for t in ruby:
                if has_kanji(t.get("t", "")) and not t.get("r"):
                    problems.append(f"seg {s.get('id')}: token {t.get('t')!r} has kanji but empty reading")
    if problems:
        return chk("ruby_readings", FAIL, "; ".join(problems[:4]))
    if not kanji_segs:
        return chk("ruby_readings", PASS, "no kanji in the reply (ruby present, nothing to read)")
    return chk("ruby_readings", PASS, f"{kanji_segs} kanji segment(s), all kanji tokens have readings")


def check_tr_follows(a: Analysis):
    ja = a.ja_segments()
    if not ja:
        return chk("ja_has_following_tr", SKIP, "no ja segments")
    missing = []
    for s in ja:
        t = [x for x in a.trs if x.get("id") == s.get("id")]
        if not t or not (t[0].get("tr") or "").strip():
            missing.append(s.get("id"))
        elif t[0]["seq"] < s["seq"]:
            missing.append(f"{s.get('id')}(tr before segment)")
    if missing:
        return chk("ja_has_following_tr", FAIL, f"ja segment id(s) without a following tr: {missing}")
    return chk("ja_has_following_tr", PASS, f"{len(ja)}/{len(ja)} ja segments have a tr")


def check_audio_counts(a: Analysis):
    spoken = a.spoken_segments()
    n_aud = len(a.audio)
    en_spoken = [s.get("id") for s in a.segments if s.get("lang") != "ja" and s.get("spoken")]
    orphan = [x["seq"] for x in a.audio if x["seg_id"] is None]
    detail = f"{n_aud} audio frames, {len(spoken)} spoken segments, " \
             f"{len([s for s in a.segments if not s.get('spoken')])} silent, {len(a.trs)} tr"
    if en_spoken:
        return chk("audio_only_for_spoken_ja", FAIL, detail + f"; en segments marked spoken: {en_spoken}")
    if orphan:
        return chk("audio_only_for_spoken_ja", FAIL, detail + f"; audio frame(s) not following a spoken segment: {orphan}")
    if n_aud == len(spoken):
        return chk("audio_only_for_spoken_ja", PASS, detail)
    return chk("audio_only_for_spoken_ja", WARN, detail + " (count mismatch)")


def check_audio_loud(a: Analysis):
    if not a.audio:
        return chk("audio_non_silent", FAIL, "no audio frames")
    vals = [wav_rms(x["wav"]) for x in a.audio]
    low = [round(v, 4) for v in vals if v <= RMS_MIN]
    st = PASS if not low else FAIL
    return chk("audio_non_silent", st, f"rms min {min(vals):.3f} max {max(vals):.3f} (need > {RMS_MIN})"
               + (f"; silent frames: {low}" if low else ""))


def check_length(a: Analysis):
    n = len(a.spoken_segments())
    return chk("reply_length", PASS if n <= MAX_SPOKEN_LINES else FAIL,
               f"{n} spoken line(s) (max {MAX_SPOKEN_LINES})")


def check_no_leak(a: Analysis):
    texts = [m.get("reply", "") for m in a.metas] + [s.get("text", "") for s in a.segments] \
        + [t.get("tr", "") or "" for t in a.trs]
    bad = [t[:50] for t in texts if TAG_LEAK.search(t)]
    return chk("no_leaked_tags", PASS if not bad else FAIL,
               "no [ja]/[en]/[tr] in meta or segment text" if not bad else f"leaked: {bad[:2]}")


def check_latency(first_audio_s):
    if first_audio_s is None:
        return chk("first_audio_latency", SKIP, "no audio")
    st = PASS if first_audio_s <= FIRST_AUDIO_WARN_S else WARN
    return chk("first_audio_latency", st, f"{first_audio_s:.2f} s (warn > {FIRST_AUDIO_WARN_S:.0f} s)")


def all_reply_text(a: Analysis) -> str:
    parts = [a.reply_text] + [s.get("text", "") for s in a.segments] + [t.get("tr", "") or "" for t in a.trs]
    return "\n".join(parts)


def check_expect_any(a: Analysis, needles, name="content_expectation"):
    hay = unicodedata.normalize("NFKC", all_reply_text(a)).lower()
    hay_h = norm_text(to_hira(hay))
    for n in needles:
        nn = unicodedata.normalize("NFKC", n).lower()
        if nn in hay or norm_text(to_hira(nn)) in hay_h:
            return chk(name, PASS, f"found {n!r}")
    return chk(name, WARN, f"none of {needles} found in the reply")


def check_persona(a: Analysis):
    text = all_reply_text(a)
    out = []
    m = TSUNDERE.search(text)
    out.append(chk("persona_no_tsundere", FAIL if m else PASS,
                   f"marker {m.group(0)!r} found" if m else "no tsundere markers"))
    m = CLAIMS_CHARACTER.search(text)
    out.append(chk("persona_not_character", FAIL if m else PASS,
                   f"claims to be a fictional character: {m.group(0)!r}" if m else "does not claim to be a fictional character"))
    m = AI_IDENTITY.search(text)
    out.append(chk("persona_states_ai_identity", PASS if m else WARN,
                   f"identity wording: {m.group(0)!r}" if m else "no AI/partner wording in the reply"))
    return out


def structural_checks(a: Analysis, http_status):
    """The checks every normal /api/converse reply must satisfy (no whisper)."""
    wf = check_wellformed(a, http_status)
    out = [wf]
    if wf["status"] == FAIL:
        return out
    out += [check_ja_present(a), check_ruby(a), check_tr_follows(a), check_audio_counts(a),
            check_audio_loud(a), check_length(a), check_no_leak(a)]
    return out
