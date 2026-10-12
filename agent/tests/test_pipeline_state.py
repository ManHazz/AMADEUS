"""pipeline.py: State (per-session conversation) and SessionStore (the
per-session isolation added 2026-09-22 — see notes/notes-sept-22). These are
exactly the behaviors manually verified against the live deployment with two
concurrent "Alice"/"Bob" sessions; formalized here so a regression fails CI
instead of needing another live phone test to notice."""
import time

import pipeline


def test_state_add_user_and_assistant_round_trip():
    st = pipeline.State()
    snap = st.add_user("hello")
    assert snap[-1] == {"role": "user", "content": "hello"}
    st.add_assistant("hi there")
    assert st.history[-1] == {"role": "assistant", "content": "hi there"}


def test_state_trim_keeps_system_message_plus_last_n():
    st = pipeline.State()
    n = pipeline.HISTORY_TURNS
    for i in range(n + 10):
        st.add_user(f"msg {i}")
        st.add_assistant(f"reply {i}")
    assert len(st.history) == 1 + n
    assert st.history[0]["role"] == "system"


def test_state_drop_last_user_only_pops_a_matching_message():
    st = pipeline.State()
    st.add_user("keep this")
    st.drop_last_user("something else")  # doesn't match -> no-op
    assert st.history[-1]["content"] == "keep this"
    st.drop_last_user("keep this")
    assert st.history[-1]["role"] == "system"


def test_state_reset_clears_history_and_deliveries():
    st = pipeline.State()
    st.add_user("hi")
    st.push_delivery({"question": "q", "text": "a", "wavs": []})
    st.reset()
    assert len(st.history) == 1 and st.history[0]["role"] == "system"
    assert st.deliveries == []


def test_state_deliveries_are_fifo():
    st = pipeline.State()
    st.push_delivery({"question": "q1", "text": "a1", "wavs": []})
    st.push_delivery({"question": "q2", "text": "a2", "wavs": []})
    assert st.pop_delivery()["question"] == "q1"
    assert st.pop_delivery()["question"] == "q2"
    assert st.pop_delivery() is None


def test_state_search_active_counter_never_goes_negative():
    st = pipeline.State()
    st.search_begin()
    st.search_begin()
    assert st.searches_active == 2
    st.search_end()
    st.search_end()
    st.search_end()  # extra end
    assert st.searches_active == 0


def test_sessionstore_same_id_returns_the_same_state():
    store = pipeline.SessionStore()
    a1 = store.get("sess-a")
    a2 = store.get("sess-a")
    assert a1 is a2


def test_sessionstore_different_ids_are_fully_isolated():
    """The exact bug this was built to fix: two concurrent sessions must
    never see each other's conversation."""
    store = pipeline.SessionStore()
    a = store.get("sess-a")
    b = store.get("sess-b")
    assert a is not b
    a.add_user("my name is alice")
    b.add_user("my name is bob")
    assert "alice" in a.history[-1]["content"]
    assert all("alice" not in m["content"] for m in b.history)
    assert "bob" in b.history[-1]["content"]
    assert all("bob" not in m["content"] for m in a.history)


def test_sessionstore_reset_on_one_session_does_not_affect_another():
    store = pipeline.SessionStore()
    a = store.get("sess-a")
    b = store.get("sess-b")
    a.add_user("alice's message")
    b.add_user("bob's message")
    a.reset()
    assert len(a.history) == 1  # just the system prompt
    assert b.history[-1]["content"] == "bob's message"  # untouched


def test_sessionstore_stats_reflects_active_sessions():
    store = pipeline.SessionStore()
    store.get("sess-a")
    store.get("sess-b")
    assert store.stats()["active_sessions"] == 2


def test_sessionstore_evicts_sessions_idle_past_the_ttl(monkeypatch):
    store = pipeline.SessionStore()
    store.get("sess-a")
    assert store.stats()["active_sessions"] == 1
    future = time.time() + pipeline.SESSION_TTL_SECONDS + 1
    monkeypatch.setattr(time, "time", lambda: future)
    store.get("sess-b")  # any get() sweeps expired sessions first
    assert store.stats()["active_sessions"] == 1  # sess-a evicted, only sess-b left
