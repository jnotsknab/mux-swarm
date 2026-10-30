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


# --- command surface: console notices, main menu, quick menu ----------------------------------------------------------

def test_command_answer_notices_are_relayed():
    s = tb.MuxSession()
    s.sent("/status", plain=False)
    assert s.on_event({"type": "table", "title": "Status", "rows": [{"key": "Agent", "value": "MuxAgent"}]}) == []
    s.on_event({"type": "warning", "message": "[bold]a < b[/]"})
    out = s.on_event({"type": "prompt", "message": "> "})
    assert len(out) == 1 and "<b>Status</b>" in out[0].text and "<pre>Agent  MuxAgent</pre>" in out[0].text
    assert "⚠️ a &lt; b" in out[0].text


def test_notices_not_asked_for_are_not_relayed():
    s = tb.MuxSession()
    s.on_event({"type": "success", "message": "Bridge started"})
    assert s.on_event({"type": "prompt", "message": "> "}) == []
    s.sent("hi", plain=True)
    s.on_event({"type": "agent_turn_start", "agent": "MuxAgent"})
    s.on_event({"type": "stream", "text": "hello"})
    assert [o.text for o in s.on_event({"type": "agent_turn_end"})] == ["hello"]
    s.on_event({"type": "debug", "message": "[AGENT SESSION] Saved to C:/x"})
    assert s.on_event({"type": "prompt", "message": "> "}) == []


def test_plain_text_at_main_menu_offers_to_start_an_agent_with_it():
    s = tb.MuxSession()
    s.sent("hello there", plain=True)
    s.on_event({"type": "debug", "message": "This is the main menu - text here isn't sent to an agent."})
    out = s.on_event({"type": "prompt", "message": "> "})
    assert len(out) == 1 and "main menu" in out[0].text
    assert out[0].buttons[0] == [("Start agent with this message", "cmd:1")]
    assert s.answer_for_button("cmd:1") == "/agent"
    s.sent("/agent", plain=False)
    s.on_event({"type": "banner", "title": "AGENTIC CHAT INTERFACE"})
    out = s.on_event({"type": "prompt", "message": "> "})
    assert [o.mux for o in out] == ["hello there"]                        # the banner is session chrome


def test_quick_menu_by_button_or_number():
    s = tb.MuxSession()
    item = s.menu_message("What now?", tb.QUICK_MENU)
    assert [row[0][1] for row in item.buttons] == ["cmd:1", "cmd:2", "cmd:3", "cmd:4", "cmd:5"]
    assert s.answer_for_text("3") == "/status" and s.answer_for_text("3") is None
    s.menu_message("What now?", tb.QUICK_MENU)
    assert s.answer_for_button("cmd:9") is None and s.answer_for_button("cmd:2") == "/resume"


def test_command_run_after_ending_the_session_is_relayed():
    s = tb.MuxSession()
    s.sent("y", plain=False)
    assert s.on_event({"type": "prompt", "message": "> "}, now=10.0) == []
    s.on_event({"type": "panel", "title": "Mux-Swarm Status", "content": "Selected lead MuxAgent"}, now=10.2)
    out = s.on_event({"type": "prompt", "message": "> "}, now=10.3)
    assert len(out) == 1 and "Selected lead MuxAgent" in out[0].text
    s.on_event({"type": "info", "message": "later, unrelated"}, now=20.0)
    assert s.take_idle() == []


# --- attachments ----------------------------------------------------------------------------------------------------

def test_upload_matches_the_web_app_contract(monkeypatch):
    seen = {}

    class Resp:
        status_code = 200
        def json(self):
            return {"filename": "/srv/sandbox/uploads/x.jpg"}

    def fake_post(url, params=None, headers=None, files=None, timeout=None):
        seen.update(url=url, params=params, headers=headers, files=files)
        return Resp()

    monkeypatch.setattr(tb.requests, "post", fake_post)
    assert tb.upload_to_mux("ws://10.0.0.5:6723/ws", None, "photo_1.jpg", b"jpg") == "/srv/sandbox/uploads/x.jpg"
    assert seen["url"] == "http://10.0.0.5:6723/api/upload" and seen["params"] == {"dir": "uploads"}
    assert seen["headers"] == {} and seen["files"] == {"file": ("photo_1.jpg", b"jpg")}
    assert tb.with_file_refs(["/a.jpg", "/b.pdf"], "") == "[file: /a.jpg]\n[file: /b.pdf]"


def test_upload_errors_and_size_limit(monkeypatch):
    class Resp:
        status_code = 401
        def json(self):
            raise ValueError("not json")

    monkeypatch.setattr(tb.requests, "post", lambda *a, **k: Resp())
    with pytest.raises(ValueError, match="HTTP 401"):
        tb.upload_to_mux("ws://h/ws", "bad", "a.png", b"x")
    with pytest.raises(ValueError, match="too large"):
        tb.upload_to_mux("ws://h/ws", None, "b.bin", b"x" * (tb.MAX_UPLOAD_BYTES + 1))


