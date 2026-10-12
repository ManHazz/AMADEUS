#!/usr/bin/env bash
# Launch the voice agent backend in the foreground.
#
#   ./run.sh              # server on 0.0.0.0:$PORT (default 5000)
#   ./run.sh tunnel       # + a Cloudflare quick tunnel (public HTTPS URL)
#
# First run copies .env.example -> .env. Edit .env, then re-run.
# For a detached start that waits for /health, use ./start.sh instead.
set -euo pipefail
cd "$(dirname "$0")"

if [ ! -f .env ]; then
  cp .env.example .env
  # give it a random token so it works out of the box
  tok=$(python3 -c 'import secrets;print(secrets.token_urlsafe(16))')
  sed -i "s/^VOICE_AGENT_TOKEN=.*/VOICE_AGENT_TOKEN=$tok/" .env
  echo "[run] wrote .env (edit BRAIN_URL etc. as needed)"
fi

set -a; . ./.env; set +a
PORT="${PORT:-5000}"
echo "[run] token : ${VOICE_AGENT_TOKEN:-<random at startup>}"
echo "[run] listen: http://0.0.0.0:$PORT"

if [ "${1:-}" = "tunnel" ]; then
  CF="$(command -v cloudflared || echo "$HOME/.local/bin/cloudflared")"
  ( sleep 3; exec "$CF" tunnel --url "http://localhost:$PORT" ) &
fi

exec .venv/bin/uvicorn server:app --host 0.0.0.0 --port "$PORT"
