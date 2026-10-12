# syntax=docker/dockerfile:1
# GPU image for the voice agent (FastAPI + faster-whisper + Kokoro).
# Also runs on CPU: just do not pass a GPU and set STT_DEVICE=cpu / TTS_DEVICE=cpu.
# Base: CUDA 12.6 + cuDNN 9 runtime, which is what ctranslate2 (faster-whisper) needs.
FROM nvidia/cuda:12.6.3-cudnn-runtime-ubuntu24.04

ENV DEBIAN_FRONTEND=noninteractive \
    PYTHONUNBUFFERED=1 \
    PIP_NO_CACHE_DIR=1 \
    PIP_DISABLE_PIP_VERSION_CHECK=1 \
    HF_HOME=/models \
    PATH=/opt/venv/bin:$PATH

# python3.12 ships with Ubuntu 24.04. build-essential + cmake are a fallback for
# pyopenjtalk-plus if no prebuilt wheel matches; espeak-ng is misaki's English
# fallback; libsndfile1 is for soundfile; ffmpeg decodes browser audio (webm/ogg).
RUN apt-get update && apt-get install -y --no-install-recommends \
        python3 python3-venv python3-dev \
        build-essential cmake git \
        espeak-ng libsndfile1 ffmpeg \
        ca-certificates curl \
    && rm -rf /var/lib/apt/lists/*

RUN python3 -m venv /opt/venv

WORKDIR /app

# Dependencies first so source edits do not invalidate this layer.
# torch from the cu126 index (the default PyPI wheel would pull a different CUDA build).
RUN pip install torch --index-url https://download.pytorch.org/whl/cu126

COPY requirements.txt .
# av 19+ breaks faster-whisper audio decoding, so cap it.
RUN echo "av<19" > /tmp/constraints.txt \
    && pip install -c /tmp/constraints.txt -r requirements.txt

# Non-root user; /models is the Hugging Face cache (mount a volume here).
RUN useradd --create-home --uid 10001 agent \
    && mkdir -p /models \
    && chown -R agent:agent /models /app
USER agent

COPY --chown=agent:agent . .

VOLUME /models
EXPOSE 5000

# First start downloads models, so allow a long start period.
HEALTHCHECK --interval=30s --timeout=10s --start-period=900s --retries=5 \
    CMD curl -fsS http://127.0.0.1:5000/health || exit 1

# Same as run_local.ps1, but bound to 0.0.0.0 so the published port works.
CMD ["python", "-m", "uvicorn", "server:app", "--host", "0.0.0.0", "--port", "5000"]
