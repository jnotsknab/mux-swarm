#!/usr/bin/env python3
"""
Mux-Swarm Telegram Bridge (Audio Edition)
Connects Telegram <-> Mux-Swarm WebSocket runtime.
Supports text, voice, audio, video_note, photos, documents.
Voice/audio/video_note are transcribed via OpenAI Whisper (local, no API key).
FFmpeg resolved automatically via static-ffmpeg fallback (cross-platform).

Usage:
  python telegram_bridge.py --token YOUR_BOT_TOKEN --ws ws://localhost:6723/ws

Or set environment variables:
  TELEGRAM_BOT_TOKEN=...
  MUX_WS_URL=...
  WHISPER_MODEL=...
  ALLOWED_CHAT_IDS=123456,789012
"""

from __future__ import annotations

import asyncio
import json
import os
import re
import sys
import shutil
import signal
import tempfile
import logging
import argparse
import threading
import time
from typing import Optional

import websocket
from telegram import Update
from telegram.ext import (
    ApplicationBuilder,
    ContextTypes,
    MessageHandler,
    CommandHandler,
    filters,
)

# ---------------------------------------------------------------------------
# Config (defaults, overridden by CLI args)
# ---------------------------------------------------------------------------
WS_URL: str = os.environ.get("MUX_WS_URL", "ws://localhost:6723/ws")
TELEGRAM_TOKEN: str = os.environ.get("TELEGRAM_BOT_TOKEN", "")
WHISPER_MODEL: str = os.environ.get("WHISPER_MODEL", "base")
ALLOWED_CHAT_IDS: set[int] = set()

logging.basicConfig(
    format="%(asctime)s [%(levelname)s] %(message)s", level=logging.INFO
)
log: logging.Logger = logging.getLogger("telegram-bridge")

# ---------------------------------------------------------------------------
# FFmpeg (ensure available before Whisper loads)
# ---------------------------------------------------------------------------

def ensure_ffmpeg() -> bool:
    """
    Ensure ffmpeg is on PATH. Checks system install first,
    falls back to static-ffmpeg package (cross-platform: Win/Mac/Linux).
    Returns True if ffmpeg is available after this call.
    """
    if shutil.which("ffmpeg"):
        log.info(f"ffmpeg found at: {shutil.which('ffmpeg')}")
        return True

    try:
        import static_ffmpeg
        static_ffmpeg.add_paths()
        if shutil.which("ffmpeg"):
            log.info("ffmpeg provisioned via static-ffmpeg package")
            return True
    except ImportError:
        log.warning(
            "ffmpeg not found on PATH and 'static-ffmpeg' package is not installed. "
            "Audio transcription will fail. Install via: pip install static-ffmpeg"
        )
    except Exception as e:
        log.warning(f"static-ffmpeg setup failed: {e}")

    return False


_ffmpeg_available: bool = False

# ---------------------------------------------------------------------------
# Whisper (lazy load, thread-safe)
# ---------------------------------------------------------------------------
_whisper_model = None
_whisper_lock: threading.Lock = threading.Lock()


def get_whisper():
    """Thread-safe lazy load of Whisper model."""
    global _whisper_model
    if _whisper_model is not None:
        return _whisper_model

    with _whisper_lock:
        if _whisper_model is not None:
            return _whisper_model

        import whisper
        log.info(f"Loading Whisper model '{WHISPER_MODEL}'...")
        _whisper_model = whisper.load_model(WHISPER_MODEL)
        log.info(f"Whisper model '{WHISPER_MODEL}' loaded.")
        return _whisper_model


def transcribe_audio(file_path: str) -> str:
    """Transcribe audio file to text using Whisper."""
    if not _ffmpeg_available:
        return "[Transcription unavailable: ffmpeg not found]"

    try:
        model = get_whisper()
        result = model.transcribe(file_path)
        return result.get("text", "").strip()
    except FileNotFoundError as e:
        log.error(f"Whisper dependency missing: {e}")
        return f"[Transcription error: missing dependency - {e}]"
    except Exception as e:
        log.error(f"Whisper transcription failed: {e}")
        return f"[Transcription error: {e}]"


# ---------------------------------------------------------------------------
# WebSocket connection to Mux runtime
# ---------------------------------------------------------------------------
ws_conn: Optional[websocket.WebSocketApp] = None
ws_lock: threading.Lock = threading.Lock()

# Streaming state
stream_buffer: str = ""
buffer_lock: threading.Lock = threading.Lock()
last_chunk_time: float = 0.0
FLUSH_TIMEOUT: float = 3.0
current_chat_id: Optional[int] = None

