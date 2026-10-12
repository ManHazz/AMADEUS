# Running the agent with Docker

Docker is the easiest way to run the voice agent. The image bundles Python 3.12, the CUDA 12.6 and cuDNN 9 libraries, espeak-ng, ffmpeg and every Python dependency at a working version, so you do not have to fight dependency conflicts on your own machine. You only install Docker (and, for GPU use, the NVIDIA driver).

Status of this document: the Dockerfile and compose files were written and the compose files were validated with `docker compose config`. The image build and a running container have NOT been tested yet (the Docker daemon was not running and there was not enough free disk space on the author's machine). Treat the steps below as untested until you have run them once. See "Tested and untested" at the end.

## Prerequisites

Windows (recommended path):

1. A recent NVIDIA driver (the normal Game Ready or Studio driver is enough; do not install a Linux driver inside WSL).
2. Docker Desktop with the WSL2 backend enabled (Settings, General, "Use the WSL 2 based engine").
3. Roughly 30 GB of free disk space. The image is large (CUDA, PyTorch) and the models are downloaded on first start.

Linux:

1. NVIDIA driver, Docker Engine and the NVIDIA Container Toolkit (`nvidia-ctk runtime configure --runtime=docker`, then restart Docker).

Check that Docker can see your GPU:

```
docker run --rm --gpus all nvidia/cuda:12.6.3-base-ubuntu24.04 nvidia-smi
```

If that prints your GPU table, you are ready. No GPU? Use the CPU setup described below.

## Quick start (GPU)

Run everything from the `agent` folder.

1. Create your settings file:

   ```
   cp .env.example .env          # PowerShell: Copy-Item .env.example .env
   ```

2. Edit `.env`:
   - `BRAIN_URL`, `BRAIN_MODEL` and `BRAIN_API_KEY` point at the language model. For Gemini use its OpenAI-compatible endpoint (`https://generativelanguage.googleapis.com/v1beta/openai/`), a Gemini model name, and your Gemini API key in `BRAIN_API_KEY`. Uncomment the `BRAIN_API_KEY` line.
   - Note that the default `BRAIN_URL` (`http://127.0.0.1:8081/v1`) means "a model server on the same machine". Inside a container that address is the container itself, so a llama.cpp server running on your host must be reached as `http://host.docker.internal:8081/v1` instead.
   - `VOICE_AGENT_TOKEN` is the shared secret that Unity and the browser page must send. Generate one with:

     ```
     python -c "import secrets; print(secrets.token_urlsafe(24))"
     ```

     Paste the output after `VOICE_AGENT_TOKEN=`. If you leave it empty the server invents a random one on every start (visible in the logs), which is awkward with Unity.
   - You do not need to set `STT_DEVICE`, `TTS_DEVICE` or `PORT`. The compose files fix them (GPU file: `cuda`; port 5000 inside the container).

3. Start it:

   ```
   docker compose up --build
   ```

   The first start builds the image (several minutes) and then downloads the speech models (several GB in total). Later starts reuse them. The agent is ready when the log shows Uvicorn running and `/health` answers.

Never commit `.env`; it is git-ignored and also excluded from the image (the settings are passed in at run time).

## Quick start (CPU only)

```
docker compose -f docker-compose.cpu.yml up --build
```

This standalone file has no GPU reservation, sets `STT_DEVICE=cpu`, `TTS_DEVICE=cpu` and int8 Whisper, and is much slower than GPU but works on any machine. Set `STT_MODEL` in `.env` (for example `base` or `small`) to trade accuracy for speed. The image still contains the CUDA libraries, which is wasted space on a CPU-only machine but keeps a single Dockerfile.

## Check that it works

```
curl http://127.0.0.1:5000/health
```

You should get JSON with `"ok": true` plus the STT and TTS device (`cuda` or `cpu`). If you changed `AGENT_PORT`, use that port. In a browser, `http://127.0.0.1:5000/` serves the web test page.

Logs: `docker compose logs -f agent`. Stop: `docker compose down` (the model cache is kept).

## Connecting Unity

Copy `unity/agent.local.json.example` to `unity/agent.local.json` and set:

```
{
  "url": "http://127.0.0.1:5000",
  "token": "<the VOICE_AGENT_TOKEN from your .env>"
}
```

The example file ships with port 8000; change it to 5000 (or your `AGENT_PORT`). Unity runs on the host, so `127.0.0.1` is correct even though the agent is in a container.

## Settings reference (the ones that matter in Docker)

- `AGENT_PORT`: host port, default 5000. Set it in your shell or in a file named `.env` next to the compose file (compose reads that for variable substitution). Example: `AGENT_PORT=5078 docker compose up`.
- `STT_MODEL`: Whisper size (`tiny`, `base`, `small`, `medium`, `large-v3`). Smaller uses less GPU memory.
- `STT_LANGUAGE`: `en`, `ja` or `auto`. `ENGLISH_FREE`, `TTS_VOICE`, `TTS_VOICE_JA` and the rest are documented in `.env.example`.
- Models are stored in the named volume `models` (mounted at `/models`, `HF_HOME`).

## Updating

```
git pull
docker compose up --build -d
```

Dependencies are in a cached layer, so only changes to `requirements.txt` trigger a long rebuild.

## Cleaning up

```
docker compose down            # stop and remove the container, keep models
docker compose down -v         # also delete the model cache (re-download next time)
docker image rm amadeus-agent:latest
```

## Troubleshooting

- GPU not visible in Docker on Windows: update the NVIDIA driver, make sure Docker Desktop uses the WSL2 engine, run `wsl --update`, restart Docker Desktop, then retry the `nvidia-smi` check above. If `docker compose up` says no `nvidia` device driver, the same applies. As a fallback use the CPU file.
- Out of memory on a 6 GB GPU: Whisper and Kokoro share the GPU, and Docker Desktop/WSL2 and the Windows desktop take some memory too. Set `STT_MODEL=small` (or `base`) in `.env`. If it still fails, run only STT on CPU by editing the compose file (`STT_DEVICE: cpu`) or use the CPU file. Do not run another GPU-heavy server (for example a second copy of the agent) at the same time.
- CUDA or cuDNN errors (`libcudnn`, `libcublas`, `CUDA driver version is insufficient`): the driver is too old for CUDA 12.6; update it. The image already contains cuDNN 9 and cuBLAS.
- Port 5000 already in use: `AGENT_PORT=5078 docker compose up` (and use that port in Unity).
- The agent cannot reach the language model: see the `host.docker.internal` note above. Gemini needs outbound internet from the container, which is on by default.
- First start looks stuck: it is downloading models. Watch `docker compose logs -f agent`. The health check allows a long start period.
- Browser microphone does not work over plain http from another device: browsers only allow the mic on `localhost` or https. Use Unity or open the page on the same machine.

## Tested and untested

Tested: `docker compose config` accepts both compose files; the base image tag `nvidia/cuda:12.6.3-cudnn-runtime-ubuntu24.04` exists on Docker Hub (found with `docker manifest inspect`).

Untested: image build, container start, `/health`, GPU passthrough, model download, Unity connection, CPU file at runtime. If you hit a failure, please report it with the log output.
