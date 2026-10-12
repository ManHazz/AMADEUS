"""Offline unit tests for the e2e suite's frame parser and scoring helpers
(no GPU, no network, no server)."""
import io
import json
import pathlib
import struct
import sys

import numpy as np
import soundfile as sf

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent.parent / "e2e"))

import helpers as H  # noqa: E402


def wav_bytes(amp=0.1, n=2400, sr=24000):
    t = np.arange(n) / sr
    buf = io.BytesIO()
    sf.write(buf, (amp * np.sin(2 * np.pi * 220 * t)).astype("float32"), sr, format="WAV")
    return buf.getvalue()


def ev(**kw):
    return H.encode_frame(H.FRAME_EVENT, json.dumps(kw).encode())


def meta(user="", reply="", done=False):
    return H.encode_frame(H.FRAME_META, json.dumps({"user": user, "reply": reply, "done": done}).encode())


def good_stream(**over):
    ruby = [{"t": "昨日", "r": "きのう"}, {"t": "は", "r": ""}]
    parts = [meta("hi"),
             ev(kind="segment", id=0, lang="ja", text="昨日は", ruby=ruby, tr=None, spoken=True),
             H.encode_frame(H.FRAME_AUDIO, wav_bytes()),
             ev(kind="segment_tr", id=0, tr="Yesterday"),
             ev(kind="segment", id=1, lang="en", text="a tip", ruby=[], tr=None, spoken=False),
             meta("hi", "ok", done=True)]
    return b"".join(parts)


def analyze_bytes(data):
    frames, p = H.parse_stream(data)
    return H.analyze(frames, p.leftover, p.error)


# ---- frame parser
def test_parser_byte_by_byte_equals_one_shot():
    data = good_stream()
    p = H.FrameParser()
    frames = []
    for i in range(len(data)):
        frames += p.feed(data[i:i + 1])
    assert [f.type for f in frames] == [f.type for f in H.parse_stream(data)[0]]
    assert p.leftover == 0 and p.error is None


def test_parser_detects_truncation_and_unknown_type():
    data = good_stream()
    frames, p = H.parse_stream(data[:-3])
    assert p.leftover > 0
    _, p2 = H.parse_stream(b"\x09" + struct.pack(">I", 1) + b"x")
    assert p2.error and "unknown" in p2.error


# ---- analysis + checks
def test_good_stream_passes_structural_checks():
    a = analyze_bytes(good_stream())
    assert a.done and a.user_heard == "hi"
    checks = {c["name"]: c for c in H.structural_checks(a, 200)}
    assert all(c["status"] == H.PASS for c in checks.values()), checks
    assert a.audio[0]["seg_id"] == 0


def test_missing_tr_fails():
    data = good_stream().replace(ev(kind="segment_tr", id=0, tr="Yesterday"), b"")
    a = analyze_bytes(data)
    assert H.check_tr_follows(a)["status"] == H.FAIL


def test_audio_for_silent_en_segment_fails():
    data = b"".join([meta("x"),
                     ev(kind="segment", id=0, lang="en", text="tip", ruby=[], tr=None, spoken=False),
                     H.encode_frame(H.FRAME_AUDIO, wav_bytes()), meta("x", "r", done=True)])
    a = analyze_bytes(data)
    assert H.check_audio_counts(a)["status"] == H.FAIL


def test_ruby_empty_reading_for_kanji_fails():
    bad = [{"t": "昨日", "r": ""}]
    data = b"".join([meta("x"), ev(kind="segment", id=0, lang="ja", text="昨日", ruby=bad, tr=None, spoken=True),
                     meta("x", "r", done=True)])
    assert H.check_ruby(analyze_bytes(data))["status"] == H.FAIL


def test_tag_leak_and_length_and_silence():
    segs = [ev(kind="segment", id=i, lang="ja", text=f"はい{i}", ruby=[{"t": f"はい{i}", "r": ""}], tr=None, spoken=True)
            for i in range(5)]
    a = analyze_bytes(b"".join([meta("x")] + segs + [meta("x", "[ja] leaked", done=True)]))
    assert H.check_length(a)["status"] == H.FAIL
    assert H.check_no_leak(a)["status"] == H.FAIL
    quiet = analyze_bytes(b"".join([meta("x"), ev(kind="segment", id=0, lang="ja", text="は", ruby=[{"t": "は", "r": ""}],
                                                 tr=None, spoken=True),
                                    H.encode_frame(H.FRAME_AUDIO, wav_bytes(amp=0.001)), meta("x", "r", True)]))
    assert H.check_audio_loud(quiet)["status"] == H.FAIL
    assert H.wav_rms(wav_bytes(amp=0.1)) > 0.01


# ---- scoring helpers
def test_similarity_english_and_japanese():
    assert H.similarity("Teach me how to say good morning.", "teach me how to say good morning") == 1.0
    assert H.similarity("おはようございます", "おはようございます。") == 1.0
    assert H.similarity("こんにちは", "さようなら") < 0.6
    assert H.similarity("", "x") == 0.0


def test_similarity_kanji_vs_kana_fold():
    assert H.similarity("昨日、映画を見ます", "きのうえいがをみます") > 0.9


def test_persona_checks():
    def mk(text):
        return analyze_bytes(b"".join([meta("x"), ev(kind="segment", id=0, lang="en", text=text, ruby=[], tr=None, spoken=False),
                                       meta("x", text, done=True)]))
    ok = H.check_persona(mk("No, I am an AI tutor."))
    assert [c["status"] for c in ok] == [H.PASS, H.PASS, H.PASS]
    bad = H.check_persona(mk("Hah? Tch, it's not like I am a tutor."))
    assert bad[0]["status"] == H.FAIL
    assert H.check_persona(mk("I am a fictional character."))[1]["status"] == H.FAIL


def test_stats_status_and_timing_parse():
    assert H.median([3, 1, 2]) == 2 and H.median([1, 2, 3, 4]) == 2.5
    assert H.percentile([1, 2, 3, 4, 5], 90) == 4.6
    assert H.worst([H.PASS, H.WARN, H.INCONCLUSIVE]) == H.INCONCLUSIVE
    assert H.worst([H.PASS, H.FAIL, H.INCONCLUSIVE]) == H.FAIL
    t = H.parse_timing_lines(["[timing] STT done                 812 ms", "noise", "[timing] total  4000 ms"])
    assert t == {"STT done": 812, "total": 4000}
    assert H.upstream_error_in(["openai.RateLimitError: Error code: 429"])
    assert not H.upstream_error_in(["INFO: ok"])


def test_resample_length():
    x = np.sin(np.linspace(0, 100, 24000)).astype("float32")
    assert len(H.resample(x, 24000, 16000)) == 16000
