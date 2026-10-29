#!/usr/bin/env python3
"""
Mux-Swarm Discord bridge: chat with your Mux agents from Discord (DMs or one channel).

Runs as a daemon `bridge` trigger (Mux starts it and passes MUX_WS_URL / MUX_SERVE_TOKEN) or by hand:

  uv run --project Runtime Runtime/discord_bridge.py
  uv run --project Runtime --extra voice Runtime/discord_bridge.py    # + voice messages (local Whisper)

Environment:
  DISCORD_BOT_TOKEN    bot token (Developer Portal > Bot; enable the Message Content intent) (required)
  DISCORD_CHANNEL_ID   also listen in this server channel (optional; DMs to the bot always work)
  MUX_WS_URL           Mux /ws endpoint (set by the daemon; default ws://127.0.0.1:6723/ws)
  MUX_SERVE_TOKEN      serve.auth token (set by the daemon when auth is on)
  ALLOWED_USER_IDS     comma-separated Discord user IDs that may use the bot
  MUX_BRIDGE_ALLOW_ALL set to 1 to let ANY user use the bot (not recommended: it drives agents with shell access)
  MUX_BRIDGE_STATE     file that remembers paired users (default: discord_bridge.state.json next to this script)
  WHISPER_MODEL        tiny | base | small | medium | large | turbo (voice extra only; default base)

Commands: any Mux slash command works from the chat (/agent, /resume, /status, /help, /qc, ...); its answer is posted
back. /menu shows quick actions as buttons, /stop interrupts the running turn. Attached files and images are uploaded
to Mux (like the web app's attachments) and handed to the agent.

Access: by default only users in ALLOWED_USER_IDS or paired users are answered. To pair, look for the one-time code
Mux shows when the bridge starts (also in /logs) and send `/pair <code>` to the bot (DM or the channel).
"""

from __future__ import annotations

import argparse
import asyncio
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

log = logging.getLogger("discord-bridge")

DISCORD_LIMIT = 2000
CHUNK_LIMIT = 1990
FLUSH_IDLE_SECONDS = 2.5
REPLY_GRACE_SECONDS = 3.0   # notices right after the reply's prompt still belong to it (a session ends, then the command runs)
TYPING_EVERY_SECONDS = 8.0
MAX_BUTTONS = 20            # Discord allows 25 per message; keep room for the custom/cancel row
MAX_UPLOAD_BYTES = 25_000_000   # under Mux's default request size limit


# =====================================================================================================================
# Rendering: Mux Markdown -> Discord Markdown
# =====================================================================================================================

_ANSI_RE = re.compile(r"\x1b\[[0-9;?]*[ -/]*[@-~]")
# Only real Spectre console tags: [/], styles, colours (named, numbered, #hex), optional "on <colour>", link=.
# A bare word in brackets (e.g. a Markdown link label "[docs]") is NOT markup and is kept.
_SPECTRE_STYLE = r"(?:bold|dim|italic|underline|strikethrough|invert|blink|conceal|default|black|red|green|yellow|blue|magenta|purple|cyan|teal|white|silver|grey\d*|gray\d*|orange\d*|aqua|lime|navy|maroon|olive|fuchsia|#[0-9a-fA-F]{6}|rgb\(\d+,\d+,\d+\))"
_SPECTRE_RE = re.compile(r"\[(?:/|link(?:=[^\]]*)?|" + _SPECTRE_STYLE + r"(?:\s+(?:" + _SPECTRE_STYLE + r"|on\s+" + _SPECTRE_STYLE + r"))*)\]")
_FENCE_RE = re.compile(r"^\s*```")
_TABLE_SEP_RE = re.compile(r"^\s*\|?\s*:?-{3,}:?\s*(\|\s*:?-{3,}:?\s*)*\|?\s*$")


def strip_terminal_markup(text: str) -> str:
    """Remove ANSI escapes and Spectre console markup (`[bold]...[/]`) that may leak into text."""
    return _SPECTRE_RE.sub("", _ANSI_RE.sub("", text))


