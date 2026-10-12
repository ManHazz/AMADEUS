"""End-to-end test suite for the Japanese-tutor voice agent.

    .venv\\Scripts\\python.exe e2e\\run_e2e.py [--scenario N[,M]] [--no-server] [--port P]

Starts the REAL server (own port 5077, DEBUG_TIMING=1) unless --no-server, feeds it
Kokoro-synthesised speech, parses the frame stream, runs automated assertions and
writes e2e/report/report.{md,html,json}. See e2e/README.md.
"""
from __future__ import annotations

import argparse
import atexit
import datetime as dt
import os
import pathlib
import sys
import time
import uuid

HERE = pathlib.Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))

import httpx  # noqa: E402

import audio_gen  # noqa: E402
import client  # noqa: E402
import helpers as H  # noqa: E402
import report  # noqa: E402
import server_ctl  # noqa: E402
from scenarios import SCENARIOS  # noqa: E402

OWN_PORT = 5077
REUSE_PORT = 5000
SLEEP_BETWEEN_TURNS = 4.0
RETRY_BACKOFF = 15.0
REPORT_DIR = HERE / "report"


def log(msg):
    print(msg, flush=True)


# ------------------------------------------------------------------ whisper (round-trip)
class Whisper:
    def __init__(self):
        self.model = None
        self.device = None

    def load(self):
        if self.model is not None:
            return
        if os.name == "nt":
            import site
            for sp in site.getsitepackages():
                for d in ("cublas", "cudnn", "cuda_nvrtc"):
                    b = os.path.join(sp, "nvidia", d, "bin")
                    if os.path.isdir(b):
                        os.add_dll_directory(b)
                        os.environ["PATH"] = b + os.pathsep + os.environ["PATH"]
        from faster_whisper import WhisperModel
        import numpy as np
        try:
            m = WhisperModel("small", device="cuda", compute_type="float16")
            list(m.transcribe(np.zeros(16000, dtype="float32"), language="ja")[0])
            self.model, self.device = m, "cuda/float16"
        except Exception as e:  # noqa: BLE001
            log(f"[e2e] whisper on CUDA unavailable ({type(e).__name__}); using CPU int8")
            self.model = WhisperModel("small", device="cpu", compute_type="int8")
            self.device = "cpu/int8"

    def ja(self, wav: bytes) -> str:
        self.load()
        data, sr = H.wav_to_float(wav)
        x = H.resample(data, sr, 16000)
        segs, _ = self.model.transcribe(x, language="ja", beam_size=5)
        return " ".join(s.text for s in segs).strip()


# ------------------------------------------------------------------ one request (+ retry)
class Ctx:
    def __init__(self, base, token, server, mode):
        self.base, self.token, self.server, self.mode = base, token, server, mode
        self.whisper = Whisper()
        self.gemini_requests = 0

    def log_mark(self):
        return len(self.server.lines) if self.server else 0

    def log_since(self, i):
        return self.server.lines[i:] if self.server else []


def _is_bad(res):
    if res["error"]:
        return True
    if res["status"] in (429, 500, 502, 503, 504):
        return True
    a = res["analysis"]
    return bool(res["status"] == 200 and (a is None or not a.done))


def post_with_retry(ctx: Ctx, session: str, wav: bytes, label: str):
    """-> (result, server_log_lines, retries, upstream_failed)"""
    retries = 0
    while True:
        mark = ctx.log_mark()
        res = client.converse(ctx.base, ctx.token, session, wav)
        if res["status"] == 200:
            ctx.gemini_requests += 1
        time.sleep(0.6)                                  # let the server flush its [timing] lines
        lines = ctx.log_since(mark)
        bad = _is_bad(res)
        upstream = bad and (H.upstream_error_in(lines) or ctx.server is None)
        if bad and upstream and retries < 1:
            retries += 1
            log(f"      upstream trouble ({res['status'] or res['error']}); retrying once in {RETRY_BACKOFF:.0f} s")
            time.sleep(RETRY_BACKOFF)
            continue
        return res, lines, retries, upstream and bad


