#!/usr/bin/env python3
"""
Mux-Swarm Signal bridge: chat with your Mux agents from Signal, via a self-hosted signal-cli-rest-api.

Runs as a daemon `bridge` trigger (Mux starts it and passes MUX_WS_URL / MUX_SERVE_TOKEN) or by hand:

  uv run --project Runtime Runtime/signal_bridge.py
  uv run --project Runtime --extra voice Runtime/signal_bridge.py     # + voice notes (local Whisper)

Environment:
  SIGNAL_NUMBER        the Signal number the REST API is registered/linked as, E.164 (required)
  SIGNAL_API_URL       signal-cli-rest-api base URL (default http://127.0.0.1:8080; run it in json-rpc mode)
  MUX_WS_URL           Mux /ws endpoint (set by the daemon; default ws://127.0.0.1:6723/ws)
  MUX_SERVE_TOKEN      serve.auth token (set by the daemon when auth is on)
  ALLOWED_NUMBERS      comma-separated E.164 numbers that may use the bridge
  MUX_BRIDGE_ALLOW_ALL set to 1 to let ANY sender use the bridge (not recommended: it drives agents with shell access)
  MUX_BRIDGE_STATE     file that remembers paired numbers (default: signal_bridge.state.json next to this script)
  WHISPER_MODEL        tiny | base | small | medium | large | turbo (voice extra only; default base)

Commands: any Mux slash command works from the chat (/agent, /resume, /status, /help, /qc, ...); its answer is posted
back. /menu lists quick actions (reply with a number), /stop interrupts the running turn. Attachments are uploaded to
Mux (like the web app's attachments) and handed to the agent.

Access: only ALLOWED_NUMBERS, paired senders, and your own "Note to Self" are answered. To pair another number, send
`/pair <code>` with the one-time code Mux shows when the bridge starts (also in /logs).
"""

from __future__ import annotations

import argparse
import json
import logging
import os
import re
import secrets
import shutil
import sys
import tempfile
import threading
import time
from dataclasses import dataclass, field
from typing import Any, Callable, Optional
from urllib.parse import parse_qsl, urlencode, urlsplit, urlunsplit

import requests
import websocket  # websocket-client

log = logging.getLogger("signal-bridge")

CHUNK_LIMIT = 1800          # Signal does not document a hard limit; stay well under the ~2000 seen in practice
FLUSH_IDLE_SECONDS = 2.5
REPLY_GRACE_SECONDS = 3.0   # notices right after the reply's prompt still belong to it (a session ends, then the command runs)
MAX_UPLOAD_BYTES = 25_000_000


# =====================================================================================================================
# Rendering: Mux Markdown -> Signal "styled" text (signal-cli-rest-api text_mode=styled)
# Supported there: **bold**, *italic*, ~strikethrough~, `monospace`, ||spoiler||. No headings, links or tables.
# =====================================================================================================================

_ANSI_RE = re.compile(r"\x1b\[[0-9;?]*[ -/]*[@-~]")
# Only real Spectre console tags: [/], styles, colours (named, numbered, #hex), optional "on <colour>", link=.
# A bare word in brackets (e.g. a Markdown link label "[docs]") is NOT markup and is kept.
_SPECTRE_STYLE = r"(?:bold|dim|italic|underline|strikethrough|invert|blink|conceal|default|black|red|green|yellow|blue|magenta|purple|cyan|teal|white|silver|grey\d*|gray\d*|orange\d*|aqua|lime|navy|maroon|olive|fuchsia|#[0-9a-fA-F]{6}|rgb\(\d+,\d+,\d+\))"
_SPECTRE_RE = re.compile(r"\[(?:/|link(?:=[^\]]*)?|" + _SPECTRE_STYLE + r"(?:\s+(?:" + _SPECTRE_STYLE + r"|on\s+" + _SPECTRE_STYLE + r"))*)\]")
_FENCE_RE = re.compile(r"^\s*```")
_TABLE_SEP_RE = re.compile(r"^\s*\|?\s*:?-{3,}:?\s*(\|\s*:?-{3,}:?\s*)*\|?\s*$")


def strip_terminal_markup(text: str) -> str:
    return _SPECTRE_RE.sub("", _ANSI_RE.sub("", text))


