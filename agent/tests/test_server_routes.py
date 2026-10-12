"""server.py: routing/auth wiring via FastAPI's TestClient. No real audio,
brain, or network involved -- just that the endpoints require the token and
that GET / mints a fresh, distinct session id per request."""
import re

from fastapi.testclient import TestClient

import server

client = TestClient(server.app)


def test_health_reports_ok_and_session_stats():
    r = client.get("/health")
    assert r.status_code == 200
    body = r.json()
    assert body["ok"] is True
    assert "active_sessions" in body


def test_index_injects_distinct_session_ids_per_load():
    html1 = client.get("/").text
    html2 = client.get("/").text
    assert "__SESSION_ID__" not in html1
    assert "__ACCESS_TOKEN__" not in html1
    id1 = re.search(r"SESSION_ID = '([^']+)'", html1).group(1)
    id2 = re.search(r"SESSION_ID = '([^']+)'", html2).group(1)
    assert id1 and id2 and id1 != id2


def test_converse_requires_a_valid_token():
    r = client.post("/api/converse", files={"audio": ("clip.wav", b"x" * 3000, "audio/wav")})
    assert r.status_code == 401


def test_pending_requires_a_valid_token():
    assert client.get("/api/pending").status_code == 401


def test_reset_requires_a_valid_token():
    assert client.post("/api/reset").status_code == 401


def test_reset_succeeds_with_the_right_token():
    r = client.post("/api/reset", headers={
        "X-Access-Token": server.ACCESS_TOKEN, "X-Session-Id": "test-session",
    })
    assert r.status_code == 200
    assert r.json() == {"status": "reset"}
