"""Tag parsing (incremental), ruby, segment event ordering, tag-less fallback."""
import json
import struct
import types

import pytest

import pipeline
import chat_agent


@pytest.fixture(autouse=True)
def _english_spoken(monkeypatch):
    monkeypatch.setattr(pipeline, "ENGLISH_FREE", False)   # legacy behaviour unless a test opts in


def run_ts(deltas):
    ts = pipeline.TagStream()
    items = []
    for d in deltas:
        items += ts.feed(d)
    items += ts.finish()
    return items


def chars(s):
    return list(s)  # worst case: one char per token


TEXT = "[ja] こんにちは。元気ですか。\n[tr] Hello. How are you?\n[en] Say it slowly.\n"


@pytest.mark.parametrize("deltas", [[TEXT], chars(TEXT), [TEXT[:2], TEXT[2:5], TEXT[5:]]])
def test_tags_split_across_tokens(deltas):
    items = run_ts(deltas)
    segs = [(i["lang"], i["text"]) for i in items if i["kind"] == "seg"]
    assert segs == [("ja", "こんにちは。"), ("ja", "元気ですか。"), ("en", "Say it slowly.")]
    trs = [i for i in items if i["kind"] == "tr"]
    assert len(trs) == 2 and all(t["text"] == "Hello. How are you?" for t in trs)
    assert [i["id"] for i in trs] == [0, 1]       # every ja segment of the line gets it
    assert not any("[" in i["text"] for i in items)


def test_tr_never_a_segment_and_orphan_tr_dropped():
    items = run_ts(["[tr] nothing before me\n[ja] はい。\n[tr] Yes.\n"])
    assert [i["kind"] for i in items] == ["seg", "tr"]


def test_non_tag_bracket_kept_as_text():
    items = run_ts(["[en] see [1] and ", "[x] ok.\n"])
    assert items[0]["text"] == "see [1] and [x] ok."


def test_ja_flushes_before_newline():
    ts = pipeline.TagStream()
    out = ts.feed("[ja] はい。")
    assert [i["text"] for i in out] == ["はい。"]


def test_tagless_falls_back_to_english():
    items = run_ts(["Hello there. ", "How are you today? Fine."])
    assert items and all(i["kind"] == "seg" and i["lang"] == "en" for i in items)
    assert " ".join(i["text"] for i in items) == "Hello there. How are you today? Fine."


def test_ruby_kanji_and_kana():
    r = pipeline.ruby_for("日本語を勉強します")
    assert "".join(x["t"] for x in r) == "日本語を勉強します"
    by = {x["t"]: x["r"] for x in r}
    assert by.get("を") == ""
    assert any(x["r"] and x["t"] != x["r"] for x in r)
    assert all(x["r"] == "" for x in pipeline.ruby_for("こんにちは、コーヒー"))
    kanji = [x for x in r if x["r"]]
    assert kanji and all(all("぀" <= c <= "ゟ" for c in x["r"]) for x in kanji)


def _parse(frames):
    out = []
    for f in frames:
        (n,) = struct.unpack(">I", f[1:5])
        p = f[5:5 + n]
        out.append((f[0], json.loads(p) if f[0] != pipeline.FRAME_AUDIO else p))
    return out


class _State:
    def __init__(self):
        self.reply = None

    def add_assistant(self, t):
        self.reply = t


def _fake_brain(deltas):
    def call(*a, **kw):
        return [types.SimpleNamespace(choices=[types.SimpleNamespace(
            delta=types.SimpleNamespace(content=d))]) for d in deltas]
    return call


def _run(monkeypatch, deltas):
    calls = []
    monkeypatch.setattr(chat_agent, "brain_call", _fake_brain(deltas))
    monkeypatch.setattr(chat_agent, "audio_frame",
                        lambda text, lang="en": calls.append((text, lang)) or pipeline.frame(pipeline.FRAME_AUDIO, b"W"))
    st = _State()
    frames = list(chat_agent.handle("hi", [], st))
    return _parse(frames), calls, st


def test_event_precedes_audio_and_tr_not_spoken(monkeypatch):
    deltas = chars(TEXT)
    parsed, calls, st = _run(monkeypatch, deltas)
    kinds = [(t, p.get("kind") if t != pipeline.FRAME_AUDIO else "audio") for t, p in parsed]
    seq = [k for t, k in kinds if k in ("segment", "audio", "segment_tr")]
    assert seq == ["segment", "audio", "segment", "audio", "segment_tr", "segment_tr", "segment", "audio"]
    assert calls == [("こんにちは。", "ja"), ("元気ですか。", "ja"), ("Say it slowly.", "en")]
    segs = [p for t, p in parsed if t == pipeline.FRAME_EVENT and p["kind"] == "segment"]
    assert [s["id"] for s in segs] == [0, 1, 2]
    assert segs[0]["lang"] == "ja" and "ruby" in segs[0] and segs[0]["spoken"] is True
    assert "ruby" not in segs[2]
    trs = [p for t, p in parsed if t == pipeline.FRAME_EVENT and p["kind"] == "segment_tr"]
    assert trs == [{"kind": "segment_tr", "id": i, "tr": "Hello. How are you?"} for i in (0, 1)]
    assert st.reply.startswith("[ja]")                       # history keeps tags
    assert "[" not in [p for t, p in parsed if t == pipeline.FRAME_META][-1]["reply"]


