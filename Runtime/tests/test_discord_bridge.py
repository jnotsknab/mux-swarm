"""Discord bridge: protocol handling, prompt relay, rendering, access and the live /ws link (fake Mux)."""
from __future__ import annotations

import threading
import time

import discord_bridge as db
from fake_mux import FakeMux

CUSTOM = "\u270e enter custom\u2026"
CANCEL = "\u2717 cancel (Esc)"


# --- protocol / turn handling ---------------------------------------------------------------------------------------

def test_answer_streams_then_flushes_on_turn_end():
    s = db.MuxSession()
    s.on_event({"type": "stream", "text": "Hello "})
    s.on_event({"type": "stream", "text": "**world**"})
    assert [o.text for o in s.on_event({"type": "agent_turn_end"})] == ["Hello **world**"]
    assert not s.turn_active


def test_reasoning_dropped_and_sub_agents_labelled():
    s = db.MuxSession()
    s.on_event({"type": "stream", "text": "thinking", "reasoning": True})
    s.on_event({"type": "stream", "text": "lead", "agent": "Orchestrator"})
    assert [o.text for o in s.on_event({"type": "stream", "text": "found", "agent": "WebAgent"})] == ["lead"]
    assert s.on_event({"type": "turn_complete"})[0].text == "**WebAgent:** found"


def test_idle_flush():
    s = db.MuxSession()
    s.on_event({"type": "stream", "text": "partial"}, now=50.0)
    assert not s.idle_flush_due(51.0) and s.idle_flush_due(50.0 + db.FLUSH_IDLE_SECONDS)


# --- prompts --------------------------------------------------------------------------------------------------------

def test_select_buttons_with_bailout():
    s = db.MuxSession()
    s.on_event({"type": "stream", "text": "Plan ready."})
    out = s.on_event({"type": "select_request", "prompt": "Proceed?", "choices": ["Yes, go", "Not yet", CUSTOM, CANCEL], "bailout": True})
    assert out[0].text == "Plan ready."
    assert out[1].buttons == [("Yes, go", "1"), ("Not yet", "2"), ("Type my own", "custom"), ("Cancel", "cancel")]
    assert s.answer_for_button("2") == "2" and s.pending is None
    assert s.answer_for_button("1") is None


def test_custom_cancel_confirm_input_secret():
    s = db.MuxSession()
    s.on_event({"type": "select_request", "prompt": "Pick", "choices": ["A", CUSTOM, CANCEL], "bailout": True})
    assert s.answer_for_button("custom") is None and s.answer_for_text("my way") == "=my way"
    s.on_event({"type": "select_request", "prompt": "Pick", "choices": ["A", CUSTOM, CANCEL], "bailout": True})
    assert s.answer_for_button("cancel") == "cancel"
    out = s.on_event({"type": "confirm_request", "prompt": "Delete?", "default": False})
    assert out[0].buttons == [("Yes", "y"), ("No", "n")]
    assert s.answer_for_text("yes please") == "y"
    s.on_event({"type": "input_request", "prompt": "Name?"})
    assert s.answer_for_text("Jonathan") == "Jonathan"
    out = s.on_event({"type": "input_request", "prompt": "Key", "secret": True})
    assert "can't be entered over Discord" in out[0].text and s.answer_for_text("sk-1") == ""


def test_many_choices_and_multiselect_become_numbered_lists():
    s = db.MuxSession()
    out = s.on_event({"type": "select_request", "prompt": "Pick", "choices": [f"opt{i}" for i in range(30)]})
    assert out[0].buttons == [] and "30. opt29" in out[0].text
    assert s.answer_for_text("12") == "12"
    out = s.on_event({"type": "multiselect_request", "prompt": "Which", "choices": ["a", "b", "c", CUSTOM, CANCEL], "bailout": True})
    assert "2. b" in out[0].text and s.answer_for_text("1, 3") == "1,3"


# --- rendering ------------------------------------------------------------------------------------------------------

def test_link_labels_and_bracketed_words_survive():
    assert db.render_discord("see [docs](https://x.y) and [note] [bold]b[/]") == "see [docs](https://x.y) and [note] b"


def test_render_tables_headings_and_markup():
    md = "#### Deep\n| A | B |\n|---|---|\n| x | yy |\n[bold]hi[/] \x1b[31mred\x1b[0m"
    assert db.render_discord(md) == "### Deep\n```\nA  B\n-  --\nx  yy\n```\nhi red"


def test_render_keeps_code_and_closes_unclosed_fence():
    assert db.render_discord("```py\n| not | a table |\n|---|---|\n") == "```py\n| not | a table |\n|---|---|\n```"


def test_chunks_respect_limit_and_reopen_code_blocks():
    code = "\n".join("x" * 70 for _ in range(100))
    chunks = db.chunk_discord(db.render_discord("intro\n```js\n" + code + "\n```\nend"))
    assert len(chunks) > 2
    for c in chunks:
        assert len(c) <= db.DISCORD_LIMIT
        assert c.count("```") % 2 == 0                    # every message has balanced fences
    assert chunks[1].startswith("```js")                  # the language survives the split


def test_chunks_long_line():
    assert all(len(c) <= db.DISCORD_LIMIT for c in db.chunk_discord("word " * 2000))


# --- access ---------------------------------------------------------------------------------------------------------

def test_access_pairing(tmp_path):
    state = str(tmp_path / "s.json")
    a = db.Access(set(), allow_all=False, state_path=state)
    assert not a.is_allowed(1) and not a.try_pair(1, "nope")
    assert a.try_pair(1, a.pair_code) and a.is_allowed(1)
    assert db.Access(set(), allow_all=False, state_path=state).is_allowed(1)
    assert db.Access(set(), allow_all=True, state_path=str(tmp_path / "t.json")).is_allowed(99)


# --- live link against a fake Mux -----------------------------------------------------------------------------------

def test_link_auth_replay_and_prompt_round_trip():
    s = db.MuxSession()
    outgoing: list = []
    asked = threading.Event()

    def on_event(ev):
        outgoing.extend(s.on_event(ev))
        if ev.get("type") == "confirm_request":
            asked.set()

    with FakeMux(token="t0k", replay=[{"type": "stream", "text": "OLD"}]) as mux:
        link = db.MuxLink(mux.url, "t0k", on_event)
        link.start()
        assert link.connected.wait(5)
        mux.wait_connected()
        assert mux.connections[-1]["authorized"] and mux.connections[-1]["params"]["replay"] == "0"
        mux.send("confirm_request", prompt="Ship it?", default=True)
        assert asked.wait(5)
        assert all("OLD" not in o.text for o in outgoing)
        assert link.send(s.answer_for_button("y"))
        assert mux.next_line() == "y"
        link.stop()


def test_link_reconnects():
    with FakeMux() as mux:
        link = db.MuxLink(mux.url, None, lambda ev: None)
        link.start()
        assert link.connected.wait(5)
        with link._lock:
            link._ws.close()
        deadline = time.time() + 10
        while time.time() < deadline and len(mux.connections) < 2:
            time.sleep(0.1)
        assert len(mux.connections) >= 2
        link.stop()