def seg_summary(a: H.Analysis) -> str:
    ja = len(a.ja_segments())
    en = len([s for s in a.segments if s.get("lang") != "ja"])
    return f"ja x{ja}, en(silent) x{en}, tr x{len(a.trs)}, audio x{len(a.audio)}"


def ms(x):
    return None if x is None else round(x * 1000)


def run_turn(ctx: Ctx, sc, idx: int, turn, session: str, state: dict) -> dict:
    text = turn.text(state) if callable(turn.text) else turn.text
    out = {"index": idx + 1, "note": turn.note, "input": text, "heard": None, "similarity": None,
           "checks": [], "timings": {}, "segments": [], "retries": 0, "status": H.PASS,
           "reply_summary": "", "meta_reply": ""}
    if callable(turn.text) and not state.get("prev_ja"):
        out["checks"] = [H.chk("setup", H.INCONCLUSIVE, "previous reply gave no ja line to repeat")]
        out["status"] = H.INCONCLUSIVE
        return out
    path, wav = audio_gen.get_wav(text, turn.lang)
    out["audio_file"] = f"e2e/audio/{path.name}"
    log(f"    turn {idx + 1}: {text!r}")
    res, lines, retries, upstream_failed = post_with_retry(ctx, session, wav, text)
    out["retries"] = retries
    a = res["analysis"]
    tm = {"first_meta_ms": ms(res["t_first_meta"]), "first_segment_ms": ms(res["t_first_segment"]),
          "first_audio_ms": ms(res["t_first_audio"]), "last_frame_ms": ms(res["t_last_frame"]),
          "total_ms": ms(res["t_total"])}
    st = H.parse_timing_lines(lines)
    if st:
        tm["server_stt_ms"] = st.get("STT done")
        tm["server_llm_first_token_ms"] = st.get("LLM first token")
        tm["server_first_tts_ms"] = st.get("first TTS frame")
        tm["server_total_ms"] = st.get("total")
    out["timings"] = tm
    out["http_status"] = res["status"]

    if upstream_failed:
        detail = f"LLM upstream unavailable (HTTP {res['status']}, {res['error'] or 'stream cut'}); retried {retries}x"
        out["checks"] = [H.chk("llm_upstream", H.INCONCLUSIVE, detail)]
        out["status"] = H.INCONCLUSIVE
        return out

    if a is None:
        out["checks"] = [H.chk("http_200", H.FAIL, f"HTTP {res['status']} {res['error'] or res['json_body']}")]
        out["status"] = H.FAIL
        return out

    out["heard"] = a.user_heard
    out["meta_reply"] = a.reply_text
    out["reply_summary"] = seg_summary(a)
    for s in a.segments:
        out["segments"].append({"id": s.get("id"), "lang": s.get("lang"), "text": s.get("text"),
                                "spoken": s.get("spoken"), "ruby": s.get("ruby"), "tr": a.tr_for(s.get("id"))})
    checks = H.structural_checks(a, res["status"])
    tchk, sim = H.check_transcript(a, text)
    out["similarity"] = sim
    checks.insert(1, tchk)
    checks.append(H.check_latency(res["t_first_audio"]))
    if turn.expect_ja_any:
        checks.append(H.check_expect_any(a, turn.expect_ja_any))
    if turn.persona:
        checks += H.check_persona(a)

    # Whisper round-trip of the returned Japanese audio
    rt = []
    for au in a.audio:
        seg = next((s for s in a.segments if s.get("id") == au["seg_id"]), None)
        if not seg or seg.get("lang") != "ja":
            continue
        try:
            heard = ctx.whisper.ja(au["wav"])
        except Exception as e:  # noqa: BLE001
            checks.append(H.chk("whisper_roundtrip", H.INCONCLUSIVE, f"whisper failed: {type(e).__name__}: {e}"))
            rt = None
            break
        rt.append({"id": seg["id"], "text": seg["text"], "heard": heard,
                   "sim": H.similarity(seg["text"], heard)})
    if rt is not None:
        if rt:
            low = [r for r in rt if r["sim"] < H.SIM_ROUNDTRIP_MIN]
            detail = "; ".join(f"seg{r['id']} {r['sim']:.2f}" for r in rt)
            if low:
                detail += "; below 0.5: " + ", ".join(repr(r["heard"]) for r in low)
            checks.append(H.chk("whisper_roundtrip", H.PASS if not low else H.WARN,
                                f"min {min(r['sim'] for r in rt):.2f} (min {H.SIM_ROUNDTRIP_MIN}); {detail}"))
        else:
            checks.append(H.chk("whisper_roundtrip", H.SKIP, "no ja audio"))
    out["roundtrip"] = rt or []

    # keep the returned audio for inspection
    rep = REPORT_DIR / "replies"
    rep.mkdir(parents=True, exist_ok=True)
    for i, au in enumerate(a.audio):
        (rep / f"s{sc.id}_t{idx + 1}_{i}.wav").write_bytes(au["wav"])

    out["checks"] = checks
    out["status"] = H.worst(c["status"] for c in checks)
    first_ja = a.ja_segments()
    state["prev_ja"] = first_ja[0]["text"] if first_ja else None
    log(f"      -> {out['status']}  heard={a.user_heard!r}  sim={sim:.2f}  first_audio={tm['first_audio_ms']} ms  [{out['reply_summary']}]")
    return out


