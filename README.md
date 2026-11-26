# Untitled Poker — 2D Pixel-Art Multiplayer Texas Hold'em & Baccarat (C# / .NET 8)

A 2D peer-to-peer multiplayer casino card game built in **C# (.NET 8)** with **Balatro-inspired pixelated graphics**, supporting **up to 10 players** with full rules for **Texas Hold'em** and **Baccarat (Punto Banco)**.

## Features

- **Balatro-Inspired Pixel Art (SDL2 Software Pixel Canvas)**
  - Crisp `640x360` internal pixel resolution scaled with nearest-neighbor filtering to a resizable `1280x720` window.
  - Real-time animated warped-domain swirling backgrounds (Royal Blue/Crimson for Menu, Emerald/Teal for Hold'em, Velvet Crimson/Gold for Baccarat).
  - Toggleable CRT scanline & vignette shader (`F1` or top-right `CRT` button).
  - Custom pixel-art playing cards, 4-color high-contrast suit sprites, ornate card backs, casino chips, and win-celebration pixel confetti.
  - Zero external NuGet dependencies — uses system `SDL2` (`libSDL2-2.0.so.0` on Linux, `SDL2.dll` on Windows, `libSDL2.dylib` on macOS).
- **Peer-to-Peer Hosting (Up to 10 Players)**
  - One player clicks **HOST P2P ROOM** (default TCP port `7777`) and plays directly in the same window.
  - Automatic **LAN Room Discovery** via UDP broadcast on port `7778` — players on the same network see active rooms automatically.
  - Direct **IP:Port Join** for WAN / VPN (Tailscale, ZeroTier, Hamachi, or port-forwarded router).
  - Authoritative host with **per-player sanitized state snapshots** (other players' Hold'em hole cards are masked until Showdown).
  - Built-in **AI Bots**: choose whether bots are enabled and how many (`0–9`) before hosting, or click individual seats in the Lobby (`+ BOT` / `REM`, `-`/`+`/`NONE`/`FILL`) to pick **exactly where** bots sit!
  - **SAM Voice Chat & Balatro Speech Bubbles**: Press **`T`** or **`ENTER`** (or click **`CHAT`**) to type a message — it is spoken aloud using a built-in pure-code **retro 8-bit SAM formant voice synthesizer** (zero LLM) with **adjustable Pitch (`25–130`)**, and pops up in a **wavy Balatro-style speech bubble** right next to the player who said it!
  - **Hide IP (Streamer Mode)**: click `HIDE IP` (or press `F2`) on the Main Menu or Lobby to mask your LAN/Host IP address everywhere on screen.
- **5 Complete Casino & Poker Game Modes (Up to 10 Players)**
  - **Texas Hold'em**: Rotating Dealer (`D`), `SB`/`BB`, heads-up rules, full betting rounds (`Pre-Flop` to `Showdown`), 5-of-7 hand evaluator, and multi-way side pots.
  - **Omaha Hold'em**: 4 hole cards per player with strict **2 hole + 3 board** showdown hand evaluation and full betting/side-pot rules.
  - **Baccarat (Punto Banco)**: 8-deck shoe, exact Natural 8/9 & Third-Card Tableau rules, Player (1:1), Banker (0.95:1), Tie (8:1 + push), Player/Banker Pairs (11:1), and Bead Plate history.
  - **Guts Poker**: Classic 2-card high-stakes IN-or-OUT showdown where losing `IN` players match the pot into the next round's carryover pot.
  - **21 / Blackjack**: 6-deck shoe against the Dealer with `Hit`, `Stand`, `Double Down`, Soft-Ace handling, and 3:2 Natural Blackjack payouts.

## How to Build & Run

### 1. Desktop 2D Pixel-Art Client (P2P Hosting)
```bash
# Build the entire solution
dotnet build

# Run the automated rules & 10-player P2P networking tests
dotnet run --project tests/BalatroPoker.Tests

# Launch the 2D desktop game
dotnet run --project src/BalatroPoker.Game
```

### 2. Standalone Web Server (Play in Browser with Friends)
Anyone can host an independent web server so friends can join from any browser (desktop or mobile) via Room Code or Invite Link — no client download needed:

```bash
# Starts on http://0.0.0.0:5080 by default (override with PORT=8080)
dotnet run --project src/BalatroPoker.Web
```

Then open `http://localhost:5080` (or share `http://<your-lan-ip>:5080` on LAN, or expose port `5080` via Cloudflare Tunnel / Tailscale / ngrok for internet friends).
- Create **Public** or **Private (Room Code)** rooms for up to **10 players**
- Click **COPY INVITE** to share a direct `/?room=CODE` link
- Place bots in specific seats (`+ BOT` / `REM`) or set bot counts (`0–9`), switch between **Texas Hold'em** and **Baccarat**, and toggle **HIDE IP (`F2`)** or **CRT (`F1`)** right in the browser!

## Controls

- **Mouse**: Click buttons, text fields, betting zones, and chip denominations.
- **F1**: Toggle CRT scanline/vignette effect.
- **F2**: Toggle **Hide IP** mode (masks your P2P host/join IP on screen).
- **ESC**: Unfocus text input.

## Star History

<a href="https://www.star-history.com/?repos=imonpsychedelics%2Funtitled-poker&type=date&legend=top-left">
 <picture>
   <source media="(prefers-color-scheme: dark)" srcset="https://api.star-history.com/chart?repos=imonpsychedelics/untitled-poker&type=date&theme=dark&legend=top-left" />
   <source media="(prefers-color-scheme: light)" srcset="https://api.star-history.com/chart?repos=imonpsychedelics/untitled-poker&type=date&legend=top-left" />
   <img alt="Star History Chart" src="https://api.star-history.com/chart?repos=imonpsychedelics/untitled-poker&type=date&legend=top-left" />
 </picture>
</a>
