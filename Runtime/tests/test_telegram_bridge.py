"""Telegram bridge: protocol handling, prompt relay, rendering, access and the live /ws link (fake Mux)."""
from __future__ import annotations

import json
import threading
import time

import pytest

import telegram_bridge as tb
from fake_mux import FakeMux

CUSTOM = "\u270e enter custom\u2026"
CANCEL = "\u2717 cancel (Esc)"


# --- protocol / turn handling ---------------------------------------------------------------------------------------

def test_answer_streams_then_flushes_on_turn_end():
    s = tb.MuxSession()
    assert s.on_event({"type": "stream", "text": "Hello "}) == []
    assert s.on_event({"type": "stream", "text": "**world**"}) == []
    out = s.on_event({"type": "agent_turn_end", "tokens": 10})
    assert [o.text for o in out] == ["Hello <b>world</b>"]
    assert not s.turn_active


def test_reasoning_chunks_are_not_sent():
    s = tb.MuxSession()
    s.on_event({"type": "stream", "text": "let me think...", "reasoning": True})
    s.on_event({"type": "stream", "text": "Answer."})
    assert [o.text for o in s.on_event({"type": "stream_end"})] == ["Answer."]


def test_sub_agent_output_is_labelled():
    s = tb.MuxSession()
    s.on_event({"type": "stream", "text": "lead says", "agent": "Orchestrator"})
    out = s.on_event({"type": "stream", "text": "found it", "agent": "WebAgent"})
    assert [o.text for o in out] == ["lead says"]           # lead text flushed before the sub-agent starts
    out = s.on_event({"type": "turn_complete"})
    assert out[0].text.startswith("<b>WebAgent:</b>")


def test_idle_flush():
    s = tb.MuxSession()
    s.on_event({"type": "stream", "text": "partial"}, now=100.0)
    assert not s.idle_flush_due(101.0)
    assert s.idle_flush_due(100.0 + tb.FLUSH_IDLE_SECONDS)


def test_errors_are_escaped():
    out = tb.MuxSession().on_event({"type": "error", "message": "bad <tag> & stuff"})
    assert out[0].text == "⚠️ bad &lt;tag&gt; &amp; stuff"


# --- prompts (ask_user, approvals) -----------------------------------------------------------------------------------

def test_select_with_bailout_becomes_buttons_and_index_answer():
    s = tb.MuxSession()
    s.on_event({"type": "stream", "text": "Before I start:"})
    out = s.on_event({"type": "select_request", "prompt": "Pick a plan", "choices": ["Fast", "Safe", CUSTOM, CANCEL], "bailout": True})
    assert out[0].text == "Before I start:"                  # what the agent said, then the question
    q = out[1]
    assert [row[0][0] for row in q.buttons[:2]] == ["Fast", "Safe"]
    assert q.buttons[-1] == [("Type my own", "a:custom"), ("Cancel", "a:cancel")]
    assert s.answer_for_button("a:2") == "2"
    assert s.pending is None
    assert s.answer_for_button("a:1") is None                # stale button after answering


def test_select_custom_then_text():
    s = tb.MuxSession()
    s.on_event({"type": "select_request", "prompt": "Pick", "choices": ["A", CUSTOM, CANCEL], "bailout": True})
    assert s.answer_for_button("a:custom") is None
    assert s.answer_for_text("something else") == "=something else"


def test_select_cancel_and_typed_number():
    s = tb.MuxSession()
    s.on_event({"type": "select_request", "prompt": "Pick", "choices": ["A", "B", CUSTOM, CANCEL], "bailout": True})
    assert s.answer_for_text("/cancel") == "cancel"
    s.on_event({"type": "select_request", "prompt": "Pick", "choices": ["A", "B", CUSTOM, CANCEL], "bailout": True})
    assert s.answer_for_text("2") == "2"


def test_plain_select_free_text_does_not_send_garbage():
    s = tb.MuxSession()
    s.on_event({"type": "select_request", "prompt": "Mode", "choices": ["x", "y"]})   # no bailout rows
    assert s.answer_for_text("whatever") == "1"


def test_multiselect_lists_choices_and_takes_numbers():
    s = tb.MuxSession()
    out = s.on_event({"type": "multiselect_request", "prompt": "Which", "choices": ["a", "b", "c", CUSTOM, CANCEL], "bailout": True})
    assert "1. a" in out[0].text and "3. c" in out[0].text and out[0].buttons == []
    assert s.answer_for_text("1, 3") == "1,3"


def test_confirm_buttons():
    s = tb.MuxSession()
    out = s.on_event({"type": "confirm_request", "prompt": "Delete it?", "default": False, "bailout": True})
    assert out[0].buttons[0] == [("Yes", "a:y"), ("No", "a:n")]
    assert s.answer_for_button("a:y") == "y"
    s.on_event({"type": "confirm_request", "prompt": "Again?", "default": True})
    assert s.answer_for_text("nope") == "n"


def test_input_request_takes_next_message():
    s = tb.MuxSession()
    s.on_event({"type": "input_request", "prompt": "Name?", "default": "x"})
    assert s.answer_for_text("Jonathan") == "Jonathan"
    assert s.answer_for_text("hello") is None                # no prompt pending: a normal message


