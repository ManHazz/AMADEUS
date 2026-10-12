"""Report generation: summary stats + report.json / report.md / report.html (self-contained)."""
from __future__ import annotations

import html
import json
import pathlib
from collections import Counter

import helpers as H

LAT_KEYS = [("first_meta_ms", "request -> first META"), ("first_segment_ms", "request -> first segment event"),
            ("first_audio_ms", "request -> first AUDIO"), ("last_frame_ms", "request -> last frame"),
            ("server_stt_ms", "server STT done (DEBUG_TIMING)")]
ORDER = [H.PASS, H.WARN, H.FAIL, H.INCONCLUSIVE]


def build(meta: dict, results: list[dict]) -> dict:
    sc_counts = Counter(r["status"] for r in results)
    turns = [t for r in results for t in r["turns"]]
    t_counts = Counter(t["status"] for t in turns)
    c_counts = Counter(c["status"] for t in turns for c in t["checks"] if c["status"] != H.SKIP)
    lat = {}
    for k, _ in LAT_KEYS:
        vals = [t["timings"].get(k) for t in turns if t["timings"].get(k) is not None]
        lat[k] = {"n": len(vals), "median": None if not vals else round(H.median(vals)),
                  "p90": None if not vals else round(H.percentile(vals, 90)),
                  "min": min(vals) if vals else None, "max": max(vals) if vals else None}
    notable = []
    for r in results:
        for t in r["turns"]:
            for c in t["checks"]:
                if c["status"] in (H.FAIL, H.WARN, H.INCONCLUSIVE):
                    notable.append({"scenario": r["id"], "name": r["name"], "turn": t["index"],
                                    "check": c["name"], "status": c["status"], "detail": c["detail"]})
    notable.sort(key=lambda n: {H.FAIL: 0, H.INCONCLUSIVE: 1, H.WARN: 2}[n["status"]])
    sims = [t["similarity"] for t in turns if t.get("similarity") is not None]
    summary = {"scenarios": dict(sc_counts), "turns": dict(t_counts), "checks": dict(c_counts),
               "latency": lat, "notable": notable,
               "stt_similarity": {"n": len(sims), "median": H.median(sims), "min": min(sims) if sims else None}}
    return {"meta": meta, "summary": summary, "scenarios": results}


# ------------------------------------------------------------------ markdown
def _md(s) -> str:
    return str(s if s is not None else "").replace("|", "\\|").replace("\n", " ")


def _fmt_ms(v):
    return "n/a" if v is None else f"{v / 1000:.2f} s"


def _reply_lines(t) -> str:
    out = []
    for s in t["segments"]:
        if s["lang"] == "ja":
            out.append(f"[ja] {s['text']}" + (f" / {s['tr']}" if s.get("tr") else " / (no tr)"))
        else:
            out.append(f"[{s['lang']} silent] {s['text']}")
    return "<br>".join(_md(x) for x in out) or _md(t.get("reply_summary"))


