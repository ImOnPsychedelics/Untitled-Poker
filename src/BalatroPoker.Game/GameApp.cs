using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using BalatroPoker.Core.Audio;
using BalatroPoker.Core.Cards;
using BalatroPoker.Core.Models;
using BalatroPoker.Core.Networking;
using BalatroPoker.Game.Graphics;

namespace BalatroPoker.Game;

public sealed class GameApp : IDisposable
{
    private enum ScreenState
    {
        MainMenu,
        Connected
    }

    private enum InputField
    {
        None,
        PlayerName,
        JoinIp,
        Port,
        Chat
    }

    private readonly PixelCanvas _canvas = new();
    private readonly ParticleSystem _particles = new();
    private readonly LanDiscoveryService _lanDiscovery = new();

    private IntPtr _window;
    private IntPtr _renderer;
    private IntPtr _texture;

    private ScreenState _screen = ScreenState.MainMenu;
    private GameRoomHost? _host;
    private GameRoomClient? _client;

    // Menu inputs
    private string _playerName = "ACE";
    private string _joinIp = "127.0.0.1";
    private string _portText = PacketFraming.DefaultGamePort.ToString();
    private InputField _focusedField = InputField.None;
    private string _menuStatus = "READY TO DEAL - HOST OR JOIN A P2P TABLE";
    private string _localLanIp = "127.0.0.1";
    private bool _hostBotsEnabled = true;
    private int _hostBotCount = 3;
    private bool _hideIp = false;

    // Input state
    private int _mouseX, _mouseY;
    private bool _mouseDown;
    private bool _mouseJustPressed;
    private bool _mouseJustReleased;
    private bool _crtEnabled = true;

    // Gameplay UI state
    private int _holdemRaiseTo = 40;
    private int _selectedChip = 25;
    private int _lastCelebratedHand = -1;
    private int _lastCelebratedBaccaratRound = -1;
    private int _lastCelebratedGutsRound = -1;
    private int _lastCelebratedBjRound = -1;

    // SAM Voice Chat state
    private uint _audioDevice;
    private bool _samVoiceEnabled = true;
    private int _samPitch = SamVoiceSynth.DefaultPitch;
    private string _chatDraft = string.Empty;
    private bool _chatBarOpen = false;
    private long _lastSpokenChatId = 0;

    public void Run()
    {
        _localLanIp = DetectLocalIp();

        if (Sdl2.SDL_Init(Sdl2.SDL_INIT_VIDEO) != 0)
        {
            Console.Error.WriteLine($"SDL_Init failed: {Sdl2.GetErrorString()}");
            return;
        }

        Sdl2.SDL_SetHint("SDL_RENDER_SCALE_QUALITY", "0");

        _window = Sdl2.SDL_CreateWindow(
            "UNTITLED POKER - Texas Hold'em & Baccarat (P2P 10-Player)",
            Sdl2.SDL_WINDOWPOS_CENTERED,
            Sdl2.SDL_WINDOWPOS_CENTERED,
            1280,
            720,
            Sdl2.SDL_WINDOW_SHOWN | Sdl2.SDL_WINDOW_RESIZABLE);

        if (_window == IntPtr.Zero)
        {
            Console.Error.WriteLine($"SDL_CreateWindow failed: {Sdl2.GetErrorString()}");
            Sdl2.SDL_Quit();
            return;
        }

        _renderer = Sdl2.SDL_CreateRenderer(_window, -1, Sdl2.SDL_RENDERER_ACCELERATED | Sdl2.SDL_RENDERER_PRESENTVSYNC);
        if (_renderer == IntPtr.Zero)
            _renderer = Sdl2.SDL_CreateRenderer(_window, -1, 0);

        Sdl2.SDL_RenderSetLogicalSize(_renderer, PixelCanvas.Width, PixelCanvas.Height);

        _texture = Sdl2.SDL_CreateTexture(
            _renderer,
            Sdl2.SDL_PIXELFORMAT_ARGB8888,
            Sdl2.SDL_TEXTUREACCESS_STREAMING,
            PixelCanvas.Width,
            PixelCanvas.Height);

        Sdl2.SDL_StartTextInput();
        InitSamAudio();
        _lanDiscovery.StartListening();

        bool running = true;
        ulong lastTicks = Sdl2.SDL_GetTicks64();
        float timeSec = 0f;

        while (running)
        {
            ulong now = Sdl2.SDL_GetTicks64();
            float dt = Math.Clamp((now - lastTicks) / 1000f, 0.001f, 0.1f);
            lastTicks = now;
            timeSec += dt;

            _mouseJustPressed = false;
            _mouseJustReleased = false;

            while (Sdl2.SDL_PollEvent(out var ev) != 0)
            {
                switch (ev.type)
                {
                    case Sdl2.SDL_QUIT:
                        running = false;
                        break;
                    case Sdl2.SDL_MOUSEMOTION:
                        _mouseX = ev.motion.x;
                        _mouseY = ev.motion.y;
                        break;
                    case Sdl2.SDL_MOUSEBUTTONDOWN:
                        if (ev.button.button == 1)
                        {
                            _mouseX = ev.button.x;
                            _mouseY = ev.button.y;
                            _mouseDown = true;
                            _mouseJustPressed = true;
                        }
                        break;
                    case Sdl2.SDL_MOUSEBUTTONUP:
                        if (ev.button.button == 1)
                        {
                            _mouseX = ev.button.x;
                            _mouseY = ev.button.y;
                            _mouseDown = false;
                            _mouseJustReleased = true;
                        }
                        break;
                    case Sdl2.SDL_KEYDOWN:
                        HandleKeyDown(ev.key.keysym.sym);
                        break;
                    case Sdl2.SDL_TEXTINPUT:
                        HandleTextInput(ev.text);
                        break;
                }
            }

            _host?.TickBots();

            RenderFrame(timeSec, dt);
            PresentCanvas();

            Sdl2.SDL_Delay(1);
        }
    }

    private void RenderFrame(float timeSec, float dt)
    {
        if (_screen == ScreenState.MainMenu)
        {
            RenderMainMenu(timeSec);
        }
        else
        {
            TableSnapshot? snap = GetCurrentSnapshot();
            if (snap is null)
            {
                _canvas.DrawBalatroSwirlBackground(timeSec, SwirlTheme.MenuRoyal);
                PixelFont.DrawTextCentered(_canvas, "CONNECTING TO HOST...", PixelCanvas.Width / 2, 170, BalatroUi.ColGold, 2);
                if (BalatroUi.DrawButton(_canvas, new UiRect(270, 210, 100, 24), "CANCEL", BalatroUi.ColRed, _mouseX, _mouseY, _mouseDown, _mouseJustReleased))
                    DisconnectToMenu();
            }
            else
            {
                CheckAndSpeakNewChats(snap);

                if (snap.Phase == RoomPhase.Lobby)
                {
                    RenderLobby(snap, timeSec);
                }
                else if (snap.Config.Mode is GameMode.TexasHoldem or GameMode.OmahaHoldem)
                {
                    RenderHoldemTable(snap, timeSec);
                }
                else if (snap.Config.Mode == GameMode.Baccarat)
                {
                    RenderBaccaratTable(snap, timeSec);
                }
                else if (snap.Config.Mode == GameMode.GutsPoker)
                {
                    RenderGutsTable(snap, timeSec);
                }
                else
                {
                    RenderBlackjackTable(snap, timeSec);
                }

                DrawChatOverlayIfOpen(timeSec);
            }
        }

        _particles.UpdateAndDraw(_canvas, dt);

        if (_crtEnabled)
            _canvas.ApplyCrtEffect();
    }

