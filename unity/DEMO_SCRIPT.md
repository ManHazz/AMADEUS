# AMADEUS demo script

A short checklist for recording a demo video of AMADEUS: a voice-driven Japanese tutor with a 3D avatar. Total recording time is about 6 to 8 minutes. Do one full dry run before you record, because the expected results below have not all been verified against the live server.

## Before you record

1. Stop other loud apps (music, WhatsApp sounds, notifications). Use the headset microphone and check it with the F2 panel before recording.
2. Start the agent: `powershell -ExecutionPolicy Bypass -File agent\run_local.ps1` (from the repository root). Wait until it has finished loading models.
3. Optional but useful for reviewers: set `DEBUG_TIMING=1` in the agent's `.env` so the server window prints per-stage timings. Show that window for part of the video.
4. Open the AMADEUS project in Unity, open `Assets/Scenes/Room.unity`, press Play. For a cleaner recording, use a Maximize On Play game view or a built player. Camera preset 2 (face close-up) is the default.
5. Start your screen recorder with microphone and system audio both captured, so the viewer hears you and the tutor.

## Scenes to record

### 1. Introduction (30 seconds)
Say (to the camera, not to the tutor): what the project is. Suggested points: a voice agent for Japanese conversation practice, local speech recognition (Whisper) and local speech synthesis (Kokoro) on the laptop GPU, a cloud language model for the replies, and a 3D avatar in Unity with live lip sync and expressions.
Show: the room scene and the avatar in the close-up camera. Press 1, 2, 3 to show the three camera views, then return to 2.

### 2. Greeting and the push-to-talk loop (45 seconds)
Hold Space and say: 「こんにちは」 or in English "Hello, who are you?"
Expect: your words appear in the "You (heard)" box at the top left, she answers by voice in Japanese, the subtitle shows Japanese with furigana above the kanji and the English translation below, and her mouth moves with the voice.
Point out: transcript box (what the speech recognition heard), subtitles, and lip sync.

### 3. Teach-me mode (60 seconds)
Hold Space and say: "Teach me how to say good morning in Japanese."
Expect: a short lesson with one or two example sentences, each with furigana and an English line, and a check question for you to answer. English tips appear as small amber text on screen and are never spoken (English-free mode), so you only hear the Japanese voice.
Then answer the check question in Japanese, for example: 「おはようございます」.
Expect: she judges the answer and continues.

### 4. Gentle correction (45 seconds)
Say a Japanese sentence with a deliberate mistake, for example: 「昨日、映画を見ます」 (present tense for yesterday).
Expect: a short amber tip about the past tense, the corrected sentence in Japanese, then a follow-up question.
Point out: the tutor corrects one mistake per turn, in character, without breaking the conversation.

### 5. Quiz mode (60 seconds)
Say: "Quiz me."
Expect: one question at a time. Answer one correctly and one wrongly.
Expect: she says clearly whether you were right, gives the correct answer with its translation when you were wrong, and asks the next question.

### 6. Shadowing practice (45 seconds)
Say: "Let's do shadowing."
Expect: she says a short Japanese line and asks you to repeat it. Repeat it. She compares your transcript to her line.
Point out, honestly: she only receives the text transcript from speech recognition, not the sound of your voice. So this checks wording, not pronunciation. It is a known limitation and a good point for the discussion at the end.

### 7. Expressions and identity (45 seconds)
Say something that should provoke a reaction, for example: "Thank you, you are a great teacher!" or "Who are you?"
Expect: a friendly, neutral tutor reply that says it is an AI Japanese tutor (no character persona and no teasing or sarcastic tone), and a visible expression change. While she speaks, the strong whole-face expressions are capped so the mouth can follow the voice; the eyes and brows carry the mood.
If you want a guaranteed expression shot, press F2 and use the emotion buttons, then close the panel.

### 8. Mixed English and Japanese input (30 seconds)
Say a sentence that mixes both languages: "How do I say 'thank you very much' in Japanese?"
Expect: speech recognition handles both languages (automatic language detection), and the answer comes back in Japanese with the English subtitle.

### 9. Stress and failure cases (optional, 60 seconds)
Pick one or two. These show honesty about limits, which reviewers usually value.
- Speak while there is background noise, or very quietly, and show the transcript box catching a wrong word. Explain that the transcript box exists so the learner sees what the recogniser heard.
- Stop the server window with Ctrl+C, then press Space and speak. The HUD should show the link error rather than crash. Restart the server afterward.
- Ask for something outside the lessons, such as a long explanation, and note the short spoken answers by design.

### 10. Closing summary (30 seconds)
Say to the camera: what works, what limits remain, and the next steps (progress memory, pronunciation scoring from the audio itself, a named Cloudflare tunnel for always-on use from the lab).

## What to mention in the discussion

- Architecture: Unity client, local FastAPI server with Whisper and Kokoro on the GPU, Gemini language model, a segment protocol that carries Japanese text, furigana readings and translations next to the audio.
- Latency: first speech typically arrives within a couple of seconds on this laptop. Report the number you actually measure with `DEBUG_TIMING=1` rather than a guess.
- Known limits: pronunciation is not scored from audio, the free-tier language model has rate limits, the avatar's strongest expressions are limited while she talks, and quality depends on the microphone.

## Checklist after recording

- Watch the video once with the sound on: check that the voice is not clipped, the subtitles are readable, and no personal notifications or private windows are visible.
- Check that no API key, token or `.env` content appears on screen. The server window prints a token on startup in some versions, so crop or hide it.