# ------------------------------------------------------------------ special scenarios
def run_noise(ctx: Ctx, sc) -> list[dict]:
    turns = []
    for i, (label, wav) in enumerate([("2 s of low-level noise", audio_gen.noise_wav(2.0, 0.01)),
                                      ("0.05 s clip (too short)", audio_gen.tiny_wav(0.05))]):
        log(f"    turn {i + 1}: {label}")
        session = str(uuid.uuid4())
        mark = ctx.log_mark()
        res = client.converse(ctx.base, ctx.token, session, wav)
        time.sleep(0.5)
        lines = ctx.log_since(mark)
        checks = []
        st = res["status"]
        if res["error"]:
            checks.append(H.chk("graceful_response", H.FAIL, f"transport error {res['error']}"))
        elif st is not None and st >= 500:
            checks.append(H.chk("graceful_response", H.FAIL, f"server error HTTP {st}"))
        elif st == 422:
            msg = (res["json_body"] or {}).get("error")
            checks.append(H.chk("graceful_response", H.PASS if msg else H.WARN,
                                f"HTTP 422 with JSON error {msg!r}"))
        elif st == 200:
            a = res["analysis"]
            checks.append(H.check_wellformed(a, st))
            checks.append(H.chk("no_hallucinated_transcript", H.WARN,
                                f"noise was transcribed as {a.user_heard!r} and sent to the LLM"))
        else:
            checks.append(H.chk("graceful_response", H.WARN, f"unexpected HTTP {st}"))
        if any("Traceback" in ln for ln in lines):
            checks.append(H.chk("server_log_clean", H.FAIL, "traceback in server log"))
        tm = {"total_ms": ms(res["t_total"])}
        a = res["analysis"]
        turns.append({"index": i + 1, "note": "", "input": label, "heard": (a.user_heard if a else None),
                      "similarity": None, "checks": checks, "timings": tm, "segments": [], "retries": 0,
                      "status": H.worst(c["status"] for c in checks), "http_status": st,
                      "reply_summary": f"HTTP {st}: {res['json_body']}" if st != 200 else "", "meta_reply": ""})
        log(f"      -> {turns[-1]['status']} (HTTP {st})")
    return turns