# Cross-thread Telegram references (set in main)
_tg_loop: Optional[asyncio.AbstractEventLoop] = None
_tg_app = None


def chunk_message(text: str, limit: int = 4000) -> list[str]:
    """Split long messages for Telegram's 4096 char limit."""
    chunks: list[str] = []
    while len(text) > limit:
        idx: int = text.rfind("\n", 0, limit)
        if idx == -1:
            idx = text.rfind(" ", 0, limit)
        if idx == -1:
            idx = limit
        chunks.append(text[:idx])
        text = text[idx:].lstrip("\n")
    if text:
        chunks.append(text)
    return chunks


def _send_telegram_sync(chat_id: int, text: str) -> None:
    """Send a message to Telegram from a non-async context (WS thread)."""
    if not _tg_loop or not _tg_app:
        log.warning("Telegram loop/app not ready, dropping message")
        return

    chunks: list[str] = chunk_message(text)
    for chunk in chunks:
        try:
            future = asyncio.run_coroutine_threadsafe(
                _tg_app.bot.send_message(chat_id=chat_id, text=chunk),
                _tg_loop,
            )
            future.result(timeout=15)
        except asyncio.TimeoutError:
            log.error(f"Telegram send timed out for chat {chat_id}")
        except Exception as e:
            log.error(f"Telegram send failed: {e}")


def flush_buffer() -> None:
    """Send accumulated stream response to Telegram."""
    global stream_buffer, last_chunk_time

    with buffer_lock:
        text: str = stream_buffer.strip()
        stream_buffer = ""
        last_chunk_time = 0.0

    if not text or not current_chat_id:
        return

    text = re.sub(r"\n{3,}", "\n\n", text)
    _send_telegram_sync(current_chat_id, text)


def flush_timer() -> None:
    """Background thread: flush buffer after idle timeout."""
    while True:
        time.sleep(0.5)
        with buffer_lock:
            has_content: bool = bool(stream_buffer.strip())
            elapsed: float = time.time() - last_chunk_time if last_chunk_time > 0 else 0.0
        if has_content and elapsed >= FLUSH_TIMEOUT:
            flush_buffer()


def send_to_mux(message: str) -> None:
    """Send raw text to Mux WS."""
    with ws_lock:
        if ws_conn and ws_conn.sock and ws_conn.sock.connected:
            try:
                ws_conn.send(message)
            except Exception as e:
                log.error(f"WS send error: {e}")
        else:
            log.warning("WS not connected, dropping message")


def on_ws_open(ws) -> None:
    log.info("Connected to Mux runtime WS")


def on_ws_message(ws, message: str) -> None:
    """Receive NDJSON events from Mux runtime, forward to Telegram."""
    global stream_buffer, last_chunk_time

    try:
        for line in message.strip().split("\n"):
            line = line.strip()
            if not line:
                continue
            try:
                data: dict = json.loads(line)
            except json.JSONDecodeError:
                continue

            ev_type: str = data.get("type", "")

            if ev_type == "stream":
                text: str = data.get("text", "")
                if text:
                    with buffer_lock:
                        stream_buffer += text
                    last_chunk_time = time.time()

            elif ev_type in ("stream_end", "agent_turn_end"):
                with buffer_lock:
                    has_content: bool = bool(stream_buffer.strip())
                if has_content:
                    flush_buffer()

            elif ev_type == "response":
                text = data.get("message") or data.get("content") or data.get("response", "")
                chat_id: Optional[int] = data.get("chat_id")
                if text and chat_id:
                    _send_telegram_sync(chat_id, text)

            elif ev_type == "error":
                msg: str = data.get("message", "Unknown error")
                if current_chat_id:
                    _send_telegram_sync(current_chat_id, f"Error: {msg[:4000]}")

            elif ev_type == "system":
                log.info(f"System: {data.get('message', '')}")

    except Exception as e:
        log.error(f"WS message handler error: {e}")


def on_ws_error(ws, error) -> None:
    log.error(f"WS error: {error}")


def on_ws_close(ws, code, msg) -> None:
    log.warning(f"WS closed: code={code} msg={msg}")
    threading.Timer(3.0, start_ws).start()
    log.info("Scheduled WS reconnect in 3s")


def start_ws() -> None:
    """Start WebSocket connection in a background thread."""
    global ws_conn
    ws_conn = websocket.WebSocketApp(
        WS_URL,
        on_open=on_ws_open,
        on_message=on_ws_message,
        on_error=on_ws_error,
        on_close=on_ws_close,
    )
    t = threading.Thread(target=ws_conn.run_forever, daemon=True)
    t.start()
    log.info(f"WS thread started for {WS_URL}")