    private void RenderMainMenu(float timeSec)
    {
        _canvas.DrawBalatroSwirlBackground(timeSec, SwirlTheme.MenuRoyal);

        // Decorative floating pixel cards behind title
        Card[] deco =
        {
            new(Rank.Ace, Suit.Spades),
            new(Rank.King, Suit.Hearts),
            new(Rank.Queen, Suit.Diamonds),
            new(Rank.Jack, Suit.Clubs),
            new(Rank.Ten, Suit.Spades)
        };
        for (int i = 0; i < deco.Length; i++)
        {
            int cx = 195 + i * 52;
            CardSprites.DrawCard(_canvas, deco[i], cx, 16, compact: false, highlightGold: i == 0, bobPhase: timeSec * 2.4f + i * 0.7f);
        }

        // Title banner
        PixelFont.DrawTextCentered(_canvas, "UNTITLED POKER", PixelCanvas.Width / 2, 82, BalatroUi.ColGold, 3);
        PixelFont.DrawTextCentered(_canvas, "TEXAS HOLD'EM & BACCARAT  //  P2P UP TO 10 PLAYERS", PixelCanvas.Width / 2, 108, BalatroUi.ColWhite, 1);

        // Left panel: Host / Join Form
        _canvas.DrawBalatroPanel(28, 126, 300, 204, BalatroUi.ColPanel, BalatroUi.ColPanelBorder, PixelCanvas.Rgba(76, 86, 114));

        if (BalatroUi.DrawTextInput(_canvas, new UiRect(42, 144, 175, 20), "PLAYER NAME", _playerName, _focusedField == InputField.PlayerName, _mouseX, _mouseY, _mouseJustPressed, timeSec))
            _focusedField = InputField.PlayerName;

        if (BalatroUi.DrawTextInput(_canvas, new UiRect(228, 144, 86, 20), "PORT", _portText, _focusedField == InputField.Port, _mouseX, _mouseY, _mouseJustPressed, timeSec))
            _focusedField = InputField.Port;

        string joinIpDisplay = _hideIp && _focusedField != InputField.JoinIp
            ? new string('*', Math.Max(7, _joinIp.Length))
            : _joinIp;
        if (BalatroUi.DrawTextInput(_canvas, new UiRect(42, 180, 272, 20), "HOST IP ADDRESS (FOR JOIN)", joinIpDisplay, _focusedField == InputField.JoinIp, _mouseX, _mouseY, _mouseJustPressed, timeSec))
            _focusedField = InputField.JoinIp;

        // Pre-host Bot Setup Row
        if (BalatroUi.DrawButton(_canvas, new UiRect(42, 208, 96, 22), _hostBotsEnabled ? "BOTS: ON" : "BOTS: OFF", _hostBotsEnabled ? BalatroUi.ColPurple : PixelCanvas.Rgba(52, 58, 76), _mouseX, _mouseY, _mouseDown, _mouseJustReleased))
        {
            _hostBotsEnabled = !_hostBotsEnabled;
            if (_hostBotsEnabled && _hostBotCount == 0) _hostBotCount = 1;
        }

        if (BalatroUi.DrawButton(_canvas, new UiRect(144, 208, 28, 22), "-", BalatroUi.ColBgDark, _mouseX, _mouseY, _mouseDown, _mouseJustReleased, enabled: _hostBotsEnabled && _hostBotCount > 0))
            _hostBotCount = Math.Max(0, _hostBotCount - 1);

        int effectiveBots = _hostBotsEnabled ? _hostBotCount : 0;
        PixelFont.DrawTextCentered(_canvas, $"COUNT: {effectiveBots}", 228, 215, _hostBotsEnabled ? BalatroUi.ColGold : BalatroUi.ColMuted, 1);

        if (BalatroUi.DrawButton(_canvas, new UiRect(286, 208, 28, 22), "+", BalatroUi.ColBgDark, _mouseX, _mouseY, _mouseDown, _mouseJustReleased, enabled: _hostBotsEnabled && _hostBotCount < 9))
            _hostBotCount = Math.Min(9, _hostBotCount + 1);

        if (BalatroUi.DrawButton(_canvas, new UiRect(42, 238, 130, 30), "HOST P2P ROOM", BalatroUi.ColGreen, _mouseX, _mouseY, _mouseDown, _mouseJustReleased))
        {
            StartHosting(effectiveBots);
        }

        if (BalatroUi.DrawButton(_canvas, new UiRect(184, 238, 130, 30), "JOIN BY IP", BalatroUi.ColBlue, _mouseX, _mouseY, _mouseDown, _mouseJustReleased))
        {
            JoinRoom(_joinIp, ParsePort());
        }

        if (BalatroUi.DrawButton(_canvas, new UiRect(42, 274, 272, 24), $"QUICK SOLO + {Math.Max(1, effectiveBots)} BOTS (HOLD'EM)", BalatroUi.ColOrange, _mouseX, _mouseY, _mouseDown, _mouseJustReleased))
        {
            StartHosting(Math.Max(1, effectiveBots));
            _host?.StartGame();
        }

        string shownIp = _hideIp ? "***.***.***.***" : _localLanIp;
        PixelFont.DrawText(_canvas, $"YOUR LAN IP: {shownIp}", 42, 308, BalatroUi.ColMuted, 1);
        if (BalatroUi.DrawButton(_canvas, new UiRect(232, 303, 82, 18), _hideIp ? "SHOW IP" : "HIDE IP", _hideIp ? BalatroUi.ColRed : PixelCanvas.Rgba(52, 58, 76), _mouseX, _mouseY, _mouseDown, _mouseJustReleased))
            _hideIp = !_hideIp;

        // Right panel: LAN Discovered Rooms
        _canvas.DrawBalatroPanel(344, 126, 268, 198, BalatroUi.ColPanel, BalatroUi.ColPanelBorder, PixelCanvas.Rgba(76, 86, 114));
        PixelFont.DrawText(_canvas, "LAN ROOMS (AUTO-DISCOVERY)", 356, 136, BalatroUi.ColGold, 1);

        var rooms = _lanDiscovery.GetRooms();
        if (rooms.Count == 0)
        {
            PixelFont.DrawText(_canvas, "SCANNING LAN ON UDP 7778...", 356, 162, BalatroUi.ColMuted, 1);
            PixelFont.DrawText(_canvas, "FRIENDS ON YOUR NETWORK WILL", 356, 182, BalatroUi.ColMuted, 1);
            PixelFont.DrawText(_canvas, "APPEAR HERE AUTOMATICALLY!", 356, 194, BalatroUi.ColMuted, 1);
            PixelFont.DrawText(_canvas, "OR ENTER HOST IP ON THE LEFT.", 356, 214, BalatroUi.ColMuted, 1);
        }
        else
        {
            int ry = 154;
            foreach (var r in rooms.Take(5))
            {
                string modeTag = r.Mode == GameMode.TexasHoldem ? "HOLDEM" : "BACC";
                string label = $"{r.RoomName} [{modeTag}] {r.PlayerCount}/{r.MaxPlayers}";
                if (label.Length > 34) label = label[..34];
                if (BalatroUi.DrawButton(_canvas, new UiRect(356, ry, 244, 26), label, BalatroUi.ColPurple, _mouseX, _mouseY, _mouseDown, _mouseJustReleased))
                {
                    JoinRoom(r.HostIp, r.Port);
                }
                ry += 32;
            }
        }

        // Footer status bar
        _canvas.FillRect(0, 336, PixelCanvas.Width, 24, PixelCanvas.Rgba(14, 16, 24, 215));
        PixelFont.DrawText(_canvas, _menuStatus, 12, 344, BalatroUi.ColWhite, 1);
        DrawTopRightCrtButton();
    }

    private void RenderLobby(TableSnapshot snap, float timeSec)
    {
        _canvas.DrawBalatroSwirlBackground(timeSec, SwirlTheme.MenuRoyal);

        // Header
        _canvas.FillRect(0, 0, PixelCanvas.Width, 30, PixelCanvas.Rgba(16, 18, 28, 225));
        PixelFont.DrawText(_canvas, $"LOBBY: {snap.Config.RoomName}", 12, 10, BalatroUi.ColGold, 1);
        string hostIpDisplay = _hideIp ? "[IP HIDDEN]" : _localLanIp;
        string hostInfo = _host is not null ? $"HOSTING {hostIpDisplay}:{_host.Port}" : "CONNECTED AS PEER";
        PixelFont.DrawText(_canvas, hostInfo, 210, 10, _hideIp ? BalatroUi.ColOrange : BalatroUi.ColGreen, 1);

        if (BalatroUi.DrawButton(_canvas, new UiRect(382, 4, 50, 22), "CHAT", _chatBarOpen ? BalatroUi.ColGold : BalatroUi.ColPurple, _mouseX, _mouseY, _mouseDown, _mouseJustReleased))
        {
            _chatBarOpen = !_chatBarOpen;
            _focusedField = _chatBarOpen ? InputField.Chat : InputField.None;
        }

        if (_host is not null)
        {
            if (BalatroUi.DrawButton(_canvas, new UiRect(436, 4, 70, 22), _hideIp ? "SHOW IP" : "HIDE IP", _hideIp ? BalatroUi.ColRed : PixelCanvas.Rgba(52, 58, 76), _mouseX, _mouseY, _mouseDown, _mouseJustReleased))
                _hideIp = !_hideIp;
        }

        if (BalatroUi.DrawButton(_canvas, new UiRect(512, 4, 62, 22), "LEAVE", BalatroUi.ColRed, _mouseX, _mouseY, _mouseDown, _mouseJustReleased))
        {
            DisconnectToMenu();
            return;
        }
        DrawTopRightCrtButton();

        // 10-Seat Roster Grid (2 columns x 5 rows) — Host can click any seat to place/remove a bot!
        _canvas.DrawBalatroPanel(16, 40, 384, 252, BalatroUi.ColPanel, BalatroUi.ColPanelBorder, PixelCanvas.Rgba(76, 86, 114));
        bool isHost = _host is not null;
        string rosterTitle = isHost
            ? $"PLAYERS ({snap.Seats.Count}/{snap.Config.MaxPlayers}) - CLICK SEATS FOR BOTS"
            : $"PLAYERS ({snap.Seats.Count}/{snap.Config.MaxPlayers})";
        PixelFont.DrawText(_canvas, rosterTitle, 28, 50, BalatroUi.ColGold, 1);

        for (int i = 0; i < 10; i++)
        {
            int col = i % 2;
            int row = i / 2;
            int sx = 28 + col * 184;
            int sy = 68 + row * 42;
            var seat = snap.Seats.FirstOrDefault(s => s.SeatIndex == i);

            uint fill = seat is not null ? PixelCanvas.Rgba(44, 54, 74) : PixelCanvas.Rgba(26, 30, 42);
            uint border = seat?.PlayerId == snap.ViewerPlayerId ? BalatroUi.ColGold : BalatroUi.ColPanelBorder;
            _canvas.DrawBalatroPanel(sx, sy, 174, 34, fill, border);

            if (seat is not null)
            {
                string tag = seat.IsHost ? " [H]" : seat.IsBot ? " [BOT]" : "";
                string displayName = seat.Name.Length > 10 ? seat.Name[..10] : seat.Name;
                PixelFont.DrawText(_canvas, $"#{i + 1} {displayName}{tag}", sx + 6, sy + 6, BalatroUi.ColWhite, 1);
                PixelFont.DrawText(_canvas, $"${seat.Chips}", sx + 6, sy + 19, BalatroUi.ColGold, 1);

                if (isHost && seat.IsBot)
                {
                    if (BalatroUi.DrawButton(_canvas, new UiRect(sx + 126, sy + 7, 42, 20), "REM", BalatroUi.ColRed, _mouseX, _mouseY, _mouseDown, _mouseJustReleased))
                        _host!.RemoveBotAtSeat(i);
                }
                DrawSeatChatBubbleIfActive(seat, sx + 87, sy - 4, timeSec);
            }
            else
            {
                PixelFont.DrawText(_canvas, $"SEAT #{i + 1} OPEN", sx + 6, sy + 13, BalatroUi.ColMuted, 1);
                if (isHost)
                {
                    if (BalatroUi.DrawButton(_canvas, new UiRect(sx + 116, sy + 7, 52, 20), "+ BOT", BalatroUi.ColPurple, _mouseX, _mouseY, _mouseDown, _mouseJustReleased))
                        _host!.AddBotAtSeat(i);
                }
            }
        }

        // Right panel: Game Setup & Controls
        _canvas.DrawBalatroPanel(412, 40, 212, 252, BalatroUi.ColPanel, BalatroUi.ColPanelBorder, PixelCanvas.Rgba(76, 86, 114));
        PixelFont.DrawText(_canvas, "GAME SETTINGS", 424, 50, BalatroUi.ColGold, 1);

        var mode = snap.Config.Mode;
        bool needsTwo = mode is GameMode.TexasHoldem or GameMode.OmahaHoldem or GameMode.GutsPoker;

        PixelFont.DrawText(_canvas, "MODE:", 424, 65, BalatroUi.ColMuted, 1);
        if (BalatroUi.DrawButton(_canvas, new UiRect(424, 75, 60, 20), "HOLD'EM", mode == GameMode.TexasHoldem ? BalatroUi.ColGreen : PixelCanvas.Rgba(52, 58, 76), _mouseX, _mouseY, _mouseDown, _mouseJustReleased, enabled: isHost))
            _host?.SwitchGameMode(GameMode.TexasHoldem);
        if (BalatroUi.DrawButton(_canvas, new UiRect(488, 75, 60, 20), "OMAHA", mode == GameMode.OmahaHoldem ? BalatroUi.ColGreen : PixelCanvas.Rgba(52, 58, 76), _mouseX, _mouseY, _mouseDown, _mouseJustReleased, enabled: isHost))
            _host?.SwitchGameMode(GameMode.OmahaHoldem);
        if (BalatroUi.DrawButton(_canvas, new UiRect(552, 75, 60, 20), "BACCARAT", mode == GameMode.Baccarat ? BalatroUi.ColRed : PixelCanvas.Rgba(52, 58, 76), _mouseX, _mouseY, _mouseDown, _mouseJustReleased, enabled: isHost))
            _host?.SwitchGameMode(GameMode.Baccarat);
        if (BalatroUi.DrawButton(_canvas, new UiRect(424, 99, 92, 20), "GUTS POKER", mode == GameMode.GutsPoker ? BalatroUi.ColOrange : PixelCanvas.Rgba(52, 58, 76), _mouseX, _mouseY, _mouseDown, _mouseJustReleased, enabled: isHost))
            _host?.SwitchGameMode(GameMode.GutsPoker);
        if (BalatroUi.DrawButton(_canvas, new UiRect(520, 99, 92, 20), "21 BLACKJACK", mode == GameMode.Blackjack ? BalatroUi.ColBlue : PixelCanvas.Rgba(52, 58, 76), _mouseX, _mouseY, _mouseDown, _mouseJustReleased, enabled: isHost))
            _host?.SwitchGameMode(GameMode.Blackjack);

        PixelFont.DrawText(_canvas, $"BLINDS/ANTE: ${snap.Config.SmallBlind}/${snap.Config.BigBlind}", 424, 126, BalatroUi.ColWhite, 1);
        if (isHost)
        {
            if (BalatroUi.DrawButton(_canvas, new UiRect(424, 138, 58, 20), "10/20", BalatroUi.ColBlue, _mouseX, _mouseY, _mouseDown, _mouseJustReleased))
                _host!.SetBlinds(10, 20);
            if (BalatroUi.DrawButton(_canvas, new UiRect(488, 138, 58, 20), "25/50", BalatroUi.ColBlue, _mouseX, _mouseY, _mouseDown, _mouseJustReleased))
                _host!.SetBlinds(25, 50);
            if (BalatroUi.DrawButton(_canvas, new UiRect(552, 138, 60, 20), "50/100", BalatroUi.ColBlue, _mouseX, _mouseY, _mouseDown, _mouseJustReleased))
                _host!.SetBlinds(50, 100);

            int botCount = snap.Seats.Count(s => s.IsBot);
            int maxBotsPossible = snap.Config.MaxPlayers - snap.Seats.Count(s => !s.IsBot);
            PixelFont.DrawText(_canvas, $"AI BOTS: {botCount}/{maxBotsPossible}", 424, 166, BalatroUi.ColGold, 1);
            if (BalatroUi.DrawButton(_canvas, new UiRect(424, 178, 42, 22), "-", BalatroUi.ColOrange, _mouseX, _mouseY, _mouseDown, _mouseJustReleased, enabled: botCount > 0))
                _host!.SetBotCount(botCount - 1);
            if (BalatroUi.DrawButton(_canvas, new UiRect(470, 178, 42, 22), "+", BalatroUi.ColPurple, _mouseX, _mouseY, _mouseDown, _mouseJustReleased, enabled: snap.Seats.Count < snap.Config.MaxPlayers))
                _host!.SetBotCount(botCount + 1);
            if (BalatroUi.DrawButton(_canvas, new UiRect(516, 178, 46, 22), "NONE", BalatroUi.ColRed, _mouseX, _mouseY, _mouseDown, _mouseJustReleased, enabled: botCount > 0))
                _host!.SetBotCount(0);
            if (BalatroUi.DrawButton(_canvas, new UiRect(566, 178, 46, 22), "FILL", BalatroUi.ColBlue, _mouseX, _mouseY, _mouseDown, _mouseJustReleased, enabled: snap.Seats.Count < snap.Config.MaxPlayers))
                _host!.SetBotCount(maxBotsPossible);

            PixelFont.DrawText(_canvas, "TIP: CLICK ANY SEAT TO", 424, 208, BalatroUi.ColMuted, 1);
            PixelFont.DrawText(_canvas, "PLACE OR REMOVE A BOT!", 424, 219, BalatroUi.ColMuted, 1);

            bool canStart = !needsTwo || snap.Seats.Count >= 2;
            if (BalatroUi.DrawButton(_canvas, new UiRect(424, 244, 188, 36), "DEAL / START", BalatroUi.ColGreen, _mouseX, _mouseY, _mouseDown, _mouseJustReleased, enabled: canStart, textScale: 2))
                _host!.StartGame();
        }
        else
        {
            PixelFont.DrawText(_canvas, "WAITING FOR HOST", 424, 175, BalatroUi.ColGold, 1);
            PixelFont.DrawText(_canvas, "TO START THE TABLE...", 424, 190, BalatroUi.ColMuted, 1);
        }

        // Action log footer
        DrawActionLogStrip(snap, 16, 300, 608, 52);
    }

