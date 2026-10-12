"""pipeline.py: markdown stripping and the sentence-chunking used for TTS."""
import re

import pipeline


def test_clean_for_speech_strips_bold_and_headers():
    # heading marker only strips at the real start of a line (so "C#" or a
    # mid-sentence "#1" is left alone) -- put it on its own line to test that
    text = "**Bold**\n# Heading\nplain text"
    cleaned = pipeline.clean_for_speech(text)
    assert "*" not in cleaned
    assert "#" not in cleaned
    assert "Bold" in cleaned and "Heading" in cleaned and "plain text" in cleaned


def test_clean_for_speech_strips_code_and_links():
    text = "See `code` and [a link](https://example.com) for details"
    cleaned = pipeline.clean_for_speech(text)
    assert "`" not in cleaned and "(" not in cleaned
    assert "code" in cleaned and "a link" in cleaned


def test_clean_for_speech_strips_list_markers():
    text = "- first\n- second\n1. third"
    cleaned = pipeline.clean_for_speech(text)
    assert "first" in cleaned and "second" in cleaned and "third" in cleaned
    assert "- " not in cleaned and "1." not in cleaned


def test_speak_chunks_splits_on_sentence_boundaries():
    # each sentence individually well over MIN_CHUNK_CHARS so none get merged
    text = ("This is the first complete sentence here. "
            "This is the second complete one now. "
            "And this is the third and final sentence.")
    chunks = pipeline.speak_chunks(text)
    assert len(chunks) == 3


def test_speak_chunks_merges_tiny_fragments():
    # "Ok." and "Sure." are both under MIN_CHUNK_CHARS -> should merge into one
    chunks = pipeline.speak_chunks("Ok. Sure.")
    assert len(chunks) == 1
    assert "Ok." in chunks[0] and "Sure." in chunks[0]


def test_speak_chunks_empty_input():
    assert pipeline.speak_chunks("") == []
    assert pipeline.speak_chunks("   ") == []


def test_split_ready_returns_complete_sentences_and_leftover():
    ready, buffer = pipeline.split_ready("First sentence. Second is unfin")
    assert ready == ["First sentence."]
    assert buffer == "Second is unfin"


def test_split_ready_flushes_an_overlong_buffer_with_no_punctuation():
    long_text = "word " * 100  # past MAX_SPEAK_CHARS, no sentence-ending punctuation
    ready, buffer = pipeline.split_ready(long_text)
    assert ready  # forced a flush rather than buffering forever
    assert len(buffer) < len(long_text)


def test_system_prompt_has_no_accidental_word_fusion():
    """Regression test for a real bug (2026-09-22): adjacent string literals
    in SYSTEM_PROMPT without a trailing space silently concatenate with no
    separator at all (Python doesn't add one), e.g. "(abc)as of September"
    or "...to the user.Keep every reply". A sentence-ending period glued
    directly to the next capitalized word, or a closing parenthesis glued
    directly to the next word, is the signature of that -- catch it here
    instead of hearing it out loud on a live phone call."""
    prompt = pipeline.SYSTEM_PROMPT
    glued_sentences = re.findall(r"\.[A-Z]\w*", prompt)
    glued_parens = re.findall(r"\)[a-z]\w*", prompt)
    assert not glued_sentences, f"sentence glued to the next with no space: {glued_sentences}"
    assert not glued_parens, f"parenthesis glued to the next word with no space: {glued_parens}"
