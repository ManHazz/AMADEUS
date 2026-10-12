"""Mic + STT diagnostic. Run with the Voice-Agent venv python:
  Voice-Agent\\.venv\\Scripts\\python.exe AMADEUS\\tools\\mic_test.py [--device N] [--play] [--file x.wav] [--models small,medium,large-v3-turbo]
Stop the agent server (port 5000) first so VRAM is free.
"""
import argparse, math, subprocess, sys, time
from pathlib import Path

import os, site
import numpy as np
import soundfile as sf

# Windows: make pip-installed CUDA 12 DLLs findable by ctranslate2 (same as Voice-Agent/pipeline.py)
if sys.platform == "win32":
    for _sp in site.getsitepackages():
        for _d in ("cublas", "cudnn", "cuda_nvrtc"):
            _bin = os.path.join(_sp, "nvidia", _d, "bin")
            if os.path.isdir(_bin):
                os.add_dll_directory(_bin)
                os.environ["PATH"] = _bin + os.pathsep + os.environ["PATH"]

SR = 16000
OUT = Path(__file__).resolve().parent.parent / "Logs" / "mic_test.wav"
VRAM_NEED_MB = {"small": 1500, "medium": 3500, "large-v3-turbo": 3500}


def dbfs(x):
    return 20 * math.log10(max(float(x), 1e-10))


def list_devices():
    import sounddevice as sd
    print("Input devices:")
    default = sd.default.device[0]
    for i, d in enumerate(sd.query_devices()):
        if d["max_input_channels"] > 0:
            api = sd.query_hostapis(d["hostapi"])["name"]
            print(f"  [{i}] {d['name']} ({api}, {d['max_input_channels']}ch, {int(d['default_samplerate'])} Hz)"
                  + ("   <-- default" if i == default else ""))
    return default


def record(device):
    import sounddevice as sd
    for n in (3, 2, 1):
        print(f"Speak in {n}...", flush=True)
        time.sleep(1)
    print("SPEAK NOW (6 s): say a normal sentence.", flush=True)
    a = sd.rec(6 * SR, samplerate=SR, channels=1, dtype="float32", device=device)
    sd.wait()
    print("done.")
    return a[:, 0]


def analyze(a):
    peak, rms = dbfs(np.max(np.abs(a))), dbfs(np.sqrt(np.mean(a ** 2)))
    floor = dbfs(np.sqrt(np.mean(a[: SR // 2] ** 2)))
    print(f"Peak {peak:.1f} dBFS | RMS {rms:.1f} dBFS | noise floor (first 0.5s) {floor:.1f} dBFS | SNR~{rms - floor:.1f} dB")
    if peak > -1: print("  !! CLIPPING: lower mic gain")
    if rms < -35: print("  !! TOO QUIET: raise mic gain / move closer / wrong device")
    if floor > -50: print("  !! HIGH NOISE FLOOR (> -50 dBFS): noisy mic/room")
    if rms - floor < 15: print("  !! LOW SNR (<15 dB): speech barely above noise (or you did not speak in the first 0.5 s window? it should be silence)")


def free_vram_mb():
    try:
        o = subprocess.check_output(["nvidia-smi", "--query-gpu=memory.free", "--format=csv,noheader,nounits"], text=True)
        return int(o.strip().splitlines()[0])
    except Exception:
        return None


def transcribe_all(path, models):
    from faster_whisper import WhisperModel
    import gc
    for m in models:
        free = free_vram_mb()
        need = VRAM_NEED_MB.get(m, 3500)
        if free is not None and free < need:
            print(f"\n[{m}] SKIP: {free} MiB free < ~{need} needed. Stop the agent server (port 5000) and retry.")
            continue
        try:
            t = time.perf_counter()
            wm = WhisperModel(m, device="cuda", compute_type="float16")
            print(f"\n[{m}] loaded in {time.perf_counter() - t:.1f}s")
        except Exception as e:
            print(f"\n[{m}] load failed: {type(e).__name__}: {str(e)[:150]}")
            continue
        for beam in (1, 5):
            for lang in ("en", None):
                try:
                    t = time.perf_counter()
                    segs, info = wm.transcribe(str(path), language=lang, beam_size=beam)
                    segs = list(segs)
                    dt = time.perf_counter() - t
                    text = " ".join(s.text.strip() for s in segs)
                    lp = np.mean([s.avg_logprob for s in segs]) if segs else float("nan")
                    ns = np.mean([s.no_speech_prob for s in segs]) if segs else float("nan")
                    print(f"  beam={beam} lang={lang or 'auto'}({info.language} {info.language_probability:.2f}) "
                          f"{dt:.2f}s avg_logprob={lp:.2f} no_speech={ns:.2f}\n    -> {text!r}")
                except Exception as e:
                    print(f"  beam={beam} lang={lang}: error {type(e).__name__}: {str(e)[:150]}")
        del wm
        gc.collect()


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--device", type=int)
    ap.add_argument("--play", action="store_true")
    ap.add_argument("--file", help="skip recording, analyze/transcribe this wav")
    ap.add_argument("--models", default="small,medium,large-v3-turbo")
    ap.add_argument("--no-stt", action="store_true")
    a = ap.parse_args()

    if a.file:
        audio, sr = sf.read(a.file, dtype="float32")
        if audio.ndim > 1: audio = audio.mean(axis=1)
        if sr != SR:
            audio = np.interp(np.linspace(0, len(audio), int(len(audio) * SR / sr), endpoint=False), np.arange(len(audio)), audio).astype("float32")
        path = Path(a.file)
    else:
        default = list_devices()
        print(f"Using device: {a.device if a.device is not None else default}")
        audio = record(a.device)
        OUT.parent.mkdir(parents=True, exist_ok=True)
        sf.write(OUT, audio, SR)
        path = OUT
        print(f"Saved {OUT}")
    analyze(audio)
    if a.play and not a.file:
        import sounddevice as sd
        print("Playing back..."); sd.play(audio, SR); sd.wait()
    if not a.no_stt:
        transcribe_all(path, [m.strip() for m in a.models.split(",") if m.strip()])


if __name__ == "__main__":
    sys.exit(main())