    private void RenderHoldemTable(TableSnapshot snap, float timeSec)
    {
        _canvas.DrawBalatroSwirlBackground(timeSec, SwirlTheme.HoldemEmerald);

        // Celebrate showdown winners once per hand
        if (snap.Holdem.Stage == HoldemStage.Showdown && snap.Holdem.HandNumber != _lastCelebratedHand)
        {
            _lastCelebratedHand = snap.Holdem.HandNumber;
            _particles.SpawnWinBurst(PixelCanvas.Width / 2, 145, 45);
        }

        // Top HUD bar
        string modeTitle = snap.Config.Mode == GameMode.OmahaHoldem ? "OMAHA HOLD'EM (2+3)" : "TEXAS HOLD'EM";
        DrawTableTopBar(snap, $"{modeTitle}  HAND #{snap.Holdem.HandNumber}  ${snap.Config.SmallBlind}/${snap.Config.BigBlind}");

        // Pixel-art oval poker table felt with gold/mahogany rail
        _canvas.FillEllipse(320, 158, 228, 92, PixelCanvas.Rgba(78, 36, 24));
        _canvas.FillEllipse(320, 158, 222, 86, PixelCanvas.Rgba(198, 152, 52));
        _canvas.FillEllipse(320, 158, 218, 82, PixelCanvas.Rgba(24, 96, 66));
        _canvas.FillEllipse(320, 158, 206, 72, PixelCanvas.Rgba(28, 114, 78));

        // Pot & Banner in center
        _canvas.DrawBalatroPanel(225, 86, 190, 22, PixelCanvas.Rgba(18, 24, 34), BalatroUi.ColGold);
        string potLabel = snap.Holdem.SidePots.Count > 1
            ? $"TOTAL POT: ${snap.Holdem.TotalPot} ({snap.Holdem.SidePots.Count} POTS)"
            : $"POT: ${snap.Holdem.TotalPot}";
        PixelFont.DrawTextCentered(_canvas, potLabel, 320, 93, BalatroUi.ColGold, 1);

        // Community cards (5 slots)
        int startX = 320 - (5 * 44 - 4) / 2;
        int cardY = 114;
        for (int i = 0; i < 5; i++)
        {
            int cx = startX + i * 44;
            if (i < snap.Holdem.CommunityCards.Count)
            {
                bool winGlow = snap.Holdem.Stage == HoldemStage.Showdown;
                CardSprites.DrawCard(_canvas, snap.Holdem.CommunityCards[i], cx, cardY, compact: false, highlightGold: winGlow, bobPhase: timeSec * 2f + i * 0.5f);
            }
            else
            {
                _canvas.DrawRect(cx, cardY, 40, 56, PixelCanvas.Rgba(44, 142, 98, 150), 1);
            }
        }

        // Banner message under community cards
        if (!string.IsNullOrEmpty(snap.Holdem.BannerMessage))
        {
            string msg = snap.Holdem.BannerMessage.Length > 52 ? snap.Holdem.BannerMessage[..52] : snap.Holdem.BannerMessage;
            PixelFont.DrawTextCentered(_canvas, msg, 320, 178, BalatroUi.ColWhite, 1);
        }

        // 10 seat positions around the oval (slot 0 = bottom center = local viewer)
        (int X, int Y)[] seatCoords =
        {
            (320, 244), // 0: Bottom center (Local player)
            (168, 242), // 1: Bottom-left
            (66,  196), // 2: Mid-left low
            (66,  118), // 3: Mid-left high
            (156, 62),  // 4: Top-left
            (266, 52),  // 5: Top-center-left
            (374, 52),  // 6: Top-center-right
            (484, 62),  // 7: Top-right
            (574, 118), // 8: Mid-right high
            (574, 196), // 9: Mid-right low
        };

        int viewerSeat = snap.ViewerSeatIndex >= 0 ? snap.ViewerSeatIndex : 0;

        foreach (var seat in snap.Seats)
        {
            int visualSlot = (seat.SeatIndex - viewerSeat + 10) % 10;
            var (sx, sy) = seatCoords[visualSlot];
            bool isTurn = snap.Holdem.CurrentTurnSeat == seat.SeatIndex;
            bool isWinner = snap.Holdem.WinnerSeats.Contains(seat.SeatIndex);
            bool isLocal = seat.PlayerId == snap.ViewerPlayerId;

            if (isLocal)
            {
                DrawLocalHoldemSeat(seat, snap, sx, sy, isTurn, isWinner, timeSec);
            }
            else
            {
                DrawOpponentHoldemSeat(seat, snap, sx, sy, isTurn, isWinner);
            }
            DrawSeatChatBubbleIfActive(seat, sx, isLocal ? sy - 54 : sy - 24, timeSec);
        }

        // Bottom Action Bar
        DrawHoldemActionBar(snap);
    }

    private void DrawLocalHoldemSeat(SeatSnapshot seat, TableSnapshot snap, int cx, int cy, bool isTurn, bool isWinner, float timeSec)
    {
        // Local player's large hole cards (2 for Texas Hold'em, 4 fanned for Omaha Hold'em)
        int count = seat.HoleCards.Count;
        int spacing = count > 2 ? 32 : 46;
        int totalW = count > 0 ? (count - 1) * spacing + 40 : 0;
        int startX = cx - totalW / 2;
        for (int i = 0; i < count; i++)
        {
            CardSprites.DrawCard(_canvas, seat.HoleCards[i], startX + i * spacing, cy - 46, compact: false, highlightGold: isWinner || isTurn, bobPhase: timeSec * 2.5f + i);
        }

        // Nameplate below cards
        uint border = isWinner ? BalatroUi.ColGold : isTurn ? BalatroUi.ColGreen : BalatroUi.ColPanelBorder;
        uint fill = seat.Folded ? PixelCanvas.Rgba(40, 36, 42) : BalatroUi.ColPanel;
        _canvas.DrawBalatroPanel(cx - 64, cy + 12, 128, 32, fill, border);

        PixelFont.DrawTextCentered(_canvas, $"{seat.Name}  ${seat.Chips}", cx, cy + 17, BalatroUi.ColGold, 1);
        string sub = !string.IsNullOrEmpty(seat.ShowdownHandName)
            ? seat.ShowdownHandName
            : !string.IsNullOrEmpty(seat.LastAction)
                ? seat.LastAction
                : seat.SittingOut ? "SITTING OUT" : "READY";
        if (sub.Length > 20) sub = sub[..20];
        PixelFont.DrawTextCentered(_canvas, sub, cx, cy + 30, seat.Folded ? BalatroUi.ColMuted : BalatroUi.ColWhite, 1);

        DrawDealerBlindBadges(seat, snap, cx + 68, cy + 14);
        if (seat.CurrentBet > 0)
        {
            CardSprites.DrawChip(_canvas, cx - 74, cy + 26, 9, BalatroUi.ColBlue);
            PixelFont.DrawText(_canvas, $"${seat.CurrentBet}", cx - 108, cy + 23, BalatroUi.ColWhite, 1);
        }
    }

