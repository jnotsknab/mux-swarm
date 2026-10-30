"""A fake Mux `/ws` endpoint for bridge tests.

Speaks the same NDJSON-over-WebSocket protocol as `mux --serve`: every frame is one or more JSON lines, the client
sends plain text lines (a user message, an answer to a prompt, or `__CANCEL__`). Tests script what Mux sends and
assert what the bridge sent back. Runs on a background thread so synchronous bridge code can be tested as is.
"""
from __future__ import annotations

import asyncio
import json
import queue
import threading
from typing import Any

import websockets


class FakeMux:
    """Start with `with FakeMux(token=...) as mux:`; connect bridges to `mux.url`."""

    def __init__(self, token: str | None = None, replay: list[dict[str, Any]] | None = None) -> None:
        self.token = token
        self.replay = [json.dumps(e) for e in (replay or [])]
        self.received: "queue.Queue[str]" = queue.Queue()   # lines the bridge sent
        self.connections: list[dict[str, Any]] = []         # path + query + whether auth passed, per connect
        self._loop: asyncio.AbstractEventLoop | None = None
        self._clients: set[Any] = set()
        self._ready = threading.Event()
        self._stop: asyncio.Future | None = None
        self._thread = threading.Thread(target=self._run, daemon=True)
        self.port = 0

    @property
    def url(self) -> str:
        return f"ws://127.0.0.1:{self.port}/ws"

    # --- test API -----------------------------------------------------------------------------------------------
    def send(self, event_type: str, **fields: Any) -> None:
        """Broadcast one event frame to every connected bridge."""
        line = json.dumps({"type": event_type, **fields})
        assert self._loop is not None
        asyncio.run_coroutine_threadsafe(self._broadcast(line), self._loop).result(timeout=5)

    def next_line(self, timeout: float = 5.0) -> str:
        """The next line the bridge sent (raises queue.Empty on timeout)."""
        return self.received.get(timeout=timeout)

    def wait_connected(self, count: int = 1, timeout: float = 5.0) -> None:
        deadline = threading.Event()
        for _ in range(int(timeout * 20)):
            if len(self._clients) >= count:
                return
            deadline.wait(0.05)
        raise TimeoutError(f"expected {count} connection(s), have {len(self._clients)}")

    # --- server ---------------------------------------------------------------------------------------------------
    def __enter__(self) -> "FakeMux":
        self._thread.start()
        if not self._ready.wait(5):
            raise RuntimeError("fake /ws did not start")
        return self

    def __exit__(self, *exc: Any) -> None:
        if self._loop and self._stop and not self._stop.done():
            self._loop.call_soon_threadsafe(self._stop.set_result, None)
        self._thread.join(timeout=5)

    def _run(self) -> None:
        self._loop = asyncio.new_event_loop()
        asyncio.set_event_loop(self._loop)
        self._loop.run_until_complete(self._serve())

    async def _serve(self) -> None:
        self._stop = self._loop.create_future()  # type: ignore[union-attr]
        async with websockets.serve(self._handler, "127.0.0.1", 0, process_request=self._check) as server:
            self.port = server.sockets[0].getsockname()[1]
            self._ready.set()
            await self._stop

    def _check(self, connection: Any, request: Any = None) -> Any:
        # websockets >= 13 passes (connection, request); older versions pass (path, headers).
        path = request.path if request is not None else connection
        headers = request.headers if request is not None else None
        query = path.split("?", 1)[1] if "?" in path else ""
        params = dict(p.split("=", 1) for p in query.split("&") if "=" in p)
        presented = params.get("token")
        if presented is None and headers is not None:
            auth = headers.get("Authorization", "")
            if auth.lower().startswith("bearer "):
                presented = auth[7:].strip()
        ok = self.token is None or presented == self.token
        self.connections.append({"path": path, "params": params, "authorized": ok})
        if not ok:
            if request is not None:
                return connection.respond(401, "Unauthorized\n")
            from http import HTTPStatus
            return HTTPStatus.UNAUTHORIZED, [], b"Unauthorized\n"
        return None

    async def _handler(self, ws: Any, *args: Any) -> None:
        self._clients.add(ws)
        try:
            if self.connections and self.connections[-1]["params"].get("replay") not in ("0", "false"):
                for line in self.replay:
                    await ws.send(line + "\n")
            async for message in ws:
                for line in str(message).splitlines():
                    if line.strip():
                        self.received.put(line.strip())
        finally:
            self._clients.discard(ws)

    async def _broadcast(self, line: str) -> None:
        for ws in list(self._clients):
            try:
                await ws.send(line + "\n")
            except Exception:
                self._clients.discard(ws)
