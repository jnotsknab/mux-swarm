#!/usr/bin/env python3
"""
Mux-Swarm Telegram bridge: chat with your Mux agents from Telegram.

Runs as a daemon `bridge` trigger (Mux starts it and passes MUX_WS_URL / MUX_SERVE_TOKEN) or by hand:

  uv run --project Runtime Runtime/telegram_bridge.py            # text
  uv run --project Runtime --extra voice Runtime/telegram_bridge.py   # + voice notes (local Whisper)

Environment:
  TELEGRAM_BOT_TOKEN   bot token from @BotFather (required)
  MUX_WS_URL           Mux /ws endpoint (set by the daemon; default ws://127.0.0.1:6723/ws)
  MUX_SERVE_TOKEN      serve.auth token (set by the daemon when auth is on)
  ALLOWED_CHAT_IDS     comma-separated chat IDs that may use the bot
  MUX_BRIDGE_ALLOW_ALL set to 1 to let ANY chat use the bot (not recommended: it drives agents with shell access)
  MUX_BRIDGE_STATE     file that remembers paired chats (default: telegram_bridge.state.json next to this script)
  WHISPER_MODEL        tiny | base | small | medium | large | turbo (voice extra only; default base)

Access: by default only chats in ALLOWED_CHAT_IDS or paired chats are answered. To pair, look for the one-time
code in the bridge log (or `/daemon` output) and send `/pair <code>` to the bot from your phone.
"""

from __future__ import annotations

import argparse
import asyncio
import html
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

import websocket  # websocket-client

log = logging.getLogger("telegram-bridge")

TELEGRAM_LIMIT = 4096
CHUNK_LIMIT = 3900          # leave room for HTML tags added while chunking
FLUSH_IDLE_SECONDS = 2.5    # send buffered answer text after this much stream silence
TYPING_EVERY_SECONDS = 4.0


# =====================================================================================================================
# Rendering: Mux Markdown -> Telegram HTML
# =====================================================================================================================

_ANSI_RE = re.compile(r"\x1b\[[0-9;?]*[ -/]*[@-~]")
_SPECTRE_RE = re.compile(r"\[(?:/|(?:bold|dim|italic|underline|strikethrough|link|grey\d*|gray\d*|#?[0-9a-fA-F]{6}|[a-z]+\d*)(?:[ =][^\]]*)?)\]")
_FENCE_RE = re.compile(r"^```([\w+-]*)\s*$")
_TABLE_SEP_RE = re.compile(r"^\s*\|?\s*:?-{3,}:?\s*(\|\s*:?-{3,}:?\s*)*\|?\s*$")


def strip_terminal_markup(text: str) -> str:
    """Remove ANSI escapes and Spectre console markup (`[bold]...[/]`) that may leak into text."""
    return _SPECTRE_RE.sub("", _ANSI_RE.sub("", text))


def _inline(text: str) -> str:
    """Escape for HTML, then apply inline Markdown: code, bold, italic, strike, links."""
    parts = re.split(r"(`[^`\n]+`)", text)
    out: list[str] = []
    for part in parts:
        if len(part) >= 2 and part.startswith("`") and part.endswith("`"):
            out.append(f"<code>{html.escape(part[1:-1], quote=False)}</code>")
            continue
        s = html.escape(part, quote=False)
        s = re.sub(r"\[([^\]\n]+)\]\((https?://[^)\s]+)\)", lambda m: f'<a href="{html.escape(m.group(2))}">{m.group(1)}</a>', s)
        s = re.sub(r"\*\*(.+?)\*\*", r"<b>\1</b>", s)
        s = re.sub(r"__(.+?)__", r"<b>\1</b>", s)
        s = re.sub(r"(?<![\w*])\*(?!\s)([^*\n]+?)(?<!\s)\*(?![\w*])", r"<i>\1</i>", s)
        s = re.sub(r"(?<![\w_])_(?!\s)([^_\n]+?)(?<!\s)_(?![\w_])", r"<i>\1</i>", s)
        s = re.sub(r"~~(.+?)~~", r"<s>\1</s>", s)
        out.append(s)
    return "".join(out)


