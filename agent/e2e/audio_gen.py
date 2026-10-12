"""Offline spoken test inputs: text -> local Kokoro -> 16 kHz mono WAV, cached in e2e/audio/.

Runs Kokoro on the CPU on purpose (keeps GPU VRAM free for the server under test).
English text uses the English voice, text containing Japanese script uses the
Japanese voice (same voices the agent itself uses, so the loop is synthetic-on-synthetic).
"""
from __future__ import annotations

import hashlib
import io
import pathlib

import numpy as np
import soundfile as sf

from helpers import has_japanese, resample

HERE = pathlib.Path(__file__).parent
AUDIO_DIR = HERE / "audio"
VOICE_EN, VOICE_JA = "af_heart", "jf_alpha"
_pipes: dict = {}


def _pipe(lang_code: str):
    if lang_code not in _pipes:
        from kokoro import KPipeline
        if lang_code == "j" and "a" in _pipes:
            _pipes["j"] = KPipeline(lang_code="j", model=_pipes["a"].model, device="cpu")
        else:
            _pipes[lang_code] = KPipeline(lang_code=lang_code, device="cpu")
    return _pipes[lang_code]


def synth_16k(text: str, lang: str) -> np.ndarray:
    pipe = _pipe("j" if lang == "ja" else "a")
    voice = VOICE_JA if lang == "ja" else VOICE_EN
    parts = [a for _, _, a in pipe(text, voice=voice) if a is not None]
    audio = np.concatenate([np.asarray(p, dtype="float32") for p in parts])
    pad = np.zeros(int(0.25 * 24000), dtype="float32")        # short lead/tail silence like a real clip
    return resample(np.concatenate([pad, audio, pad]), 24000, 16000)


def get_wav(text: str, lang: str | None = None) -> tuple[pathlib.Path, bytes]:
    """Cached synthesis; returns (path, wav bytes)."""
    lang = lang or ("ja" if has_japanese(text) else "en")
    key = hashlib.sha1(f"{lang}|{text}".encode("utf-8")).hexdigest()[:10]
    AUDIO_DIR.mkdir(exist_ok=True)
    path = AUDIO_DIR / f"{lang}_{key}.wav"
    if not path.exists():
        sf.write(str(path), synth_16k(text, lang), 16000, subtype="PCM_16")
    return path, path.read_bytes()


def noise_wav(seconds: float = 2.0, level: float = 0.01, seed: int = 0) -> bytes:
    rng = np.random.default_rng(seed)
    x = rng.normal(0, level, int(16000 * seconds)).astype("float32")
    buf = io.BytesIO()
    sf.write(buf, x, 16000, format="WAV", subtype="PCM_16")
    return buf.getvalue()


def tiny_wav(seconds: float = 0.05) -> bytes:
    buf = io.BytesIO()
    sf.write(buf, np.zeros(int(16000 * seconds), dtype="float32"), 16000, format="WAV", subtype="PCM_16")
    return buf.getvalue()