def to_markdown(d: dict) -> str:
    m, s = d["meta"], d["summary"]
    L = ["# Japanese-tutor voice agent: end-to-end test report", ""]
    L += [f"- Run: {m['started']} ({m['duration_s']} s)", f"- Server: {m['server_mode']}",
          f"- Brain: {m.get('model')}, STT {m.get('stt')}, TTS {m.get('tts')}",
          f"- Whisper round-trip on: {m.get('whisper_roundtrip_device')}, LLM requests made: {m['llm_requests']}",
          "- Inputs: synthetic Kokoro voices (not a real microphone), so STT accuracy is optimistic.", ""]
    L += ["## Summary", "", "| | PASS | WARN | FAIL | INCONCLUSIVE |", "|---|---|---|---|---|"]
    for label, key in (("Scenarios", "scenarios"), ("Turns", "turns"), ("Individual checks", "checks")):
        L.append(f"| {label} | " + " | ".join(str(s[key].get(k, 0)) for k in ORDER) + " |")
    L += ["", "| Latency (ms) | n | median | p90 | min | max |", "|---|---|---|---|---|---|"]
    for k, label in LAT_KEYS:
        v = s["latency"][k]
        L.append(f"| {label} | {v['n']} | {v['median']} | {v['p90']} | {v['min']} | {v['max']} |")
    sim = s["stt_similarity"]
    L += ["", f"STT transcript similarity over {sim['n']} spoken turns: median {sim['median']}, min {sim['min']}.", ""]
    if s["notable"]:
        L += ["### Findings (WARN / FAIL / INCONCLUSIVE)", ""]
        for n in s["notable"]:
            L.append(f"- **{n['status']}** S{n['scenario']} turn {n['turn']} `{n['check']}`: {n['detail']}")
        L.append("")
    for r in d["scenarios"]:
        L += [f"## Scenario {r['id']}: {r['name']}  [{r['status']}]", ""]
        L += ["| # | Input | Heard | Sim | Reply (ja / tr) | Segments | Timings | Status |",
              "|---|---|---|---|---|---|---|---|"]
        for t in r["turns"]:
            tm = t["timings"]
            timing = (f"meta {_fmt_ms(tm.get('first_meta_ms'))}<br>seg {_fmt_ms(tm.get('first_segment_ms'))}<br>"
                      f"audio {_fmt_ms(tm.get('first_audio_ms'))}<br>last {_fmt_ms(tm.get('last_frame_ms') or tm.get('total_ms'))}"
                      f"<br>stt {_fmt_ms(tm.get('server_stt_ms'))}")
            L.append(f"| {t['index']} | {_md(t['input'])} | {_md(t['heard'])} | "
                     f"{'' if t['similarity'] is None else t['similarity']} | {_reply_lines(t)} | "
                     f"{_md(t.get('reply_summary'))} | {timing} | **{t['status']}** |")
        L.append("")
        for t in r["turns"]:
            L.append(f"Checks, turn {t['index']}" + (f" ({t['note']})" if t.get("note") else "") +
                     (f", retried {t['retries']}x" if t.get("retries") else "") + ":")
            L.append("")
            for c in t["checks"]:
                L.append(f"- {c['status']} `{c['name']}`: {c['detail']}")
            L.append("")
    return "\n".join(L)


# ------------------------------------------------------------------ html
CSS = """
:root{--bg:#f6f7f9;--fg:#1c2330;--card:#fff;--mut:#667085;--line:#e3e6ec;--pass:#1a7f4b;--warn:#b26a00;--fail:#c62828;--inc:#5b5fc7}
@media(prefers-color-scheme:dark){:root{--bg:#10141b;--fg:#e8ecf3;--card:#181e29;--mut:#9aa4b5;--line:#293142;--pass:#4cc38a;--warn:#f0b24a;--fail:#ff6b6b;--inc:#9a9dff}}
*{box-sizing:border-box}body{margin:0;background:var(--bg);color:var(--fg);font:15px/1.5 system-ui,"Segoe UI","Yu Gothic",sans-serif}
main{max-width:1100px;margin:0 auto;padding:24px 16px 64px}h1{font-size:26px;margin:0 0 4px}h2{font-size:19px;margin:0}
.sub{color:var(--mut);margin:0 0 20px}.card{background:var(--card);border:1px solid var(--line);border-radius:10px;padding:16px;margin:16px 0}
.badge{display:inline-block;padding:1px 9px;border-radius:99px;font-size:12px;font-weight:700;color:#fff;letter-spacing:.3px}
.PASS{background:var(--pass)}.WARN{background:var(--warn)}.FAIL{background:var(--fail)}.INCONCLUSIVE{background:var(--inc)}.SKIP{background:var(--mut)}
.kpis{display:flex;flex-wrap:wrap;gap:12px}.kpi{flex:1 1 150px;border:1px solid var(--line);border-radius:8px;padding:10px 12px}
.kpi b{display:block;font-size:24px}.kpi span{color:var(--mut);font-size:13px}
table{border-collapse:collapse;width:100%;font-size:14px}th,td{border-top:1px solid var(--line);padding:6px 8px;text-align:left;vertical-align:top}
th{color:var(--mut);font-weight:600}.turn{border-top:1px solid var(--line);padding:12px 0}.turn:first-of-type{border-top:0}
.row{display:flex;justify-content:space-between;gap:8px;align-items:baseline}.in{font-weight:600}.heard{color:var(--mut)}
.seg{margin:4px 0}.ja{font-size:20px;line-height:2}.tr{color:var(--mut);margin-left:2px}.en{color:var(--warn);font-style:italic}
ruby rt{font-size:10px;color:var(--mut)}.checks{margin:6px 0 0;padding:0;list-style:none;font-size:13px}.checks li{margin:2px 0}
.bar{height:8px;background:var(--line);border-radius:4px;overflow:hidden;margin-top:3px}.bar i{display:block;height:100%;background:var(--pass)}
.bar i.slow{background:var(--warn)}.mut{color:var(--mut)}code{font-size:12.5px}.lat{display:grid;grid-template-columns:130px 1fr 70px;gap:6px;align-items:center;font-size:13px}
details summary{cursor:pointer;color:var(--mut);font-size:13px}
@media print{body{background:#fff}.card{break-inside:avoid}}
"""