def _table_block(rows: list[str]) -> str:
    """A Markdown table as an aligned monospace block (Telegram has no tables)."""
    cells = [[c.strip() for c in r.strip().strip("|").split("|")] for r in rows if not _TABLE_SEP_RE.match(r)]
    if not cells:
        return ""
    width = max(len(r) for r in cells)
    cells = [r + [""] * (width - len(r)) for r in cells]
    widths = [max(len(strip_terminal_markup(r[i])) for r in cells) for i in range(width)]
    lines = ["  ".join(c.ljust(widths[i]) for i, c in enumerate(r)).rstrip() for r in cells]
    if len(lines) > 1:
        lines.insert(1, "  ".join("-" * w for w in widths))
    return "<pre>" + html.escape("\n".join(lines), quote=False) + "</pre>"


def render_html(markdown: str) -> str:
    """Mux's Markdown answer -> Telegram HTML (parse_mode=HTML). Headings bold, lists kept, tables and code as <pre>."""
    text = strip_terminal_markup(markdown).replace("\r\n", "\n")
    lines = text.split("\n")
    out: list[str] = []
    i = 0
    while i < len(lines):
        line = lines[i]
        fence = _FENCE_RE.match(line.strip())
        if fence:
            lang = fence.group(1)
            body: list[str] = []
            i += 1
            while i < len(lines) and not lines[i].strip().startswith("```"):
                body.append(lines[i])
                i += 1
            i += 1  # closing fence (or end of text)
            code = html.escape("\n".join(body), quote=False)
            out.append(f'<pre><code class="language-{lang}">{code}</code></pre>' if lang else f"<pre>{code}</pre>")
            continue
        if line.strip().startswith("|") and i + 1 < len(lines) and _TABLE_SEP_RE.match(lines[i + 1]):
            rows: list[str] = []
            while i < len(lines) and lines[i].strip().startswith("|"):
                rows.append(lines[i])
                i += 1
            out.append(_table_block(rows))
            continue
        heading = re.match(r"^(#{1,6})\s+(.*)$", line)
        if heading:
            out.append(f"<b>{_inline(heading.group(2).strip())}</b>")
        elif re.match(r"^\s*[-*+]\s+", line):
            out.append(re.sub(r"^(\s*)[-*+]\s+", lambda m: m.group(1) + "• ", _inline(line), count=1))
        elif re.match(r"^\s*>\s?", line):
            out.append("<i>" + _inline(re.sub(r"^\s*>\s?", "", line)) + "</i>")
        elif re.match(r"^\s*(-{3,}|\*{3,}|_{3,})\s*$", line):
            out.append("──────────")
        else:
            out.append(_inline(line))
        i += 1
    rendered = "\n".join(out)
    return re.sub(r"\n{3,}", "\n\n", rendered).strip()


def chunk_html(rendered: str, limit: int = CHUNK_LIMIT) -> list[str]:
    """
    Split rendered HTML into messages under the limit at line boundaries (then word boundaries), never inside a tag;
    a <pre> block split across messages is closed and reopened so every message is valid HTML on its own.
    """
    if len(rendered) <= limit:
        return [rendered] if rendered else []
    chunks: list[str] = []
    current = ""
    open_pre: Optional[str] = None   # the opening tag(s) of a <pre> block we are inside, to reopen in the next chunk

    def close_suffix() -> str:
        return ("</code></pre>" if open_pre and "<code" in open_pre else "</pre>") if open_pre else ""

    for line in rendered.split("\n"):
        pieces = [line]
        if len(line) > limit - 40:   # a single huge line: fall back to word, then hard, splits
            pieces = []
            rest = line
            while len(rest) > limit - 40:
                cut = rest.rfind(" ", 0, limit - 40)
                cut = cut if cut > 0 else limit - 40
                while "<" in rest[:cut] and rest[:cut].rfind("<") > rest[:cut].rfind(">"):
                    cut = rest[:cut].rfind("<")  # never split inside a tag
                pieces.append(rest[:cut])
                rest = rest[cut:].lstrip(" ")
            pieces.append(rest)
        for piece in pieces:
            candidate = (current + "\n" + piece) if current else piece
            if len(candidate) + len(close_suffix()) > limit and current:
                chunks.append(current + close_suffix())
                current = (open_pre or "") + piece
            else:
                current = candidate
            for m in re.finditer(r"<pre>(?:<code[^>]*>)?|</pre>", piece):
                open_pre = None if m.group(0) == "</pre>" else m.group(0)
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
    default: Any = None