def run_health(ctx: Ctx, sc) -> list[dict]:
    checks = []
    base, tok = ctx.base, ctx.token
    session = str(uuid.uuid4())
    hdr = {"X-Access-Token": tok, "X-Session-Id": session}
    t0 = time.perf_counter()
    try:
        r = httpx.get(base + "/health", timeout=10)
        j = r.json()
        miss = [k for k in ("ok", "brain", "model", "stt", "tts", "active_sessions") if k not in j]
        checks.append(H.chk("GET /health", H.PASS if r.status_code == 200 and j.get("ok") and not miss else H.FAIL,
                            f"HTTP {r.status_code}, model {j.get('model')}, stt {j.get('stt')}, tts {j.get('tts')}"
                            + (f", missing {miss}" if miss else "")))
        r = httpx.get(base + "/", timeout=10)
        ok = r.status_code == 200 and "__ACCESS_TOKEN__" not in r.text and "__SESSION_ID__" not in r.text
        checks.append(H.chk("GET / (page, token+session injected)", H.PASS if ok else H.FAIL,
                            f"HTTP {r.status_code}, placeholders replaced: {ok}"))
        r = httpx.post(base + "/api/reset", headers=hdr, timeout=10)
        checks.append(H.chk("POST /api/reset (valid token)",
                            H.PASS if r.status_code == 200 and r.json().get("status") == "reset" else H.FAIL,
                            f"HTTP {r.status_code} {r.text[:60]}"))
        r = httpx.post(base + "/api/reset", headers={"X-Session-Id": session}, timeout=10)
        checks.append(H.chk("POST /api/reset (no token) rejected", H.PASS if r.status_code == 401 else H.FAIL,
                            f"HTTP {r.status_code}"))
        r = httpx.post(base + "/api/converse", headers={"X-Access-Token": "wrong", "X-Session-Id": session},
                       files={"audio": ("a.wav", audio_gen.tiny_wav(0.3), "audio/wav")}, timeout=20)
        checks.append(H.chk("POST /api/converse (bad token) rejected", H.PASS if r.status_code == 401 else H.FAIL,
                            f"HTTP {r.status_code}"))
        r = httpx.get(base + "/api/pending", headers=hdr, timeout=10)
        checks.append(H.chk("GET /api/pending (nothing queued)", H.PASS if r.status_code == 204 else H.WARN,
                            f"HTTP {r.status_code}"))
    except Exception as e:  # noqa: BLE001
        checks.append(H.chk("endpoints reachable", H.FAIL, f"{type(e).__name__}: {e}"))
    status = H.worst(c["status"] for c in checks)
    log(f"      -> {status}")
    return [{"index": 1, "note": "", "input": "(HTTP calls, no audio)", "heard": None, "similarity": None,
             "checks": checks, "timings": {"total_ms": ms(time.perf_counter() - t0)}, "segments": [],
             "retries": 0, "status": status, "http_status": 200, "reply_summary": "", "meta_reply": ""}]


# ------------------------------------------------------------------ main
def pick_server(args, env_file):
    """-> (server_or_None, base, mode_text). Exits with a clear message if nothing usable."""
    if args.no_server:
        port = args.port or REUSE_PORT
        base = f"http://127.0.0.1:{port}"
        if not server_ctl.health(base):
            sys.exit(f"[e2e] --no-server: no healthy server on {base} (GET /health failed). Start it first.")
        return None, base, f"reusing existing server on port {port} (--no-server)"
    port = args.port or OWN_PORT
    base = f"http://127.0.0.1:{port}"
    if server_ctl.health(base):
        return None, base, f"reusing server already healthy on port {port}"
    free = server_ctl.gpu_free_mb()
    why = None
    if free is not None and free < server_ctl.MIN_FREE_MB:
        why = f"only {free} MiB GPU VRAM free (< {server_ctl.MIN_FREE_MB} needed for a 2nd instance)"
    else:
        srv = server_ctl.ServerProc(port, env_file)
        log(f"[e2e] starting server on port {port} (GPU free: {free} MiB) ...")
        srv.start()
        atexit.register(srv.stop)
        ok, msg = srv.wait_ready()
        if ok:
            return srv, base, f"own server subprocess on port {port} ({msg})"
        why = f"own server failed: {msg}\n" + srv.tail(8)
        srv.stop()
    fb = f"http://127.0.0.1:{REUSE_PORT}"
    if server_ctl.health(fb):
        log(f"[e2e] cannot start own server ({why.splitlines()[0]}); reusing healthy server on port {REUSE_PORT}")
        return None, fb, f"reusing existing server on port {REUSE_PORT} (own instance impossible: {why.splitlines()[0]})"
    sys.exit(f"[e2e] ERROR: cannot start a server on port {port} ({why}) and no healthy server on port {REUSE_PORT}.\n"
             f"       Free some GPU memory or start the agent (run_local.ps1) and re-run with --no-server.")