def _b(status):
    return f'<span class="badge {status}">{status}</span>'


def _ruby_html(seg) -> str:
    toks = seg.get("ruby") or [{"t": seg["text"], "r": ""}]
    return "".join(f"<ruby>{html.escape(t['t'])}<rt>{html.escape(t['r'])}</rt></ruby>" if t.get("r")
                   else html.escape(t["t"]) for t in toks)


def to_html(d: dict) -> str:
    m, s = d["meta"], d["summary"]
    e = html.escape
    P = ["<!doctype html><html lang=en><head><meta charset=utf-8><meta name=viewport content='width=device-width,initial-scale=1'>"
         f"<title>Voice agent e2e report</title><style>{CSS}</style></head><body><main>",
         "<h1>Japanese-tutor voice agent: end-to-end test report</h1>",
         f"<p class=sub>{e(m['started'])} &middot; {m['duration_s']} s &middot; {e(m['server_mode'])}<br>"
         f"Brain {e(str(m.get('model')))} &middot; STT {e(str(m.get('stt')))} &middot; TTS {e(str(m.get('tts')))} "
         f"&middot; {m['llm_requests']} LLM requests &middot; inputs are synthetic Kokoro speech (not a real mic)</p>"]
    # KPIs
    P.append("<div class=card><h2>Summary</h2><div class=kpis>")
    for label, key in (("Scenarios", "scenarios"), ("Turns", "turns"), ("Checks", "checks")):
        cnt = s[key]
        tot = sum(cnt.values())
        cells = " ".join(f"{_b(k)} {cnt.get(k, 0)}" for k in ORDER if cnt.get(k))
        P.append(f"<div class=kpi><b>{tot}</b><span>{label}</span><br>{cells}</div>")
    fa = s["latency"]["first_audio_ms"]
    P.append(f"<div class=kpi><b>{_fmt_ms(fa['median'])}</b><span>median first audio</span><br>p90 {_fmt_ms(fa['p90'])}</div>")
    P.append("</div>")
    mx = max([v["max"] or 0 for v in s["latency"].values()] + [1])
    P.append("<h2 style='margin-top:16px'>Latency (median / p90)</h2>")
    for k, label in LAT_KEYS:
        v = s["latency"][k]
        if v["median"] is None:
            continue
        cls = "slow" if k == "first_audio_ms" and v["p90"] and v["p90"] > 6000 else ""
        P.append(f"<div class=lat><span>{e(label)}</span><div class=bar><i class='{cls}' style='width:{100 * v['median'] / mx:.0f}%'></i></div>"
                 f"<span>{_fmt_ms(v['median'])} / {_fmt_ms(v['p90'])}</span></div>")
    sim = s["stt_similarity"]
    P.append(f"<p class=mut>STT transcript similarity ({sim['n']} turns): median {sim['median']}, min {sim['min']}</p></div>")
    # scenario overview
    P.append("<div class=card><h2>Scenarios</h2><table><tr><th>#<th>Scenario<th>Demo<th>Status</tr>")
    for r in d["scenarios"]:
        P.append(f"<tr><td>{r['id']}<td><a href='#s{r['id']}'>{e(r['name'])}</a><td class=mut>{e(r['demo_ref'])}<td>{_b(r['status'])}</tr>")
    P.append("</table></div>")
    if s["notable"]:
        P.append("<div class=card><h2>Findings</h2><ul class=checks>")
        for n in s["notable"]:
            P.append(f"<li>{_b(n['status'])} S{n['scenario']} turn {n['turn']} <code>{e(n['check'])}</code> {e(n['detail'])}</li>")
        P.append("</ul></div>")
    # details
    for r in d["scenarios"]:
        P.append(f"<div class=card id='s{r['id']}'><div class=row><h2>Scenario {r['id']}: {e(r['name'])}</h2>{_b(r['status'])}</div>")
        for t in r["turns"]:
            tm = t["timings"]
            P.append("<div class=turn><div class=row><span class=in>"
                     f"Turn {t['index']}: {e(str(t['input']))}</span>{_b(t['status'])}</div>")
            if t.get("note"):
                P.append(f"<div class=mut>{e(t['note'])}</div>")
            if t.get("heard") is not None:
                sim_txt = "" if t["similarity"] is None else f" &middot; similarity {t['similarity']:.2f}"
                P.append(f"<div class=heard>heard: &ldquo;{e(t['heard'])}&rdquo;{sim_txt}</div>")
            for sg in t["segments"]:
                if sg["lang"] == "ja":
                    P.append(f"<div class=seg><span class=ja>{_ruby_html(sg)}</span><br>"
                             f"<span class=tr>{e(sg.get('tr') or '(no translation)')}</span></div>")
                else:
                    P.append(f"<div class=seg><span class=en>{e(sg['text'])}</span> <span class=mut>(silent tip)</span></div>")
            if not t["segments"] and t.get("reply_summary"):
                P.append(f"<div class=mut>{e(t['reply_summary'])}</div>")
            bits = [f"{lab} {_fmt_ms(tm.get(k))}" for k, lab in (("first_meta_ms", "meta"), ("first_segment_ms", "segment"),
                                                                 ("first_audio_ms", "audio"), ("last_frame_ms", "last"),
                                                                 ("server_stt_ms", "STT (server)")) if tm.get(k) is not None]
            if bits:
                P.append(f"<div class=mut>timings: {' &middot; '.join(bits)}"
                         + (f" &middot; retried {t['retries']}x" if t.get("retries") else "") + "</div>")
            P.append("<ul class=checks>" + "".join(
                f"<li>{_b(c['status'])} <code>{e(c['name'])}</code> <span class=mut>{e(c['detail'])}</span></li>"
                for c in t["checks"]) + "</ul></div>")
        P.append("</div>")
    P.append("</main></body></html>")
    return "".join(P)


def write_all(outdir: pathlib.Path, data: dict):
    outdir.mkdir(parents=True, exist_ok=True)
    (outdir / "report.json").write_text(json.dumps(data, ensure_ascii=False, indent=2), encoding="utf-8")
    (outdir / "report.md").write_text(to_markdown(data), encoding="utf-8")
    (outdir / "report.html").write_text(to_html(data), encoding="utf-8")
