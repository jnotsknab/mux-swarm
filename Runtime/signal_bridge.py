#!/usr/bin/env python3
"""
Mux-Swarm Signal Bridge (Audio Edition)
Connects Signal (via signal-cli-rest-api Docker container) <-> Mux-Swarm WebSocket runtime.
Supports text, voice, audio, video.
Voice/audio/video are transcribed via OpenAI Whisper (local, no API key).
FFmpeg resolved automatically via static-ffmpeg fallback (cross-platform).
"""

from __future__ import annotations

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

import requests
import websocket

# ---------------------------------------------------------------------------
# Config (defaults, overridden by CLI args)
# ---------------------------------------------------------------------------
SIGNAL_API_URL: str = os.environ.get("SIGNAL_API_URL", "http://127.0.0.1:8080")
SIGNAL_NUMBER: str = os.environ.get("SIGNAL_NUMBER", "")
WS_URL: str = os.environ.get("MUX_WS_URL", "ws://localhost:6723/ws")
WHISPER_MODEL: str = os.environ.get("WHISPER_MODEL", "base")
ALLOWED_NUMBERS: set[str] = set()

logging.basicConfig(
    format="%(asctime)s [%(levelname)s] %(message)s", level=logging.INFO
)
log: logging.Logger = logging.getLogger("signal-bridge")

def ensure_ffmpeg() -> bool:
    if shutil.which("ffmpeg"):
        return True
    try:
        import static_ffmpeg
        static_ffmpeg.add_paths()
        if shutil.which("ffmpeg"):
            return True
    except ImportError:
        pass
    return False

_ffmpeg_available: bool = False

_whisper_model = None
_whisper_lock: threading.Lock = threading.Lock()

def get_whisper():
    global _whisper_model
    if _whisper_model is not None:
        return _whisper_model
    with _whisper_lock:
        if _whisper_model is not None:
            return _whisper_model
        import whisper
        log.info(f"Loading Whisper model '{WHISPER_MODEL}'...")
        _whisper_model = whisper.load_model(WHISPER_MODEL)
        return _whisper_model

def transcribe_audio(file_path: str) -> str:
    if not _ffmpeg_available:
        return "[Transcription unavailable: ffmpeg not found]"
    try:
        model = get_whisper()
        result = model.transcribe(file_path)
        return result.get("text", "").strip()
    except Exception as e:
        log.error(f"Whisper transcription failed: {e}")
        return f"[Transcription error: {e}]"

ws_conn: Optional[websocket.WebSocketApp] = None
ws_lock: threading.Lock = threading.Lock()

stream_buffer: str = ""
buffer_lock: threading.Lock = threading.Lock()
last_chunk_time: float = 0.0
FLUSH_TIMEOUT: float = 3.0
current_chat_id: Optional[str] = None

def chunk_message(text: str, limit: int = 4000) -> list[str]:
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

def _send_signal_sync(recipient: str, text: str) -> None:
    if not SIGNAL_NUMBER:
        return
    chunks: list[str] = chunk_message(text)
    for chunk in chunks:
        try:
            payload = {
                "message": chunk,
                "number": SIGNAL_NUMBER,
                "recipients": [recipient]
            }
            resp = requests.post(f"{SIGNAL_API_URL}/v2/send", json=payload, timeout=15)
            resp.raise_for_status()
        except Exception as e:
            log.error(f"Signal send failed for {recipient}: {e}")

def flush_buffer() -> None:
    global stream_buffer, last_chunk_time
    with buffer_lock:
        text: str = stream_buffer.strip()
        stream_buffer = ""
        last_chunk_time = 0.0
    if not text or not current_chat_id:
        return
    text = re.sub(r"\n{3,}", "\n\n", text)
    _send_signal_sync(current_chat_id, text)

def flush_timer() -> None:
    while True:
        time.sleep(0.5)
        with buffer_lock:
            has_content: bool = bool(stream_buffer.strip())
            elapsed: float = time.time() - last_chunk_time if last_chunk_time > 0 else 0.0
        if has_content and elapsed >= FLUSH_TIMEOUT:
            flush_buffer()

def send_to_mux(message: str) -> None:
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
                chat_id = data.get("chat_id")
                if text and chat_id:
                    _send_signal_sync(str(chat_id), text)

            elif ev_type == "error":
                msg: str = data.get("message", "Unknown error")
                if current_chat_id:
                    _send_signal_sync(current_chat_id, f"Error: {msg[:4000]}")

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

def _is_authorized(number: str) -> bool:
    if not ALLOWED_NUMBERS:
        return True
    return number in ALLOWED_NUMBERS