def _table_block(rows: list[str]) -> str:
    """A Markdown table as an aligned monospace block (Discord does not render tables)."""
    cells = [[c.strip() for c in r.strip().strip("|").split("|")] for r in rows if not _TABLE_SEP_RE.match(r)]
    if not cells:
        return ""
    width = max(len(r) for r in cells)
    cells = [r + [""] * (width - len(r)) for r in cells]
    widths = [max(len(r[i]) for r in cells) for i in range(width)]
    lines = ["  ".join(c.ljust(widths[i]) for i, c in enumerate(r)).rstrip() for r in cells]
    if len(lines) > 1:
        lines.insert(1, "  ".join("-" * w for w in widths))
    return "```\n" + "\n".join(lines) + "\n```"


def render_discord(markdown: str) -> str:
    """Mux's Markdown answer -> Discord Markdown: tables as code blocks, deep headings capped at ###, markup stripped."""
    lines = strip_terminal_markup(markdown).replace("\r\n", "\n").split("\n")
    out: list[str] = []
    i = 0
    in_fence = False
    while i < len(lines):
        line = lines[i]
        if _FENCE_RE.match(line):
            in_fence = not in_fence
            out.append(line.strip())
            i += 1
            continue
        if not in_fence and line.strip().startswith("|") and i + 1 < len(lines) and _TABLE_SEP_RE.match(lines[i + 1]):
            rows: list[str] = []
            while i < len(lines) and lines[i].strip().startswith("|"):
                rows.append(lines[i])
                i += 1
            out.append(_table_block(rows))
            continue
        if not in_fence:
            line = re.sub(r"^#{4,6}\s+", "### ", line)                  # Discord renders #, ## and ### only
            line = re.sub(r"^\s*(-{3,}|\*{3,}|_{3,})\s*$", "──────────", line)
        out.append(line)
        i += 1
    if in_fence:
        while out and not out[-1].strip():
            out.pop()
        out.append("```")                                                # an unclosed fence would swallow the rest
    return re.sub(r"\n{3,}", "\n\n", "\n".join(out)).strip()


def chunk_discord(text: str, limit: int = CHUNK_LIMIT) -> list[str]:
    """Split under Discord's 2000-char limit at line (then word) boundaries; a split code block is closed and reopened."""
    if len(text) <= limit:
        return [text] if text else []
    chunks: list[str] = []
    current = ""
    fence: Optional[str] = None       # the opening fence line of a code block we are inside

    def closing() -> str:
        return "\n```" if fence is not None else ""

    for line in text.split("\n"):
        pieces = [line]
        if len(line) > limit - 20:
            pieces, rest = [], line
            while len(rest) > limit - 20:
                cut = rest.rfind(" ", 0, limit - 20)
                cut = cut if cut > 0 else limit - 20
                pieces.append(rest[:cut])
                rest = rest[cut:].lstrip(" ")
            pieces.append(rest)
        for piece in pieces:
            candidate = (current + "\n" + piece) if current else piece
            if len(candidate) + len(closing()) > limit and current:
                chunks.append(current + closing())
                current = ((fence + "\n") if fence is not None else "") + piece
            else:
                current = candidate
            if _FENCE_RE.match(piece):
                fence = None if fence is not None else piece.strip()
    if current:
        chunks.append(current)
    return chunks


# =====================================================================================================================
# Protocol: Mux /ws events -> what to send to the chat
# =====================================================================================================================

@dataclass
class PendingPrompt:
    kind: str                      # select | multiselect | confirm | input
    prompt: str
    choices: list[str] = field(default_factory=list)
    bailout: bool = False
    secret: bool = False


