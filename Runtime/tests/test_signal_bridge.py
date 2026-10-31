"""Signal bridge: protocol, numbered prompts, styled rendering, envelope parsing, access, and both live links."""
from __future__ import annotations

import json
import threading

import signal_bridge as sb
from fake_mux import FakeMux

CUSTOM = "\u270e enter custom\u2026"
CANCEL = "\u2717 cancel (Esc)"
ME = "+15550000001"
FRIEND = "+15550000002"


def test_turn_flush_reasoning_and_labels():
    s = sb.MuxSession()
    s.on_event({"type": "stream", "text": "hmm", "reasoning": True})
    s.on_event({"type": "stream", "text": "lead", "agent": "Orchestrator"})
    assert s.on_event({"type": "stream", "text": "found", "agent": "WebAgent"}) == ["lead"]
    assert s.on_event({"type": "agent_turn_end"}) == ["**WebAgent:** found"]


def test_select_is_numbered_and_answers_map():
    s = sb.MuxSession()
    out = s.on_event({"type": "select_request", "prompt": "Pick", "choices": ["Fast", "Safe", CUSTOM, CANCEL], "bailout": True})
    assert "1. Fast\n2. Safe" in out[0] and "/cancel" in out[0]
    assert s.answer_for_text("2") == "2"
    s.on_event({"type": "select_request", "prompt": "Pick", "choices": ["Fast", CUSTOM, CANCEL], "bailout": True})
    assert s.answer_for_text("something else") == "=something else"
    s.on_event({"type": "multiselect_request", "prompt": "Which", "choices": ["a", "b", "c"]})
    assert s.answer_for_text("1, 3") == "1,3"


def test_confirm_input_secret_cancel():
    s = sb.MuxSession()
    s.on_event({"type": "confirm_request", "prompt": "Go?", "bailout": True})
    assert s.answer_for_text("/cancel") == "cancel"
    s.on_event({"type": "confirm_request", "prompt": "Go?"})
    assert s.answer_for_text("Yes") == "y"
    s.on_event({"type": "input_request", "prompt": "Name?"})
    assert s.answer_for_text("J") == "J"
    out = s.on_event({"type": "input_request", "prompt": "Key", "secret": True})
    assert "can't be entered over Signal" in out[0] and s.answer_for_text("sk") == ""
    assert s.answer_for_text("hello") is None


def test_command_answer_notices_are_relayed():
    s = sb.MuxSession()
    s.sent("/status", plain=False)
    assert s.on_event({"type": "table", "title": "Status", "rows": [{"key": "Agent", "value": "MuxAgent"}]}) == []
    s.on_event({"type": "success", "message": "done"})
    out = s.on_event({"type": "prompt", "message": "> "})
    assert len(out) == 1 and "Agent  MuxAgent" in out[0] and "✅ done" in out[0]


def test_notices_not_asked_for_and_turn_bookkeeping_stay_out():
    s = sb.MuxSession()
    s.on_event({"type": "success", "message": "Bridge started"})
    assert s.on_event({"type": "prompt", "message": "> "}) == []
    s.sent("hi", plain=True)
    s.on_event({"type": "agent_turn_start", "agent": "MuxAgent"})
    s.on_event({"type": "stream", "text": "hello"})
    assert s.on_event({"type": "agent_turn_end"}) == ["hello"]
    s.on_event({"type": "debug", "message": "[AGENT SESSION] Saved to /x"})
    assert s.on_event({"type": "prompt", "message": "> "}) == []


def test_plain_text_at_main_menu_offers_numbered_menu_and_replays_it():
    s = sb.MuxSession()
    s.sent("hello there", plain=True)
    s.on_event({"type": "debug", "message": "This is the main menu - text here isn't sent to an agent."})
    out = s.on_event({"type": "prompt", "message": "> "})
    assert len(out) == 1 and "1. Start agent with this message" in out[0] and "Reply with a number" in out[0]
    assert s.answer_for_text("1") == "/agent"
    s.sent("/agent", plain=False)
    s.on_event({"type": "banner", "title": "AGENTIC CHAT INTERFACE"})
    out = s.on_event({"type": "prompt", "message": "> "})
    assert out == [] and s.to_mux == ["hello there"]                      # the banner is session chrome