def _mono_lines(lines: list[str]) -> list[str]:
    """Signal has no code blocks: each line becomes inline monospace (blank lines stay blank)."""
    return [f"`{ln.replace('`', chr(0x2018))}`" if ln.strip() else "" for ln in lines]


def _table_lines(rows: list[str]) -> list[str]:
    cells = [[c.strip() for c in r.strip().strip("|").split("|")] for r in rows if not _TABLE_SEP_RE.match(r)]
    if not cells:
        return []
    width = max(len(r) for r in cells)
    cells = [r + [""] * (width - len(r)) for r in cells]
    widths = [max(len(r[i]) for r in cells) for i in range(width)]
    lines = ["  ".join(c.ljust(widths[i]) for i, c in enumerate(r)).rstrip() for r in cells]
    if len(lines) > 1:
        lines.insert(1, "  ".join("-" * w for w in widths))
    return _mono_lines(lines)


def _inline(line: str) -> str:
    # Mux uses _x_ / __x__ too; Signal styled mode only knows *x* / **x**.
    line = re.sub(r"__(.+?)__", r"**\1**", line)
    line = re.sub(r"(?<![\w_])_(?!\s)([^_\n]+?)(?<!\s)_(?![\w_])", r"*\1*", line)
    line = re.sub(r"~~(.+?)~~", r"~\1~", line)
    line = re.sub(r"\[([^\]\n]+)\]\((https?://[^)\s]+)\)", r"\1 (\2)", line)   # no link markup in Signal
    return line


def render_signal(markdown: str) -> str:
    lines = strip_terminal_markup(markdown).replace("\r\n", "\n").split("\n")
    out: list[str] = []
    i = 0
    while i < len(lines):
        line = lines[i]
        if _FENCE_RE.match(line):
            body: list[str] = []
            i += 1
            while i < len(lines) and not _FENCE_RE.match(lines[i]):
                body.append(lines[i])
                i += 1
            i += 1
            out.extend(_mono_lines(body))
            continue
        if line.strip().startswith("|") and i + 1 < len(lines) and _TABLE_SEP_RE.match(lines[i + 1]):
            rows: list[str] = []
            while i < len(lines) and lines[i].strip().startswith("|"):
                rows.append(lines[i])
                i += 1
            out.extend(_table_lines(rows))
            continue
        heading = re.match(r"^#{1,6}\s+(.*)$", line)
        if heading:
            out.append(f"**{_inline(heading.group(1).strip())}**")
        elif re.match(r"^\s*[-*+]\s+", line):
            out.append(re.sub(r"^(\s*)[-*+]\s+", lambda m: m.group(1) + "• ", _inline(line), count=1))
        elif re.match(r"^\s*(-{3,}|\*{3,}|_{3,})\s*$", line):
            out.append("──────────")
        else:
            out.append(_inline(line))
        i += 1
    return re.sub(r"\n{3,}", "\n\n", "\n".join(out)).strip()


def chunk_signal(text: str, limit: int = CHUNK_LIMIT) -> list[str]:
    """Split at line, then word boundaries. Styling is per line, so a split never leaves a style open."""
    if len(text) <= limit:
        return [text] if text else []
    chunks: list[str] = []
    current = ""
    for line in text.split("\n"):
        pieces = [line]
        if len(line) > limit:
            pieces, rest = [], line
            while len(rest) > limit:
                cut = rest.rfind(" ", 0, limit)
                cut = cut if cut > 0 else limit
                pieces.append(rest[:cut])
                rest = rest[cut:].lstrip(" ")
            pieces.append(rest)
        for piece in pieces:
            candidate = (current + "\n" + piece) if current else piece
            if len(candidate) > limit and current:
                chunks.append(current)
                current = piece
            else:
                current = candidate
    if current:
        chunks.append(current)
    return chunks


# =====================================================================================================================
# Protocol: Mux /ws events -> what to send (Signal has no buttons: prompts are numbered lists)
# =====================================================================================================================

@dataclass
class PendingPrompt:
    kind: str
    prompt: str
    choices: list[str] = field(default_factory=list)
    bailout: bool = False
    secret: bool = False


