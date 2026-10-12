# Segment protocol (tutor mode)

The brain (system prompt `prompts/tutor_neutral.txt`) tags each line of its reply:

| tag    | meaning                                              | spoken |
|--------|------------------------------------------------------|--------|
| `[ja]` | Japanese line (Kokoro lang `j`, voice `TTS_VOICE_JA`) | yes    |
| `[en]` | English tip (voice `TTS_VOICE` only if `ENGLISH_FREE=0`) | no (default) |
| `[tr]` | English translation of the preceding `[ja]` line      | never  |

Tags are parsed incrementally (`pipeline.TagStream`), also when a tag is split
across streamed tokens (`[` + `ja]`). Tags are never spoken or shown. Text with no
tag is treated as `[en]` (so plain replies keep working). A `[ja]`/`[en]` line is
split into sentences, each its own segment, so audio starts early.

## Frames (unchanged envelope: 1B type | 4B BE length | payload)

`0x01` meta JSON, `0x02` audio WAV, `0x03` event JSON.

For every spoken segment the server sends, in this order:

1. EVENT `segment`
   ```json
   {"kind":"segment","id":0,"lang":"ja","text":"昨日は何をしましたか。",
    "ruby":[{"t":"昨日","r":"きのう"},{"t":"は","r":""},{"t":"何","r":"なに"}],
    "tr":null,"spoken":true}
   ```
   `ruby` exists for `lang:"ja"` only. `r` is a hiragana reading for tokens with
   kanji and `""` for kana/punctuation. `tr` is always `null` here (see below).
2. AUDIO frame with that segment's WAV.

When a `[tr]` line follows a `[ja]` line, it arrives later as
```json
{"kind":"segment_tr","id":0,"tr":"What did you do yesterday?"}
```
`id` is the id of the `ja` segment it translates (ids increment per reply from 0).
No audio is ever sent for `[tr]`. A client shows `tr` as a subtitle under that
segment.

The brain is told to give every `[ja]` line its own `[tr]`, but sometimes writes
several `[ja]` lines and only one `[tr]` (`[ja] A`, `[ja] B`, `[tr] ...`), or puts
several sentences in one `[ja]` line. So that every Japanese segment still gets a
subtitle, a `[tr]` is sent as one `segment_tr` event for each `ja` segment that
has had no `[tr]` yet (since the previous `[tr]`), all carrying the same text (a
combined translation of those segments). Events are never empty. In the normal
`[ja]`/`[tr]`/`[ja]`/`[tr]` order with one sentence per line this is one event per
`[tr]`, as before. `[en]` lines in between do not matter; untranslated `ja`
segments wait for the next `[tr]`. If the reply ends with untranslated `ja`
segments and no later `[tr]`, nothing is sent for them (no translation is
invented). The Unity client keys subtitles by segment id, so it needs no change.

`ENGLISH_FREE` (default `1`): `[en]` and untagged English text is never synthesized.
It is sent as a silent tip: a `segment` event with `lang:"en"`, `spoken:false`,
`ruby:[]`, `tr:null` and NO audio frame; clients show it as a muted note line.
Japanese script in an `[en]`/untagged chunk is split out and routed to the ja voice.
With `ENGLISH_FREE=0` English segments are spoken (`spoken:true`, no `ruby`).

Meta frames carry the reply with tags stripped. Chat history keeps the raw tagged
text so the model keeps the format.

## Config

`ENGLISH_FREE` (default 1, see above).
`TTS_VOICE_JA` (default `jf_alpha`), `TTS_JA_WARMUP` (1: load JA pipeline at warmup),
`STT_LANGUAGE=auto`, `STT_INITIAL_PROMPT`, `STT_VAD` (default 1).