# ---------------------------------------------------------------------------
# Telegram handlers
# ---------------------------------------------------------------------------

def _is_authorized(chat_id: int) -> bool:
    """Check if chat is authorized. Empty allowlist = open access."""
    if not ALLOWED_CHAT_IDS:
        return True
    return chat_id in ALLOWED_CHAT_IDS


async def handle_start(update: Update, context: ContextTypes.DEFAULT_TYPE) -> None:
    """Handle /start command."""
    if not update.message:
        return
    chat_id: int = update.effective_chat.id
    audio_status: str = "available" if _ffmpeg_available else "unavailable (ffmpeg missing)"
    await update.message.reply_text(
        f"Mux-Swarm bridge connected (Audio Edition).\n"
        f"Chat ID: `{chat_id}`\n"
        f"Whisper model: {WHISPER_MODEL}\n"
        f"Audio transcription: {audio_status}",
        parse_mode="Markdown",
    )


async def handle_cancel(update: Update, context: ContextTypes.DEFAULT_TYPE) -> None:
    """Handle /cancel command: sends cancel signal to Mux runtime."""
    if not update.message:
        return
    if not _is_authorized(update.effective_chat.id):
        await update.message.reply_text("Unauthorized.")
        return

    send_to_mux("__CANCEL__")
    log.info(f"Cancel signal sent (user: {update.effective_user.username})")
    await update.message.reply_text("Cancel signal sent.")


async def handle_message(update: Update, context: ContextTypes.DEFAULT_TYPE) -> None:
    """Handle all incoming Telegram messages."""
    global current_chat_id

    if not update.message:
        return

    chat_id: int = update.effective_chat.id
    if not _is_authorized(chat_id):
        return

    user = update.effective_user
    username: str = user.username or user.first_name or str(user.id)
    text: Optional[str] = None
    tmp_path: Optional[str] = None

    current_chat_id = chat_id

    try:
        # --- Voice message ---
        if update.message.voice:
            if not _ffmpeg_available:
                await update.message.reply_text("Audio transcription unavailable: ffmpeg not found.")
                return
            voice = update.message.voice
            log.info(f"Voice message from @{username} ({voice.duration}s)")
            status_msg = await update.message.reply_text("Transcribing audio...")
            with tempfile.NamedTemporaryFile(suffix=".ogg", delete=False) as f:
                tmp_path = f.name
            tg_file = await context.bot.get_file(voice.file_id)
            await tg_file.download_to_drive(tmp_path)
            text = transcribe_audio(tmp_path)
            if text and not text.startswith("["):
                preview: str = text[:100] + ("..." if len(text) > 100 else "")
                await status_msg.edit_text(f"Transcribed: {preview}")
            else:
                await status_msg.edit_text(text or "[Empty transcription]")

        # --- Audio file ---
        elif update.message.audio:
            if not _ffmpeg_available:
                await update.message.reply_text("Audio transcription unavailable: ffmpeg not found.")
                return
            audio = update.message.audio
            fname: str = audio.file_name or "audio"
            log.info(f"Audio file from @{username}: {fname}")
            status_msg = await update.message.reply_text(f"Transcribing \"{fname}\"...")
            ext: str = os.path.splitext(audio.file_name or ".mp3")[1] or ".mp3"
            with tempfile.NamedTemporaryFile(suffix=ext, delete=False) as f:
                tmp_path = f.name
            tg_file = await context.bot.get_file(audio.file_id)
            await tg_file.download_to_drive(tmp_path)
            text = transcribe_audio(tmp_path)
            if text and not text.startswith("["):
                preview = text[:100] + ("..." if len(text) > 100 else "")
                await status_msg.edit_text(f"Transcribed: {preview}")
            else:
                await status_msg.edit_text(text or "[Empty transcription]")

        # --- Video note (round) ---
        elif update.message.video_note:
            if not _ffmpeg_available:
                await update.message.reply_text("Audio transcription unavailable: ffmpeg not found.")
                return
            vn = update.message.video_note
            log.info(f"Video note from @{username}")
            status_msg = await update.message.reply_text("Transcribing video note...")
            with tempfile.NamedTemporaryFile(suffix=".mp4", delete=False) as f:
                tmp_path = f.name
            tg_file = await context.bot.get_file(vn.file_id)
            await tg_file.download_to_drive(tmp_path)
            text = transcribe_audio(tmp_path)
            if text and not text.startswith("["):
                preview = text[:100] + ("..." if len(text) > 100 else "")
                await status_msg.edit_text(f"Transcribed: {preview}")
            else:
                await status_msg.edit_text(text or "[Empty transcription]")

        # --- Photo ---
        elif update.message.photo:
            caption: str = update.message.caption or ""
            text = f"[Photo received] {caption}".strip() if caption else "[Photo received - no caption]"

        # --- Document ---
        elif update.message.document:
            doc = update.message.document
            caption = update.message.caption or ""
            text = f"[Document: {doc.file_name}] {caption}".strip()

        # --- Plain text ---
        elif update.message.text:
            text = update.message.text

        if not text:
            return

        send_to_mux(text)
        log.info(f"Forwarded to Mux: [{username}] {text[:80]}...")

    except Exception as e:
        log.error(f"Handler error for @{username}: {e}", exc_info=True)
        try:
            await update.message.reply_text(f"Bridge error: {e}")
        except Exception:
            pass
    finally:
        if tmp_path and os.path.exists(tmp_path):
            try:
                os.unlink(tmp_path)
            except OSError:
                pass