# --- rendering ------------------------------------------------------------------------------------------------------

def test_render_escapes_and_formats():
    out = tb.render_html("# Title\nUse `a<b>` and **bold** & _it_\n- one\n- two")
    assert out == "<b>Title</b>\nUse <code>a&lt;b&gt;</code> and <b>bold</b> &amp; <i>it</i>\n• one\n• two"


def test_render_code_fence_and_table():
    md = "```python\nif a < b:\n    pass\n```\n| Name | Size |\n|---|---|\n| a | 1 |\n| bb | 22 |"
    out = tb.render_html(md)
    assert '<pre><code class="language-python">if a &lt; b:\n    pass</code></pre>' in out
    assert "<pre>Name  Size\n----  ----\na     1\nbb    22</pre>" in out


def test_link_labels_and_bracketed_words_survive():
    assert tb.render_html("see [docs](https://x.y) and [note]") == 'see <a href="https://x.y">docs</a> and [note]'


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


def test_multiselect_numbers_plus_text_keep_both():
    s = tb.MuxSession()
    s.on_event({"type": "multiselect_request", "prompt": "Which", "choices": ["a", "b", "c", CUSTOM, CANCEL], "bailout": True})
    assert s.answer_for_text("1,3 and also d") == "1,3,=and also d"
    s.on_event({"type": "multiselect_request", "prompt": "Which", "choices": ["a", "b", CUSTOM, CANCEL], "bailout": True})
    assert s.answer_for_text("2") == "2"                        # plain numbers unchanged
    s.on_event({"type": "multiselect_request", "prompt": "Which", "choices": ["a", "b", CUSTOM, CANCEL], "bailout": True})
    assert s.answer_for_text("just text") == "=just text"       # plain text unchanged
    s.on_event({"type": "multiselect_request", "prompt": "Which", "choices": ["a", "b"]})
    assert s.answer_for_text("1 extra") == "1"                  # no bailout: no custom form (as before)
    s.on_event({"type": "multiselect_request", "prompt": "Which", "choices": ["a", "b", "c", CUSTOM, CANCEL], "bailout": True})
    assert s.answer_for_text("1, 3") == "1,3"                   # spaced numbers are still just numbers


# --- session chrome / settings --------------------------------------------------------------------------------------

def test_session_chrome_is_hidden_and_verbose_shows_it():
    chrome = [{"type": "success", "message": "Loaded session: 2026-10-29_01-26-07"},
              {"type": "banner", "title": "AGENTIC CHAT INTERFACE"},
              {"type": "debug", "message": "Type /qc to exit, /compact to compress context. Press [Esc] to cancel the current turn."},
              {"type": "success", "message": "MuxAgent has 50 tools available"},
              {"type": "debug", "message": "[Hook:phone-notify:stderr] DISCORD_WEBHOOK_URL is not set; notifications disabled"}]
    s = tb.MuxSession()
    s.sent("/resume", plain=False)
    for ev in chrome:
        s.on_event(ev)
    assert s.on_event({"type": "prompt", "message": "> "}) == []
    assert s.apply_setting("set:verbose") == "Chat output is now verbose." and s.verbose
    s.sent("/resume", plain=False)
    for ev in chrome:
        s.on_event(ev)
    out = s.on_event({"type": "prompt", "message": "> "})
    assert len(out) == 1 and "50 tools" in out[0].text
    assert s.apply_setting("nonsense") is None and s.verbose


def test_answering_a_question_mid_turn_keeps_turn_bookkeeping_out():
    s = tb.MuxSession()
    s.sent("try ask user", plain=True)
    s.on_event({"type": "agent_turn_start", "agent": "MuxAgent"})
    s.on_event({"type": "confirm_request", "prompt": "Ok?", "default": False})
    s.sent("y", plain=False)                                     # the answer goes out while the turn runs
    s.on_event({"type": "stream", "text": "done"})
    s.on_event({"type": "agent_turn_end"})
    s.on_event({"type": "debug", "message": "[AGENT SESSION] Saved to C:/x/Sessions/1"})
    s.on_event({"type": "debug", "message": "Total Tokens: 123,070 - Cached Tokens: 122,384"})
    assert s.on_event({"type": "prompt", "message": "> "}) == [] and s.take_idle() == []
    s.sent("/status", plain=False)                                 # a real command still answers
    s.on_event({"type": "panel", "title": "Mux-Swarm Status", "content": "lead MuxAgent"})
    assert "lead MuxAgent" in s.on_event({"type": "prompt", "message": "> "})[0].text


def test_state_file_keeps_pairing_and_settings(tmp_path):
    path = str(tmp_path / "state.json")
    tb.update_state(path, verbose=True)
    tb.update_state(path, paired=[1])
    assert tb.read_state(path) == {"verbose": True, "paired": [1]}
