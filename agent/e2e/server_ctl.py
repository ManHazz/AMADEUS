"""Start/stop the real voice-agent server as a subprocess; read .env without ever printing it."""
from __future__ import annotations

import os
import pathlib
import subprocess
import threading
import time

import httpx

REPO = pathlib.Path(__file__).resolve().parent.parent
PY = REPO / ".venv" / "Scripts" / "python.exe"
MIN_FREE_MB = 1800          # below this a 2nd server (whisper + kokoro on CUDA) will not fit


def load_env(path: pathlib.Path | None = None) -> dict:
    """Parse .env into a dict. Values are never printed by this suite."""
    env = {}
    p = path or REPO / ".env"
    if p.exists():
        for ln in p.read_text(encoding="utf-8").splitlines():
            ln = ln.strip()
            if not ln or ln.startswith("#") or "=" not in ln:
                continue
            k, v = ln.split("=", 1)
            env[k.strip()] = v.strip().strip('"').strip("'")
    return env


def gpu_free_mb():
    try:
        out = subprocess.run(["nvidia-smi", "--query-gpu=memory.free", "--format=csv,noheader,nounits"],
                             capture_output=True, text=True, timeout=15).stdout.strip().splitlines()
        return int(out[0]) if out else None
    except Exception:  # noqa: BLE001
        return None


def health(base: str, timeout=3.0):
    try:
        r = httpx.get(base + "/health", timeout=timeout)
        if r.status_code == 200 and r.json().get("ok"):
            return r.json()
    except Exception:  # noqa: BLE001
        pass
    return None


class ServerProc:
    """Real `uvicorn server:app` child. stdout+stderr are captured (token/API key redacted)."""

    def __init__(self, port: int, env_file: dict):
        self.port = port
        self.base = f"http://127.0.0.1:{port}"
        self.lines: list[str] = []
        self.proc: subprocess.Popen | None = None
        self.secrets = [v for k, v in env_file.items()
                        if v and any(w in k.upper() for w in ("KEY", "TOKEN", "SECRET", "PASSWORD")) and len(v) >= 6]
        env = os.environ.copy()
        env.update(env_file)
        env.update({"DEBUG_TIMING": "1", "PORT": str(port), "PYTHONUTF8": "1", "PYTHONUNBUFFERED": "1"})
        self.env = env

    def _redact(self, s: str) -> str:
        for sec in self.secrets:
            s = s.replace(sec, "***")
        return s

    def _pump(self):
        for raw in self.proc.stdout:
            self.lines.append(self._redact(raw.rstrip("\r\n")))

    def start(self):
        self.proc = subprocess.Popen(
            [str(PY), "-m", "uvicorn", "server:app", "--host", "127.0.0.1", "--port", str(self.port)],
            cwd=str(REPO), env=self.env, stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
            text=True, encoding="utf-8", errors="replace",
            creationflags=getattr(subprocess, "CREATE_NEW_PROCESS_GROUP", 0))
        threading.Thread(target=self._pump, daemon=True).start()

    def wait_ready(self, timeout=420):
        t0 = time.time()
        while time.time() - t0 < timeout:
            if self.proc.poll() is not None:
                return False, f"server exited early (code {self.proc.returncode})"
            if health(self.base, 2.0):
                return True, f"ready after {time.time() - t0:.0f} s"
            time.sleep(2)
        return False, f"not healthy after {timeout} s"

    def stop(self):
        """Kill the whole process tree (venv launcher + python + children)."""
        p = self.proc
        if p is None:
            return
        try:
            if p.poll() is None:
                if os.name == "nt":
                    subprocess.run(["taskkill", "/F", "/T", "/PID", str(p.pid)],
                                   capture_output=True, timeout=30)
                else:
                    p.kill()
                try:
                    p.wait(timeout=15)
                except Exception:  # noqa: BLE001
                    pass
        finally:
            self.proc = None

    def tail(self, n=15):
        return "\n".join(self.lines[-n:])