def test_secret_input_is_never_relayed():
    s = tb.MuxSession()
    out = s.on_event({"type": "input_request", "prompt": "API key", "secret": True})
    assert "can't be entered over Telegram" in out[0].text
    assert s.answer_for_text("sk-123") == ""


# --- rendering ------------------------------------------------------------------------------------------------------

def test_render_escapes_and_formats():
    out = tb.render_html("# Title\nUse `a<b>` and **bold** & _it_\n- one\n- two")
    assert out == "<b>Title</b>\nUse <code>a&lt;b&gt;</code> and <b>bold</b> &amp; <i>it</i>\n• one\n• two"


def test_render_code_fence_and_table():
    md = "```python\nif a < b:\n    pass\n```\n| Name | Size |\n|---|---|\n| a | 1 |\n| bb | 22 |"
    out = tb.render_html(md)
    assert '<pre><code class="language-python">if a &lt; b:\n    pass</code></pre>' in out
    assert "<pre>Name  Size\n----  ----\na     1\nbb    22</pre>" in out


def test_render_strips_terminal_markup():
    assert tb.render_html("[bold]hi[/] \x1b[31mred\x1b[0m") == "hi red"


def test_chunking_never_breaks_code_blocks_or_tags():
    code = "\n".join(f"line {i} " + "x" * 60 for i in range(200))
    chunks = tb.chunk_html(tb.render_html("intro\n```\n" + code + "\n```\noutro"))
    assert len(chunks) > 1
    for c in chunks:
        assert len(c) <= tb.TELEGRAM_LIMIT
        assert c.count("<pre>") == c.count("</pre>")          # every message is valid on its own
        assert c.count("<") == c.count(">")


def test_chunking_long_single_line():
    chunks = tb.chunk_html(tb.render_html("word " * 3000))
    assert all(len(c) <= tb.TELEGRAM_LIMIT for c in chunks) and len(chunks) >= 4


# --- access ---------------------------------------------------------------------------------------------------------

def test_access_denies_by_default_and_pairs(tmp_path):
    state = str(tmp_path / "state.json")
    a = tb.Access(set(), allow_all=False, state_path=state)
    assert not a.is_allowed(42)
    assert not a.try_pair(42, "WRONG1")
    code = a.pair_code
    assert a.try_pair(42, code.lower())
    assert a.is_allowed(42)
    assert a.pair_code != code                               # one-time
    assert tb.Access(set(), allow_all=False, state_path=state).is_allowed(42)   # remembered across restarts


def test_access_allowlist_and_allow_all(tmp_path):
    a = tb.Access({7}, allow_all=False, state_path=str(tmp_path / "s.json"))
    assert a.is_allowed(7) and not a.is_allowed(8)
    assert tb.Access(set(), allow_all=True, state_path=str(tmp_path / "s2.json")).is_allowed(8)


# --- the live link against a fake Mux -------------------------------------------------------------------------------

def test_url_has_replay_off_and_token():
    url = tb.ws_url_with("ws://100.71.234.84:6723/ws", "s3cret")
    assert url == "ws://100.71.234.84:6723/ws?replay=0&token=s3cret"
    assert tb.ws_url_with("ws://127.0.0.1:6723/ws", None).endswith("?replay=0")


def test_link_authenticates_skips_replay_and_round_trips():
    events: list[dict] = []
    got = threading.Event()

    def on_event(ev):
        events.append(ev)
        got.set()

    with FakeMux(token="s3cret", replay=[{"type": "stream", "text": "OLD OUTPUT"}]) as mux:
        link = tb.MuxLink(mux.url, "s3cret", on_event)
        link.start()
        assert link.connected.wait(5)
        mux.wait_connected()
        assert mux.connections[-1]["authorized"]
        assert mux.connections[-1]["params"]["replay"] == "0"

        mux.send("stream", text="new answer")
        assert got.wait(5)
        assert [e.get("text") for e in events] == ["new answer"]     # replay never arrived

        assert link.send("hi from telegram")
        assert mux.next_line() == "hi from telegram"
        link.stop()


def test_link_reconnects_after_drop():
    with FakeMux() as mux:
        link = tb.MuxLink(mux.url, None, lambda ev: None)
        link.start()
        assert link.connected.wait(5)
        with link._lock:
            link._ws.close()
        deadline = time.time() + 10
        while time.time() < deadline and len(mux.connections) < 2:
            time.sleep(0.1)
        assert len(mux.connections) >= 2
        link.stop()


def test_prompt_answer_round_trip_through_mux():
    """A select_request from Mux becomes buttons; pressing one sends the index Mux expects."""
    s = tb.MuxSession()
    lock = threading.Lock()
    outgoing: list = []
    asked = threading.Event()

    def on_event(ev):
        with lock:
            outgoing.extend(s.on_event(ev))
        if ev.get("type") == "select_request":
            asked.set()

    with FakeMux() as mux:
        link = tb.MuxLink(mux.url, None, on_event)
        link.start()
        assert link.connected.wait(5)
        mux.wait_connected()
        mux.send("select_request", prompt="Approve?", choices=["Yes, go", "Not yet", CUSTOM, CANCEL], bailout=True)
        assert asked.wait(5)
        with lock:
            assert outgoing[-1].buttons[0][0] == ("Yes, go", "a:1")
            line = s.answer_for_button("a:1")
        assert link.send(line)
        assert mux.next_line() == "1"
        link.stop()
