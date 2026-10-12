"""HTTP client for /api/converse that parses the frame stream live and records timings."""
from __future__ import annotations

import json
import time

import httpx

from helpers import FRAME_AUDIO, FRAME_EVENT, FRAME_META, FrameParser, analyze


def converse(base: str, token: str, session: str, wav: bytes, filename="input.wav", timeout=180) -> dict:
    """POST wav as multipart 'audio'. Returns status, frames, timings (seconds after
    send), analysis, transport error and json_body (for non-stream replies)."""
    parser = FrameParser()
    frames, err, status, body = [], None, None, b""
    t_send = time.perf_counter()
    first = {"meta": None, "segment": None, "audio": None}
    last = None
    ctype = ""
    try:
        with httpx.Client(timeout=httpx.Timeout(timeout, connect=10)) as c:
            with c.stream("POST", base + "/api/converse",
                          headers={"X-Access-Token": token, "X-Session-Id": session},
                          files={"audio": (filename, wav, "audio/wav")}) as r:
                status = r.status_code
                ctype = r.headers.get("content-type", "")
                for chunk in r.iter_raw():
                    now = time.perf_counter() - t_send
                    if status != 200:
                        body += chunk
                        continue
                    for fr in parser.feed(chunk):
                        fr.t = now
                        frames.append(fr)
                        last = now
                        if fr.type == FRAME_META and first["meta"] is None:
                            first["meta"] = now
                        elif fr.type == FRAME_AUDIO and first["audio"] is None:
                            first["audio"] = now
                        elif fr.type == FRAME_EVENT and first["segment"] is None:
                            try:
                                if json.loads(fr.payload).get("kind") == "segment":
                                    first["segment"] = now
                            except Exception:  # noqa: BLE001
                                pass
    except httpx.HTTPError as e:
        err = f"{type(e).__name__}: {e}"
    total = time.perf_counter() - t_send
    out = {
        "status": status, "error": err, "content_type": ctype,
        "frames": frames, "t_first_meta": first["meta"], "t_first_segment": first["segment"],
        "t_first_audio": first["audio"], "t_last_frame": last, "t_total": total,
        "analysis": analyze(frames, parser.leftover, parser.error) if status == 200 else None,
        "json_body": None,
    }
    if status != 200 and body:
        try:
            out["json_body"] = json.loads(body.decode("utf-8", "replace"))
        except Exception:  # noqa: BLE001
            out["json_body"] = {"raw": body[:200].decode("utf-8", "replace")}
    return out
