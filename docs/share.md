# Session Sharing (`/share`, `/join`)

Share a live session with someone on the same network. Guests see exactly what your TUI draws, in real time.

**Host**
- `/share` asks where to listen: **This machine only** (loopback, the default) or **All interfaces** (same-network
  guests). `/share --local` / `/share --lan` skip the prompt. The join link looks like
  `http://host:port/s/<room>#<secret>`; send it over a channel you trust.
- Every join asks you **Deny / Watch / Watch + type** (Deny is the default; no answer within 2 minutes = Deny).
- `/share status`, `/share control <name|#id|all> on|off`, `/share kick <name|#id>`, `/share stop`.
- **Ctrl+]** takes typing away from every guest instantly, at the prompt or mid-turn. Your own keyboard is never locked.
- Footer: `sharing · N watching · M typing`.

**Guest**
- `/join <link>` inside Mux, or `mux-swarm --join "<link>"` (cold path: no config, provider, or setup needed).
- The guest screen shows the host's frame at the host's size; resize your terminal or font to fit.
- **Ctrl+]** opens the local menu: `q` leave, `r` redraw. Ctrl+] is never sent to the host.

**Guest typing (only when the host grants it)**
- Guest keys are limited to editing keys (text, Enter, Backspace, Delete, Left/Right, Home/End, Tab, Ctrl+A/E/K/U/W).
  Host hotkeys, history, clipboard, NAV, mouse and terminal replies are dropped **on the host**.
- Guest keys go to a separate queue that only the host's idle prompt reads. No modal, picker, tool-permission prompt,
  or join approval ever receives a guest key; anything typed while the agent is working is dropped.
- A paste never submits on its own. Only a lone Enter submits.
- A line a guest typed into may only be a prompt or one of `/help /shortcuts /status /tokens /context /cost /diff
  /compact /retry /undo /redo`. `!shell`, `/exit`, `/share`, `/join`, config/provider/clipboard and all other commands
  are refused with a notice naming the guest, even if the host presses Enter. Accepted guest prompts are marked
  `↳ typed by <name>`.

**Security**
- The link is the credential: the room id routes the request, and the 32-byte secret stays in the URL fragment
  (browsers never send it) and is only used for key derivation. A new link is made on every `/share`; `/share stop`
  invalidates it. The secret is masked in everything sent to guests.
- Handshake: P-256 ECDH + HKDF-SHA256 with HMAC-SHA256 mutual proofs over the full transcript (host proves first);
  fresh keys per session. Channel: AES-256-GCM with per-direction counter nonces; tampered, replayed or reordered frames
  drop the connection.
- The guest filters host output to text, colour and cursor control; OSC (incl. clipboard writes), DCS, terminal queries
  and mode toggles are dropped.
- Limits: 4 guests, 4 pending handshakes, 10 s handshake timeout, per-IP ban after repeated failed proofs; slow guests
  are disconnected rather than stalling the host.
- Same-network only in this release (direct connect; no relay/NAT traversal).