def main():
    for s in (sys.stdout, sys.stderr):
        try:
            s.reconfigure(encoding="utf-8", errors="replace")
        except Exception:  # noqa: BLE001
            pass
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--scenario", "-s", action="append", default=[], help="scenario number(s), e.g. 3 or 1,2,8")
    ap.add_argument("--no-server", action="store_true", help="do not start a server; use a healthy one on --port (default 5000)")
    ap.add_argument("--port", type=int, default=None)
    args = ap.parse_args()
    wanted = {int(x) for part in args.scenario for x in part.split(",") if x.strip()}
    todo = [s for s in SCENARIOS if not wanted or s.id in wanted]
    if not todo:
        sys.exit(f"no such scenario; available 1..{len(SCENARIOS)}")

    env_file = server_ctl.load_env()
    token = env_file.get("VOICE_AGENT_TOKEN", "")
    if not token and not args.no_server:
        token = uuid.uuid4().hex
        env_file["VOICE_AGENT_TOKEN"] = token
    if not token:
        sys.exit("[e2e] VOICE_AGENT_TOKEN is not set in .env; cannot authenticate to an existing server.")

    started = dt.datetime.now()
    srv = None
    try:
        srv, base, mode = pick_server(args, env_file)
        log(f"[e2e] server: {mode}")
        hl = server_ctl.health(base) or {}
        ctx = Ctx(base, token, srv, mode)
        results = []
        n_gem_turns = 0
        for sc in todo:
            log(f"[e2e] scenario {sc.id}: {sc.name}")
            state: dict = {}
            session = str(uuid.uuid4())
            if sc.kind == "noise":
                turns = run_noise(ctx, sc)
            elif sc.kind == "health":
                turns = run_health(ctx, sc)
            else:
                turns = []
                for i, t in enumerate(sc.turns):
                    if i:
                        time.sleep(SLEEP_BETWEEN_TURNS)
                    turns.append(run_turn(ctx, sc, i, t, session, state))
                time.sleep(SLEEP_BETWEEN_TURNS)
            results.append({"id": sc.id, "name": sc.name, "demo_ref": sc.demo_ref, "kind": sc.kind,
                            "status": H.worst(t["status"] for t in turns), "turns": turns})
        meta = {
            "started": started.isoformat(timespec="seconds"),
            "duration_s": round((dt.datetime.now() - started).total_seconds()),
            "server_mode": mode, "base_url": base, "model": hl.get("model"), "stt": hl.get("stt"),
            "tts": hl.get("tts"), "whisper_roundtrip_device": ctx.whisper.device,
            "llm_requests": ctx.gemini_requests, "debug_timing_log": srv is not None,
            "scenarios_run": [s.id for s in todo],
        }
        data = report.build(meta, results)
        report.write_all(REPORT_DIR, data)
        if srv:
            (REPORT_DIR / "server.log").write_text("\n".join(srv.lines), encoding="utf-8")
        s = data["summary"]
        log(f"[e2e] done in {meta['duration_s']} s: scenarios {s['scenarios']}, turns {s['turns']}, checks {s['checks']}")
        log(f"[e2e] median first audio: {s['latency']['first_audio_ms']['median']} ms; report: {REPORT_DIR / 'report.html'}")
        return 1 if s["scenarios"].get("FAIL") else 0
    finally:
        if srv:
            srv.stop()
            log("[e2e] server subprocess terminated")


if __name__ == "__main__":
    sys.exit(main())