@dataclass
class Outgoing:
    """Something the bridge should show in the chat."""
    text: str = ""                 # rendered HTML
    buttons: list[list[tuple[str, str]]] = field(default_factory=list)   # rows of (label, callback data)


CUSTOM_LABEL_MARK = "enter custom"
CANCEL_LABEL_MARK = "cancel"


class MuxSession:
    """
    Turns Mux /ws events into chat output for the one chat currently driving Mux. Pure logic (no I/O), so it can be
    tested without Telegram: feed events with `on_event`, read what to send from the returned list.
    """

    def __init__(self) -> None:
        self.buffer: list[str] = []
        self.last_chunk = 0.0
        self.pending: Optional[PendingPrompt] = None
        self.turn_active = False
        self.agent_label: Optional[str] = None   # sub-agent currently streaming (prefix once)

    def take_answer(self) -> list[Outgoing]:
        text = "".join(self.buffer).strip()
        self.buffer.clear()
        self.agent_label = None
        if not text:
            return []
        return [Outgoing(text=c) for c in chunk_html(render_html(text))]

    def idle_flush_due(self, now: float) -> bool:
        return bool(self.buffer) and self.last_chunk > 0 and now - self.last_chunk >= FLUSH_IDLE_SECONDS

    def on_event(self, ev: dict[str, Any], now: Optional[float] = None) -> list[Outgoing]:
        now = time.time() if now is None else now
        t = ev.get("type", "")
        out: list[Outgoing] = []
        if t == "stream":
            if ev.get("reasoning"):
                return out                       # thinking text is not the answer
            text = ev.get("text") or ""
            agent = ev.get("agent")
            if agent and agent != self.agent_label and agent != "Orchestrator":
                if self.buffer:
                    out.extend(self.take_answer())
                self.agent_label = agent
                self.buffer.append(f"**{agent}:** ")
            self.buffer.append(text)
            self.last_chunk = now
            self.turn_active = True
        elif t in ("stream_end", "agent_turn_end", "turn_complete"):
            out.extend(self.take_answer())
            if t != "stream_end":
                self.turn_active = False
        elif t == "agent_turn_start":
            self.turn_active = True
        elif t in ("select_request", "multiselect_request", "confirm_request", "input_request"):
            out.extend(self.take_answer())       # show what the agent said before asking
            out.append(self._prompt(t, ev))
        elif t == "error":
            msg = ev.get("message") or ev.get("text") or "Unknown error"
            out.append(Outgoing(text="⚠️ " + html.escape(str(msg)[:3500], quote=False)))
        return out

    def _prompt(self, t: str, ev: dict[str, Any]) -> Outgoing:
        prompt = str(ev.get("prompt") or "")
        if t == "input_request":
            self.pending = PendingPrompt("input", prompt, secret=bool(ev.get("secret")), default=ev.get("default"))
            if self.pending.secret:
                return Outgoing(text="🔒 " + render_html(prompt) + "\n\n<i>Secrets can't be entered over Telegram; "
                                     "answer this at the computer. Reply /cancel to skip.</i>")
            hint = f"\n<i>Default: {html.escape(str(ev['default']), quote=False)}</i>" if ev.get("default") else ""
            return Outgoing(text="✍️ " + render_html(prompt) + hint + "\n\n<i>Reply with your answer.</i>")
        if t == "confirm_request":
            self.pending = PendingPrompt("confirm", prompt, bailout=bool(ev.get("bailout")), default=ev.get("default"))
            row = [("Yes", "a:y"), ("No", "a:n")]
            return Outgoing(text="❓ " + render_html(prompt), buttons=[row] + ([[("Cancel", "a:cancel")]] if self.pending.bailout else []))
        choices = [str(c) for c in (ev.get("choices") or [])]
        bailout = bool(ev.get("bailout"))
        # The bailout affordance rows (custom / cancel) arrive as the last two choices; render them as actions.
        real = [c for c in choices if not (bailout and (CUSTOM_LABEL_MARK in c.lower() or c.lower().endswith("(esc)")))]
        kind = "multiselect" if t == "multiselect_request" else "select"
        self.pending = PendingPrompt(kind, prompt, choices=real, bailout=bailout)
        rows = [[(c[:60], f"a:{i + 1}")] for i, c in enumerate(real[:40])]
        if bailout:
            rows.append([("Type my own", "a:custom"), ("Cancel", "a:cancel")])
        note = "\n\n<i>Reply with the numbers, comma-separated (e.g. 1,3).</i>" if kind == "multiselect" else ""
        listing = "\n".join(f"{i + 1}. {html.escape(c, quote=False)}" for i, c in enumerate(real)) if kind == "multiselect" else ""
        body = "❓ " + render_html(prompt) + ("\n" + listing if listing else "") + note
        return Outgoing(text=body, buttons=[] if kind == "multiselect" else rows)

    def answer_for_button(self, data: str) -> Optional[str]:
        """The line to send to Mux for a pressed button (None = ignore, e.g. a stale prompt)."""
        if not self.pending or not data.startswith("a:"):
            return None
        v = data[2:]
        p = self.pending
        if v == "cancel":
            self.pending = None
            return "cancel" if p.bailout else ("n" if p.kind == "confirm" else "")
        if v == "custom":
            p.kind = "input"   # the next text message is the custom value
            p.prompt = "custom"
            return None
        if p.kind == "confirm":
            self.pending = None
            return "y" if v == "y" else "n"
        self.pending = None
        return v               # 1-based index, what Mux expects

    def answer_for_text(self, text: str) -> Optional[str]:
        """If a prompt is waiting, the line to send for this typed message; None = it is a normal message."""
        p = self.pending
        if not p:
            return None
        if text.strip().lower() in ("/cancel", "cancel"):
            self.pending = None
            return "cancel" if p.bailout else ("n" if p.kind == "confirm" else "")
        if p.kind == "input":
            self.pending = None
            if p.prompt == "custom":
                return "=" + text.strip()          # bailout syntax for a typed custom value
            return "" if p.secret else text
        if p.kind == "confirm":
            self.pending = None
            return "y" if text.strip().lower().startswith("y") else "n"
        if p.kind in ("select", "multiselect"):
            self.pending = None
            raw = text.strip()
            if re.fullmatch(r"\d+(\s*,\s*\d+)*", raw):
                return raw.replace(" ", "")
            return ("=" + raw) if p.bailout else "1"
        return None


