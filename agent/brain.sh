#!/usr/bin/env bash
# OPTIONAL helper: run a co-located single-node llama.cpp server for the brain.
# Handy for a Mixture-of-Experts model on a small GPU — the routed experts are
# pinned to system RAM (-ot ...=CPU) while the active path + attention + KV cache
# stay on the GPU, so it fits next to STT/TTS with no RPC and no network hop.
#
#   MODEL=/path/to/model.gguf ./brain.sh        # start on 127.0.0.1:$BRAIN_PORT
#   ./brain.sh stop
#
# Env: MODEL (required), LLAMA_SERVER (default: llama-server on PATH),
#      BRAIN_PORT=8081  BRAIN_CTX=8192  BRAIN_NGL=99
set -u
cd "$(dirname "$0")"

LLAMA_SERVER="${LLAMA_SERVER:-llama-server}"
MODEL="${MODEL:?set MODEL to your .gguf path}"
PORT="${BRAIN_PORT:-8081}"
CTX="${BRAIN_CTX:-8192}"
NGL="${BRAIN_NGL:-99}"

if [ "${1:-}" = "stop" ]; then
    pkill -f "llama-server .*port $PORT" && echo "stopped" || echo "not running"
    exit 0
fi

pkill -f "llama-server .*port $PORT" 2>/dev/null
sleep 1
: > brain.log

# -ot ".ffn_.*_exps.=CPU" : routed-expert FFN weights -> CPU RAM
# -fa on + q8_0 KV cache  : smaller / faster attention, less VRAM
setsid "$LLAMA_SERVER" \
    -m "$MODEL" \
    -ot ".ffn_.*_exps.=CPU" \
    -ngl "$NGL" \
    -c "$CTX" \
    -fa on \
    --cache-type-k q8_0 --cache-type-v q8_0 \
    --jinja \
    --host 127.0.0.1 --port "$PORT" \
    >> brain.log 2>&1 < /dev/null &

echo "starting llama-server (pid group $!), ctx=$CTX ngl=$NGL port=$PORT"
for i in $(seq 1 180); do
    if curl -sf -m 2 "http://127.0.0.1:$PORT/v1/models" >/dev/null 2>&1; then
        echo "brain up after ${i}s"
        nvidia-smi --query-gpu=memory.used,memory.free --format=csv,noheader
        exit 0
    fi
    sleep 1
done
echo "brain did not come up in 180s — tail brain.log:"
tail -n 40 brain.log
exit 1
