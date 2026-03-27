#!/usr/bin/env python3
"""
Mux-Swarm Discord Bridge (Audio Edition)
Connects Discord <-> Mux-Swarm WebSocket runtime.
Supports text, voice messages, audio attachments, and video attachments.
Voice/audio/video are transcribed via OpenAI Whisper (local, no API key).
FFmpeg resolved automatically via static-ffmpeg fallback (cross-platform).

Discord voice messages are OGG/Opus (1-channel, 48kHz, 32kbps).

Usage:
  python discord_bridge.py --token YOUR_BOT_TOKEN --channel CHANNEL_ID --ws ws://localhost:6723/ws

Or set environment variables:
  DISCORD_BOT_TOKEN=...
  DISCORD_CHANNEL_ID=...
  MUX_WS_URL=...
  WHISPER_MODEL=...
"""

from __future__ import annotations

import argparse
import asyncio
import json
import os
import re
import shutil
import signal
import sys
import tempfile
import threading
import time
import logging
from typing import Optional

import discord
import websocket

# ---------------------------------------------------------------------------
# Config (defaults, overridden by CLI args)
# ---------------------------------------------------------------------------
WS_URL: str = os.environ.get("MUX_WS_URL", "ws://localhost:6723/ws")
DISCORD_TOKEN: str = os.environ.get("DISCORD_BOT_TOKEN", "")
WHISPER_MODEL: str = os.environ.get("WHISPER_MODEL", "base")

logging.basicConfig(
    format="%(asctime)s [%(levelname)s] %(message)s", level=logging.INFO
)
log: logging.Logger = logging.getLogger("discord-bridge")

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
ws_conn: Optional[websocket.WebSocket] = None
ws_lock: threading.Lock = threading.Lock()

# Streaming state
stream_buffer: str = ""
buffer_lock: threading.Lock = threading.Lock()
last_chunk_time: float = 0.0
FLUSH_TIMEOUT: float = 3.0
target_channel: Optional[discord.TextChannel] = None


def strip_markdown_for_discord(text: str) -> str:
    """Clean up agent markdown for Discord display."""
    text = re.sub(r"```(\w+)\n", "```\n", text)
    text = re.sub(r"\n{3,}", "\n\n", text)
    return text.strip()


def chunk_message(text: str, limit: int = 1900) -> list[str]:
    """Split long messages to fit Discord's 2000 char limit."""
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


def _send_discord_sync(text: str) -> None:
    """Send a message to the target Discord channel from a non-async context."""
    if not target_channel:
        log.warning("No target channel set, dropping message")
        return

    clean: str = strip_markdown_for_discord(text)
    if not clean:
        return

    chunks: list[str] = chunk_message(clean)
    loop = target_channel._state.loop

    for chunk in chunks:
        try:
            future = asyncio.run_coroutine_threadsafe(
                target_channel.send(chunk),
                loop,
            )
            future.result(timeout=15)
        except asyncio.TimeoutError:
            log.error("Discord send timed out")
        except Exception as e:
            log.error(f"Discord send failed: {e}")


def flush_buffer() -> None:
    """Send accumulated stream response to Discord."""
    global stream_buffer, last_chunk_time

    with buffer_lock:
        text: str = stream_buffer.strip()
        stream_buffer = ""
        last_chunk_time = 0.0

    if not text:
        return

    _send_discord_sync(text)


def flush_timer() -> None:
    """Background thread: flush buffer after idle timeout."""
    while True:
        time.sleep(0.5)
        with buffer_lock:
            has_content: bool = bool(stream_buffer.strip())
            elapsed: float = time.time() - last_chunk_time if last_chunk_time > 0 else 0.0
        if has_content and elapsed >= FLUSH_TIMEOUT:
            flush_buffer()