    private void DrawOpponentHoldemSeat(SeatSnapshot seat, TableSnapshot snap, int cx, int cy, bool isTurn, bool isWinner)
    {
        int pw = 104, ph = 34;
        uint border = isWinner ? BalatroUi.ColGold : isTurn ? BalatroUi.ColGreen : BalatroUi.ColPanelBorder;
        uint fill = seat.Folded ? PixelCanvas.Rgba(34, 32, 40) : BalatroUi.ColPanel;
        _canvas.DrawBalatroPanel(cx - pw / 2, cy - ph / 2, pw, ph, fill, border);

        string name = seat.Name.Length > 10 ? seat.Name[..10] : seat.Name;
        PixelFont.DrawText(_canvas, name, cx - pw / 2 + 5, cy - ph / 2 + 5, seat.Folded ? BalatroUi.ColMuted : BalatroUi.ColWhite, 1);
        PixelFont.DrawText(_canvas, $"${seat.Chips}", cx - pw / 2 + 5, cy - ph / 2 + 16, BalatroUi.ColGold, 1);

        string status = seat.LastWinAmount > 0
            ? $"+${seat.LastWinAmount}"
            : seat.LastAction;
        if (!string.IsNullOrEmpty(status))
        {
            if (status.Length > 12) status = status[..12];
            PixelFont.DrawText(_canvas, status, cx - pw / 2 + 5, cy - ph / 2 + 25, seat.LastWinAmount > 0 ? BalatroUi.ColGreen : BalatroUi.ColOrange, 1);
        }

        // Compact hole cards tucked on the right side of the opponent panel
        int oppStep = seat.HoleCards.Count > 2 ? 9 : 16;
        for (int i = 0; i < seat.HoleCards.Count; i++)
        {
            CardSprites.DrawCard(_canvas, seat.HoleCards[i], cx + 4 + i * oppStep, cy - 16, compact: true, highlightGold: isWinner);
        }

        DrawDealerBlindBadges(seat, snap, cx - pw / 2 - 14, cy - 10);

        if (seat.CurrentBet > 0)
        {
            CardSprites.DrawChip(_canvas, cx, cy + 24, 7, BalatroUi.ColRed);
            PixelFont.DrawTextCentered(_canvas, $"${seat.CurrentBet}", cx + 22, cy + 20, BalatroUi.ColWhite, 1);
        }
    }

    private void DrawDealerBlindBadges(SeatSnapshot seat, TableSnapshot snap, int x, int y)
    {
        if (snap.Holdem.DealerSeat == seat.SeatIndex)
        {
            _canvas.FillCircle(x + 6, y + 6, 7, BalatroUi.ColWhite);
            PixelFont.DrawTextCentered(_canvas, "D", x + 6, y + 3, BalatroUi.ColPanelBorder, 1, shadow: false);
        }
        else if (snap.Holdem.SmallBlindSeat == seat.SeatIndex)
        {
            _canvas.FillRoundedRect(x - 2, y, 16, 12, BalatroUi.ColBlue);
            PixelFont.DrawTextCentered(_canvas, "SB", x + 6, y + 3, BalatroUi.ColWhite, 1, shadow: false);
        }
        else if (snap.Holdem.BigBlindSeat == seat.SeatIndex)
        {
            _canvas.FillRoundedRect(x - 2, y, 16, 12, BalatroUi.ColOrange);
            PixelFont.DrawTextCentered(_canvas, "BB", x + 6, y + 3, BalatroUi.ColWhite, 1, shadow: false);
        }
    }

    private void DrawHoldemActionBar(TableSnapshot snap)
    {
        _canvas.FillRect(0, 294, PixelCanvas.Width, 66, PixelCanvas.Rgba(14, 18, 26, 232));
        _canvas.FillRect(0, 294, PixelCanvas.Width, 2, BalatroUi.ColGold);

        var me = snap.Seats.FirstOrDefault(s => s.PlayerId == snap.ViewerPlayerId);
        bool myTurn = me is not null && snap.Holdem.CurrentTurnSeat == me.SeatIndex &&
                      snap.Holdem.Stage is HoldemStage.PreFlop or HoldemStage.Flop or HoldemStage.Turn or HoldemStage.River;

        if (snap.Holdem.Stage is HoldemStage.Showdown or HoldemStage.Waiting)
        {
            if (_host is not null)
            {
                if (BalatroUi.DrawButton(_canvas, new UiRect(230, 306, 180, 36), "DEAL NEXT HAND", BalatroUi.ColGreen, _mouseX, _mouseY, _mouseDown, _mouseJustReleased, textScale: 2))
                {
                    _host.NextHandOrRound();
                }
            }
            else
            {
                PixelFont.DrawTextCentered(_canvas, "WAITING FOR HOST TO DEAL NEXT HAND...", 320, 320, BalatroUi.ColGold, 1);
            }

            if (me is not null && me.Chips < snap.Config.BigBlind)
            {
                if (BalatroUi.DrawButton(_canvas, new UiRect(430, 310, 110, 28), $"REBUY ${snap.Config.StartingChips}", BalatroUi.ColPurple, _mouseX, _mouseY, _mouseDown, _mouseJustReleased))
                    SendRebuy();
            }
            return;
        }

        if (!myTurn || me is null)
        {
            var turnSeat = snap.Seats.FirstOrDefault(s => s.SeatIndex == snap.Holdem.CurrentTurnSeat);
            string waiting = turnSeat is not null ? $"WAITING FOR {turnSeat.Name} TO ACT..." : "DEALING...";
            PixelFont.DrawTextCentered(_canvas, waiting, 320, 322, BalatroUi.ColMuted, 1);
            return;
        }

        int toCall = Math.Max(0, snap.Holdem.CurrentBet - me.CurrentBet);
        int minTarget = snap.Holdem.CurrentBet == 0
            ? snap.Config.BigBlind
            : snap.Holdem.CurrentBet + Math.Max(snap.Holdem.MinRaise, snap.Config.BigBlind);
        int maxTarget = me.CurrentBet + me.Chips;
        _holdemRaiseTo = Math.Clamp(_holdemRaiseTo, Math.Min(minTarget, maxTarget), maxTarget);

        // Main buttons
        if (BalatroUi.DrawButton(_canvas, new UiRect(12, 306, 76, 36), "FOLD", BalatroUi.ColRed, _mouseX, _mouseY, _mouseDown, _mouseJustReleased))
            SendHoldemAction(HoldemActionType.Fold);

        string callLabel = toCall == 0 ? "CHECK" : $"CALL ${Math.Min(toCall, me.Chips)}";
        if (BalatroUi.DrawButton(_canvas, new UiRect(96, 306, 102, 36), callLabel, BalatroUi.ColBlue, _mouseX, _mouseY, _mouseDown, _mouseJustReleased))
            SendHoldemAction(toCall == 0 ? HoldemActionType.Check : HoldemActionType.Call);

        bool canRaise = maxTarget > snap.Holdem.CurrentBet;
        string raiseVerb = snap.Holdem.CurrentBet == 0 ? "BET" : "RAISE";
        if (BalatroUi.DrawButton(_canvas, new UiRect(206, 306, 126, 36), $"{raiseVerb} ${_holdemRaiseTo}", BalatroUi.ColOrange, _mouseX, _mouseY, _mouseDown, _mouseJustReleased, enabled: canRaise))
            SendHoldemAction(snap.Holdem.CurrentBet == 0 ? HoldemActionType.Bet : HoldemActionType.Raise, _holdemRaiseTo);

        if (BalatroUi.DrawButton(_canvas, new UiRect(340, 306, 78, 36), "ALL-IN", BalatroUi.ColPurple, _mouseX, _mouseY, _mouseDown, _mouseJustReleased, enabled: me.Chips > 0))
            SendHoldemAction(HoldemActionType.AllIn);

        // Quick bet sizing controls
        int step = Math.Max(10, snap.Config.BigBlind);
        if (BalatroUi.DrawButton(_canvas, new UiRect(430, 304, 34, 22), "-", BalatroUi.ColPanel, _mouseX, _mouseY, _mouseDown, _mouseJustReleased))
            _holdemRaiseTo = Math.Max(Math.Min(minTarget, maxTarget), _holdemRaiseTo - step);

        if (BalatroUi.DrawButton(_canvas, new UiRect(468, 304, 34, 22), "+", BalatroUi.ColPanel, _mouseX, _mouseY, _mouseDown, _mouseJustReleased))
            _holdemRaiseTo = Math.Min(maxTarget, _holdemRaiseTo + step);

        if (BalatroUi.DrawButton(_canvas, new UiRect(508, 304, 54, 22), "1/2 POT", BalatroUi.ColPanel, _mouseX, _mouseY, _mouseDown, _mouseJustReleased))
            _holdemRaiseTo = Math.Clamp(snap.Holdem.CurrentBet + Math.Max(snap.Config.BigBlind, snap.Holdem.TotalPot / 2), Math.Min(minTarget, maxTarget), maxTarget);

        if (BalatroUi.DrawButton(_canvas, new UiRect(566, 304, 60, 22), "POT", BalatroUi.ColPanel, _mouseX, _mouseY, _mouseDown, _mouseJustReleased))
            _holdemRaiseTo = Math.Clamp(snap.Holdem.CurrentBet + Math.Max(snap.Config.BigBlind, snap.Holdem.TotalPot), Math.Min(minTarget, maxTarget), maxTarget);

        PixelFont.DrawText(_canvas, $"YOUR CHIPS: ${me.Chips}  TO CALL: ${toCall}", 430, 334, BalatroUi.ColGold, 1);
    }