# ---------------------------------------------------------------------------
# Main
# ---------------------------------------------------------------------------

def _parse_chat_ids(raw: str) -> set[int]:
    """Parse comma-separated chat IDs from string."""
    ids: set[int] = set()
    for part in raw.split(","):
        part = part.strip()
        if part.lstrip("-").isdigit():
            ids.add(int(part))
    return ids


def main() -> None:
    global WS_URL, TELEGRAM_TOKEN, WHISPER_MODEL, ALLOWED_CHAT_IDS
    global _tg_loop, _tg_app, _ffmpeg_available

    parser = argparse.ArgumentParser(
        description="Mux-Swarm Telegram Bridge (Audio Edition)"
    )
    parser.add_argument("--token", default=TELEGRAM_TOKEN,
                        help="Telegram bot token (or TELEGRAM_BOT_TOKEN env)")
    parser.add_argument("--ws", default=WS_URL,
                        help="Mux WebSocket URL (or MUX_WS_URL env)")
    parser.add_argument("--whisper-model", default=WHISPER_MODEL,
                        help="Whisper model: tiny/base/small/medium/large/turbo")
    parser.add_argument("--allowed-chats", default=os.environ.get("ALLOWED_CHAT_IDS", ""),
                        help="Comma-separated Telegram chat IDs to allow (empty = open access)")
    args = parser.parse_args()

    WS_URL = args.ws
    TELEGRAM_TOKEN = args.token or TELEGRAM_TOKEN
    WHISPER_MODEL = args.whisper_model

    if args.allowed_chats.strip():
        ALLOWED_CHAT_IDS = _parse_chat_ids(args.allowed_chats)

    if not TELEGRAM_TOKEN:
        log.error("No Telegram bot token. Set TELEGRAM_BOT_TOKEN env or use --token")
        sys.exit(1)

    _ffmpeg_available = ensure_ffmpeg()
    if not _ffmpeg_available:
        log.warning("Proceeding without audio transcription support")

    log.info(f"Starting Telegram bridge (Whisper={WHISPER_MODEL}, ffmpeg={'yes' if _ffmpeg_available else 'no'})")
    log.info(f"WS target: {WS_URL}")
    if ALLOWED_CHAT_IDS:
        log.info(f"Authorized chat IDs: {ALLOWED_CHAT_IDS}")
    else:
        log.info("No chat ID restrictions (open access)")

    start_ws()
    threading.Thread(target=flush_timer, daemon=True).start()

    app = ApplicationBuilder().token(TELEGRAM_TOKEN).build()
    _tg_app = app
    import warnings
    with warnings.catch_warnings():
        warnings.simplefilter("ignore", DeprecationWarning)
        _tg_loop = asyncio.get_event_loop()

    app.add_handler(CommandHandler("start", handle_start))
    app.add_handler(CommandHandler("cancel", handle_cancel))
    app.add_handler(MessageHandler(filters.ALL & ~filters.COMMAND, handle_message))

    def _shutdown(signum, frame) -> None:
        log.info(f"Received signal {signum}, shutting down...")
        if ws_conn:
            try:
                ws_conn.close()
            except Exception:
                pass
        sys.exit(0)

    signal.signal(signal.SIGINT, _shutdown)
    signal.signal(signal.SIGTERM, _shutdown)

    log.info("Starting Telegram polling...")
    app.run_polling(drop_pending_updates=True)


if __name__ == "__main__":
    main()