def on_signal_message(ws, message: str):
    global current_chat_id
    try:
        data = json.loads(message)
        envelope = data.get("envelope", {})
        if not envelope:
            return

        source = envelope.get("source") or envelope.get("sourceNumber")
        if not source:
            return

        if not _is_authorized(source):
            return

        data_msg = envelope.get("dataMessage", {})
        sync_msg = envelope.get("syncMessage", {}).get("sentMessage", {})

        if not data_msg and not sync_msg:
            return

        msg_payload = data_msg if data_msg else sync_msg

        # If it's a sync message (sent from your phone), only process it if it was sent to yourself (Note to Self)
        if sync_msg:
            destination = sync_msg.get("destination") or sync_msg.get("destinationNumber")
            if destination != source:
                return

        text = msg_payload.get("message", "") or ""
        attachments = msg_payload.get("attachments", [])
        
        current_chat_id = source

        # Handle commands
        if text.strip() == "/start":
            audio_status = "available" if _ffmpeg_available else "unavailable (ffmpeg missing)"
            _send_signal_sync(source, f"Mux-Swarm Signal Bridge connected.\nWhisper model: {WHISPER_MODEL}\nAudio transcription: {audio_status}")
            return
        elif text.strip() == "/cancel":
            send_to_mux("__CANCEL__")
            _send_signal_sync(source, "Cancel signal sent.")
            log.info(f"Cancel signal sent (user: {source})")
            return

        # Process attachments (Audio/Video)
        for att in attachments:
            att_id = att.get("id")
            content_type = att.get("contentType", "")
            
            if "audio" in content_type or "video" in content_type:
                if not _ffmpeg_available:
                    _send_signal_sync(source, "Audio transcription unavailable: ffmpeg not found.")
                    continue

                _send_signal_sync(source, "Transcribing media...")
                try:
                    resp = requests.get(f"{SIGNAL_API_URL}/v1/attachments/{att_id}", stream=True)
                    if resp.status_code == 200:
                        ext = ".ogg" if "ogg" in content_type else ".mp4"
                        with tempfile.NamedTemporaryFile(suffix=ext, delete=False) as f:
                            for chunk in resp.iter_content(chunk_size=8192):
                                f.write(chunk)
                            tmp_path = f.name
                        
                        transcription = transcribe_audio(tmp_path)
                        if transcription and not transcription.startswith("["):
                            _send_signal_sync(source, f"Transcribed: {transcription[:100]}...")
                            text = (text + "\n\n" + f"[Transcribed Audio]: {transcription}").strip()
                        else:
                            _send_signal_sync(source, transcription or "[Empty transcription]")
                        
                        os.unlink(tmp_path)
                except Exception as e:
                    log.error(f"Failed to process attachment: {e}")

        if not text:
            return

        send_to_mux(text)
        log.info(f"Forwarded to Mux: [{source}] {text[:80]}...")

    except Exception as e:
        log.error(f"Signal WS message handler error: {e}", exc_info=True)

def start_signal_ws():
    ws_url = SIGNAL_API_URL.replace("http://", "ws://").replace("https://", "wss://")
    ws_url = f"{ws_url}/v1/receive/{SIGNAL_NUMBER}"
    log.info(f"Connecting to Signal API WS: {ws_url}")
    
    def run_signal_ws():
        while True:
            ws = websocket.WebSocketApp(
                ws_url,
                on_message=on_signal_message,
                on_error=lambda w, e: log.error(f"Signal API WS Error: {e}"),
                on_close=lambda w, c, m: log.warning("Signal API WS Closed. Reconnecting in 5s..."),
            )
            ws.run_forever()
            time.sleep(5)

    t = threading.Thread(target=run_signal_ws, daemon=True)
    t.start()

def _parse_numbers(raw: str) -> set[str]:
    nums: set[str] = set()
    for part in raw.split(","):
        part = part.strip()
        if part:
            if part.isdigit():
                part = "+" + part
            nums.add(part)
    return nums

def main() -> None:
    global SIGNAL_API_URL, SIGNAL_NUMBER, WS_URL, WHISPER_MODEL, ALLOWED_NUMBERS
    global _ffmpeg_available

    parser = argparse.ArgumentParser(description="Mux-Swarm Signal Bridge (Audio Edition)")
    parser.add_argument("--number", default=SIGNAL_NUMBER, help="Your registered Signal number")
    parser.add_argument("--api", default=SIGNAL_API_URL, help="Signal-cli-rest-api URL")
    parser.add_argument("--ws", default=WS_URL, help="Mux WebSocket URL")
    parser.add_argument("--whisper-model", default=WHISPER_MODEL, help="Whisper model: tiny/base/small/medium/large/turbo")
    parser.add_argument("--allowed-numbers", default=os.environ.get("ALLOWED_NUMBERS", ""))
    args = parser.parse_args()

    SIGNAL_API_URL = args.api.rstrip("/")
    SIGNAL_NUMBER = args.number
    WS_URL = args.ws
    WHISPER_MODEL = args.whisper_model

    if args.allowed_numbers.strip():
        ALLOWED_NUMBERS = _parse_numbers(args.allowed_numbers)

    if not SIGNAL_NUMBER:
        log.error("No Signal number provided.")
        sys.exit(1)

    _ffmpeg_available = ensure_ffmpeg()
    
    start_ws()
    threading.Thread(target=flush_timer, daemon=True).start()
    start_signal_ws()

    def _shutdown(signum, frame) -> None:
        sys.exit(0)

    signal.signal(signal.SIGINT, _shutdown)
    signal.signal(signal.SIGTERM, _shutdown)

    while True:
        time.sleep(1)

if __name__ == "__main__":
    main()
