"""Dynamic web-search path.

A separate architecture from chat_agent: the answer can't be produced in one
streamed pass, so the request returns *immediately* with a spoken "hold on, keep
talking" notification while a BACKGROUND task runs the tool loop. The finished
answer is picked up later by GET /api/pending.

  wants_search(text)  -> should this turn go here instead of chat_agent?
  handle(...)         -> generator of frames: search_started event + notification
  run_job(...)        -> background coroutine: tool loop -> queue a delivery
"""
import re
import html as htmllib
import json
import socket
import asyncio
import ipaddress
import urllib.parse

import httpx

from pipeline import brain_call, speak_chunks, synth, meta_frame, event_frame, audio_frame

# --------------------------------------------------------------------------- routing
# Explicit trigger only: the user has to ask for a search out loud. Everything
# else goes to chat_agent (fast, no tools). Tune these to taste.
_POLITE = r"(?:hey[,. ]+)?(?:can you |could you |would you |will you |please )*"

_TRIGGER = re.compile(
    # unambiguous phrases, anywhere in the utterance
    r"\b(?:search (?:the web|online|the internet)|(?:web|internet) search"
    r"|do an? (?:web |internet )?search|search google|search it up|search for\b"
    r"|google (?:it|that|the|for|me|up|whether|if|how|what|who|when|where|why|\w+ )"
    r")"
    # OR an imperative "look … up" at the very start (so "I look up to him" won't match)
    rf"|^{_POLITE}look (?:it |that |this |up )*up\b"
    rf"|^{_POLITE}google\b",
    re.I,
)

_TRIGGER_PREFIX = re.compile(
    rf"^\s*{_POLITE}"
    r"(?:search (?:the web|online|the internet)(?: for)?"
    r"|(?:web|internet) search(?: for)?"
    r"|do an? (?:web |internet )?search(?: for)?"
    r"|look (?:it |that |this )?up"
    r"|google)\s*[:,]?\s*(?:for\s+|about\s+|whether\s+|if\s+)?",
    re.I,
)


def wants_search(text: str) -> bool:
    return bool(_TRIGGER.search(text or ""))


def strip_trigger(text: str) -> str:
    """Drop a leading 'search the web for …' so the spoken restatement reads well."""
    return _TRIGGER_PREFIX.sub("", text or "").strip() or text


SEARCH_SYSTEM = (
    "The user explicitly asked you to search the web. Call web_search with a good query "
    "(then web_fetch on the best result if the snippets disagree — prefer official and "
    "most-recently-dated sources). Then answer in one or two plain spoken sentences, "
    "briefly restating what they wanted. No markdown."
)

# --------------------------------------------------------------------------- tools
UA = "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36"
TIMEOUT = 12.0
MAX_BYTES = 2_000_000
MAX_TEXT = 3500

_RESULT_A = re.compile(r'<a[^>]+class="result__a"[^>]+href="([^"]+)"[^>]*>(.*?)</a>', re.S)
_SNIPPET = re.compile(r'<a[^>]+class="result__snippet"[^>]*>(.*?)</a>', re.S)
_TAG = re.compile(r"<[^>]+>")
_WS = re.compile(r"[ \t\r\f\v]+")
_NL = re.compile(r"\n{3,}")
_SCRIPT_STYLE = re.compile(r"<(script|style|noscript|svg|head)\b.*?</\1>", re.S | re.I)


def _clean(s: str) -> str:
    return _WS.sub(" ", htmllib.unescape(_TAG.sub("", s))).strip()


def _real_url(href: str) -> str:
    if "uddg=" in href:
        q = urllib.parse.parse_qs(urllib.parse.urlparse(href).query)
        if q.get("uddg"):
            return q["uddg"][0]
    return ("https:" + href) if href.startswith("//") else href


def web_search(query: str, n: int = 6) -> str:
    query = (query or "").strip()
    if not query:
        return "web_search error: empty query"
    try:
        r = httpx.get("https://html.duckduckgo.com/html/", params={"q": query},
                      headers={"User-Agent": UA}, timeout=TIMEOUT, follow_redirects=True)
        r.raise_for_status()
    except Exception as e:  # noqa: BLE001
        return f"web_search error: {type(e).__name__}: {e}"
    titles = _RESULT_A.findall(r.text)
    snips = _SNIPPET.findall(r.text)
    if not titles:
        return f"web_search: no results for {query!r}"
    out = []
    for i, (href, title) in enumerate(titles[:n]):
        snip = _clean(snips[i]) if i < len(snips) else ""
        out.append(f"{i + 1}. {_clean(title)}\n   {snip}\n   {_real_url(href)}")
    return "\n".join(out)


def _is_public(host: str) -> bool:
    try:
        infos = socket.getaddrinfo(host, None)
    except Exception:  # noqa: BLE001
        return False
    for *_, sa in infos:
        try:
            ip = ipaddress.ip_address(sa[0])
        except ValueError:
            return False
        if (ip.is_private or ip.is_loopback or ip.is_link_local
                or ip.is_reserved or ip.is_multicast or ip.is_unspecified):
            return False
    return True