def ws_receiver() -> None:
    """Receive NDJSON events from mux-swarm WebSocket, accumulate text."""
    global stream_buffer, last_chunk_time, ws_conn

    while True:
        try:
            if ws_conn is None:
                time.sleep(1)
                continue

            data: str = ws_conn.recv()
            if not data:
                continue

            for line in data.strip().split("\n"):
                line = line.strip()
                if not line:
                    continue
                try:
                    ev: dict = json.loads(line)
                except json.JSONDecodeError:
                    continue

                ev_type: str = ev.get("type", "")

                if ev_type == "stream":
                    text: str = ev.get("text", "")
                    if text:
                        with buffer_lock:
                            stream_buffer += text
                        last_chunk_time = time.time()

                elif ev_type in ("stream_end", "agent_turn_end"):
                    with buffer_lock:
                        has_content: bool = bool(stream_buffer.strip())
                    if has_content:
                        flush_buffer()

                elif ev_type == "error":
                    msg: str = ev.get("message", "")
                    if msg:
                        _send_discord_sync(f"**Error:** {msg[:1900]}")

                elif ev_type == "success":
                    msg = ev.get("message", "")
                    if msg and "session" in msg.lower():
                        with buffer_lock:
                            has_content = bool(stream_buffer.strip())
                        if has_content:
                            flush_buffer()

                elif ev_type == "system":
                    log.info(f"System: {ev.get('message', '')}")

        except websocket.WebSocketConnectionClosedException:
            log.warning("WebSocket closed. Reconnecting in 3s...")
            ws_conn = None
            time.sleep(3)
            connect_ws()
        except Exception as e:
            log.error(f"WS receive error: {e}")
            time.sleep(1)


def connect_ws(url: Optional[str] = None) -> None:
    """Connect to mux-swarm WebSocket."""
    global ws_conn
    url = url or WS_URL
    with ws_lock:
        try:
            ws_conn = websocket.create_connection(url, timeout=5)
            ws_conn.settimeout(None)
            log.info(f"Connected to {url}")
        except Exception as e:
            log.error(f"WebSocket connection failed: {e}")
            ws_conn = None


def send_to_mux(text: str) -> bool:
    """Send a message to mux-swarm via WebSocket."""
    global ws_conn
    with ws_lock:
        if ws_conn is None:
            return False
        try:
            ws_conn.send(text)
            return True
        except Exception as e:
            log.error(f"WS send error: {e}")
            ws_conn = None
            return False


# ---------------------------------------------------------------------------
# Discord handlers
# ---------------------------------------------------------------------------

async def _transcribe_attachment(
    message: discord.Message,
    att: discord.Attachment,
    suffix: str,
    label: str,
) -> Optional[str]:
    """Download attachment, transcribe, update status message. Returns transcribed text."""
    if not _ffmpeg_available:
        await message.channel.send("Audio transcription unavailable: ffmpeg not found.")
        return None

    log.info(f"{label} from {message.author}: {att.filename}")
    status_msg: discord.Message = await message.channel.send(f"Transcribing {label.lower()}...")
    tmp_path: Optional[str] = None

    try:
        with tempfile.NamedTemporaryFile(suffix=suffix, delete=False) as f:
            tmp_path = f.name
        await att.save(tmp_path)
        text: str = transcribe_audio(tmp_path)
        if text and not text.startswith("["):
            preview: str = text[:100] + ("..." if len(text) > 100 else "")
            await status_msg.edit(content=f"Transcribed: {preview}")
        else:
            await status_msg.edit(content=text or "[Empty transcription]")
        return text
    finally:
        if tmp_path and os.path.exists(tmp_path):
            try:
                os.unlink(tmp_path)
            except OSError:
                pass


