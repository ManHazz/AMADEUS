# AMADEUS (Japanese tutor with a 3D avatar)

[![CI](https://github.com/ManHazz/AMADEUS/actions/workflows/ci.yml/badge.svg?branch=main)](https://github.com/ManHazz/AMADEUS/actions/workflows/ci.yml)
![Python](https://img.shields.io/badge/python-3.12-blue)
![Unity](https://img.shields.io/badge/Unity-6000.6-black)
![Coverage](https://img.shields.io/badge/coverage-workflow%20artifact-lightgrey)
![Last commit](https://img.shields.io/github/last-commit/ManHazz/AMADEUS)

A voice-driven Japanese tutor with a 3D anime avatar. You hold a key and speak; a local FastAPI agent transcribes your speech, asks a language model for a reply, synthesizes the Japanese voice, and streams text and audio back to a Unity client, where the avatar speaks with lip sync, expressions and subtitles (Japanese with furigana, plus an English translation).

The tutor is a friendly, neutral AI Japanese tutor aimed at an English-speaking learner at roughly JLPT N5 to N4. It has no character persona and no personal name; when asked, it says it is an AI tutor.

## Features

- Push-to-talk voice conversation in English or Japanese.
- Local speech recognition (faster-whisper) and local speech synthesis (Kokoro, with a Japanese voice for the Japanese lines).
- Language model through any OpenAI-compatible chat endpoint (developed against Gemini; a local llama.cpp server also works).
- Tutor protocol: every line of a reply is tagged `[ja]` (spoken Japanese), `[en]` (silent English tip) or `[tr]` (silent English translation). See `agent/PROTOCOL_SEGMENTS.md`.
- Teaching modes by plain words: free conversation with gentle corrections, "quiz me", "teach me <topic>", and shadowing drills.
- Unity client: VRM 1.0 avatar, lip sync driven by the audio level, keyword-based emotion, idle motion, subtitles with furigana, debug panel (F2).
- Optional web search in the agent (can be turned off with `ENABLE_WEB_SEARCH=0`).

## Architecture

    Unity client (unity/)                           Local agent (agent/)
    +--------------------------+   audio (HTTP)   +-------------------------------+
    | microphone, push-to-talk | ---------------> | FastAPI server (server.py)    |
    |                          |                  |   faster-whisper (speech to   |
    | avatar (VRM) + lip sync  | <--------------- |   text)                       |
    | subtitles + furigana     |  event stream:   |   brain: chat model via an    |
    | emotion / idle           |  text segments,  |   OpenAI-compatible API       |
    +--------------------------+  audio chunks    |   Kokoro (text to speech)     |
                                                  +-------------------------------+

The Unity client reads the agent address and access token from `unity/agent.local.json` (a template is provided as `agent.local.json.example`).

## Repository layout

    agent/    the voice agent (Python): server, pipeline, prompts, tests, end-to-end suite
    unity/    the Unity 6 project (client, avatar scripts, room scene, EditMode tests, helper scripts)

## Docker (agent)

The simplest way to run the agent is Docker: the image bundles Python 3.12, CUDA 12.6, cuDNN 9 and all dependencies, so there is no dependency setup. Copy `agent/.env.example` to `agent/.env`, fill in the brain key and `VOICE_AGENT_TOKEN`, then run `docker compose up --build` inside `agent/` (use `-f docker-compose.cpu.yml` without an NVIDIA GPU). See `agent/DOCKER.md` for prerequisites and troubleshooting. This setup is documented but not yet tested end to end.

## Setup: agent

Requirements: Windows (the launcher is a PowerShell script; the code itself is Python), Python 3.12 (the project was developed with 3.12), and a language-model endpoint or key.

1. Create a virtual environment and install dependencies:

        cd agent
        python -m venv .venv
        .\.venv\Scripts\Activate.ps1
        pip install -r requirements.txt

   Read the comments in `requirements.txt` first: on Windows the Japanese text-processing dependency needs a prebuilt wheel, and CUDA users need extra libraries. The first start downloads the speech models.
2. Copy `.env.example` to `.env` and edit it. Set `BRAIN_URL`, `BRAIN_MODEL` and, for a hosted provider, `BRAIN_API_KEY`. Set `VOICE_AGENT_TOKEN` to a shared secret (if unset the server prints a random one at startup). Choose `STT_DEVICE` and `TTS_DEVICE` (`cpu` or `cuda`). Never commit `.env`.
3. Start the server:

        powershell -ExecutionPolicy Bypass -File run_local.ps1

   It listens on `http://127.0.0.1:5000` by default (`PORT` in `.env`).

The system prompt is loaded from `SYSTEM_PROMPT_FILE`, else `SYSTEM_PROMPT`, else the bundled `agent/prompts/tutor_neutral.txt`.

## Setup: Unity client

1. Install Unity 6000.6.5f1 (the version in `unity/ProjectSettings/ProjectVersion.txt`) and open the `unity/` folder as a project. The first import resolves the UniVRM packages from GitHub (see `unity/Packages/manifest.json`).
2. Add your own VRM 1.0 avatar as `unity/Assets/Characters/amadeus.vrm`. No model is included. Read `unity/Assets/Characters/README.md` for the exact name, how to regenerate the scene for your model, and what happens without it.
3. Copy `unity/agent.local.json.example` to `unity/agent.local.json` and fill in the agent URL and token (the token is the `VOICE_AGENT_TOKEN` from the agent's `.env`).
4. Open `Assets/Scenes/Room.unity`, press Play, then hold Space and speak. The F2 key opens a debug panel.

## Hardware notes

- A microphone is required. Use a headset for the cleanest recognition.
- The speech models run on the CPU or on an NVIDIA GPU (`STT_DEVICE`, `TTS_DEVICE`). CPU works but replies are slower. Measure latency on your own machine (the agent prints per-stage timings with `DEBUG_TIMING=1`) rather than assuming a number.
- The language model runs wherever `BRAIN_URL` points: a hosted API or a local server.

## Testing

- Agent unit tests (no GPU, models or network needed): from `agent/`, run `python -m pytest -q` in the virtual environment.
- Unity EditMode tests: `powershell -File unity/tools/unity.ps1 test-edit` (close the Editor first), or use the Test Runner window. The helper script expects Unity at the default Unity Hub path for the version above.
- End-to-end suite: `agent/e2e/` drives a running server with generated speech and writes a report. It uses the real language model, so it consumes API quota. See `agent/e2e/README.md`.

The demo checklist is in `unity/DEMO_SCRIPT.md`.

## Continuous integration

Workflows live in `.github/workflows/`. They have not been run on GitHub yet, so treat them as unverified until the first run.

- `ci.yml` runs on pushes and pull requests to `main` that touch `agent/**`, `unity/**` or the workflow, and on manual dispatch.
  - `lint`: `ruff check agent --select E9,F63,F7,F82` (syntax errors and undefined names only, not a style gate).
  - `agent-tests`: Python 3.12, installs `agent/requirements-dev.txt` only (no GPU, models or `.env`; `faster-whisper` and `kokoro` are stubbed in `agent/tests/conftest.py`), byte-compiles the modules, checks `static/index.html`, then runs `pytest` with coverage. `coverage.xml` is uploaded as the `coverage-xml` workflow artifact; there is no external coverage service.
  - `unity-tests`: Unity EditMode tests through GameCI. Disabled by default and untested.
- `cd.yml` runs when a tag matching `v*` is pushed. It creates a GitHub Release with generated notes and attaches a zip of the tracked `agent/` source. A Unity player build is not automated because it needs a Unity licence.
- `dependabot.yml` proposes weekly updates for `pip` (`/agent`) and GitHub Actions.

Run the same checks locally:

- Agent: from `agent/`, `pip install -r requirements-dev.txt` then `pytest`.
- End-to-end suite (`agent/e2e/`): needs a GPU and a real Gemini key, uses quota, and is not part of CI.
- Unity EditMode: `powershell -File unity/tools/unity.ps1 test-edit` with the Editor closed.

### Enabling the Unity job

The `unity-tests` job only runs when the repository variable `UNITY_CI` is `true`. To enable it:

1. Check that GameCI publishes an editor image for Unity 6000.6.5f1 at https://game-ci.com/docs/docker/versions. If it does not, the job will fail; change `unityVersion` or leave it disabled.
2. Add the repository secrets `UNITY_LICENSE`, `UNITY_EMAIL` and `UNITY_PASSWORD` (see the GameCI activation guide).
3. Under Settings, Secrets and variables, Actions, Variables, add `UNITY_CI` with the value `true`.

Branch protection and required status checks cannot be set from files; configure them in the repository settings after the first successful run.

## Known limitations

- No pronunciation scoring from audio. The tutor only receives a text transcript of what you said, so shadowing drills check wording, not pronunciation, and speech recognition errors can look like learner mistakes.
- Free-tier language-model endpoints have rate limits, which can cause slow or failed replies.
- No avatar is bundled. Without a VRM file the Unity client uses a placeholder avatar (see `unity/Assets/Characters/README.md`), and the committed scene needs to be regenerated for your own model.
- The avatar's strongest expressions are limited while it is talking so the mouth can follow the voice.
- Emotion is chosen by keyword cues in the text, not by a model of the conversation.

## Credits and license

The `agent/` directory is based on [ManHazz/Voice-Agent](https://github.com/ManHazz/Voice-Agent) and has been modified for Japanese tutoring. The Noto Sans JP font in the Unity project is under the SIL Open Font License. UniVRM is a separate package with its own license.

No license has been chosen for this repository yet; add a `LICENSE` file before publishing.