# =====================================================================================================================
# Access control: allowlist + pairing (deny unknown chats by default)
# =====================================================================================================================

class Access:
    def __init__(self, allowed: set[int], allow_all: bool, state_path: str) -> None:
        self.allowed = set(allowed)
        self.allow_all = allow_all
        self.state_path = state_path
        self.pair_code = secrets.token_hex(3).upper()   # 6 hex chars, regenerated each start
        self._load()

    def _load(self) -> None:
        try:
            with open(self.state_path, encoding="utf-8") as fh:
                self.allowed |= {int(x) for x in json.load(fh).get("paired", [])}
        except (OSError, ValueError):
            pass

    def _save(self, paired: set[int]) -> None:
        try:
            with open(self.state_path, "w", encoding="utf-8") as fh:
                json.dump({"paired": sorted(paired)}, fh)
        except OSError as e:
            log.warning("could not save paired chats: %s", e)

    def is_allowed(self, chat_id: int) -> bool:
        return self.allow_all or chat_id in self.allowed

    def try_pair(self, chat_id: int, code: str) -> bool:
        if not secrets.compare_digest(code.strip().upper(), self.pair_code):
            return False
        self.allowed.add(chat_id)
        existing: set[int] = set()
        try:
            with open(self.state_path, encoding="utf-8") as fh:
                existing = {int(x) for x in json.load(fh).get("paired", [])}
        except (OSError, ValueError):
            pass
        self._save(existing | {chat_id})
        self.pair_code = secrets.token_hex(3).upper()   # one-time
        log.info("chat %s paired. New pairing code: %s", chat_id, self.pair_code)
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
        except Exception as e:  # noqa: BLE001 - any send failure means "not delivered"
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
                except Exception:  # noqa: BLE001 - one bad event must not kill the link
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
            ws.run_forever(ping_interval=30, ping_timeout=10)
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
# Telegram glue
# =====================================================================================================================