def test_quick_menu_numbers_are_one_shot_and_after_session_end_relayed():
    s = sb.MuxSession()
    assert "5. End session" in s.menu_message("What now?", sb.QUICK_MENU)
    assert s.answer_for_text("4") == "/help" and s.answer_for_text("4") is None
    s.sent("y", plain=False)
    assert s.on_event({"type": "prompt", "message": "> "}, now=5.0) == []
    s.on_event({"type": "panel", "title": "Mux-Swarm Status", "content": "lead MuxAgent"}, now=5.5)
    assert "lead MuxAgent" in s.on_event({"type": "prompt", "message": "> "}, now=5.6)[0]


def test_upload_matches_the_web_app_contract(monkeypatch):
    seen = {}

    class Resp:
        status_code = 200
        def json(self):
            return {"filename": "/s/uploads/x.png"}

    def fake_post(url, params=None, headers=None, files=None, timeout=None):
        seen.update(url=url, params=params, headers=headers, files=files)
        return Resp()

    monkeypatch.setattr(sb.requests, "post", fake_post)
    assert sb.upload_to_mux("ws://127.0.0.1:6723/ws", "t", "x.png", b"p") == "/s/uploads/x.png"
    assert seen["url"] == "http://127.0.0.1:6723/api/upload" and seen["headers"] == {"Authorization": "Bearer t"}
    assert sb.with_file_refs(["/s/uploads/x.png"], "look") == "[file: /s/uploads/x.png]\nlook"
    try:
        sb.upload_to_mux("ws://h/ws", None, "big", b"x" * (sb.MAX_UPLOAD_BYTES + 1))
        raise AssertionError("expected failure")
    except ValueError as e:
        assert "too large" in str(e)


def test_link_labels_and_bracketed_words_survive():
    assert sb.strip_terminal_markup("[docs] [note] [bold]x[/] [red on black]y[/] [link=https://a]z[/]") == "[docs] [note] x y z"


def test_render_styled_text():
    md = "# Title\nUse __this__ and _that_ ~~old~~ [docs](https://x.y)\n```\ncode `x`\n```\n| A | B |\n|---|---|\n| 1 | 22 |"
    assert sb.render_signal(md) == (
        "**Title**\nUse **this** and *that* ~old~ docs (https://x.y)\n"
        "`code \u2018x\u2018`\n`A  B`\n`-  --`\n`1  22`")


def test_chunking_keeps_lines_whole():
    text = "\n".join(f"**line {i}** " + "y" * 80 for i in range(100))
    chunks = sb.chunk_signal(text)
    assert all(len(c) <= sb.CHUNK_LIMIT for c in chunks)
    assert all(c.count("**") % 2 == 0 for c in chunks)       # no style left open across messages


def test_parse_envelopes():
    dm = json.dumps({"envelope": {"sourceNumber": FRIEND, "dataMessage": {"message": "hi"}}})
    assert sb.parse_envelope(dm, ME) == sb.Incoming(FRIEND, "hi", [])
    rpc = json.dumps({"jsonrpc": "2.0", "method": "receive", "params": {"envelope": {"source": FRIEND, "dataMessage": {"message": "rpc"}}}})
    assert sb.parse_envelope(rpc, ME).text == "rpc"
    note = json.dumps({"envelope": {"sourceNumber": ME, "syncMessage": {"sentMessage": {"destinationNumber": ME, "message": "to self"}}}})
    assert sb.parse_envelope(note, ME).text == "to self"
    other_sync = json.dumps({"envelope": {"sourceNumber": ME, "syncMessage": {"sentMessage": {"destinationNumber": FRIEND, "message": "x"}}}})
    assert sb.parse_envelope(other_sync, ME) is None              # my message to someone else, not to Mux
    group = json.dumps({"envelope": {"sourceNumber": FRIEND, "dataMessage": {"message": "g", "groupInfo": {"groupId": "a"}}}})
    assert sb.parse_envelope(group, ME) is None
    receipt = json.dumps({"envelope": {"sourceNumber": FRIEND, "receiptMessage": {}}})
    assert sb.parse_envelope(receipt, ME) is None
    assert sb.parse_envelope("not json", ME) is None