CUSTOM_LABEL_MARK = "enter custom"
# Console notices a command (or the main menu) answers with: shown in the chat when they reply to this bridge.
NOTICE_TYPES = ("info", "success", "warning", "debug", "body", "markup", "panel", "table", "banner")
# Quick menu (/menu and the main-menu hint): (label, command). "start" = /agent, then the message sent at the menu.
QUICK_MENU = [("New agent session", "/agent"), ("Resume a session", "/resume"), ("Status", "/status"),
              ("Commands", "/help"), ("End session", "/qc")]


def notice_text(t: str, ev: dict[str, Any]) -> str:
    """One console notice event as Markdown (the same events the web app renders)."""
    msg = strip_terminal_markup(str(ev.get("message") or ""))
    if t == "banner":
        return f"**{ev.get('title') or ''}**"
    if t == "body":                                   # preformatted (help text, listings): keep the columns
        return "```\n" + msg.replace("```", "\u02cb\u02cb\u02cb").rstrip() + "\n```" if msg.strip() else ""
    if t == "panel":
        title = str(ev.get("title") or "")
        return (f"**{title}**\n" if title else "") + strip_terminal_markup(str(ev.get("content") or ""))
    if t == "table":
        rows = [(str(r.get("key", "")), str(r.get("value", ""))) for r in (ev.get("rows") or []) if isinstance(r, dict)]
        width = max((len(k) for k, _ in rows), default=0)
        body = "\n".join(f"{k.ljust(width)}  {v}" for k, v in rows)
        return (f"**{ev.get('title')}**\n" if ev.get("title") else "") + (f"```\n{body}\n```" if body else "")
    if t == "success":
        return "✅ " + msg
    if t == "warning":
        return "⚠️ " + msg
    return msg