@dataclass
class Outgoing:
    text: str = ""
    buttons: list[tuple[str, str]] = field(default_factory=list)   # (label, answer id); rendered 5 per row
    mux: Optional[str] = None      # a line to send to Mux instead of a chat message


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
    """Mux /ws events -> Discord output for whoever is currently driving Mux. Pure logic, testable without Discord."""

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
        self.menu: list[tuple[str, str]] = []   # quick-menu choices currently offered (also answerable by number)

    def sent(self, line: str, plain: bool) -> None:
        """Record a line this bridge sent to Mux, so the notices Mux answers with are shown in the chat."""
        self.expecting, self.turn_seen, self.grace_until = True, False, 0.0
        self.last_plain = line if plain else None
        self.menu = []

    def menu_message(self, prompt: str, items: list[tuple[str, str]]) -> Outgoing:
        self.menu = list(items)
        return Outgoing(text=prompt, buttons=[(label, "cmd:" + str(i + 1)) for i, (label, _) in enumerate(items)])

    def take_notices(self) -> list[Outgoing]:
        text = "\n".join(n for n in self.notices if n.strip())
        self.notices.clear()
        return [Outgoing(text=c) for c in chunk_discord(render_discord(text))] if text.strip() else []

    def take_answer(self) -> list[Outgoing]:
        text = "".join(self.buffer).strip()
        self.buffer.clear()
        self.agent_label = None
        return [Outgoing(text=c) for c in chunk_discord(render_discord(text))] if text else []

    def idle_flush_due(self, now: float) -> bool:
        return bool(self.buffer or self.notices) and self.last_chunk > 0 and now - self.last_chunk >= FLUSH_IDLE_SECONDS

    def take_idle(self) -> list[Outgoing]:
        return self.take_answer() + self.take_notices()

    def on_event(self, ev: dict[str, Any], now: Optional[float] = None) -> list[Outgoing]:
        now = time.time() if now is None else now
        t = ev.get("type", "")
        out: list[Outgoing] = []
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
                out.append(Outgoing(mux=line))
        elif t in ("select_request", "multiselect_request", "confirm_request", "input_request"):
            out.extend(self.take_answer())
            out.extend(self.take_notices())
            out.append(self._prompt(t, ev))
        elif t == "error":
            out.append(Outgoing(text="⚠️ " + str(ev.get("message") or ev.get("text") or "Unknown error")[:1900]))
        return out

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

    def _prompt(self, t: str, ev: dict[str, Any]) -> Outgoing:
        prompt = render_discord(str(ev.get("prompt") or ""))
        if t == "input_request":
            self.pending = PendingPrompt("input", prompt, secret=bool(ev.get("secret")))
            if self.pending.secret:
                return Outgoing(text=f"🔒 {prompt}\n\n*Secrets can't be entered over Discord; answer this at the "
                                     "computer. Send /cancel to skip.*")
            hint = f"\n*Default: {ev['default']}*" if ev.get("default") else ""
            return Outgoing(text=f"✍️ {prompt}{hint}\n\n*Reply with your answer.*")
        if t == "confirm_request":
            self.pending = PendingPrompt("confirm", prompt, bailout=bool(ev.get("bailout")))
            buttons = [("Yes", "y"), ("No", "n")] + ([("Cancel", "cancel")] if self.pending.bailout else [])
            return Outgoing(text=f"❓ {prompt}", buttons=buttons)
        choices = [str(c) for c in (ev.get("choices") or [])]
        bailout = bool(ev.get("bailout"))
        real = [c for c in choices if not (bailout and (CUSTOM_LABEL_MARK in c.lower() or c.lower().endswith("(esc)")))]
        kind = "multiselect" if t == "multiselect_request" else "select"
        self.pending = PendingPrompt(kind, prompt, choices=real, bailout=bailout)
        if kind == "multiselect" or len(real) > MAX_BUTTONS:
            listing = "\n".join(f"{i + 1}. {c}" for i, c in enumerate(real))
            how = "the numbers, comma-separated (e.g. 1,3)" if kind == "multiselect" else "a number"
            return Outgoing(text=f"❓ {prompt}\n{listing}\n\n*Reply with {how}.*")
        buttons = [(c[:80], str(i + 1)) for i, c in enumerate(real)]
        if bailout:
            buttons += [("Type my own", "custom"), ("Cancel", "cancel")]
        return Outgoing(text=f"❓ {prompt}", buttons=buttons)

    def answer_for_button(self, value: str) -> Optional[str]:
        if value.startswith("cmd:"):
            return self.command_for_menu(value[4:])
        p = self.pending
        if not p:
            return None
        if value == "cancel":
            self.pending = None
            return "cancel" if p.bailout else ("n" if p.kind == "confirm" else "")
        if value == "custom":
            p.kind, p.prompt = "input", "custom"
            return None
        self.pending = None
        return value

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
            if p.prompt == "custom":
                return "=" + raw
            return "" if p.secret else text
        if p.kind == "confirm":
            return "y" if raw.lower().startswith("y") else "n"
        if re.fullmatch(r"\d+(\s*,\s*\d+)*", raw):
            return raw.replace(" ", "")
        return ("=" + raw) if p.bailout else "1"