def test_chat_tagless_reply_speaks_english(monkeypatch):
    parsed, calls, _ = _run(monkeypatch, ["Hmph. ", "Fine, I will help."])
    assert calls and all(lang == "en" for _, lang in calls)
    assert " ".join(t for t, _ in calls) == "Hmph. Fine, I will help."


# ---- Japanese routing safety net + tag variants + ENGLISH_FREE
def segs_of(deltas):
    return [(i["lang"], i["text"]) for i in run_ts(deltas) if i["kind"] == "seg"]


def test_japanese_in_en_or_untagged_goes_to_ja_voice():
    assert segs_of(["[en] おはようございます。\n"]) == [("ja", "おはようございます。")]
    assert segs_of(["私は学生です。"]) == [("ja", "私は学生です。")]
    assert segs_of(["[en] Say こんにちは to greet."]) == [
        ("en", "Say"), ("ja", "こんにちは"), ("en", "to greet.")]


@pytest.mark.parametrize("tag", ["[JA]", "[ ja ]", "【ja】", "**[ja]**"])
def test_tag_variants(tag):
    assert segs_of([f"{tag} はい。\n[tr] Yes.\n"]) == [("ja", "はい。")]
    assert segs_of(list(f"{tag} はい。\n")) == [("ja", "はい。")]


def test_tag_mid_line():
    assert segs_of(["Hmm. [ja] はい。"]) == [("en", "Hmm."), ("ja", "はい。")]


def test_english_free_silent_tip(monkeypatch):
    monkeypatch.setattr(pipeline, "ENGLISH_FREE", True)
    items = run_ts(["[en] Use the past tense.\n[ja] はい。\n[tr] Yes.\n"])
    segs = [i for i in items if i["kind"] == "seg"]
    assert [(s["lang"], s["spoken"]) for s in segs] == [("en", False), ("ja", True)]
    payload = json.loads(pipeline.segment_frame(segs[0])[5:])
    assert payload["spoken"] is False and payload["ruby"] == [] and payload["tr"] is None
    assert json.loads(pipeline.segment_frame(segs[1])[5:])["spoken"] is True


def test_english_free_no_synth_for_en_and_tagless(monkeypatch):
    monkeypatch.setattr(pipeline, "ENGLISH_FREE", True)
    parsed, calls, _ = _run(monkeypatch, ["[en] Tip here.\n[ja] はい。\n"])
    assert calls == [("はい。", "ja")]
    parsed, calls, _ = _run(monkeypatch, ["Hmph. ", "Fine, I will help."])
    assert calls == []
    evs = [p for t, p in parsed if t == pipeline.FRAME_EVENT and p["kind"] == "segment"]
    assert evs and all(e["spoken"] is False for e in evs)


# --- one [tr] for several untranslated [ja] lines ---------------------------------
def _trs(items):
    return [(i["id"], i["text"]) for i in items if i["kind"] == "tr"]


def _chunks(s, n):
    return [s[i:i + n] for i in range(0, len(s), n)]


def test_two_ja_one_tr_covers_both():
    t = "[ja] はい。\n[ja] いいですね。\n[tr] Yes. Nice.\n"
    for deltas in ([t], chars(t), _chunks(t, 3)):
        items = run_ts(deltas)
        assert _trs(items) == [(0, "Yes. Nice."), (1, "Yes. Nice.")]


def test_ja_tr_ja_tr_unchanged():
    t = "[ja] はい。\n[tr] Yes.\n[ja] いいですね。\n[tr] Nice.\n"
    for deltas in ([t], chars(t), _chunks(t, 4)):
        assert _trs(run_ts(deltas)) == [(0, "Yes."), (1, "Nice.")]


def test_ja_ja_without_tr_emits_nothing_extra():
    items = run_ts(["[ja] はい。\n[ja] いいですね。\n"])
    assert [i["kind"] for i in items] == ["seg", "seg"]


def test_ja_en_tr_attaches_to_ja():
    t = "[ja] はい。\n[en] Short tip.\n[tr] Yes.\n"
    for deltas in ([t], chars(t)):
        items = run_ts(deltas)
        assert _trs(items) == [(0, "Yes.")]


def test_multi_sentence_ja_lines_all_segments_get_tr():
    t = "[ja] はい。そうです。\n[ja] いいですね。\n[tr] Yes. Nice.\n"
    for deltas in ([t], chars(t), _chunks(t, 5)):
        assert _trs(run_ts(deltas)) == [(0, "Yes. Nice."), (1, "Yes. Nice."), (2, "Yes. Nice.")]


def test_tr_after_tr_is_consumed_once():
    items = run_ts(["[ja] はい。\n[tr] Yes.\n[ja] ええ。\n[ja] そう。\n[tr] Right.\n"])
    assert _trs(items) == [(0, "Yes."), (1, "Right."), (2, "Right.")]