class MuxSession:
    """Mux /ws events -> Signal messages for whoever is currently driving Mux. Pure logic, testable offline."""

    def __init__(self) -> None:
        self.buffer: list[str] = []
        self.last_chunk = 0.0
        self.pending: Optional[PendingPrompt] = None
        self.turn_active = False
        self.agent_label: Optional[str] = None
        self.notices: list[str] = []
        self.expecting = False                  # this bridge sent a line; Mux's next `prompt` ends the reply
        self.grace_until = 0.0                  # ...but notices until this time still belong to it
        self.turn_seen = False                  # an agent turn ran since that line (its bookkeeping is not shown)
        self.last_plain: Optional[str] = None   # that line, when it was a plain message (not a command or answer)
        self.stash: Optional[str] = None        # a message sent at the main menu, kept for "Start agent with it"
        self.after_prompt: Optional[str] = None # sent to Mux at the next `prompt` (the stash, once /agent is up)
        self.menu: list[tuple[str, str]] = []   # quick-menu choices currently offered (answered by number)
        self.to_mux: list[str] = []             # lines the bridge should send to Mux (drained by the glue)

    def take_answer(self) -> list[str]:
        text = "".join(self.buffer).strip()
        self.buffer.clear()
        self.agent_label = None
        return chunk_signal(render_signal(text)) if text else []

    def sent(self, line: str, plain: bool) -> None:
        """Record a line this bridge sent to Mux, so the notices Mux answers with are shown in the chat."""
        self.expecting, self.turn_seen, self.grace_until = True, False, 0.0
        self.last_plain = line if plain else None
        self.menu = []

    def menu_message(self, prompt: str, items: list[tuple[str, str]]) -> str:
        self.menu = list(items)
        listing = "\n".join(f"{i + 1}. {label}" for i, (label, _) in enumerate(items))
        return f"{prompt}\n{listing}\n\n*Reply with a number.*"

    def take_notices(self) -> list[str]:
        text = "\n".join(n for n in self.notices if n.strip())
        self.notices.clear()
        return chunk_signal(render_signal(text)) if text.strip() else []

    def idle_flush_due(self, now: float) -> bool:
        return bool(self.buffer or self.notices) and self.last_chunk > 0 and now - self.last_chunk >= FLUSH_IDLE_SECONDS

    def take_idle(self) -> list[str]:
        return self.take_answer() + self.take_notices()

    def command_for_menu(self, index: str) -> Optional[str]:
        """The line to send for quick-menu choice `index` (1-based), or None when it is not on offer."""
        if not index.isdigit() or not 1 <= int(index) <= len(self.menu):
            return None
        command = self.menu[int(index) - 1][1]
        self.menu = []
        if command == "start":
            self.after_prompt, self.stash = self.stash, None
            return "/agent"
        return command

    def on_event(self, ev: dict[str, Any], now: Optional[float] = None) -> list[str]:
        now = time.time() if now is None else now
        t = ev.get("type", "")
        out: list[str] = []
        if t == "stream":
            if ev.get("reasoning"):
                return out
            agent = ev.get("agent")
            if agent and agent != self.agent_label and agent != "Orchestrator":
                if self.buffer:
                    out.extend(self.take_answer())
                self.agent_label = agent
                self.buffer.append(f"**{agent}:** ")
            self.buffer.append(ev.get("text") or "")
            self.last_chunk = now
            self.turn_active = True
        elif t in ("stream_end", "agent_turn_end", "turn_complete"):
            out.extend(self.take_answer())
            if t != "stream_end":
                self.turn_active = False
        elif t == "agent_turn_start":
            self.turn_active = True
            self.turn_seen = True
        elif t in NOTICE_TYPES:
            if (self.expecting or now < self.grace_until) and not self.turn_active and not self.turn_seen:
                self.notices.append(notice_text(t, ev))
                self.last_chunk = now
        elif t == "prompt":
            out.extend(self.take_answer())
            at_menu = self.expecting and not self.turn_seen and bool(self.last_plain)
            if at_menu:
                self.notices.clear()            # Mux's "this is the main menu" hint; the menu below says it for chat
            out.extend(self.take_notices())
            if at_menu:
                # A plain message that reached no agent: Mux is at its main menu. Offer to start one with it.
                self.stash = self.last_plain
                out.append(self.menu_message("Mux is at its main menu, so that message wasn't sent to an agent.",
                                             [("Start agent with this message", "start")] + QUICK_MENU[1:4]))
            if self.expecting:
                self.expecting, self.grace_until = False, now + REPLY_GRACE_SECONDS
            if self.after_prompt is not None:
                line, self.after_prompt = self.after_prompt, None
                self.sent(line, plain=True)
                self.to_mux.append(line)
        elif t in ("select_request", "multiselect_request", "confirm_request", "input_request"):
            out.extend(self.take_answer())
            out.extend(self.take_notices())
            out.append(self._prompt(t, ev))
        elif t == "error":
            out.append("⚠️ " + str(ev.get("message") or ev.get("text") or "Unknown error")[:1700])
        return out

    def _prompt(self, t: str, ev: dict[str, Any]) -> str:
        prompt = render_signal(str(ev.get("prompt") or ""))
        if t == "input_request":
            self.pending = PendingPrompt("input", prompt, secret=bool(ev.get("secret")))
            if self.pending.secret:
                return f"🔒 {prompt}\n\n*Secrets can't be entered over Signal; answer this at the computer. Send /cancel to skip.*"
            hint = f"\n*Default: {ev['default']}*" if ev.get("default") else ""
            return f"✍️ {prompt}{hint}\n\n*Reply with your answer.*"
        if t == "confirm_request":
            self.pending = PendingPrompt("confirm", prompt, bailout=bool(ev.get("bailout")))
            return f"❓ {prompt}\n\n*Reply yes or no" + (", or /cancel.*" if self.pending.bailout else ".*")
        choices = [str(c) for c in (ev.get("choices") or [])]
        bailout = bool(ev.get("bailout"))
        real = [c for c in choices if not (bailout and (CUSTOM_LABEL_MARK in c.lower() or c.lower().endswith("(esc)")))]
        kind = "multiselect" if t == "multiselect_request" else "select"
        self.pending = PendingPrompt(kind, prompt, choices=real, bailout=bailout)
        listing = "\n".join(f"{i + 1}. {c}" for i, c in enumerate(real))
        how = ("the numbers, comma-separated (e.g. 1,3" + (", or 1,3 plus your own text" if bailout else "") + ")"
               if kind == "multiselect" else "a number")
        extra = ", type your own answer, or /cancel" if bailout else ""
        return f"❓ {prompt}\n{listing}\n\n*Reply with {how}{extra}.*"

    def answer_for_text(self, text: str) -> Optional[str]:
        p = self.pending
        if not p:
            return self.command_for_menu(text.strip()) if self.menu else None
        raw = text.strip()
        if raw.lower() in ("/cancel", "cancel"):
            self.pending = None
            return "cancel" if p.bailout else ("n" if p.kind == "confirm" else "")
        self.pending = None
        if p.kind == "input":
            return "" if p.secret else text
        if p.kind == "confirm":
            return "y" if raw.lower().startswith("y") else "n"
        if p.kind == "multiselect" and p.bailout:
            m = re.fullmatch(r"(\d+(?:\s*,\s*\d+)*)\s*[,;:]?\s+([^\d\s,].*)", raw, re.S)
            if m:                                   # "1,3 my own text": the checked options plus a custom answer
                return m.group(1).replace(" ", "") + ",=" + m.group(2)
        if re.fullmatch(r"\d+(\s*,\s*\d+)*", raw):
            return raw.replace(" ", "")
        return ("=" + raw) if p.bailout else "1"


