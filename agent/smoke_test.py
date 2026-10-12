"""Offline smoke test: TTS -> file -> STT round trip, plus a brain round trip.

    .venv/bin/python smoke_test.py          # uses BRAIN_URL / BRAIN_MODEL / *_DEVICE from env
"""
import time
import tempfile
import os

import numpy as np
import soundfile as sf

BRAIN_URL = os.environ.get("BRAIN_URL", "http://127.0.0.1:8081/v1")
BRAIN_MODEL = os.environ.get("BRAIN_MODEL", "local-model")
STT_DEVICE = os.environ.get("STT_DEVICE", "cpu")
TTS_DEVICE = os.environ.get("TTS_DEVICE", "cpu")
STT_COMPUTE = "int8" if STT_DEVICE == "cpu" else "int8_float16"
print(f"brain={BRAIN_URL}  stt={STT_DEVICE}({STT_COMPUTE})  tts={TTS_DEVICE}", flush=True)

print("1/4 kokoro TTS ...", flush=True)
t = time.time()
from kokoro import KPipeline
tts = KPipeline(lang_code="a", device=TTS_DEVICE)
phrase = "Testing one two three, the quick brown fox jumps over the lazy dog."
audio = np.concatenate([c for _, _, c in tts(phrase, voice="af_heart")]).astype("float32")
wav = os.path.join(tempfile.gettempdir(), "smoke_tts.wav")
sf.write(wav, audio, 24000)
print(f"    ok  {audio.shape[0] / 24000:.1f}s audio in {time.time() - t:.1f}s", flush=True)

print("2/4 faster-whisper STT ...", flush=True)
t = time.time()
from faster_whisper import WhisperModel
stt = WhisperModel("small", device=STT_DEVICE, compute_type=STT_COMPUTE)
segs, _ = stt.transcribe(wav, language="en", beam_size=5)
text = " ".join(s.text for s in segs).strip()
print(f"    ok  in {time.time() - t:.1f}s  heard: {text!r}", flush=True)

print("3/4 brain round trip ...", flush=True)
t = time.time()
from openai import OpenAI
c = OpenAI(base_url=BRAIN_URL, api_key="not-needed")
r = c.chat.completions.create(
    model=BRAIN_MODEL,
    messages=[{"role": "user", "content": "Reply with a single short sentence: are you there?"}],
    max_tokens=64,
)
print(f"    ok  in {time.time() - t:.1f}s  reply: {r.choices[0].message.content!r}", flush=True)

print("4/4 full chain (STT text -> brain -> TTS) ...", flush=True)
t = time.time()
r = c.chat.completions.create(
    model=BRAIN_MODEL,
    messages=[{"role": "system", "content": "You are a concise voice assistant."},
              {"role": "user", "content": text}],
    max_tokens=128,
)
reply = r.choices[0].message.content.strip()
out = np.concatenate([c2 for _, _, c2 in tts(reply, voice="af_heart")]).astype("float32")
sf.write(os.path.join(tempfile.gettempdir(), "smoke_reply.wav"), out, 24000)
print(f"    ok  in {time.time() - t:.1f}s  reply: {reply!r}")
print("ALL GREEN")
