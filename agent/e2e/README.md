# End-to-end test suite (demo + regression)

Drives the REAL server (Whisper STT, Kokoro TTS, Gemini brain) with spoken test
inputs and checks the whole reply stream. Produces a readable report for a demo.

## Run

```
.venv\Scripts\python.exe e2e\run_e2e.py                 # all 10 scenarios (~3.5 min)
.venv\Scripts\python.exe e2e\run_e2e.py --scenario 4    # one scenario (also: -s 1,2,8)
.venv\Scripts\python.exe e2e\run_e2e.py --no-server     # reuse a healthy server on port 5000
.venv\Scripts\python.exe -m pytest tests\test_e2e_helpers.py   # offline unit tests (no GPU/network)
```

Reports: `e2e\report\report.html` (self-contained, open it and screen-record),
`report.md`, `report.json`. A partial run (`--scenario`) overwrites the report with only those scenarios.
Exit code is 1 if any scenario FAILs.

Runtime: first run about 6 min (Kokoro synthesises and caches the input WAVs in `e2e\audio\`, ~40 s server
start); later runs about 3.5 min. About 15 Gemini requests (+1 for the server warm-up); 4 s sleep between turns.

## How it works

1. Server: starts `uvicorn server:app` as a subprocess on **port 5077** with `DEBUG_TIMING=1`
   (so STT/LLM/TTS stage timings come from the server's own `[timing]` log lines). The user's server
   on 5000 is never touched. If less than 1800 MiB of GPU memory is free, or the own instance fails to
   start, a healthy server on 5000 (`/health`) is reused instead (no server-side timings then);
   otherwise the run stops with a clear message. The subprocess process tree is always killed in `finally`.
   Token and API key come from `.env`, are read in Python only, and are redacted from the saved `server.log`.
2. Inputs: text -> local Kokoro (`af_heart` English, `jf_alpha` Japanese, CPU) -> 16 kHz mono WAV, cached.
3. Each turn: POST multipart `audio` to `/api/converse` with `X-Access-Token` and a fresh `X-Session-Id`
   per scenario; the length-prefixed frame stream (0x01 META, 0x02 AUDIO, 0x03 EVENT) is parsed live.
4. Returned Japanese audio is transcribed with faster-whisper (`small`, `language="ja"`, GPU if it fits).

## Scenarios (see unity/DEMO_SCRIPT.md)

1 greeting; 2 "teach me good morning"; 3 answer the check question; 4 tense correction;
5 quiz (fixed answers, structure only); 6 shadowing (the learner repeats her first ja line);
7 mixed English/Japanese request; 8 persona (must be a neutral AI conversation partner, not a fictional character);
9 noise and too-short audio; 10 /health, /, /api/reset, /api/pending, auth rejection.

## Checks

Status values: PASS, WARN (quality below threshold), FAIL (protocol/contract violation),
INCONCLUSIVE (Gemini 429/503 or cut stream after one retry with 15 s backoff). A turn or scenario takes its worst check
(FAIL > INCONCLUSIVE > WARN > PASS).

| check | meaning | threshold / status |
|---|---|---|
| frame_stream_wellformed | HTTP 200, frames parse, no truncated bytes, final META done=true, audio is WAV | FAIL |
| stt_transcript_similarity | difflib ratio of input text vs heard transcript (punctuation stripped; Japanese also compared after folding to hiragana, so kanji/kana variants are not penalised) | >= 0.6 else WARN |
| ja_segment_present | at least one `lang:"ja"` segment | FAIL |
| ruby_readings | ja segments carry ruby that rebuilds the text; every kanji token has a non-empty reading | FAIL |
| ja_has_following_tr | every ja segment has a `segment_tr` event after it | FAIL |
| audio_only_for_spoken_ja | audio frames == spoken segments; en segments silent (ENGLISH_FREE); no audio for en/tr | FAIL if audio for a non-spoken segment, WARN on count mismatch |
| audio_non_silent | RMS of each returned WAV | > 0.01 else FAIL |
| reply_length | spoken lines per reply | <= 4 else FAIL |
| no_leaked_tags | no `[ja]/[en]/[tr]` in META text, segment text or translations | FAIL |
| first_audio_latency | request -> first AUDIO frame | WARN > 6 s |
| whisper_roundtrip | Whisper(ja) on each returned ja audio vs its segment text | each >= 0.5 else WARN |
| content_expectation | e.g. lesson contains おはよう, correction contains 見ました | WARN |
| persona_* | no tsundere markers (Hah, Tch, "it's not like", ふん ...), no claim to be a fictional character, states AI conversation-partner identity | FAIL / FAIL / WARN |

Timings reported per turn: request -> first META, -> first segment event, -> first AUDIO, -> last frame, plus
server-side STT time. Note META arrives right after STT, so "first META" is roughly upload + STT.

## Known limits

- Inputs are synthetic TTS voices, not a real microphone with room noise and accents: STT accuracy is optimistic.
  (Even so, Whisper can still mishear words in short questions.)
- Gemini free tier is rate limited; the suite sleeps 4 s between turns and retries a 429/503 once, otherwise reports INCONCLUSIVE.
  A retry re-sends the user turn, so that session's history contains it twice.
- The LLM is not deterministic: WARN/FAIL on `ja_has_following_tr` etc. can vary between runs. Content checks are loose on purpose.
- Quiz and shadowing check structure, not whether the partner judged correctly. No pronunciation scoring (the agent only gets text).
- A second server needs about 1.5-2 GB free VRAM; otherwise the suite reuses the server on port 5000.