    private void RenderBaccaratTable(TableSnapshot snap, float timeSec)
    {
        _canvas.DrawBalatroSwirlBackground(timeSec, SwirlTheme.BaccaratCrimson);

        var bac = snap.Baccarat;
        if (bac.Stage == BaccaratStage.Settled && bac.RoundNumber != _lastCelebratedBaccaratRound)
        {
            _lastCelebratedBaccaratRound = bac.RoundNumber;
            _particles.SpawnWinBurst(PixelCanvas.Width / 2, 120, 40);
        }

        DrawTableTopBar(snap, $"BACCARAT (PUNTO BANCO)  ROUND #{bac.RoundNumber}  SHOE: {bac.ShoeRemaining}/416");

        // Player & Banker Hand Panels
        bool pWin = bac.Stage == BaccaratStage.Settled && bac.LastOutcome == BaccaratOutcome.PlayerWin;
        bool bWin = bac.Stage == BaccaratStage.Settled && bac.LastOutcome == BaccaratOutcome.BankerWin;

        _canvas.DrawBalatroPanel(20, 34, 278, 108, PixelCanvas.Rgba(24, 42, 74, 225), pWin ? BalatroUi.ColGold : BalatroUi.ColBlue);
        PixelFont.DrawText(_canvas, "PLAYER HAND", 32, 42, BalatroUi.ColBlue, 2);
        PixelFont.DrawText(_canvas, bac.PlayerCards.Count > 0 ? $"SCORE: {bac.PlayerScore}" : "SCORE: -", 206, 46, BalatroUi.ColGold, 1);

        for (int i = 0; i < bac.PlayerCards.Count; i++)
        {
            CardSprites.DrawCard(_canvas, bac.PlayerCards[i], 36 + i * 52, 68, compact: false, highlightGold: pWin, bobPhase: timeSec * 2f + i);
        }

        _canvas.DrawBalatroPanel(342, 34, 278, 108, PixelCanvas.Rgba(78, 22, 34, 225), bWin ? BalatroUi.ColGold : BalatroUi.ColRed);
        PixelFont.DrawText(_canvas, "BANKER HAND", 354, 42, BalatroUi.ColRed, 2);
        PixelFont.DrawText(_canvas, bac.BankerCards.Count > 0 ? $"SCORE: {bac.BankerScore}" : "SCORE: -", 528, 46, BalatroUi.ColGold, 1);

        for (int i = 0; i < bac.BankerCards.Count; i++)
        {
            CardSprites.DrawCard(_canvas, bac.BankerCards[i], 358 + i * 52, 68, compact: false, highlightGold: bWin, bobPhase: timeSec * 2f + i);
        }

        // VS center badge
        PixelFont.DrawTextCentered(_canvas, "VS", 320, 82, BalatroUi.ColGold, 2);

        // Outcome Banner
        _canvas.DrawBalatroPanel(120, 148, 400, 22, PixelCanvas.Rgba(18, 20, 30), BalatroUi.ColGold);
        PixelFont.DrawTextCentered(_canvas, bac.BannerMessage, 320, 155, BalatroUi.ColWhite, 1);

        // 5 Interactive Betting Zones
        var me = snap.Seats.FirstOrDefault(s => s.PlayerId == snap.ViewerPlayerId);
        bool canBet = me is not null && bac.Stage == BaccaratStage.Betting && !me.BaccaratBets.LockedIn && me.Chips >= _selectedChip;

        int by = 176;
        if (BalatroUi.DrawButton(_canvas, new UiRect(20, by, 144, 44), $"PLAYER 1:1 (${me?.BaccaratBets.Player ?? 0})", BalatroUi.ColBlue, _mouseX, _mouseY, _mouseDown, _mouseJustReleased, enabled: canBet))
            SendBaccaratBet(BaccaratBetType.Player, _selectedChip);

        if (BalatroUi.DrawButton(_canvas, new UiRect(172, by, 120, 44), $"TIE 8:1 (${me?.BaccaratBets.Tie ?? 0})", BalatroUi.ColGreen, _mouseX, _mouseY, _mouseDown, _mouseJustReleased, enabled: canBet))
            SendBaccaratBet(BaccaratBetType.Tie, _selectedChip);

        if (BalatroUi.DrawButton(_canvas, new UiRect(300, by, 156, 44), $"BANKER 0.95:1 (${me?.BaccaratBets.Banker ?? 0})", BalatroUi.ColRed, _mouseX, _mouseY, _mouseDown, _mouseJustReleased, enabled: canBet))
            SendBaccaratBet(BaccaratBetType.Banker, _selectedChip);

        if (BalatroUi.DrawButton(_canvas, new UiRect(464, by, 76, 44), $"P.PAIR (${me?.BaccaratBets.PlayerPair ?? 0})", BalatroUi.ColPurple, _mouseX, _mouseY, _mouseDown, _mouseJustReleased, enabled: canBet))
            SendBaccaratBet(BaccaratBetType.PlayerPair, _selectedChip);

        if (BalatroUi.DrawButton(_canvas, new UiRect(544, by, 76, 44), $"B.PAIR (${me?.BaccaratBets.BankerPair ?? 0})", BalatroUi.ColOrange, _mouseX, _mouseY, _mouseDown, _mouseJustReleased, enabled: canBet))
            SendBaccaratBet(BaccaratBetType.BankerPair, _selectedChip);

        // Bead Plate History Strip + 10-Player Compact Seat Strip
        _canvas.DrawBalatroPanel(20, 226, 600, 22, PixelCanvas.Rgba(20, 22, 32), BalatroUi.ColPanelBorder);
        PixelFont.DrawText(_canvas, "HISTORY:", 28, 234, BalatroUi.ColMuted, 1);
        for (int i = 0; i < bac.History.Count; i++)
        {
            var h = bac.History[i];
            int hx = 88 + i * 18;
            if (hx > 604) break;
            uint col = h.Outcome switch
            {
                BaccaratOutcome.PlayerWin => BalatroUi.ColBlue,
                BaccaratOutcome.BankerWin => BalatroUi.ColRed,
                _ => BalatroUi.ColGreen
            };
            string ch = h.Outcome switch
            {
                BaccaratOutcome.PlayerWin => "P",
                BaccaratOutcome.BankerWin => "B",
                _ => "T"
            };
            _canvas.FillCircle(hx, 237, 7, col);
            PixelFont.DrawTextCentered(_canvas, ch, hx, 234, BalatroUi.ColWhite, 1, shadow: false);
        }

        // Compact 10-seat strip
        _canvas.DrawBalatroPanel(20, 252, 600, 38, PixelCanvas.Rgba(24, 28, 40), BalatroUi.ColPanelBorder);
        int idx = 0;
        foreach (var s in snap.Seats.Take(10))
        {
            int col = idx % 5;
            int row = idx / 5;
            int px = 28 + col * 118;
            int py = 256 + row * 16;
            string n = s.Name.Length > 6 ? s.Name[..6] : s.Name;
            string act = !string.IsNullOrEmpty(s.LastAction) ? s.LastAction : $"${s.Chips}";
            uint c = s.PlayerId == snap.ViewerPlayerId ? BalatroUi.ColGold : BalatroUi.ColWhite;
            PixelFont.DrawText(_canvas, $"{n}:${s.Chips} {act}", px, py, c, 1, shadow: false);
            DrawSeatChatBubbleIfActive(s, px + 56, py - 4, timeSec);
            idx++;
        }

        // Bottom Controls: Chip selector + Clear + Lock/Deal
        _canvas.FillRect(0, 296, PixelCanvas.Width, 64, PixelCanvas.Rgba(14, 18, 26, 235));
        _canvas.FillRect(0, 296, PixelCanvas.Width, 2, BalatroUi.ColGold);

        PixelFont.DrawText(_canvas, $"CHIPS: ${me?.Chips ?? 0}", 14, 304, BalatroUi.ColGold, 1);
        PixelFont.DrawText(_canvas, $"WAGER: ${me?.BaccaratBets.Total ?? 0}", 14, 320, BalatroUi.ColWhite, 1);

        int[] chips = { 10, 25, 100, 500 };
        uint[] chipColors = { BalatroUi.ColBlue, BalatroUi.ColGreen, BalatroUi.ColRed, BalatroUi.ColPurple };
        for (int i = 0; i < chips.Length; i++)
        {
            int cx = 148 + i * 44;
            int cy = 328;
            if (_selectedChip == chips[i])
                _canvas.FillCircle(cx, cy, 17, BalatroUi.ColGold);
            CardSprites.DrawChip(_canvas, cx, cy, 14, chipColors[i], chips[i].ToString());
            if (_mouseJustReleased && new UiRect(cx - 15, cy - 15, 30, 30).Contains(_mouseX, _mouseY))
                _selectedChip = chips[i];
        }

        if (bac.Stage == BaccaratStage.Betting)
        {
            bool notLocked = me is not null && !me.BaccaratBets.LockedIn;
            if (BalatroUi.DrawButton(_canvas, new UiRect(334, 310, 74, 34), "CLEAR", BalatroUi.ColRed, _mouseX, _mouseY, _mouseDown, _mouseJustReleased, enabled: notLocked && (me?.BaccaratBets.Total ?? 0) > 0))
                SendBaccaratClear();

            if (BalatroUi.DrawButton(_canvas, new UiRect(416, 310, 100, 34), notLocked ? "LOCK BETS" : "LOCKED!", BalatroUi.ColGreen, _mouseX, _mouseY, _mouseDown, _mouseJustReleased, enabled: notLocked))
                SendBaccaratLock();

            if (_host is not null)
            {
                if (BalatroUi.DrawButton(_canvas, new UiRect(524, 310, 102, 34), "DEAL NOW", BalatroUi.ColOrange, _mouseX, _mouseY, _mouseDown, _mouseJustReleased))
                    _host.NextHandOrRound();
            }
        }
        else
        {
            if (_host is not null)
            {
                if (BalatroUi.DrawButton(_canvas, new UiRect(350, 310, 170, 34), "NEW BACCARAT ROUND", BalatroUi.ColGreen, _mouseX, _mouseY, _mouseDown, _mouseJustReleased))
                    _host.NextHandOrRound();
            }
            else
            {
                PixelFont.DrawText(_canvas, "WAITING FOR HOST...", 360, 322, BalatroUi.ColGold, 1);
            }

            if (me is not null && me.Chips < 10)
            {
                if (BalatroUi.DrawButton(_canvas, new UiRect(530, 310, 96, 34), "REBUY", BalatroUi.ColPurple, _mouseX, _mouseY, _mouseDown, _mouseJustReleased))
                    SendRebuy();
            }
        }
    }