def test_access(tmp_path):
    a = sb.Access(ME, set(), allow_all=False, state_path=str(tmp_path / "s.json"))
    assert a.is_allowed(ME)                                       # Note to Self always works
    assert not a.is_allowed(FRIEND) and not a.try_pair(FRIEND, "bad")
    assert a.try_pair(FRIEND, a.pair_code) and a.is_allowed(FRIEND)
    assert sb.Access(ME, set(), allow_all=False, state_path=str(tmp_path / "s.json")).is_allowed(FRIEND)


def test_mux_link_auth_replay_round_trip():
    s = sb.MuxSession()
    out: list[str] = []
    asked = threading.Event()

    def on_event(ev):
        out.extend(s.on_event(ev))
        if ev.get("type") == "select_request":
            asked.set()

    with FakeMux(token="tok", replay=[{"type": "stream", "text": "OLD"}]) as mux:
        link = sb.ReconnectingWs(sb.ws_url_with(mux.url, "tok"), sb.mux_event_handler(on_event), "Mux")
        link.start()
        assert link.connected.wait(5)
        mux.wait_connected()
        assert mux.connections[-1]["authorized"] and mux.connections[-1]["params"]["replay"] == "0"
        mux.send("select_request", prompt="Approve?", choices=["Yes", "No", CUSTOM, CANCEL], bailout=True)
        assert asked.wait(5)
        assert not any("OLD" in o for o in out)
        assert link.send(s.answer_for_text("1"))
        assert mux.next_line() == "1"
        link.stop()


def test_receive_url():
    assert sb.SignalApi("https://sig.local:8443/", ME).receive_url() == f"wss://sig.local:8443/v1/receive/{ME}"
    assert sb.SignalApi("http://127.0.0.1:8080", ME).receive_url() == f"ws://127.0.0.1:8080/v1/receive/{ME}"


def test_multiselect_numbers_plus_text_keep_both():
    s = sb.MuxSession()
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
    s = sb.MuxSession()
    s.sent("/resume", plain=False)
    for ev in chrome:
        s.on_event(ev)
    assert s.on_event({"type": "prompt", "message": "> "}) == []
    assert s.apply_setting("set:verbose") == "Chat output is now verbose." and s.verbose
    s.sent("/resume", plain=False)
    for ev in chrome:
        s.on_event(ev)
    out = s.on_event({"type": "prompt", "message": "> "})
    assert len(out) == 1 and "50 tools" in out[0]
    assert s.apply_setting("nonsense") is None and s.verbose


def test_answering_a_question_mid_turn_keeps_turn_bookkeeping_out():
    s = sb.MuxSession()
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
    assert "lead MuxAgent" in s.on_event({"type": "prompt", "message": "> "})[0]


def test_state_file_keeps_pairing_and_settings(tmp_path):
    path = str(tmp_path / "state.json")
    sb.update_state(path, verbose=True)
    sb.update_state(path, paired=[1])
    assert sb.read_state(path) == {"verbose": True, "paired": [1]}


# --- long questions (a whole plan in ask_user) ----------------------------------------------------------------------

def test_long_prompt_is_split_under_the_limit():
    big = "Plan:\n" + "\n".join(f"Step {i:03d}: do the thing carefully and verify it" for i in range(1, 150))
    for ev in ({"type": "select_request", "prompt": big, "choices": ["Go", "Wait", CUSTOM, CANCEL], "bailout": True},
               {"type": "confirm_request", "prompt": big, "bailout": True},
               {"type": "input_request", "prompt": big},
               {"type": "multiselect_request", "prompt": big, "choices": ["a", "b", CUSTOM, CANCEL], "bailout": True}):
        out = sb.MuxSession().on_event(ev)
        assert len(out) > 1 and all(len(t) <= sb.CHUNK_LIMIT for t in out), ev["type"]
        assert "Step 001" in out[0] and "Step 149" in "".join(out) and "Reply" in out[-1]