def web_fetch(url: str) -> str:
    url = (url or "").strip()
    p = urllib.parse.urlparse(url)
    if p.scheme not in ("http", "https") or not p.hostname:
        return "web_fetch error: only http(s) URLs are allowed"
    if not _is_public(p.hostname):
        return "web_fetch error: refusing to fetch a private/local address"
    try:
        with httpx.stream("GET", url, headers={"User-Agent": UA},
                          timeout=TIMEOUT, follow_redirects=True) as r:
            r.raise_for_status()
            fh = urllib.parse.urlparse(str(r.url)).hostname
            if fh and not _is_public(fh):
                return "web_fetch error: redirect to a private address"
            buf = b""
            for chunk in r.iter_bytes():
                buf += chunk
                if len(buf) >= MAX_BYTES:
                    break
    except Exception as e:  # noqa: BLE001
        return f"web_fetch error: {type(e).__name__}: {e}"
    text = _SCRIPT_STYLE.sub(" ", buf.decode("utf-8", "ignore"))
    text = _clean(text.replace("</p>", "\n").replace("</div>", "\n"))
    text = _NL.sub("\n\n", text)
    return (text[:MAX_TEXT] + " …[truncated]") if len(text) > MAX_TEXT else (text or "web_fetch: no readable text")


TOOLS = [
    {"type": "function", "function": {
        "name": "web_search",
        "description": "Search the web for current/uncertain info. Returns titles, snippets, URLs.",
        "parameters": {"type": "object", "properties": {"query": {"type": "string"}}, "required": ["query"]}}},
    {"type": "function", "function": {
        "name": "web_fetch",
        "description": "Fetch the readable text of one web page by URL (from a web_search result).",
        "parameters": {"type": "object", "properties": {"url": {"type": "string"}}, "required": ["url"]}}},
]

_DISPATCH = {"web_search": lambda a: web_search(a.get("query", "")),
             "web_fetch": lambda a: web_fetch(a.get("url", ""))}

SEARCH_MAX_ITERS = 3


def run_tool(name: str, args: dict) -> str:
    fn = _DISPATCH.get(name)
    return fn(args) if fn else f"error: unknown tool {name!r}"


# --------------------------------------------------------------------------- request path
def handle(user_text: str, snapshot: list[dict], state, loop):
    """Return frames for the immediate spoken reply. All blocking work happens
    inside the generator (Starlette iterates it in a threadpool)."""

    def gen():
        msgs = [{"role": "system", "content": SEARCH_SYSTEM}] + snapshot[1:]
        m = brain_call(msgs, TOOLS, tool_choice="required").choices[0].message
        tool_calls = [
            {"id": t.id, "name": t.function.name, "args": t.function.arguments or ""}
            for t in (m.tool_calls or [])
        ]

        if not tool_calls:                        # model refused to call a tool
            reply = (m.content or "I couldn't work out what to search for.").strip()
            state.add_assistant(reply)
            yield meta_frame(user_text, "")
            for c in speak_chunks(reply):
                yield audio_frame(c)
            yield meta_frame(user_text, reply, done=True)
            return

        state.drop_last_user(user_text)           # the job re-adds a clean Q/A pair
        state.search_begin()
        try:
            query = json.loads(tool_calls[0]["args"] or "{}").get("query", "")
        except json.JSONDecodeError:
            query = ""
        asyncio.run_coroutine_threadsafe(
            run_job(user_text, snapshot, tool_calls, state), loop
        )
        note = (f"Let me look that up{(' — ' + query) if query else ''}. You can ask me "
                "something else while I search, and I'll tell you what I find.")
        yield event_frame("search_started", query=query)
        yield meta_frame(user_text, note)
        yield audio_frame(note)
        yield meta_frame(user_text, note, done=True)

    return gen()


async def run_job(question: str, snapshot: list[dict], tool_calls: list[dict], state):
    assistant_msg = {"role": "assistant", "content": None, "tool_calls": [
        {"id": tc["id"], "type": "function",
         "function": {"name": tc["name"], "arguments": tc["args"]}} for tc in tool_calls]}
    msgs = ([{"role": "system", "content": SEARCH_SYSTEM}] + snapshot[1:] + [assistant_msg])
    try:
        for _ in range(SEARCH_MAX_ITERS):
            for tc in tool_calls:
                try:
                    args = json.loads(tc["args"] or "{}")
                except json.JSONDecodeError:
                    args = {}
                result = await asyncio.to_thread(run_tool, tc["name"], args)
                msgs.append({"role": "tool", "tool_call_id": tc["id"], "content": result[:6000]})
            resp = await asyncio.to_thread(brain_call, msgs, TOOLS)
            mm = resp.choices[0].message
            if not mm.tool_calls:
                final = (mm.content or "").strip() or "I couldn't find a clear answer."
                break
            msgs.append(mm.model_dump(exclude_none=True))
            tool_calls = [{"id": t.id, "name": t.function.name, "args": t.function.arguments or ""}
                          for t in mm.tool_calls]
        else:
            final = "That search took too many steps; ask me again more specifically."

        asked = strip_trigger(question).rstrip(" ?.")
        spoken = f"You asked me to look up {asked}. {final}"
        wavs = await asyncio.to_thread(lambda: [synth(c) for c in speak_chunks(spoken)])
        state.add_qa(question, final)
        state.push_delivery({"question": question, "text": spoken, "wavs": wavs})
    except Exception as e:  # noqa: BLE001
        print(f"[search] job failed: {type(e).__name__}: {e}")
        msg = "Sorry, that web search failed."
        state.push_delivery({"question": question, "text": msg, "wavs": [synth(msg)]})
    finally:
        state.search_end()
