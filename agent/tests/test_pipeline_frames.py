"""pipeline.py: the wire frame protocol (1B type | 4B BE length | payload)
that static/index.html's readFrames() parses on the other end."""
import json
import struct

import pipeline


def _parse_one_frame(data: bytes):
    ftype = data[0]
    (length,) = struct.unpack(">I", data[1:5])
    payload = data[5:5 + length]
    return ftype, payload, data[5 + length:]


def test_meta_frame_round_trips():
    raw = pipeline.meta_frame("hello", "hi there", done=True)
    ftype, payload, rest = _parse_one_frame(raw)
    assert ftype == pipeline.FRAME_META
    assert rest == b""
    assert json.loads(payload) == {"user": "hello", "reply": "hi there", "done": True}


def test_event_frame_round_trips():
    raw = pipeline.event_frame("search_started", query="weather")
    ftype, payload, _ = _parse_one_frame(raw)
    assert ftype == pipeline.FRAME_EVENT
    assert json.loads(payload) == {"kind": "search_started", "query": "weather"}


def test_frame_length_prefix_matches_payload():
    payload = b"x" * 12345
    raw = pipeline.frame(pipeline.FRAME_AUDIO, payload)
    ftype, parsed, rest = _parse_one_frame(raw)
    assert ftype == pipeline.FRAME_AUDIO
    assert parsed == payload
    assert rest == b""


def test_multiple_frames_concatenate_and_parse_in_order():
    stream = (pipeline.meta_frame("a", "")
              + pipeline.event_frame("x")
              + pipeline.meta_frame("a", "done", done=True))
    parsed = []
    buf = stream
    while buf:
        ftype, payload, buf = _parse_one_frame(buf)
        parsed.append((ftype, json.loads(payload)))
    assert [p[0] for p in parsed] == [pipeline.FRAME_META, pipeline.FRAME_EVENT, pipeline.FRAME_META]
    assert parsed[-1][1]["done"] is True