    private void RenderGutsTable(TableSnapshot snap, float timeSec)
    {
        _canvas.DrawBalatroSwirlBackground(timeSec, SwirlTheme.HoldemEmerald);
        var guts = snap.Guts;
        if (guts.Stage == GutsStage.Showdown && guts.RoundNumber != _lastCelebratedGutsRound)
        {
            _lastCelebratedGutsRound = guts.RoundNumber;
            if (guts.WinnerSeats.Count > 0)
                _particles.SpawnWinBurst(PixelCanvas.Width / 2, 145, 42);
        }

        DrawTableTopBar(snap, $"GUTS POKER  ROUND #{guts.RoundNumber}  ANTE ${guts.Ante}");

        _canvas.FillEllipse(320, 158, 224, 88, PixelCanvas.Rgba(78, 36, 24));
        _canvas.FillEllipse(320, 158, 218, 82, PixelCanvas.Rgba(198, 152, 52));
        _canvas.FillEllipse(320, 158, 214, 78, PixelCanvas.Rgba(26, 90, 62));

        _canvas.DrawBalatroPanel(215, 114, 210, 34, PixelCanvas.Rgba(18, 22, 34), BalatroUi.ColGold);
        PixelFont.DrawTextCentered(_canvas, $"GUTS POT: ${guts.Pot}", 320, 124, BalatroUi.ColGold, 2);

        string banner = guts.BannerMessage.Length > 52 ? guts.BannerMessage[..52] : guts.BannerMessage;
        PixelFont.DrawTextCentered(_canvas, banner, 320, 160, BalatroUi.ColWhite, 1);

        (int X, int Y)[] seatCoords =
        {
            (320, 244), (168, 242), (66, 196), (66, 118), (156, 62),
            (266, 52),  (374, 52),  (484, 62), (574, 118), (574, 196)
        };
        int viewerSeat = snap.ViewerSeatIndex >= 0 ? snap.ViewerSeatIndex : 0;

        foreach (var seat in snap.Seats)
        {
            int slot = (seat.SeatIndex - viewerSeat + 10) % 10;
            var (sx, sy) = seatCoords[slot];
            bool isWin = guts.WinnerSeats.Contains(seat.SeatIndex);
            bool isLocal = seat.PlayerId == snap.ViewerPlayerId;

            if (isLocal)
            {
                for (int i = 0; i < seat.HoleCards.Count; i++)
                    CardSprites.DrawCard(_canvas, seat.HoleCards[i], sx - 44 + i * 46, sy - 46, compact: false, highlightGold: isWin, bobPhase: timeSec * 2.4f + i);

                _canvas.DrawBalatroPanel(sx - 66, sy + 12, 132, 32, BalatroUi.ColPanel, isWin ? BalatroUi.ColGold : BalatroUi.ColPanelBorder);
                PixelFont.DrawTextCentered(_canvas, $"{seat.Name}  ${seat.Chips}", sx, sy + 17, BalatroUi.ColGold, 1);
                string sub = !string.IsNullOrEmpty(seat.ShowdownHandName) ? seat.ShowdownHandName
                    : seat.GutsChoice == GutsDecision.In ? "CHOSE: IN"
                    : seat.GutsChoice == GutsDecision.Out ? "CHOSE: OUT" : "DECIDING...";
                PixelFont.DrawTextCentered(_canvas, sub, sx, sy + 30, BalatroUi.ColWhite, 1);
            }
            else
            {
                _canvas.DrawBalatroPanel(sx - 52, sy - 17, 104, 34, BalatroUi.ColPanel, isWin ? BalatroUi.ColGold : BalatroUi.ColPanelBorder);
                string nm = seat.Name.Length > 10 ? seat.Name[..10] : seat.Name;
                PixelFont.DrawText(_canvas, nm, sx - 47, sy - 12, BalatroUi.ColWhite, 1);
                PixelFont.DrawText(_canvas, $"${seat.Chips}", sx - 47, sy - 1, BalatroUi.ColGold, 1);
                if (!string.IsNullOrEmpty(seat.LastAction))
                    PixelFont.DrawText(_canvas, seat.LastAction.Length > 12 ? seat.LastAction[..12] : seat.LastAction, sx - 47, sy + 8, isWin ? BalatroUi.ColGreen : BalatroUi.ColOrange, 1);
                for (int i = 0; i < seat.HoleCards.Count; i++)
                    CardSprites.DrawCard(_canvas, seat.HoleCards[i], sx + 10 + i * 16, sy - 16, compact: true, highlightGold: isWin);
            }
            DrawSeatChatBubbleIfActive(seat, sx, isLocal ? sy - 54 : sy - 24, timeSec);
        }

        // Bottom bar
        _canvas.FillRect(0, 296, PixelCanvas.Width, 64, PixelCanvas.Rgba(14, 18, 26, 235));
        _canvas.FillRect(0, 296, PixelCanvas.Width, 2, BalatroUi.ColGold);
        var me = snap.Seats.FirstOrDefault(s => s.PlayerId == snap.ViewerPlayerId);

        if (guts.Stage == GutsStage.Deciding)
        {
            bool canDecide = me is not null && !me.SittingOut && me.GutsChoice == GutsDecision.Undecided;
            if (BalatroUi.DrawButton(_canvas, new UiRect(140, 308, 120, 38), "GO IN!", BalatroUi.ColGreen, _mouseX, _mouseY, _mouseDown, _mouseJustReleased, enabled: canDecide, textScale: 2))
                SendGutsDecision(GutsDecision.In);
            if (BalatroUi.DrawButton(_canvas, new UiRect(280, 308, 120, 38), "GO OUT", BalatroUi.ColRed, _mouseX, _mouseY, _mouseDown, _mouseJustReleased, enabled: canDecide, textScale: 2))
                SendGutsDecision(GutsDecision.Out);
            if (_host is not null)
            {
                if (BalatroUi.DrawButton(_canvas, new UiRect(430, 312, 110, 30), "REVEAL NOW", BalatroUi.ColOrange, _mouseX, _mouseY, _mouseDown, _mouseJustReleased))
                    _host.NextHandOrRound();
            }
        }
        else
        {
            if (_host is not null)
            {
                if (BalatroUi.DrawButton(_canvas, new UiRect(220, 308, 200, 36), "NEXT GUTS ROUND", BalatroUi.ColGreen, _mouseX, _mouseY, _mouseDown, _mouseJustReleased, textScale: 2))
                    _host.NextHandOrRound();
            }
            else
            {
                PixelFont.DrawTextCentered(_canvas, "WAITING FOR HOST FOR NEXT ROUND...", 320, 322, BalatroUi.ColGold, 1);
            }
            if (me is not null && me.Chips < guts.Ante)
            {
                if (BalatroUi.DrawButton(_canvas, new UiRect(450, 312, 110, 28), $"REBUY ${snap.Config.StartingChips}", BalatroUi.ColPurple, _mouseX, _mouseY, _mouseDown, _mouseJustReleased))
                    SendRebuy();
            }
        }
    }

    private void RenderBlackjackTable(TableSnapshot snap, float timeSec)
    {
        _canvas.DrawBalatroSwirlBackground(timeSec, SwirlTheme.MenuRoyal);
        var bj = snap.Blackjack;
        if (bj.Stage == BlackjackStage.Settled && bj.RoundNumber != _lastCelebratedBjRound)
        {
            _lastCelebratedBjRound = bj.RoundNumber;
            _particles.SpawnWinBurst(PixelCanvas.Width / 2, 110, 36);
        }

        DrawTableTopBar(snap, $"21 / BLACKJACK  ROUND #{bj.RoundNumber}  SHOE: {bj.ShoeRemaining}/312");

        // Dealer Panel
        _canvas.DrawBalatroPanel(120, 32, 400, 96, PixelCanvas.Rgba(24, 34, 58, 225), BalatroUi.ColGold);
        string dealerLabel = bj.DealerCards.Count > 0 ? $"DEALER: {bj.DealerTotal}" : "DEALER";
        if (bj.DealerBlackjack) dealerLabel = "DEALER: BLACKJACK 21!";
        else if (bj.DealerBusted) dealerLabel = $"DEALER: BUST ({bj.DealerTotal})";
        PixelFont.DrawText(_canvas, dealerLabel, 134, 40, BalatroUi.ColGold, 1);

        for (int i = 0; i < bj.DealerCards.Count; i++)
            CardSprites.DrawCard(_canvas, bj.DealerCards[i], 136 + i * 48, 58, compact: false, bobPhase: timeSec * 1.8f + i);

        // Banner
        _canvas.DrawBalatroPanel(90, 132, 460, 20, PixelCanvas.Rgba(16, 18, 28), BalatroUi.ColGold);
        PixelFont.DrawTextCentered(_canvas, bj.BannerMessage, 320, 138, BalatroUi.ColWhite, 1);

        // Up to 10 Player Seats in 2x5 grid
        int idx = 0;
        foreach (var s in snap.Seats.Take(10))
        {
            int col = idx % 5;
            int row = idx / 5;
            int px = 14 + col * 124;
            int py = 158 + row * 66;
            bool isTurn = bj.Stage == BlackjackStage.PlayerTurns && bj.CurrentTurnSeat == s.SeatIndex;
            bool isMe = s.PlayerId == snap.ViewerPlayerId;
            uint border = isTurn ? BalatroUi.ColGreen : isMe ? BalatroUi.ColGold : BalatroUi.ColPanelBorder;

            _canvas.DrawBalatroPanel(px, py, 118, 60, BalatroUi.ColPanel, border);
            string nm = s.Name.Length > 8 ? s.Name[..8] : s.Name;
            PixelFont.DrawText(_canvas, $"{nm} ${s.Chips}", px + 5, py + 4, isMe ? BalatroUi.ColGold : BalatroUi.ColWhite, 1, shadow: false);
            string info = s.BlackjackCards.Count > 0 ? $"TOT:{s.BlackjackTotal} B:${s.BlackjackBet}" : $"BET:${s.BlackjackBet}";
            PixelFont.DrawText(_canvas, info, px + 5, py + 14, BalatroUi.ColMuted, 1, shadow: false);

            for (int c = 0; c < s.BlackjackCards.Count; c++)
                CardSprites.DrawCard(_canvas, s.BlackjackCards[c], px + 6 + c * 15, py + 24, compact: true);

            if (!string.IsNullOrEmpty(s.LastAction))
                PixelFont.DrawText(_canvas, s.LastAction.Length > 12 ? s.LastAction[..12] : s.LastAction, px + 52, py + 46, s.BlackjackNet > 0 ? BalatroUi.ColGreen : BalatroUi.ColOrange, 1, shadow: false);
            DrawSeatChatBubbleIfActive(s, px + 59, py - 4, timeSec);
            idx++;
        }

        // Bottom Action Bar
        _canvas.FillRect(0, 296, PixelCanvas.Width, 64, PixelCanvas.Rgba(14, 18, 26, 235));
        _canvas.FillRect(0, 296, PixelCanvas.Width, 2, BalatroUi.ColGold);
        var me = snap.Seats.FirstOrDefault(s => s.PlayerId == snap.ViewerPlayerId);

        if (bj.Stage == BlackjackStage.Betting)
        {
            PixelFont.DrawText(_canvas, $"CHIPS: ${me?.Chips ?? 0}", 12, 304, BalatroUi.ColGold, 1);
            PixelFont.DrawText(_canvas, $"BET: ${me?.BlackjackBet ?? 0}", 12, 320, BalatroUi.ColWhite, 1);

            int[] chips = { 10, 25, 100, 500 };
            uint[] chipColors = { BalatroUi.ColBlue, BalatroUi.ColGreen, BalatroUi.ColRed, BalatroUi.ColPurple };
            for (int i = 0; i < chips.Length; i++)
            {
                int cx = 132 + i * 40;
                int cy = 328;
                if (_selectedChip == chips[i]) _canvas.FillCircle(cx, cy, 16, BalatroUi.ColGold);
                CardSprites.DrawChip(_canvas, cx, cy, 13, chipColors[i], chips[i].ToString());
                if (_mouseJustReleased && new UiRect(cx - 14, cy - 14, 28, 28).Contains(_mouseX, _mouseY))
                    _selectedChip = chips[i];
            }

            bool canBet = me is not null && !me.BlackjackLocked && me.Chips >= _selectedChip;
            if (BalatroUi.DrawButton(_canvas, new UiRect(284, 310, 74, 34), $"+ ${_selectedChip}", BalatroUi.ColBlue, _mouseX, _mouseY, _mouseDown, _mouseJustReleased, enabled: canBet))
                SendBlackjackBet(_selectedChip);
            if (BalatroUi.DrawButton(_canvas, new UiRect(364, 310, 62, 34), "CLEAR", BalatroUi.ColRed, _mouseX, _mouseY, _mouseDown, _mouseJustReleased, enabled: me is not null && !me.BlackjackLocked && me.BlackjackBet > 0))
                SendBlackjackClear();
            if (BalatroUi.DrawButton(_canvas, new UiRect(432, 310, 86, 34), me?.BlackjackLocked == true ? "LOCKED" : "LOCK BET", BalatroUi.ColGreen, _mouseX, _mouseY, _mouseDown, _mouseJustReleased, enabled: me is not null && !me.BlackjackLocked))
                SendBlackjackLock();
            if (_host is not null)
            {
                if (BalatroUi.DrawButton(_canvas, new UiRect(524, 310, 102, 34), "DEAL NOW", BalatroUi.ColOrange, _mouseX, _mouseY, _mouseDown, _mouseJustReleased))
                    _host.NextHandOrRound();
            }
        }
        else if (bj.Stage == BlackjackStage.PlayerTurns)
        {
            bool myTurn = me is not null && bj.CurrentTurnSeat == me.SeatIndex;
            if (myTurn && me is not null)
            {
                if (BalatroUi.DrawButton(_canvas, new UiRect(120, 308, 110, 36), "HIT", BalatroUi.ColGreen, _mouseX, _mouseY, _mouseDown, _mouseJustReleased, textScale: 2))
                    SendBlackjackAction(BlackjackActionType.Hit);
                if (BalatroUi.DrawButton(_canvas, new UiRect(246, 308, 120, 36), "STAND", BalatroUi.ColRed, _mouseX, _mouseY, _mouseDown, _mouseJustReleased, textScale: 2))
                    SendBlackjackAction(BlackjackActionType.Stand);
                bool canDbl = me.BlackjackCards.Count == 2 && me.Chips >= me.BlackjackBet;
                if (BalatroUi.DrawButton(_canvas, new UiRect(382, 308, 140, 36), "DOUBLE DOWN", BalatroUi.ColOrange, _mouseX, _mouseY, _mouseDown, _mouseJustReleased, enabled: canDbl))
                    SendBlackjackAction(BlackjackActionType.DoubleDown);
            }
            else
            {
                PixelFont.DrawTextCentered(_canvas, bj.BannerMessage, 320, 322, BalatroUi.ColGold, 1);
            }
        }
        else
        {
            if (_host is not null)
            {
                if (BalatroUi.DrawButton(_canvas, new UiRect(220, 308, 200, 36), "NEW 21 ROUND", BalatroUi.ColGreen, _mouseX, _mouseY, _mouseDown, _mouseJustReleased, textScale: 2))
                    _host.NextHandOrRound();
            }
            else
            {
                PixelFont.DrawTextCentered(_canvas, "WAITING FOR HOST...", 320, 322, BalatroUi.ColGold, 1);
            }
            if (me is not null && me.Chips < 10)
            {
                if (BalatroUi.DrawButton(_canvas, new UiRect(450, 310, 100, 32), "REBUY", BalatroUi.ColPurple, _mouseX, _mouseY, _mouseDown, _mouseJustReleased))
                    SendRebuy();
            }
        }
    }

