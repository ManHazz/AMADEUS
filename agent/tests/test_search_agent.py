"""search_agent.py: the explicit-trigger routing regex, and the SSRF guard on
web_fetch. These are the exact cases manually verified against the live
deployment on 2026-09-22 (see notes/notes-sept-22) — formalized here."""
import pytest

import search_agent as sa

TRIGGER_CASES = [
    ("search the web for the current UK prime minister", True),
    ("can you look up the weather in Tokyo", True),
    ("google the price of bitcoin", True),
    ("look it up for me", True),
    ("do a web search for mars mission news", True),
    ("please search online for flight prices", True),
    ("web search the latest iphone", True),
    ("who is the prime minister of the UK", False),
    ("what is the tallest mountain", False),
    ("tell me a joke", False),
    ("I look up to my father", False),
    ("the stock market went up today", False),
    ("can you help me look at this differently", False),
]


@pytest.mark.parametrize("text,expected", TRIGGER_CASES)
def test_wants_search(text, expected):
    assert sa.wants_search(text) is expected


def test_strip_trigger_removes_the_phrase():
    assert sa.strip_trigger("search the web for the current UK prime minister") \
        == "the current UK prime minister"
    assert sa.strip_trigger("can you look up the weather in Tokyo") \
        == "the weather in Tokyo"


def test_strip_trigger_noop_without_a_trigger():
    q = "what is the tallest mountain"
    assert sa.strip_trigger(q) == q


class TestIsPublic:
    def test_rejects_loopback(self):
        assert sa._is_public("127.0.0.1") is False

    def test_rejects_private_ranges(self):
        assert sa._is_public("10.0.0.5") is False
        assert sa._is_public("192.168.1.1") is False
        assert sa._is_public("172.17.102.39") is False  # this lab's own subnet

    def test_rejects_link_local_cloud_metadata(self):
        assert sa._is_public("169.254.169.254") is False

    def test_accepts_a_public_ip(self):
        assert sa._is_public("8.8.8.8") is True


def test_web_fetch_refuses_private_address():
    assert "refusing" in sa.web_fetch("http://127.0.0.1:8081/v1/models")


def test_web_fetch_refuses_non_http_scheme():
    assert "only http" in sa.web_fetch("file:///etc/passwd")


def test_real_url_unwraps_a_duckduckgo_redirect():
    wrapped = "//duckduckgo.com/l/?uddg=https%3A%2F%2Fexample.com%2Fpage&rut=abc"
    assert sa._real_url(wrapped) == "https://example.com/page"


def test_real_url_passes_through_a_plain_url():
    assert sa._real_url("https://example.com") == "https://example.com"
