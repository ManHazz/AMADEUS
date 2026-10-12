"""Pytest bootstrap.

Stubs the heavy ML deps (faster_whisper, kokoro) BEFORE anything imports
pipeline.py, so the unit tests never need a GPU, a model download, or even
the real packages installed — only fastapi/openai/httpx/numpy/soundfile
(all light, no compiled/CUDA bits). This suite only exercises the pure logic
in pipeline.py / chat_agent.py / search_agent.py / server.py's routing.

Actual STT/TTS/brain behavior against a live deployment is covered by
smoke_test.py and stream_test.py, run by hand — see notes/testing-notes for
why those aren't run in CI.
"""
import sys
import types
import pathlib

import numpy as np

# make `import pipeline` etc. work no matter where pytest is invoked from
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent.parent))


class _FakeWhisperModel:
    def __init__(self, *a, **kw):
        pass

    def transcribe(self, *a, **kw):
        return [], None


class _FakeKPipeline:
    def __init__(self, *a, **kw):
        pass

    def __call__(self, text, voice=None):
        yield (None, None, np.zeros(10, dtype="float32"))


if "faster_whisper" not in sys.modules:
    _fw = types.ModuleType("faster_whisper")
    _fw.WhisperModel = _FakeWhisperModel
    sys.modules["faster_whisper"] = _fw

if "kokoro" not in sys.modules:
    _kokoro = types.ModuleType("kokoro")
    _kokoro.KPipeline = _FakeKPipeline
    sys.modules["kokoro"] = _kokoro