# =====================================================================================================================
# Access control
# =====================================================================================================================

class Access:
    def __init__(self, own_number: str, allowed: set[str], allow_all: bool, state_path: str) -> None:
        self.own = own_number
        self.allowed = set(allowed)
        self.allow_all = allow_all
        self.state_path = state_path
        self.pair_code = secrets.token_hex(3).upper()
        self.allowed |= self._read()

    def _read(self) -> set[str]:
        try:
            with open(self.state_path, encoding="utf-8") as fh:
                return {str(x) for x in json.load(fh).get("paired", [])}
        except (OSError, ValueError):
            return set()

    def is_allowed(self, sender: str) -> bool:
        return self.allow_all or sender == self.own or sender in self.allowed

    def try_pair(self, sender: str, code: str) -> bool:
        if not secrets.compare_digest(code.strip().upper(), self.pair_code):
            return False
        self.allowed.add(sender)
        try:
            with open(self.state_path, "w", encoding="utf-8") as fh:
                json.dump({"paired": sorted(self._read() | {sender})}, fh)
        except OSError as e:
            log.warning("could not save paired numbers: %s", e)
        self.pair_code = secrets.token_hex(3).upper()
        log.info("sender paired. New pairing code: %s", self.pair_code)
        return True


@dataclass
class Incoming:
    sender: str
    text: str
    attachments: list[dict[str, Any]]


def parse_envelope(raw: str, own_number: str) -> Optional[Incoming]:
    """A signal-cli-rest-api receive frame -> the message to act on, or None (receipts, typing, groups, others' syncs)."""
    try:
        data = json.loads(raw)
    except json.JSONDecodeError:
        return None
    env = (data.get("params") or data).get("envelope") if isinstance(data, dict) else None
    if not isinstance(env, dict):
        return None
    sender = env.get("sourceNumber") or env.get("source") or ""
    data_msg = env.get("dataMessage") or {}
    sent = (env.get("syncMessage") or {}).get("sentMessage") or {}
    if data_msg:
        if data_msg.get("groupInfo"):
            return None                                   # group chats are not supported (one operator, 1:1)
        msg = data_msg
    elif sent:
        # Your own phone's message to yourself ("Note to Self") arrives as a sync of a sent message.
        dest = sent.get("destinationNumber") or sent.get("destination")
        if not (sender == own_number and dest == own_number):
            return None
        msg = sent
    else:
        return None
    text = msg.get("message") or ""
    attachments = msg.get("attachments") or []
    if not sender or (not text and not attachments):
        return None
    return Incoming(sender=sender, text=text, attachments=attachments)


# =====================================================================================================================
# Mux connection
# =====================================================================================================================

def ws_url_with(url: str, token: Optional[str]) -> str:
    parts = urlsplit(url)
    q = dict(parse_qsl(parts.query))
    q["replay"] = "0"
    if token:
        q["token"] = token
    return urlunsplit((parts.scheme, parts.netloc, parts.path, urlencode(q), parts.fragment))