# =====================================================================================================================
# Access control: allowlist + pairing (deny unknown users by default)
# =====================================================================================================================

class Access:
    def __init__(self, allowed: set[int], allow_all: bool, state_path: str) -> None:
        self.allowed = set(allowed)
        self.allow_all = allow_all
        self.state_path = state_path
        self.pair_code = secrets.token_hex(3).upper()
        self.allowed |= self._read()

    def _read(self) -> set[int]:
        try:
            with open(self.state_path, encoding="utf-8") as fh:
                return {int(x) for x in json.load(fh).get("paired", [])}
        except (OSError, ValueError):
            return set()

    def is_allowed(self, user_id: int) -> bool:
        return self.allow_all or user_id in self.allowed

    def try_pair(self, user_id: int, code: str) -> bool:
        if not secrets.compare_digest(code.strip().upper(), self.pair_code):
            return False
        self.allowed.add(user_id)
        try:
            with open(self.state_path, "w", encoding="utf-8") as fh:
                json.dump({"paired": sorted(self._read() | {user_id})}, fh)
        except OSError as e:
            log.warning("could not save paired users: %s", e)
        self.pair_code = secrets.token_hex(3).upper()
        log.info("user %s paired. New pairing code: %s", user_id, self.pair_code)
        return True


# =====================================================================================================================
# Mux connection
# =====================================================================================================================

def ws_url_with(url: str, token: Optional[str]) -> str:
    """Add replay=0 (never resend old output on reconnect) and the auth token to the /ws URL."""
    parts = urlsplit(url)
    q = dict(parse_qsl(parts.query))
    q["replay"] = "0"
    if token:
        q["token"] = token
    return urlunsplit((parts.scheme, parts.netloc, parts.path, urlencode(q), parts.fragment))


