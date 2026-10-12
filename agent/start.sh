#!/usr/bin/env bash
# Start the voice server as a detached daemon and wait until it answers /health.
# Uses setsid + </dev/null so it survives an SSH session closing.
set -u
cd "$(dirname "$0")"

[ -f .env ] || { echo "no .env — run ./run.sh once to create it"; exit 1; }
set -a; . ./.env; set +a
PORT="${PORT:-5000}"

pkill -f "uvicorn server:app" 2>/dev/null
sleep 1
: > server.log

# new session, no controlling tty, stdin from /dev/null -> survives ssh logout
setsid .venv/bin/uvicorn server:app --host 0.0.0.0 --port "$PORT" \
    >> server.log 2>&1 < /dev/null &

for i in $(seq 1 60); do
    if curl -sf -m 2 "http://127.0.0.1:$PORT/health" >/dev/null 2>&1; then
        echo "up after ${i}s"
        grep '^\[init\]' server.log
        curl -s "http://127.0.0.1:$PORT/health"; echo
        exit 0
    fi
    sleep 1
done

echo "did not come up in 60s — tail of server.log:"
tail -n 30 server.log
exit 1