def main(argv: Optional[list[str]] = None) -> int:
    from telegram import InlineKeyboardButton, InlineKeyboardMarkup, Update
    from telegram.constants import ChatAction, ParseMode
    from telegram.error import BadRequest
    from telegram.ext import (ApplicationBuilder, CallbackQueryHandler, CommandHandler, ContextTypes,
                              MessageHandler, filters)

    ap = argparse.ArgumentParser(description="Mux-Swarm Telegram bridge")
    ap.add_argument("--token", default=os.environ.get("TELEGRAM_BOT_TOKEN", ""))
    ap.add_argument("--ws", default=os.environ.get("MUX_WS_URL", "ws://127.0.0.1:6723/ws"))
    ap.add_argument("--serve-token", default=os.environ.get("MUX_SERVE_TOKEN") or None)
    ap.add_argument("--allowed", default=os.environ.get("ALLOWED_CHAT_IDS", ""))
    ap.add_argument("--allow-all", action="store_true", default=os.environ.get("MUX_BRIDGE_ALLOW_ALL") == "1")
    ap.add_argument("--state", default=os.environ.get("MUX_BRIDGE_STATE",
                                                      os.path.join(os.path.dirname(os.path.abspath(__file__)), "telegram_bridge.state.json")))
    ap.add_argument("--whisper-model", default=os.environ.get("WHISPER_MODEL", "base"))
    args = ap.parse_args(argv)

    logging.basicConfig(format="%(asctime)s [%(levelname)s] %(message)s", level=logging.INFO)
    if not args.token:
        log.error("TELEGRAM_BOT_TOKEN is not set (create a bot with @BotFather)")
        return 2

    allowed = {int(x) for x in re.split(r"[,\s]+", args.allowed) if x.strip().lstrip("-").isdigit()}
    access = Access(allowed, args.allow_all, args.state)
    if access.allow_all:
        log.warning("MUX_BRIDGE_ALLOW_ALL is on: ANY Telegram chat can drive your agents")
    else:
        log.info("Pairing code: %s  (send '/pair %s' to the bot from your phone)", access.pair_code, access.pair_code)

    session = MuxSession()
    state: dict[str, Any] = {"chat_id": None}
    loop_holder: dict[str, asyncio.AbstractEventLoop] = {}
    app = ApplicationBuilder().token(args.token).build()

    async def deliver(chat_id: int, items: list[Outgoing]) -> None:
        for item in items:
            markup = None
            if item.buttons:
                markup = InlineKeyboardMarkup([[InlineKeyboardButton(lbl, callback_data=data) for lbl, data in row]
                                               for row in item.buttons])
            try:
                await app.bot.send_message(chat_id, item.text, parse_mode=ParseMode.HTML, reply_markup=markup,
                                           disable_web_page_preview=True)
            except BadRequest:
                # Rendering produced HTML Telegram rejected: fall back to plain text rather than dropping the answer.
                plain = html.unescape(re.sub(r"<[^>]+>", "", item.text))
                for part in [plain[i:i + CHUNK_LIMIT] for i in range(0, len(plain), CHUNK_LIMIT)] or [""]:
                    await app.bot.send_message(chat_id, part or "(empty)", reply_markup=markup)

    def emit(items: list[Outgoing]) -> None:
        chat_id = state["chat_id"]
        if items and chat_id is not None and "loop" in loop_holder:
            asyncio.run_coroutine_threadsafe(deliver(chat_id, items), loop_holder["loop"])

    lock = threading.Lock()

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
                items = session.take_answer() if session.idle_flush_due(now) else []
                typing = session.turn_active and session.pending is None
            emit(items)
            if typing and state["chat_id"] is not None and now - last_typing >= TYPING_EVERY_SECONDS and "loop" in loop_holder:
                last_typing = now
                asyncio.run_coroutine_threadsafe(app.bot.send_chat_action(state["chat_id"], ChatAction.TYPING),
                                                 loop_holder["loop"])

    async def guard(update: Update) -> bool:
        chat = update.effective_chat
        if chat is None:
            return False
        if access.is_allowed(chat.id):
            return True
        if update.message:
            await update.message.reply_text("This bot is private. If it's yours, send /pair <code> with the code "
                                            "shown in the Mux bridge log.")
        log.info("ignored message from unpaired chat %s", chat.id)
        return False

    async def forward(update: Update, text: str) -> None:
        state["chat_id"] = update.effective_chat.id
        with lock:
            answer = session.answer_for_text(text)
        line = answer if answer is not None else text
        if not link.send(line):
            await update.message.reply_text("Mux isn't reachable right now; I'll keep retrying. Try again shortly.")

    async def on_pair(update: Update, ctx: ContextTypes.DEFAULT_TYPE) -> None:
        code = " ".join(ctx.args or [])
        if access.is_allowed(update.effective_chat.id):
            await update.message.reply_text("This chat is already paired.")
        elif access.try_pair(update.effective_chat.id, code):
            await update.message.reply_text("Paired. You can now talk to Mux here.")
        else:
            await update.message.reply_text("That code didn't match. Check the Mux bridge log for the current code.")

    async def on_start(update: Update, _ctx: ContextTypes.DEFAULT_TYPE) -> None:
        if await guard(update):
            await update.message.reply_text("Connected to Mux. Send a message to talk to your agent; /stop interrupts it.")

    async def on_stop(update: Update, _ctx: ContextTypes.DEFAULT_TYPE) -> None:
        if await guard(update):
            link.send("__CANCEL__")
            await update.message.reply_text("Interrupt sent.")

    async def on_cancel(update: Update, _ctx: ContextTypes.DEFAULT_TYPE) -> None:
        if await guard(update):
            await forward(update, "/cancel")

    async def on_text(update: Update, _ctx: ContextTypes.DEFAULT_TYPE) -> None:
        if update.message and update.message.text and await guard(update):
            await forward(update, update.message.text)

    async def on_media(update: Update, _ctx: ContextTypes.DEFAULT_TYPE) -> None:
        if not update.message or not await guard(update):
            return
        msg = update.message
        audio = msg.voice or msg.audio or msg.video_note
        if audio is not None:
            with tempfile.TemporaryDirectory() as tmp:
                path = os.path.join(tmp, "audio")
                await (await audio.get_file()).download_to_drive(path)
                text = await asyncio.to_thread(transcribe, path, args.whisper_model)
            if text is None:
                await msg.reply_text("Voice messages need the voice extra: add `--extra voice` to the bridge's "
                                     "args (uv run --project Runtime --extra voice Runtime/telegram_bridge.py).")
                return
            if not text:
                await msg.reply_text("I couldn't make out any speech in that.")
                return
            await msg.reply_text(f"🎙 {text}")
            await forward(update, text)
            return
        caption = msg.caption or ""
        if msg.photo:
            await forward(update, f"[Photo received] {caption}".strip())
        elif msg.document:
            await forward(update, f"[Document: {msg.document.file_name}] {caption}".strip())

    async def on_button(update: Update, _ctx: ContextTypes.DEFAULT_TYPE) -> None:
        q = update.callback_query
        if q is None or not access.is_allowed(q.message.chat.id):
            return
        await q.answer()
        state["chat_id"] = q.message.chat.id
        with lock:
            line = session.answer_for_button(q.data or "")
            asking_custom = session.pending is not None and session.pending.prompt == "custom"
        if asking_custom:
            await q.message.reply_text("Type your answer.")
        elif line is not None:
            if not link.send(line):
                await q.message.reply_text("Mux isn't reachable right now; try again shortly.")
            else:
                try:
                    await q.edit_message_reply_markup(reply_markup=None)   # buttons are one-shot
                except BadRequest:
                    pass

    async def post_init(application: Any) -> None:
        loop_holder["loop"] = asyncio.get_running_loop()
        await application.bot.set_my_commands([
            ("stop", "Interrupt the running agent"), ("cancel", "Cancel a pending question"),
            ("pair", "Pair this chat: /pair <code>")])

    app.post_init = post_init
    app.add_handler(CommandHandler("pair", on_pair))
    app.add_handler(CommandHandler("start", on_start))
    app.add_handler(CommandHandler("stop", on_stop))
    app.add_handler(CommandHandler("cancel", on_cancel))
    app.add_handler(CallbackQueryHandler(on_button))
    app.add_handler(MessageHandler(filters.VOICE | filters.AUDIO | filters.VIDEO_NOTE | filters.PHOTO | filters.Document.ALL, on_media))
    app.add_handler(MessageHandler(filters.TEXT & ~filters.COMMAND, on_text))

    link.start()
    threading.Thread(target=background, name="flush", daemon=True).start()
    log.info("Telegram bridge running (Mux at %s)", args.ws)
    app.run_polling(allowed_updates=Update.ALL_TYPES)
    link.stop()
    return 0


if __name__ == "__main__":
    sys.exit(main())