    private void DrawTableTopBar(TableSnapshot snap, string title)
    {
        _canvas.FillRect(0, 0, PixelCanvas.Width, 26, PixelCanvas.Rgba(14, 16, 24, 225));
        PixelFont.DrawText(_canvas, title, 8, 9, BalatroUi.ColGold, 1);

        if (BalatroUi.DrawButton(_canvas, new UiRect(366, 3, 48, 20), "CHAT", _chatBarOpen ? BalatroUi.ColGold : BalatroUi.ColPurple, _mouseX, _mouseY, _mouseDown, _mouseJustReleased))
        {
            _chatBarOpen = !_chatBarOpen;
            _focusedField = _chatBarOpen ? InputField.Chat : InputField.None;
        }

        if (_host is not null)
        {
            if (BalatroUi.DrawButton(_canvas, new UiRect(418, 3, 84, 20), "NEXT MODE", BalatroUi.ColPurple, _mouseX, _mouseY, _mouseDown, _mouseJustReleased))
            {
                var nextMode = (GameMode)(((int)snap.Config.Mode + 1) % 5);
                _host.SwitchGameMode(nextMode);
            }
            if (BalatroUi.DrawButton(_canvas, new UiRect(506, 3, 58, 20), "LOBBY", BalatroUi.ColBlue, _mouseX, _mouseY, _mouseDown, _mouseJustReleased))
            {
                _host.ReturnToLobby();
            }
        }
        else
        {
            if (BalatroUi.DrawButton(_canvas, new UiRect(506, 3, 58, 20), "LEAVE", BalatroUi.ColRed, _mouseX, _mouseY, _mouseDown, _mouseJustReleased))
            {
                DisconnectToMenu();
            }
        }
        DrawTopRightCrtButton();
    }

    private void DrawTopRightCrtButton()
    {
        if (BalatroUi.DrawButton(_canvas, new UiRect(578, 3, 56, 20), _crtEnabled ? "CRT:ON" : "CRT:OFF", PixelCanvas.Rgba(52, 58, 76), _mouseX, _mouseY, _mouseDown, _mouseJustReleased))
        {
            _crtEnabled = !_crtEnabled;
        }
    }

    private void DrawActionLogStrip(TableSnapshot snap, int x, int y, int w, int h)
    {
        _canvas.DrawBalatroPanel(x, y, w, h, PixelCanvas.Rgba(18, 20, 30, 225), BalatroUi.ColPanelBorder);
        var lines = snap.ActionLog.TakeLast(4).ToList();
        for (int i = 0; i < lines.Count; i++)
        {
            string line = lines[i];
            if (line.Length > 92) line = line[..92];
            PixelFont.DrawText(_canvas, line, x + 8, y + 6 + i * 10, BalatroUi.ColMuted, 1, shadow: false);
        }
    }

    private TableSnapshot? GetCurrentSnapshot()
    {
        if (_host is not null)
            return _host.BuildSnapshotFor(_host.HostPlayerId);
        return _client?.LatestSnapshot;
    }

    private void StartHosting(int initialBotCount = 0)
    {
        DisconnectToMenu();
        try
        {
            int port = ParsePort();
            var cfg = new GameConfig
            {
                RoomName = $"{CleanName(_playerName)}'S TABLE",
                StartingChips = 1000,
                SmallBlind = 10,
                BigBlind = 20,
                MaxPlayers = 10
            };
            _host = new GameRoomHost(CleanName(_playerName), cfg);
            _host.Start(port);
            if (initialBotCount > 0)
                _host.SetBotCount(initialBotCount);
            _screen = ScreenState.Connected;
            _focusedField = InputField.None;
            _menuStatus = $"HOSTING ON PORT {_host.Port}";
        }
        catch (Exception ex)
        {
            _menuStatus = $"HOST ERROR: {ex.Message}";
            DisconnectToMenu();
        }
    }

    private void JoinRoom(string ip, int port)
    {
        DisconnectToMenu();
        string shownTarget = _hideIp ? "[IP HIDDEN]" : ip;
        _menuStatus = $"CONNECTING TO {shownTarget}:{port}...";
        var client = new GameRoomClient();
        _client = client;
        _screen = ScreenState.Connected;
        _focusedField = InputField.None;

        _ = Task.Run(async () =>
        {
            var (ok, err) = await client.ConnectAsync(ip, port, CleanName(_playerName));
            if (!ok)
            {
                _menuStatus = $"JOIN FAILED: {err}";
                _screen = ScreenState.MainMenu;
                client.Dispose();
                if (_client == client) _client = null;
            }
        });
    }

    private void DisconnectToMenu()
    {
        _host?.Dispose();
        _host = null;
        _client?.Dispose();
        _client = null;
        _screen = ScreenState.MainMenu;
    }

    private void SendHoldemAction(HoldemActionType action, int raiseTo = 0)
    {
        if (_host is not null)
            _host.HandleHoldemAction(_host.HostPlayerId, action, raiseTo);
        else if (_client is not null)
            _ = _client.SendHoldemActionAsync(action, raiseTo);
    }

    private void SendBaccaratBet(BaccaratBetType betType, int amount)
    {
        if (_host is not null)
            _host.HandleBaccaratBet(_host.HostPlayerId, betType, amount);
        else if (_client is not null)
            _ = _client.SendBaccaratBetAsync(betType, amount);
    }

    private void SendBaccaratClear()
    {
        if (_host is not null)
            _host.HandleBaccaratClear(_host.HostPlayerId);
        else if (_client is not null)
            _ = _client.SendBaccaratClearAsync();
    }

    private void SendBaccaratLock()
    {
        if (_host is not null)
            _host.HandleBaccaratLock(_host.HostPlayerId);
        else if (_client is not null)
            _ = _client.SendBaccaratLockAsync();
    }

    private void SendGutsDecision(GutsDecision choice)
    {
        if (_host is not null)
            _host.HandleGutsDecision(_host.HostPlayerId, choice);
        else if (_client is not null)
            _ = _client.SendGutsDecisionAsync(choice);
    }

    private void SendBlackjackBet(int amount)
    {
        if (_host is not null)
            _host.HandleBlackjackBet(_host.HostPlayerId, amount);
        else if (_client is not null)
            _ = _client.SendBlackjackBetAsync(amount);
    }

    private void SendBlackjackClear()
    {
        if (_host is not null)
            _host.HandleBlackjackClear(_host.HostPlayerId);
        else if (_client is not null)
            _ = _client.SendBlackjackClearAsync();
    }

    private void SendBlackjackLock()
    {
        if (_host is not null)
            _host.HandleBlackjackLock(_host.HostPlayerId);
        else if (_client is not null)
            _ = _client.SendBlackjackLockAsync();
    }

    private void SendBlackjackAction(BlackjackActionType action)
    {
        if (_host is not null)
            _host.HandleBlackjackAction(_host.HostPlayerId, action);
        else if (_client is not null)
            _ = _client.SendBlackjackActionAsync(action);
    }

