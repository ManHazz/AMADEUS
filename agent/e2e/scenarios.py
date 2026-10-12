"""Scenario definitions (data only). Mirrors unity/DEMO_SCRIPT.md.

A Turn is one spoken utterance -> one POST /api/converse (= one Gemini call).
text may be a callable(ctx) -> str, where ctx["prev_ja"] is the first ja segment
text of the previous reply in the same scenario (used by the shadowing scenario).
expect_ja_any: WARN if none of these strings appears in the reply (content sanity).
"""
from __future__ import annotations

from dataclasses import dataclass, field


@dataclass
class Turn:
    text: object                                  # str | callable(ctx) -> str
    lang: str | None = None                       # force voice: "en" | "ja" (default: auto)
    expect_ja_any: list = field(default_factory=list)
    persona: bool = False
    note: str = ""


@dataclass
class Scenario:
    id: int
    name: str
    kind: str = "converse"                        # converse | noise | health
    turns: list = field(default_factory=list)
    demo_ref: str = ""


def _shadow(ctx):
    return ctx.get("prev_ja") or "おはようございます。"


SCENARIOS = [
    Scenario(1, "Greeting in Japanese", turns=[Turn("こんにちは")], demo_ref="Scene 2"),
    Scenario(2, "Teach me: good morning", turns=[
        Turn("Teach me how to say good morning in Japanese.", expect_ja_any=["おはよう"])], demo_ref="Scene 3"),
    Scenario(3, "Answer the check question in Japanese", turns=[
        Turn("Teach me how to say good morning in Japanese.", expect_ja_any=["おはよう"],
             note="setup turn, creates the check question"),
        Turn("おはようございます")], demo_ref="Scene 3"),
    Scenario(4, "Gentle correction (tense error)", turns=[
        Turn("昨日、映画を見ます", expect_ja_any=["見ました", "みました"])], demo_ref="Scene 4"),
    Scenario(5, "Quiz mode: right and wrong answer", turns=[
        Turn("Quiz me."),
        Turn("はい", note="fixed answer, structure only"),
        Turn("わかりません", note="fixed answer, structure only")], demo_ref="Scene 5"),
    Scenario(6, "Shadowing: repeat her line", turns=[
        Turn("Let's do shadowing."),
        Turn(_shadow, lang="ja", note="learner repeats the first ja line of the previous reply")],
        demo_ref="Scene 6"),
    Scenario(7, "Mixed English/Japanese request", turns=[
        Turn("How do I say thank you very much in Japanese?", expect_ja_any=["ありがとうございます"])],
        demo_ref="Scene 8"),
    Scenario(8, "Persona: neutral AI tutor (not a fictional character)", turns=[
        Turn("Are you a character from an anime?", persona=True,
             note="synthetic voice: a second, easier identity question follows in case the first is misheard"),
        Turn("Who are you, really?", persona=True)], demo_ref="Scene 7"),
    Scenario(9, "Noise / too-short audio robustness", kind="noise", demo_ref="Scene 9"),
    Scenario(10, "Health and reset endpoints", kind="health", demo_ref="infrastructure"),
]