class MuxLink:
    """Keeps one WebSocket to Mux open (reconnecting with backoff) and hands decoded events to a callback."""

    def __init__(self, url: str, token: Optional[str], on_event: Callable[[dict[str, Any]], None]) -> None:
        self.url = ws_url_with(url, token)
        self.on_event = on_event
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
            log.warning("send to Mux failed: %s", e)
            return False

    def _on_message(self, _ws: Any, message: str) -> None:
        for line in str(message).splitlines():
            line = line.strip()
            if not line:
                continue
            try:
                ev = json.loads(line)
            except json.JSONDecodeError:
                continue
            if isinstance(ev, dict):
                try:
                    self.on_event(ev)
                except Exception:  # noqa: BLE001
                    log.exception("event handler failed")

    def run_forever(self) -> None:
        delay = 1.0
        while not self._stop.is_set():
            ws = websocket.WebSocketApp(
                self.url,
                on_open=lambda _w: (self.connected.set(), log.info("connected to Mux")),
                on_message=self._on_message,
                on_error=lambda _w, e: log.warning("Mux connection error: %s", e),
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
            log.info("reconnecting to Mux in %.0fs", delay)
            self._stop.wait(delay)

    def start(self) -> None:
        threading.Thread(target=self.run_forever, name="mux-ws", daemon=True).start()

    def stop(self) -> None:
        self._stop.set()
        with self._lock:
            if self._ws:
                self._ws.close()


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
    """Transcribe with local Whisper, or None when the voice extra is not installed."""
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
            log.info("loading Whisper model '%s'", model_name)
            _whisper_model = whisper.load_model(model_name)
    return (_whisper_model.transcribe(path).get("text") or "").strip()


# =====================================================================================================================
# Discord glue
# =====================================================================================================================

def main(argv: Optional[list[str]] = None) -> int:
    import discord

    ap = argparse.ArgumentParser(description="Mux-Swarm Discord bridge")
    ap.add_argument("--token", default=os.environ.get("DISCORD_BOT_TOKEN", ""))
    ap.add_argument("--channel", default=os.environ.get("DISCORD_CHANNEL_ID", ""))
    ap.add_argument("--ws", default=os.environ.get("MUX_WS_URL", "ws://127.0.0.1:6723/ws"))
    ap.add_argument("--serve-token", default=os.environ.get("MUX_SERVE_TOKEN") or None)
    ap.add_argument("--allowed", default=os.environ.get("ALLOWED_USER_IDS", ""))
    ap.add_argument("--allow-all", action="store_true", default=os.environ.get("MUX_BRIDGE_ALLOW_ALL") == "1")
    ap.add_argument("--state", default=os.environ.get("MUX_BRIDGE_STATE",
                                                      os.path.join(os.path.dirname(os.path.abspath(__file__)), "discord_bridge.state.json")))
    ap.add_argument("--whisper-model", default=os.environ.get("WHISPER_MODEL", "base"))
    args = ap.parse_args(argv)

    logging.basicConfig(format="%(asctime)s [%(levelname)s] %(message)s", level=logging.INFO)
    if not args.token:
        log.error("DISCORD_BOT_TOKEN is not set")
        return 2
    channel_id = int(args.channel) if args.channel.strip().isdigit() else None

    allowed = {int(x) for x in re.split(r"[,\s]+", args.allowed) if x.strip().isdigit()}
    access = Access(allowed, args.allow_all, args.state)
    if access.allow_all:
        log.warning("MUX_BRIDGE_ALLOW_ALL is on: ANY Discord user can drive your agents")
    else:
        log.info("Pairing code: %s  (send '/pair %s' to the bot)", access.pair_code, access.pair_code)

    session = MuxSession()
    lock = threading.Lock()
    state: dict[str, Any] = {"channel": None}
    intents = discord.Intents.default()
    intents.message_content = True
    client = discord.Client(intents=intents)
    no_pings = discord.AllowedMentions.none()

    class AnswerView(discord.ui.View):
        def __init__(self, buttons: list[tuple[str, str]]) -> None:
            super().__init__(timeout=None)
            for label, value in buttons:
                style = discord.ButtonStyle.secondary if value in ("cancel", "custom", "n") else discord.ButtonStyle.primary
                btn = discord.ui.Button(label=label, style=style)
                btn.callback = self._make(value)
                self.add_item(btn)

        def _make(self, value: str):
            async def callback(interaction: discord.Interaction) -> None:
                if not access.is_allowed(interaction.user.id):
                    await interaction.response.send_message("This bot is private.", ephemeral=True)
                    return
                state["channel"] = interaction.channel
                with lock:
                    line = session.answer_for_button(value)
                    asking_custom = session.pending is not None and session.pending.prompt == "custom"
                if asking_custom:
                    await interaction.response.send_message("Type your answer.")
                    return
                await interaction.response.edit_message(view=None)      # buttons are one-shot
                if line is not None and not send_line(line, plain=False):
                    await interaction.followup.send("Mux isn't reachable right now; try again shortly.")
            return callback

    def send_line(line: str, plain: bool) -> bool:
        with lock:
            session.sent(line, plain)
        return link.send(line)

    async def deliver(items: list[Outgoing]) -> None:
        channel = state["channel"]
        if channel is None:
            return
        for item in items:
            await channel.send(item.text or "\u200b", view=AnswerView(item.buttons) if item.buttons else None,
                               allowed_mentions=no_pings)

    def emit(items: list[Outgoing]) -> None:
        for item in items:
            if item.mux is not None:
                link.send(item.mux)               # session.sent() was already recorded by MuxSession
        chat = [i for i in items if i.mux is None]
        if chat and state["channel"] is not None and client.loop is not None:
            asyncio.run_coroutine_threadsafe(deliver(chat), client.loop)

    def on_mux_event(ev: dict[str, Any]) -> None:
        with lock:
            items = session.on_event(ev)
        emit(items)

    link = MuxLink(args.ws, args.serve_token, on_mux_event)

    def background() -> None:
        last_typing = 0.0
        while True:
            time.sleep(0.5)
            now = time.time()
            with lock:
                items = session.take_idle() if session.idle_flush_due(now) else []
                typing = session.turn_active and session.pending is None
            emit(items)
            channel = state["channel"]
            if typing and channel is not None and now - last_typing >= TYPING_EVERY_SECONDS:
                last_typing = now
                async def pulse(ch: Any = channel) -> None:
                    await ch.typing()   # one typing pulse (~10 s); repeated while the turn runs
                asyncio.run_coroutine_threadsafe(pulse(), client.loop)

    async def forward(message: Any, text: str) -> None:
        state["channel"] = message.channel
        with lock:
            answer = session.answer_for_text(text)
        line = answer if answer is not None else text
        if not send_line(line, plain=answer is None and not line.startswith("/")):
            await message.reply("Mux isn't reachable right now; I'll keep retrying. Try again shortly.",
                                allowed_mentions=no_pings)

    @client.event
    async def on_ready() -> None:
        where = f"DMs and channel {channel_id}" if channel_id else "DMs"
        log.info("Discord bridge ready as %s (listening in %s; Mux at %s)", client.user, where, args.ws)

    @client.event
    async def on_message(message: Any) -> None:
        if message.author.bot:
            return
        is_dm = isinstance(message.channel, discord.DMChannel)
        if not is_dm and (channel_id is None or message.channel.id != channel_id):
            return
        text = (message.content or "").strip()
        if text.lower().startswith("/pair"):
            code = text[5:].strip()
            if access.is_allowed(message.author.id):
                await message.reply("You're already paired.", allowed_mentions=no_pings)
            elif access.try_pair(message.author.id, code):
                await message.reply("Paired. You can now talk to Mux here.", allowed_mentions=no_pings)
            else:
                await message.reply("That code didn't match. The current code is shown in Mux (notice, or /logs).",
                                    allowed_mentions=no_pings)
            return
        if not access.is_allowed(message.author.id):
            if is_dm:
                await message.reply("This bot is private. If it's yours, send /pair <code> with the code shown in "
                                    "Mux (a notice when the bridge starts, also in /logs).", allowed_mentions=no_pings)
            return
        if text.lower() == "/stop":
            link.send("__CANCEL__")
            await message.reply("Interrupt sent.", allowed_mentions=no_pings)
            return
        if text.lower() == "/menu":
            state["channel"] = message.channel
            with lock:
                item = session.menu_message("What would you like to do? Any Mux command (e.g. /help) also works here.",
                                            QUICK_MENU)
            await deliver([item])
            return
        audio = next((a for a in message.attachments if (a.content_type or "").startswith("audio/")), None)
        if audio is not None:
            with tempfile.TemporaryDirectory() as tmp:
                path = os.path.join(tmp, audio.filename or "audio")
                await audio.save(path)
                spoken = await asyncio.to_thread(transcribe, path, args.whisper_model)
            if spoken is None:
                await message.reply("Voice messages need the voice extra: add `--extra voice` to the bridge's args.",
                                    allowed_mentions=no_pings)
                return
            if not spoken:
                await message.reply("I couldn't make out any speech in that.", allowed_mentions=no_pings)
                return
            await message.reply(f"🎙 {spoken}", allowed_mentions=no_pings)
            text = spoken if not text else f"{text}\n{spoken}"
        paths: list[str] = []
        for att in message.attachments:
            if att is audio:
                continue
            try:
                if (att.size or 0) > MAX_UPLOAD_BYTES:
                    raise ValueError(f"{att.filename} is too large ({att.size // 1_000_000} MB; the limit is "
                                     f"{MAX_UPLOAD_BYTES // 1_000_000} MB)")
                data = await att.read()
                paths.append(await asyncio.to_thread(upload_to_mux, args.ws, args.serve_token, att.filename or "file", data))
            except Exception as e:  # noqa: BLE001
                log.warning("attachment %s not passed to Mux: %s", att.filename, e)
                await message.reply(f"Couldn't pass {att.filename} to Mux: {e}", allowed_mentions=no_pings)
        if paths and not text:
            text = "Take a look at the attached file" + ("s." if len(paths) > 1 else ".")
        if text:
            await forward(message, with_file_refs(paths, text))

    link.start()
    threading.Thread(target=background, name="flush", daemon=True).start()
    client.run(args.token, log_handler=None)
    link.stop()
    return 0


if __name__ == "__main__":
    sys.exit(main())