def main() -> None:
    global target_channel, WS_URL, DISCORD_TOKEN, WHISPER_MODEL, _ffmpeg_available

    parser = argparse.ArgumentParser(
        description="Mux-Swarm Discord Bridge (Audio Edition)"
    )
    parser.add_argument("--token", default=DISCORD_TOKEN,
                        help="Discord bot token (or DISCORD_BOT_TOKEN env)")
    parser.add_argument("--channel", default=os.environ.get("DISCORD_CHANNEL_ID"),
                        help="Discord channel ID to listen on (or DISCORD_CHANNEL_ID env)")
    parser.add_argument("--ws", default=WS_URL,
                        help="Mux WebSocket URL (or MUX_WS_URL env)")
    parser.add_argument("--whisper-model", default=WHISPER_MODEL,
                        help="Whisper model: tiny/base/small/medium/large/turbo")
    args = parser.parse_args()

    WS_URL = args.ws
    DISCORD_TOKEN = args.token or DISCORD_TOKEN
    WHISPER_MODEL = args.whisper_model

    if not DISCORD_TOKEN:
        log.error("No Discord token. Set DISCORD_BOT_TOKEN env or use --token")
        sys.exit(1)

    if not args.channel:
        log.error("No channel ID. Set DISCORD_CHANNEL_ID env or use --channel")
        sys.exit(1)

    channel_id: int = int(args.channel)

    _ffmpeg_available = ensure_ffmpeg()
    if not _ffmpeg_available:
        log.warning("Proceeding without audio transcription support")

    log.info(f"Starting Discord bridge (Whisper={WHISPER_MODEL}, ffmpeg={'yes' if _ffmpeg_available else 'no'})")
    log.info(f"WS target: {WS_URL}")
    log.info(f"Channel: {channel_id}")

    # Connect to mux-swarm with retry
    log.info(f"Connecting to mux-swarm at {WS_URL}...")
    for attempt in range(30):
        connect_ws(WS_URL)
        if ws_conn:
            break
        if attempt % 5 == 4:
            log.warning(f"Still trying to connect... ({attempt + 1}/30)")
        time.sleep(1)

    if not ws_conn:
        log.error("Could not connect to mux-swarm after 30s")
        sys.exit(1)

    # Start background threads
    threading.Thread(target=ws_receiver, daemon=True).start()
    threading.Thread(target=flush_timer, daemon=True).start()

    # Discord client
    intents: discord.Intents = discord.Intents.default()
    intents.message_content = True
    client: discord.Client = discord.Client(intents=intents)

    @client.event
    async def on_ready() -> None:
        nonlocal channel_id
        global target_channel
        target_channel = client.get_channel(channel_id)
        if target_channel:
            log.info(f"Discord ready. Listening on #{target_channel.name} ({channel_id})")
            await target_channel.send(
                f"*Mux-Swarm bridge connected (Audio Edition, Whisper={WHISPER_MODEL})*"
            )
        else:
            log.error(f"Channel {channel_id} not found. Check the ID and bot permissions.")

    @client.event
    async def on_message(message: discord.Message) -> None:
        if message.author == client.user:
            return
        if message.channel.id != channel_id:
            return

        text: Optional[str] = None

        try:
            # --- Voice message (IS_VOICE_MESSAGE flag = bit 13) ---
            is_voice: bool = bool(message.flags.value & (1 << 13))

            if is_voice and message.attachments:
                text = await _transcribe_attachment(
                    message, message.attachments[0], ".ogg", "Voice message"
                )

            # --- Audio attachment ---
            elif message.attachments and any(
                a.content_type and a.content_type.startswith("audio/")
                for a in message.attachments
            ):
                att: discord.Attachment = next(
                    a for a in message.attachments
                    if a.content_type and a.content_type.startswith("audio/")
                )
                ext: str = os.path.splitext(att.filename or ".ogg")[1] or ".ogg"
                text = await _transcribe_attachment(
                    message, att, ext, f"Audio '{att.filename}'"
                )

            # --- Video attachment ---
            elif message.attachments and any(
                a.content_type and a.content_type.startswith("video/")
                for a in message.attachments
            ):
                att = next(
                    a for a in message.attachments
                    if a.content_type and a.content_type.startswith("video/")
                )
                ext = os.path.splitext(att.filename or ".mp4")[1] or ".mp4"
                text = await _transcribe_attachment(
                    message, att, ext, f"Video '{att.filename}'"
                )

            # --- Plain text ---
            elif message.content.strip():
                text = message.content.strip()

            # --- /cancel command ---
            if text and text.lower() == "/cancel":
                send_to_mux("__CANCEL__")
                log.info(f"Cancel signal sent (user: {message.author})")
                await message.channel.send("Cancel signal sent.")
                return

            if not text:
                return

            log.info(f"Discord -> Mux: [{message.author}] {text[:80]}...")

            if not send_to_mux(text):
                await message.channel.send("*Mux-Swarm not connected. Retrying...*")
                connect_ws(WS_URL)
                if not send_to_mux(text):
                    await message.channel.send("*Could not reach Mux-Swarm.*")

        except Exception as e:
            log.error(f"Handler error for {message.author}: {e}", exc_info=True)
            try:
                await message.channel.send(f"Bridge error: {e}")
            except Exception:
                pass

    # Graceful shutdown
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

    log.info("Starting Discord client...")
    client.run(DISCORD_TOKEN, log_handler=None)


if __name__ == "__main__":
    main()
