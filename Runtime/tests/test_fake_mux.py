"""The fake /ws itself behaves like `mux --serve` for the parts bridges rely on (auth, replay, NDJSON lines)."""
from __future__ import annotations

import json

import pytest
import websocket  # websocket-client, the library the bridges use

from fake_mux import FakeMux


def test_auth_required_when_token_set():
    with FakeMux(token="s3cret") as mux:
        with pytest.raises(websocket.WebSocketBadStatusException):
            websocket.create_connection(mux.url, timeout=5)
        ws = websocket.create_connection(mux.url + "?token=s3cret", timeout=5)
        ws.close()
        assert [c["authorized"] for c in mux.connections] == [False, True]


def test_bearer_header_is_accepted():
    with FakeMux(token="s3cret") as mux:
        ws = websocket.create_connection(mux.url, timeout=5, header=["Authorization: Bearer s3cret"])
        ws.close()
        assert mux.connections[-1]["authorized"]


def test_replay_sent_unless_replay_0():
    replay = [{"type": "stream", "text": "old"}]
    with FakeMux(replay=replay) as mux:
        ws = websocket.create_connection(mux.url, timeout=5)
        assert json.loads(ws.recv())["text"] == "old"
        ws.close()

        ws = websocket.create_connection(mux.url + "?replay=0", timeout=5)
        mux.wait_connected()
        mux.send("stream", text="new")
        assert json.loads(ws.recv())["text"] == "new"
        ws.close()


def test_lines_from_the_bridge_are_recorded():
    with FakeMux() as mux:
        ws = websocket.create_connection(mux.url, timeout=5)
        ws.send("hello\n")
        ws.send("__CANCEL__")
        assert mux.next_line() == "hello"
        assert mux.next_line() == "__CANCEL__"
        ws.close()