class ReconnectingWs:
    """A websocket-client connection kept open with backoff; each text line goes to `on_line`."""

    def __init__(self, url: str, on_line: Callable[[str], None], name: str) -> None:
        self.url = url
        self.on_line = on_line
        self.name = name
        self._ws: Optional[websocket.WebSocketApp] = None
        self._lock = threading.Lock()
        self._stop = threading.Event()
        self.connected = threading.Event()

    def send(self, line: str) -> bool:
        with self._lock:
            ws = self._ws
        if ws is None or not self.connected.is_set():
            return False
        try:
            ws.send(line)
            return True
        except Exception as e:  # noqa: BLE001
            log.warning("send to %s failed: %s", self.name, e)
            return False

    def _on_message(self, _ws: Any, message: str) -> None:
        for line in str(message).splitlines():
            if line.strip():
                try:
                    self.on_line(line.strip())
                except Exception:  # noqa: BLE001
                    log.exception("%s handler failed", self.name)

    def run_forever(self) -> None:
        delay = 1.0
        while not self._stop.is_set():
            ws = websocket.WebSocketApp(
                self.url,
                on_open=lambda _w: (self.connected.set(), log.info("connected to %s", self.name)),
                on_message=self._on_message,
                on_error=lambda _w, e: log.warning("%s connection error: %s", self.name, e),
                on_close=lambda _w, c, m: self.connected.clear(),
            )
            with self._lock:
                self._ws = ws
            started = time.time()
            # No ping_timeout: the client's pong check misfires against the server's own keep-alive frames and drops a
            # healthy link every minute (output sent in the reconnect gap is lost with replay=0). A dead Mux still
            # closes the socket, which ends run_forever and reconnects.
            ws.run_forever(ping_interval=30)
            self.connected.clear()
            if self._stop.is_set():
                break
            delay = 1.0 if time.time() - started > 30 else min(delay * 2, 30.0)
            log.info("reconnecting to %s in %.0fs", self.name, delay)
            self._stop.wait(delay)

    def start(self) -> None:
        threading.Thread(target=self.run_forever, name=self.name, daemon=True).start()

    def stop(self) -> None:
        self._stop.set()
        with self._lock:
            if self._ws:
                self._ws.close()


def mux_event_handler(on_event: Callable[[dict[str, Any]], None]) -> Callable[[str], None]:
    def handle(line: str) -> None:
        try:
            ev = json.loads(line)
        except json.JSONDecodeError:
            return
        if isinstance(ev, dict):
            on_event(ev)
    return handle


def http_base(ws_url: str) -> str:
    """The Mux HTTP origin behind a /ws URL (ws://host:port/ws -> http://host:port)."""
    parts = urlsplit(ws_url)
    return urlunsplit(("https" if parts.scheme == "wss" else "http", parts.netloc, "", "", ""))


def upload_to_mux(ws_url: str, token: Optional[str], name: str, data: bytes) -> str:
    """Upload a file the way the web app does (POST /api/upload) and return the path Mux saved it at."""
    if len(data) > MAX_UPLOAD_BYTES:
        raise ValueError(f"{name} is too large ({len(data) // 1_000_000} MB; the limit is {MAX_UPLOAD_BYTES // 1_000_000} MB)")
    headers = {"Authorization": f"Bearer {token}"} if token else {}
    r = requests.post(http_base(ws_url) + "/api/upload", params={"dir": "uploads"}, headers=headers,
                      files={"file": (name, data)}, timeout=60)
    try:
        body = r.json()
    except ValueError:
        body = {}
    if r.status_code != 200 or not body.get("filename"):
        raise ValueError(str(body.get("error") or f"HTTP {r.status_code}"))
    return str(body["filename"])


def with_file_refs(paths: list[str], text: str) -> str:
    """Prefix uploaded files the way the web app does, so the agent reads them (images go to its vision tool)."""
    return "\n".join([f"[file: {p}]" for p in paths] + ([text] if text else [])) if paths else text


# =====================================================================================================================
# Voice (optional extra)
# =====================================================================================================================

_whisper_model = None
_whisper_lock = threading.Lock()


