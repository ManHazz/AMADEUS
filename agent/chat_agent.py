"""Fast conversational path -- NO tools.

STT text is already in history. Stream the brain's tokens, parse the optional
[ja]/[en]/[tr] tags incrementally (pipeline.TagStream), speak each finished
segment as soon as it's ready (TTS of segment N overlaps generation of N+1),
and frame it back to the client. Per spoken segment: a "segment" EVENT frame,
then its AUDIO frame; [tr] lines become "segment_tr" EVENTs (never spoken).
Protocol: see top of pipeline.py and PROTOCOL_SEGMENTS.md.
"""
import pipeline
from pipeline import (
    brain_call, TagStream, strip_tags, meta_frame, audio_frame, segment_frame,
    segment_tr_frame, Timer, split_scripts,
)


def handle(user_text: str, snapshot: list[dict], state, timer=None):
    """Return a generator of wire frames for StreamingResponse."""

    tm = timer or Timer()

    def emit(items):
        for it in items:
            if it["kind"] == "tr":
                yield segment_tr_frame(it)
                continue
            tm.mark('first sentence ready', once=True)
            yield segment_frame(it)
            if not it.get("spoken", True):
                continue                                    # silent English tip: subtitle only
            af = audio_frame(it["text"], it["lang"])
            tm.mark('first TTS frame', once=True)
            yield af

    def gen():
        yield meta_frame(user_text, "")
        full = ""
        ts = TagStream()
        stream = brain_call(snapshot, stream=True)          # <-- no tools
        tm.mark('LLM request sent')
        for chunk in stream:
            if not chunk.choices:
                continue
            delta = chunk.choices[0].delta.content or ""
            if not delta:
                continue
            tm.mark('LLM first token', once=True)
            full += delta
            items = ts.feed(delta)
            if items:
                yield from emit(items)
                yield meta_frame(user_text, strip_tags(full).strip())

        yield from emit(ts.finish())

        reply = full.strip()
        state.add_assistant(reply)                          # keep tags so the model keeps the format
        if ts.n_seg == 0:
            text = strip_tags(reply).strip() or "Sorry, I didn't get a response."
            for i, (lang, t) in enumerate(split_scripts(text)):
                sp = lang == "ja" or not pipeline.ENGLISH_FREE
                yield segment_frame({"id": i, "lang": lang, "text": t, "spoken": sp})
                if sp:
                    yield audio_frame(t, lang)
        yield meta_frame(user_text, strip_tags(reply).strip(), done=True)
        tm.mark('total')

    return gen()