    private void SendRebuy()
    {
        if (_host is not null)
            _host.HandleRebuy(_host.HostPlayerId);
        else if (_client is not null)
            _ = _client.SendRebuyAsync();
    }

    private void SendChat(string text, int pitch)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        if (_host is not null)
            _host.HandleChat(_host.HostPlayerId, text, pitch);
        else if (_client is not null)
            _ = _client.SendChatAsync(text, pitch);
    }

    private void InitSamAudio()
    {
        try
        {
            if (Sdl2.SDL_InitSubSystem(Sdl2.SDL_INIT_AUDIO) != 0) return;
            var desired = new Sdl2.SDL_AudioSpec
            {
                freq = SamVoiceSynth.SampleRate,
                format = Sdl2.AUDIO_S16LSB,
                channels = 1,
                samples = 1024
            };
            _audioDevice = Sdl2.SDL_OpenAudioDevice(IntPtr.Zero, 0, ref desired, out _, 0);
            if (_audioDevice != 0)
                Sdl2.SDL_PauseAudioDevice(_audioDevice, 0);
        }
        catch { }
    }

    private void CheckAndSpeakNewChats(TableSnapshot snap)
    {
        long maxSeen = _lastSpokenChatId;
        foreach (var s in snap.Seats.OrderBy(x => x.LastChatId))
        {
            if (s.LastChatId > _lastSpokenChatId && !string.IsNullOrWhiteSpace(s.LastChatMessage))
            {
                if (s.LastChatId > maxSeen) maxSeen = s.LastChatId;
                if (_samVoiceEnabled)
                    SpeakWithSam(s.LastChatMessage, s.LastChatPitch);
            }
        }
        _lastSpokenChatId = maxSeen;
    }

    private unsafe void SpeakWithSam(string text, int pitch)
    {
        if (_audioDevice == 0) return;
        try
        {
            short[] pcm = SamVoiceSynth.SynthesizePcm16(text, pitch);
            if (pcm.Length == 0) return;
            fixed (short* ptr = pcm)
            {
                Sdl2.SDL_QueueAudio(_audioDevice, (IntPtr)ptr, (uint)(pcm.Length * sizeof(short)));
            }
        }
        catch { }
    }

    private void DrawSeatChatBubbleIfActive(SeatSnapshot seat, int anchorX, int anchorY, float timeSec)
    {
        if (string.IsNullOrWhiteSpace(seat.LastChatMessage) || seat.LastChatUnixMs <= 0) return;
        long ageMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - seat.LastChatUnixMs;
        if (ageMs < 0 || ageMs > 6000) return;

        string msg = seat.LastChatMessage.Length > 26 ? seat.LastChatMessage[..26] : seat.LastChatMessage;
        int textW = PixelFont.MeasureWidth(msg, 1);
        int bw = Math.Max(46, textW + 14);
        int bh = 20;
        int bx = Math.Clamp(anchorX - bw / 2, 6, PixelCanvas.Width - bw - 6);
        int by = Math.Clamp(anchorY - bh - 4, 28, PixelCanvas.Height - bh - 70);

        // Balatro cream-and-gold speech bubble
        _canvas.DrawBalatroPanel(bx, by, bw, bh, PixelCanvas.Rgba(248, 242, 224), BalatroUi.ColGold);
        // Little pixel pointer tail
        int tailX = Math.Clamp(anchorX, bx + 8, bx + bw - 8);
        _canvas.FillRect(tailX - 3, by + bh - 1, 6, 3, BalatroUi.ColGold);
        _canvas.FillRect(tailX - 2, by + bh - 2, 4, 3, PixelCanvas.Rgba(248, 242, 224));

        // Bouncy Balatro wavy text!
        PixelFont.DrawWavyBalatroText(_canvas, msg, bx + 7, by + 6, PixelCanvas.Rgba(24, 28, 42), timeSec, 1);
    }

    private void DrawChatOverlayIfOpen(float timeSec)
    {
        if (!_chatBarOpen) return;

        int y = 262;
        _canvas.DrawBalatroPanel(16, y, 608, 30, PixelCanvas.Rgba(18, 22, 34, 242), BalatroUi.ColGold);

        if (BalatroUi.DrawTextInput(_canvas, new UiRect(24, y + 5, 290, 20), "", _chatDraft, _focusedField == InputField.Chat, _mouseX, _mouseY, _mouseJustPressed, timeSec))
            _focusedField = InputField.Chat;

        if (BalatroUi.DrawButton(_canvas, new UiRect(320, y + 5, 48, 20), "SAY", BalatroUi.ColGreen, _mouseX, _mouseY, _mouseDown, _mouseJustReleased))
        {
            if (!string.IsNullOrWhiteSpace(_chatDraft))
            {
                SendChat(_chatDraft, _samPitch);
                _chatDraft = string.Empty;
            }
        }

        if (BalatroUi.DrawButton(_canvas, new UiRect(374, y + 5, 22, 20), "-", BalatroUi.ColBgDark, _mouseX, _mouseY, _mouseDown, _mouseJustReleased))
            _samPitch = Math.Max(SamVoiceSynth.MinPitch, _samPitch - 8);
        PixelFont.DrawTextCentered(_canvas, $"PITCH:{_samPitch}", 434, y + 11, BalatroUi.ColGold, 1);
        if (BalatroUi.DrawButton(_canvas, new UiRect(472, y + 5, 22, 20), "+", BalatroUi.ColBgDark, _mouseX, _mouseY, _mouseDown, _mouseJustReleased))
            _samPitch = Math.Min(SamVoiceSynth.MaxPitch, _samPitch + 8);

        if (BalatroUi.DrawButton(_canvas, new UiRect(500, y + 5, 76, 20), _samVoiceEnabled ? "SAM:ON" : "SAM:OFF", _samVoiceEnabled ? BalatroUi.ColBlue : BalatroUi.ColRed, _mouseX, _mouseY, _mouseDown, _mouseJustReleased))
            _samVoiceEnabled = !_samVoiceEnabled;

        if (BalatroUi.DrawButton(_canvas, new UiRect(582, y + 5, 34, 20), "X", BalatroUi.ColRed, _mouseX, _mouseY, _mouseDown, _mouseJustReleased))
        {
            _chatBarOpen = false;
            if (_focusedField == InputField.Chat) _focusedField = InputField.None;
        }
    }

    private void HandleKeyDown(int sym)
    {
        if (sym == Sdl2.SDLK_F1)
        {
            _crtEnabled = !_crtEnabled;
            return;
        }
        if (sym == Sdl2.SDLK_F2)
        {
            _hideIp = !_hideIp;
            return;
        }
        if (sym == Sdl2.SDLK_ESCAPE)
        {
            _focusedField = InputField.None;
            _chatBarOpen = false;
            return;
        }
        if (sym == Sdl2.SDLK_RETURN && _screen == ScreenState.Connected)
        {
            if (_chatBarOpen && !string.IsNullOrWhiteSpace(_chatDraft))
            {
                SendChat(_chatDraft, _samPitch);
                _chatDraft = string.Empty;
            }
            else
            {
                _chatBarOpen = !_chatBarOpen;
                _focusedField = _chatBarOpen ? InputField.Chat : InputField.None;
            }
            return;
        }
        if (sym == 't' && _screen == ScreenState.Connected && _focusedField != InputField.Chat)
        {
            _chatBarOpen = true;
            _focusedField = InputField.Chat;
            return;
        }
        if (sym == Sdl2.SDLK_BACKSPACE)
        {
            if (_focusedField == InputField.Chat && _chatDraft.Length > 0)
            {
                _chatDraft = _chatDraft[..^1];
                return;
            }
            if (_screen == ScreenState.MainMenu)
            {
                switch (_focusedField)
                {
                    case InputField.PlayerName when _playerName.Length > 0:
                        _playerName = _playerName[..^1];
                        break;
                    case InputField.JoinIp when _joinIp.Length > 0:
                        _joinIp = _joinIp[..^1];
                        break;
                    case InputField.Port when _portText.Length > 0:
                        _portText = _portText[..^1];
                        break;
                }
            }
        }
    }

    private unsafe void HandleTextInput(Sdl2.SDL_TextInputEvent textEv)
    {
        if (_focusedField == InputField.None) return;

        int len = 0;
        while (len < 32 && textEv.text[len] != 0) len++;
        if (len == 0) return;

        string s = Encoding.UTF8.GetString(textEv.text, len);
        foreach (char c in s)
        {
            if (_focusedField == InputField.Chat && _chatDraft.Length < 48 && c >= 32 && c <= 126)
                _chatDraft += c;
            else if (_screen == ScreenState.MainMenu && _focusedField == InputField.PlayerName && _playerName.Length < 12 && (char.IsLetterOrDigit(c) || c == '_' || c == '-' || c == ' '))
                _playerName += char.ToUpperInvariant(c);
            else if (_screen == ScreenState.MainMenu && _focusedField == InputField.JoinIp && _joinIp.Length < 24 && (char.IsLetterOrDigit(c) || c == '.' || c == ':'))
                _joinIp += c;
            else if (_screen == ScreenState.MainMenu && _focusedField == InputField.Port && _portText.Length < 5 && char.IsDigit(c))
                _portText += c;
        }
    }

    private unsafe void PresentCanvas()
    {
        fixed (uint* ptr = _canvas.Pixels)
        {
            Sdl2.SDL_UpdateTexture(_texture, IntPtr.Zero, (IntPtr)ptr, PixelCanvas.Width * sizeof(uint));
        }
        Sdl2.SDL_RenderClear(_renderer);
        Sdl2.SDL_RenderCopy(_renderer, _texture, IntPtr.Zero, IntPtr.Zero);
        Sdl2.SDL_RenderPresent(_renderer);
    }

    private int ParsePort() =>
        int.TryParse(_portText, out int p) && p is >= 1024 and <= 65535 ? p : PacketFraming.DefaultGamePort;

    private static string CleanName(string s) =>
        string.IsNullOrWhiteSpace(s) ? "PLAYER" : s.Trim().ToUpperInvariant();

    private static string DetectLocalIp()
    {
        try
        {
            foreach (var ip in Dns.GetHostEntry(Dns.GetHostName()).AddressList)
            {
                if (ip.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ip))
                    return ip.ToString();
            }
        }
        catch { }
        return "127.0.0.1";
    }

    public void Dispose()
    {
        _lanDiscovery.Dispose();
        _host?.Dispose();
        _client?.Dispose();
        if (_audioDevice != 0) Sdl2.SDL_CloseAudioDevice(_audioDevice);
        if (_texture != IntPtr.Zero) Sdl2.SDL_DestroyTexture(_texture);
        if (_renderer != IntPtr.Zero) Sdl2.SDL_DestroyRenderer(_renderer);
        if (_window != IntPtr.Zero) Sdl2.SDL_DestroyWindow(_window);
        Sdl2.SDL_Quit();
    }
}