def transcribe(path: str, model_name: str) -> Optional[str]:
    global _whisper_model
    try:
        import whisper  # type: ignore
    except ImportError:
        return None
    if not shutil.which("ffmpeg"):
        try:
            import static_ffmpeg  # type: ignore
            static_ffmpeg.add_paths()
        except ImportError:
            return None
    with _whisper_lock:
        if _whisper_model is None:
            _whisper_model = whisper.load_model(model_name)
    return (_whisper_model.transcribe(path).get("text") or "").strip()


# =====================================================================================================================
# Signal glue
# =====================================================================================================================

class SignalApi:
    def __init__(self, base: str, number: str) -> None:
        self.base = base.rstrip("/")
        self.number = number

    def send(self, recipient: str, text: str) -> None:
        body = {"message": text, "number": self.number, "recipients": [recipient], "text_mode": "styled"}
        try:
            r = requests.post(f"{self.base}/v2/send", json=body, timeout=15)
            if r.status_code >= 400:
                # Styled text the API rejected: resend plain rather than dropping the answer.
                body["text_mode"] = "normal"
                requests.post(f"{self.base}/v2/send", json=body, timeout=15).raise_for_status()
        except requests.RequestException as e:
            log.error("Signal send failed: %s", e)

    def typing(self, recipient: str, on: bool) -> None:
        try:
            (requests.put if on else requests.delete)(f"{self.base}/v1/typing-indicator/{self.number}",
                                                     json={"recipient": recipient}, timeout=5)
        except requests.RequestException:
            pass

    def download(self, attachment_id: str, path: str) -> bool:
        try:
            with requests.get(f"{self.base}/v1/attachments/{attachment_id}", stream=True, timeout=30) as r:
                r.raise_for_status()
                with open(path, "wb") as fh:
                    for block in r.iter_content(65536):
                        fh.write(block)
            return True
        except requests.RequestException as e:
            log.warning("attachment download failed: %s", e)
            return False

    def receive_url(self) -> str:
        return self.base.replace("https://", "wss://").replace("http://", "ws://") + f"/v1/receive/{self.number}"


