#!/usr/bin/env bash
# Start (or restart) a cloudflare quick tunnel to the local voice server on :5000
# and print the public https URL.
set -u
cd "$(dirname "$0")"
PORT="${PORT:-5000}"
CF="$(command -v cloudflared || echo "$HOME/.local/bin/cloudflared")"

pkill -f "cloudflared tunnel" 2>/dev/null
sleep 1

: > cloudflared.log
setsid "$CF" tunnel --url "http://localhost:$PORT" --no-autoupdate \
    >> cloudflared.log 2>&1 < /dev/null &

for _ in $(seq 1 20); do
    url=$(grep -Eo 'https://[a-z0-9-]+\.trycloudflare\.com' cloudflared.log | head -1)
    [ -n "$url" ] && break
    sleep 1
done

if [ -n "${url:-}" ]; then
    echo "PUBLIC URL: $url"
    echo "pid: $(pgrep -f 'cloudflared tunnel' | head -1)"
else
    echo "no URL yet — tail of cloudflared.log:"
    tail -n 15 cloudflared.log
fi
