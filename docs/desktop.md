# Desktop shells (`window.muxHost`)

A desktop shell is a thin native wrapper around the `--serve` web UI: it starts or attaches to the
engine, opens a webview on the serve origin, and implements a small bridge object. The web app does
the rest (titlebar, voice, theme sync). Shells inject no CSS and do no DOM surgery.

The reference cross-platform shell is [`mux-desktop`](https://github.com/jnotsknab/mux-desktop)
(Tauri v2: WebView2 on Windows, WKWebView on macOS, WebKitGTK on Linux).

## 1. Engine lifecycle

1. Attach if a healthy serve already answers `GET /api/health` on the configured port; never stop
   a serve you did not start.
2. Otherwise spawn `MuxSwarm --serve <port>` with stdin closed and no console window, and poll
   `/api/health` until it returns 200.
3. Kill-on-close for a spawned engine only: `POST /api/shutdown`, wait a few seconds, then
   terminate. Windows: put the child in a kill-on-close Job Object. macOS/Linux: start the child in
   its own process group and signal the group of the PID you recorded at spawn. Never infer a group
   id from a PID later, and never kill by process name.

## 2. Loading the UI

Open `http://<serve>/?host=<platform>` (`tauri-win`, `wkwebview-mac`, `webkitgtk-linux`) and define
`window.muxHost` before page scripts run:

| Webview | How to inject at document start |
|---|---|
| Tauri v2 | `WebviewWindowBuilder::initialization_script` |
| WKWebView | `WKUserScript` with `.atDocumentStart`, main frame only |
| WebKitGTK | `webkit_user_content_manager_add_script` with `WEBKIT_USER_SCRIPT_INJECT_AT_DOCUMENT_START` |

`window.muxHost` is the source of truth. `?host=` only sets `data-mux-host` early so the first paint
already reserves titlebar space. A host that defines the bridge late can call `window.mux.attachHost()`.

## 3. The contract

```ts
interface MuxHost {
  platform: "windows" | "macos" | "linux";
  version: string;
  capabilities: Array<"window" | "voice" | "theme" | "notify" | "openExternal">;
  window?: {
    minimize(): Promise<void>;
    toggleMaximize(): Promise<void>;
    close(): Promise<void>;
    startDrag(): Promise<void>;          // used where CSS app-region is ignored (WKWebView, WebKitGTK)
    isMaximized(): Promise<boolean>;
    onMaximizedChange?(cb: (maximized: boolean) => void): void;
    controls: "web" | "native-left" | "native-right";
  };
  voice?: { start(): Promise<void>; stop(): Promise<string> };   // stop() returns the transcript
  theme?: { set(scheme: "light" | "dark", bg: string): void };  // native window background + dark chrome
  notify?(title: string, body: string): void;
  openExternal?(url: string): void;
}
```

What the web app does when the bridge is present:

- Sets `<html data-mux-host="windows|macos|linux">` and `data-mux-controls`.
- **window**: renders `[data-mux="titlebar-slot"]` at the end of the header. `controls: "web"` draws
  minimize / maximize / close there (Windows and Linux frameless windows). `native-left` /
  `native-right` only reserve space (macOS traffic lights with a hidden-inset titlebar). Empty header
  space is a drag region (`app-region: drag`, plus `startDrag()` on mousedown); double-click toggles
  maximize. The header-collapse button moves into the header flow so it never overlaps the controls.
- **voice**: the mic button calls `start()` / `stop()` instead of Web Speech and appends the text to
  the composer (never auto-sends). Whole-line `[...]` / `(...)` annotations such as `[BLANK_AUDIO]`
  are dropped. A shell can capture audio natively and POST it as WAV to `/api/voice/transcribe`,
  so it does not need to bundle a speech engine.
- **theme**: on every `mux:theme-changed` (`detail: { id, scheme, bg }`) the app calls
  `theme.set(scheme, bg)` so the native background matches and resizing never flashes white.
- **openExternal**: off-origin `http(s)` links open through the host.
- Without `window.muxHost` nothing changes: no titlebar slot, and the mic uses Web Speech or server
  transcription.

Voice order in the web app: `muxHost.voice`, then Web Speech (Chromium), then `MediaRecorder` +
`POST /api/voice/transcribe` (secure contexts only: localhost or https), else the mic is hidden with a
tooltip explaining why.

## 4. Per-platform notes

- **Windows (WebView2)**: frameless window, `controls: "web"`. Enable non-client region support so
  CSS `app-region: drag` works; `startDrag()` is the fallback.
- **macOS (WKWebView)**: `titleBarStyle: hiddenInset` / `fullSizeContentView`, `controls: "native-left"`.
  WKWebView ignores `app-region`, so implement `startDrag()` with `performDrag(with:)`.
- **Linux (WebKitGTK)**: frameless or client-side decorations, `controls: "web"`. Implement
  `startDrag()` with `gtk_window_begin_move_drag`. Microphone access needs the PulseAudio/PipeWire
  device and, for Flatpak, the `--device=all` or audio portal permission.

## 5. Security

- Expose the bridge (IPC) to the serve origin only: Tauri capabilities scoped to that URL, one
  `WKScriptMessageHandler` that checks `frameInfo.securityOrigin`, WebKitGTK message handlers that check
  the page URI. Never to off-origin pages or to a remote origin unless the user opted in.
- Extension web content is static CSS, fonts and images; the engine never serves extension JS, so the
  bridge is only reachable from the Mux web app itself.
- Keep `serve.auth` on when the serve is reachable beyond loopback.