def main(argv: Optional[list[str]] = None, stop: Optional[threading.Event] = None) -> int:
    """Run until interrupted (or until `stop` is set, which tests use)."""
    ap = argparse.ArgumentParser(description="Mux-Swarm Signal bridge")
    ap.add_argument("--number", default=os.environ.get("SIGNAL_NUMBER", ""))
    ap.add_argument("--api", default=os.environ.get("SIGNAL_API_URL", "http://127.0.0.1:8080"))
    ap.add_argument("--ws", default=os.environ.get("MUX_WS_URL", "ws://127.0.0.1:6723/ws"))
    ap.add_argument("--serve-token", default=os.environ.get("MUX_SERVE_TOKEN") or None)
    ap.add_argument("--allowed", default=os.environ.get("ALLOWED_NUMBERS", ""))
    ap.add_argument("--allow-all", action="store_true", default=os.environ.get("MUX_BRIDGE_ALLOW_ALL") == "1")
    ap.add_argument("--state", default=os.environ.get("MUX_BRIDGE_STATE",
                                                      os.path.join(os.path.dirname(os.path.abspath(__file__)), "signal_bridge.state.json")))
    ap.add_argument("--whisper-model", default=os.environ.get("WHISPER_MODEL", "base"))
    args = ap.parse_args(argv)

    logging.basicConfig(format="%(asctime)s [%(levelname)s] %(message)s", level=logging.INFO)
    if not args.number:
        log.error("SIGNAL_NUMBER is not set")
        return 2

    api = SignalApi(args.api, args.number)
    allowed = {x.strip() for x in args.allowed.split(",") if x.strip()}
    access = Access(args.number, allowed, args.allow_all, args.state)
    if access.allow_all:
        log.warning("MUX_BRIDGE_ALLOW_ALL is on: ANY Signal sender can drive your agents")
    else:
        log.info("Pairing code: %s  (from another number, send '/pair %s'; your Note to Self always works)",
                 access.pair_code, access.pair_code)

    session = MuxSession()
    lock = threading.Lock()
    state: dict[str, Optional[str]] = {"to": None}

    def send_all(texts: list[str]) -> None:
        to = state["to"]
        if to:
            for t in texts:
                api.send(to, t)

    def on_mux_event(ev: dict[str, Any]) -> None:
        with lock:
            texts = session.on_event(ev)
            lines, session.to_mux = session.to_mux, []
        send_all(texts)
        for line in lines:
            mux.send(line)                        # session.sent() was already recorded by MuxSession

    def send_line(line: str, plain: bool) -> bool:
        with lock:
            session.sent(line, plain)
        return mux.send(line)

    mux = ReconnectingWs(ws_url_with(args.ws, args.serve_token), mux_event_handler(on_mux_event), "Mux")

    def handle_incoming(line: str) -> None:
        msg = parse_envelope(line, args.number)
        if msg is None:
            return
        text = msg.text.strip()
        if text.lower().startswith("/pair"):
            ok = access.is_allowed(msg.sender) or access.try_pair(msg.sender, text[5:])
            api.send(msg.sender, "Paired. You can now talk to Mux here." if ok else
                     "That code didn't match. The current code is shown in Mux (notice, or /logs).")
            return
        if not access.is_allowed(msg.sender):
            log.info("ignored message from unpaired sender")
            return
        state["to"] = msg.sender
        if text.lower() == "/stop":
            mux.send("__CANCEL__")
            api.send(msg.sender, "Interrupt sent.")
            return
        if text.lower() == "/menu":
            with lock:
                body = session.menu_message("What would you like to do? Any Mux command (e.g. /help) also works here.",
                                            QUICK_MENU)
            api.send(msg.sender, body)
            return
        for att in msg.attachments:
            if str(att.get("contentType", "")).startswith("audio/") and att.get("id"):
                with tempfile.TemporaryDirectory() as tmp:
                    path = os.path.join(tmp, "audio")
                    spoken = transcribe(path, args.whisper_model) if api.download(att["id"], path) else ""
                if spoken is None:
                    api.send(msg.sender, "Voice notes need the voice extra: add --extra voice to the bridge's args.")
                    return
                if spoken:
                    api.send(msg.sender, f"🎙 {spoken}")
                    text = spoken if not text else f"{text}\n{spoken}"
        paths: list[str] = []
        for att in msg.attachments:
            if str(att.get("contentType", "")).startswith("audio/") or not att.get("id"):
                continue
            name = att.get("filename") or att["id"]
            try:
                if int(att.get("size") or 0) > MAX_UPLOAD_BYTES:
                    raise ValueError(f"{name} is too large (the limit is {MAX_UPLOAD_BYTES // 1_000_000} MB)")
                with tempfile.TemporaryDirectory() as tmp:
                    path = os.path.join(tmp, "file")
                    if not api.download(att["id"], path):
                        raise ValueError("the Signal API couldn't provide it")
                    with open(path, "rb") as fh:
                        data = fh.read()
                paths.append(upload_to_mux(args.ws, args.serve_token, name, data))
            except (ValueError, OSError, requests.RequestException) as e:
                log.warning("attachment %s not passed to Mux: %s", name, e)
                api.send(msg.sender, f"Couldn't pass {name} to Mux: {e}")
        if paths and not text:
            text = "Take a look at the attached file" + ("s." if len(paths) > 1 else ".")
        if not text:
            return
        with lock:
            answer = session.answer_for_text(text)
        line = answer if answer is not None else with_file_refs(paths, text)
        if not send_line(line, plain=answer is None and not line.startswith("/")):
            api.send(msg.sender, "Mux isn't reachable right now; I'll keep retrying. Try again shortly.")

    signal_in = ReconnectingWs(api.receive_url(), handle_incoming, "Signal")

    def background() -> None:
        typing_on = False
        while True:
            time.sleep(0.5)
            with lock:
                texts = session.take_idle() if session.idle_flush_due(time.time()) else []
                want_typing = session.turn_active and session.pending is None
            send_all(texts)
            if state["to"] and want_typing != typing_on:
                api.typing(state["to"], want_typing)
                typing_on = want_typing

    mux.start()
    signal_in.start()
    threading.Thread(target=background, name="flush", daemon=True).start()
    log.info("Signal bridge running for %s (Signal API %s, Mux at %s)", args.number, args.api, args.ws)
    try:
        (stop or threading.Event()).wait()
    except KeyboardInterrupt:
        pass
    mux.stop()
    signal_in.stop()
    return 0


if __name__ == "__main__":
    sys.exit(main())
