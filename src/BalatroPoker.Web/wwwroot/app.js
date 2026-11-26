(() => {
  const state = {
    ws: null,
    roomCode: null,
    isPrivate: false,
    playerId: null,
    snapshot: null,
    hideIp: false,
    crtOn: true,
    homeBotsOn: true,
    homeBotCount: 3,
    selectedChip: 10,
    raiseTo: 40,
    swirlTheme: 'menu',
    samEnabled: true,
    samPitch: 64,
    lastSpokenChatId: 0
  };

  const $ = (id) => document.getElementById(id);

  // ---------- Balatro Pixelated Swirl Canvas ----------
  const bgCanvas = $('swirl-bg');
  const bgCtx = bgCanvas.getContext('2d');
  const bgImg = bgCtx.createImageData(bgCanvas.width, bgCanvas.height);

  function renderSwirl(ts) {
    const t = ts * 0.00065;
    const w = bgCanvas.width, h = bgCanvas.height;
    const data = bgImg.data;
    let c1, c2, c3;
    if (state.swirlTheme === 'holdem') {
      c1 = [14, 54, 42]; c2 = [24, 106, 78]; c3 = [12, 32, 36];
    } else if (state.swirlTheme === 'baccarat') {
      c1 = [66, 16, 28]; c2 = [126, 28, 44]; c3 = [36, 14, 26];
    } else {
      c1 = [22, 36, 68]; c2 = [168, 42, 56]; c3 = [24, 68, 114];
    }

    let idx = 0;
    for (let y = 0; y < h; y++) {
      const ny = (y - h * 0.5) / (h * 0.5);
      for (let x = 0; x < w; x++) {
        const nx = (x - w * 0.5) / (h * 0.5);
        const dist = Math.hypot(nx, ny);
        const ang = Math.atan2(ny, nx) + dist * 2.1 - t * 0.7;
        const ux = Math.cos(ang) * dist;
        const uy = Math.sin(ang) * dist;
        const v = (Math.sin(ux * 3.0 + Math.cos(uy * 2.6 + t)) + Math.cos(uy * 3.3 - Math.sin(ux * 2.4 - t * 0.8))) * 0.25 + 0.5;
        const band = Math.floor(v * 7) / 7;
        let r, g, b;
        if (band < 0.5) {
          const k = band * 2;
          r = c3[0] + (c1[0] - c3[0]) * k;
          g = c3[1] + (c1[1] - c3[1]) * k;
          b = c3[2] + (c1[2] - c3[2]) * k;
        } else {
          const k = (band - 0.5) * 2;
          r = c1[0] + (c2[0] - c1[0]) * k;
          g = c1[1] + (c2[1] - c1[1]) * k;
          b = c1[2] + (c2[2] - c1[2]) * k;
        }
        data[idx++] = r;
        data[idx++] = g;
        data[idx++] = b;
        data[idx++] = 255;
      }
    }
    bgCtx.putImageData(bgImg, 0, 0);
    requestAnimationFrame(renderSwirl);
  }
  requestAnimationFrame(renderSwirl);

  // ---------- Card Rendering ----------
  const SUIT_SYM = ['♠', '♥', '♦', '♣'];
  const SUIT_CLS = ['suit-spades', 'suit-hearts', 'suit-diamonds', 'suit-clubs'];
  const RANK_STR = { 2:'2',3:'3',4:'4',5:'5',6:'6',7:'7',8:'8',9:'9',10:'10',11:'J',12:'Q',13:'K',14:'A' };

  function cardHtml(c, { compact = false, glow = false } = {}) {
    const cls = ['card'];
    if (compact) cls.push('compact');
    if (glow) cls.push('gold-glow');
    if (!c) return `<div class="card slot"></div>`;
    if (c.faceDown) return `<div class="${cls.join(' ')} back"></div>`;
    const r = RANK_STR[c.rank] || c.rank;
    const s = SUIT_SYM[c.suit] || '?';
    cls.push(SUIT_CLS[c.suit] || 'suit-spades');
    return `<div class="${cls.join(' ')}">
      <div class="card-rank">${r}</div>
      <div class="card-center">${s}</div>
      ${compact ? '' : `<div class="card-rank" style="text-align:right">${r}</div>`}
    </div>`;
  }

  // Hero decorative cards
  $('hero-cards').innerHTML = [
    { rank: 14, suit: 0 }, { rank: 13, suit: 1 }, { rank: 12, suit: 2 }, { rank: 11, suit: 3 }, { rank: 10, suit: 0 }
  ].map((c, i) => cardHtml(c, { glow: i === 0 })).join('');

  // ---------- WebSocket Helpers ----------
  function ensureSocket(onOpen) {
    if (state.ws && state.ws.readyState === WebSocket.OPEN) {
      onOpen();
      return;
    }
    const proto = location.protocol === 'https:' ? 'wss:' : 'ws:';
    const ws = new WebSocket(`${proto}//${location.host}/ws`);
    state.ws = ws;
    ws.onopen = onOpen;
    ws.onmessage = (ev) => {
      const msg = JSON.parse(ev.data);
      if (msg.type === 'error') {
        $('home-status').textContent = `ERROR: ${msg.message}`;
      } else if (msg.type === 'joined') {
        state.roomCode = msg.roomCode;
        state.isPrivate = msg.isPrivate;
        state.playerId = msg.playerId;
      } else if (msg.type === 'snapshot') {
        state.roomCode = msg.roomCode;
        state.isPrivate = msg.isPrivate;
        state.snapshot = msg.snapshot;
        checkNewSamChats(msg.snapshot);
        render();
      }
    };
    ws.onclose = () => {
      if (state.roomCode) {
        state.roomCode = null;
        state.snapshot = null;
        $('home-status').textContent = 'DISCONNECTED FROM SERVER.';
        render();
      }
    };
  }

  function send(payload) {
    if (state.ws && state.ws.readyState === WebSocket.OPEN) {
      state.ws.send(JSON.stringify(payload));
    }
  }

  // ---------- Public Rooms ----------
  async function refreshPublicRooms() {
    try {
      const res = await fetch('/api/rooms');
      const rooms = await res.json();
      const box = $('public-rooms-list');
      if (!rooms.length) {
        box.innerHTML = '<p class="muted">No public rooms yet. Create one on the left!</p>';
        return;
      }
      const MODE_NAMES = ["HOLD'EM", 'BACCARAT', 'OMAHA', 'GUTS', '21 / BJ'];
      box.innerHTML = rooms.map(r => {
        const mode = MODE_NAMES[r.mode] || "HOLD'EM";
        const codeText = state.hideIp ? '*****' : r.roomCode;
        return `<div class="room-item">
          <div>
            <div>${escapeHtml(r.roomName)} <span class="gold-label">[${mode}]</span></div>
            <div class="muted small">CODE: ${codeText} // ${r.playerCount}/${r.maxPlayers} PLAYERS</div>
          </div>
          <button class="btn btn-purple btn-sm" data-join="${r.roomCode}">JOIN</button>
        </div>`;
      }).join('');
      box.querySelectorAll('[data-join]').forEach(btn => {
        btn.onclick = () => joinByCode(btn.dataset.join);
      });
    } catch {
      $('public-rooms-list').innerHTML = '<p class="muted">Could not fetch room list.</p>';
    }
  }

  function joinByCode(code) {
    const clean = (code || '').trim().toUpperCase();
    if (!clean) {
      $('home-status').textContent = 'ENTER A ROOM CODE FIRST.';
      return;
    }
    const name = ($('inp-name').value || 'PLAYER').trim().toUpperCase();
    $('home-status').textContent = `JOINING ROOM ${state.hideIp ? '*****' : clean}...`;
    ensureSocket(() => send({ type: 'join_room', playerName: name, roomCode: clean }));
  }

  // ---------- UI Render ----------
  function render() {
    const snap = state.snapshot;
    const inRoom = !!snap;

    const mode = snap?.config.mode ?? 0;
    const isHoldemLike = mode === 0 || mode === 2;

    $('view-home').classList.toggle('hidden', inRoom);
    $('view-lobby').classList.toggle('hidden', !inRoom || snap.phase !== 0);
    $('view-holdem').classList.toggle('hidden', !inRoom || snap.phase !== 1 || !isHoldemLike);
    $('view-baccarat').classList.toggle('hidden', !inRoom || snap.phase !== 1 || mode !== 1);
    $('view-guts').classList.toggle('hidden', !inRoom || snap.phase !== 1 || mode !== 3);
    $('view-blackjack').classList.toggle('hidden', !inRoom || snap.phase !== 1 || mode !== 4);

    $('btn-leave').classList.toggle('hidden', !inRoom);
    $('btn-copy-link').classList.toggle('hidden', !inRoom);
    $('sam-chat-bar').classList.toggle('hidden', !inRoom);

    const me = snap?.seats.find(s => s.playerId === snap.viewerPlayerId);
    const isHost = !!me?.isHost;

    $('btn-switch-mode').classList.toggle('hidden', !inRoom || snap.phase !== 1 || !isHost);
    $('btn-to-lobby').classList.toggle('hidden', !inRoom || snap.phase !== 1 || !isHost);

    $('btn-hide-ip').textContent = state.hideIp ? 'HIDE IP: ON' : 'HIDE IP: OFF';
    $('btn-hide-ip').className = `btn ${state.hideIp ? 'btn-red' : 'btn-dark'}`;
    $('lbl-server-url').textContent = state.hideIp ? 'http://***.***.***.***' : location.origin;

    if (!inRoom) {
      state.swirlTheme = 'menu';
      $('header-info').textContent = 'WEB SERVER // UP TO 10 PLAYERS';
      return;
    }

    const shownCode = state.hideIp ? '*****' : state.roomCode;
    $('header-info').textContent = `ROOM ${shownCode} // ${snap.seats.length}/${snap.config.maxPlayers} PLAYERS`;

    if (snap.phase === 0) {
      state.swirlTheme = 'menu';
      renderLobby(snap, isHost);
    } else if (isHoldemLike) {
      state.swirlTheme = 'holdem';
      $('btn-switch-mode').textContent = 'NEXT MODE';
      renderHoldem(snap, me, isHost);
    } else if (mode === 1) {
      state.swirlTheme = 'baccarat';
      $('btn-switch-mode').textContent = 'NEXT MODE';
      renderBaccarat(snap, me, isHost);
    } else if (mode === 3) {
      state.swirlTheme = 'holdem';
      $('btn-switch-mode').textContent = 'NEXT MODE';
      renderGuts(snap, me, isHost);
    } else {
      state.swirlTheme = 'menu';
      $('btn-switch-mode').textContent = 'NEXT MODE';
      renderBlackjack(snap, me, isHost);
    }
  }

  function renderLobby(snap, isHost) {
    $('lobby-title').textContent = `PLAYERS (${snap.seats.length}/${snap.config.maxPlayers})`;
    $('lobby-code-badge').textContent = `CODE: ${state.hideIp ? '*****' : state.roomCode}`;
    $('lobby-seat-hint').classList.toggle('hidden', !isHost);

    const seatsEl = $('lobby-seats');
    let html = '';
    for (let i = 0; i < 10; i++) {
      const s = snap.seats.find(x => x.seatIndex === i);
      if (s) {
        const tag = s.isHost ? ' [HOST]' : s.isBot ? ' [BOT]' : '';
        const meCls = s.playerId === snap.viewerPlayerId ? ' me' : '';
        html += `<div class="seat-card occupied${meCls}">
          ${bubbleHtml(s)}
          <div>
            <div>#${i + 1} ${escapeHtml(s.name)}${tag}</div>
            <div class="gold-label">$${s.chips}</div>
          </div>
          ${isHost && s.isBot ? `<button class="btn btn-red btn-sm" data-rem-bot="${i}">REM</button>` : ''}
        </div>`;
      } else {
        html += `<div class="seat-card">
          <div class="muted">SEAT #${i + 1} OPEN</div>
          ${isHost ? `<button class="btn btn-purple btn-sm" data-add-bot="${i}">+ BOT</button>` : ''}
        </div>`;
      }
    }
    seatsEl.innerHTML = html;
    seatsEl.querySelectorAll('[data-add-bot]').forEach(b => b.onclick = () => send({ type: 'add_bot_seat', seatIndex: Number(b.dataset.addBot) }));
    seatsEl.querySelectorAll('[data-rem-bot]').forEach(b => b.onclick = () => send({ type: 'remove_bot_seat', seatIndex: Number(b.dataset.remBot) }));

    const curMode = snap.config.mode;
    const modeColor = { 0: 'btn-green', 2: 'btn-green', 1: 'btn-red', 3: 'btn-orange', 4: 'btn-blue' };
    document.querySelectorAll('.btn-mode').forEach(b => {
      const m = Number(b.dataset.mode);
      b.disabled = !isHost;
      b.className = `btn btn-mode ${curMode === m ? modeColor[m] : 'btn-dark'}`;
    });

    $('lbl-blinds').textContent = `BLINDS/ANTE: $${snap.config.smallBlind}/$${snap.config.bigBlind}`;
    document.querySelectorAll('.btn-blind').forEach(b => b.disabled = !isHost);

    const botCount = snap.seats.filter(s => s.isBot).length;
    const maxBots = snap.config.maxPlayers - snap.seats.filter(s => !s.isBot).length;
    $('host-bot-controls').classList.toggle('hidden', !isHost);
    $('lbl-lobby-bots').textContent = `AI BOTS: ${botCount}/${maxBots}`;
    $('btn-bot-dec').disabled = botCount <= 0;
    $('btn-bot-inc').disabled = snap.seats.length >= snap.config.maxPlayers;
    $('btn-bot-none').disabled = botCount <= 0;
    $('btn-bot-fill').disabled = snap.seats.length >= snap.config.maxPlayers;

    const needsTwo = curMode === 0 || curMode === 2 || curMode === 3;
    $('btn-start-game').classList.toggle('hidden', !isHost);
    $('btn-start-game').disabled = needsTwo && snap.seats.length < 2;
    $('lobby-wait-msg').classList.toggle('hidden', isHost);

    $('lobby-log').innerHTML = snap.actionLog.map(l => `<div>${escapeHtml(l)}</div>`).join('');
    $('lobby-log').scrollTop = $('lobby-log').scrollHeight;
  }

  // 10 oval seat percentage coordinates (slot 0 = bottom center = viewer)
  const OVAL_POS = [
    [50, 88], [26, 86], [10, 68], [10, 34], [25, 13],
    [42, 9],  [58, 9],  [75, 13], [90, 34], [90, 68]
  ];

  function renderHoldem(snap, me, isHost) {
    const h = snap.holdem;
    $('holdem-pot').textContent = h.sidePots.length > 1
      ? `TOTAL POT: $${h.totalPot} (${h.sidePots.length} POTS)`
      : `POT: $${h.totalPot}`;
    $('holdem-banner').textContent = h.bannerMessage || `HAND #${h.handNumber}`;

    let commHtml = '';
    for (let i = 0; i < 5; i++) {
      commHtml += cardHtml(h.communityCards[i] || null, { glow: h.stage === 5 });
    }
    $('holdem-community').innerHTML = commHtml;

    const viewerSeat = snap.viewerSeatIndex >= 0 ? snap.viewerSeatIndex : 0;
    $('holdem-seats').innerHTML = snap.seats.map(s => {
      const slot = (s.seatIndex - viewerSeat + 10) % 10;
      const [lx, ly] = OVAL_POS[slot];
      const isTurn = h.currentTurnSeat === s.seatIndex;
      const isWin = h.winnerSeats.includes(s.seatIndex);
      const isLocal = s.playerId === snap.viewerPlayerId;
      const cls = ['oval-seat'];
      if (isTurn) cls.push('turn');
      if (isWin) cls.push('winner');
      if (s.folded) cls.push('folded');

      let badge = '';
      if (h.dealerSeat === s.seatIndex) badge = '<span class="seat-badge badge-d">D</span>';
      else if (h.smallBlindSeat === s.seatIndex) badge = '<span class="seat-badge badge-sb">SB</span>';
      else if (h.bigBlindSeat === s.seatIndex) badge = '<span class="seat-badge badge-bb">BB</span>';

      const sub = s.lastWinAmount > 0 ? `+$${s.lastWinAmount} ${s.showdownHandName}`
        : (s.showdownHandName || s.lastAction || (s.currentBet > 0 ? `BET $${s.currentBet}` : ''));

      return `<div class="${cls.join(' ')}" style="left:${lx}%;top:${ly}%">
        ${bubbleHtml(s)}
        <div class="seat-top">
          <span>${escapeHtml(s.name)}${badge}</span>
          <span class="gold-label">$${s.chips}</span>
        </div>
        <div class="seat-cards">
          ${s.holeCards.map(c => cardHtml(c, { compact: !isLocal, glow: isWin })).join('')}
        </div>
        ${sub ? `<div class="small muted" style="text-align:center;margin-top:3px">${escapeHtml(sub)}</div>` : ''}
      </div>`;
    }).join('');

    const bar = $('holdem-action-bar');
    const myTurn = me && h.currentTurnSeat === me.seatIndex && h.stage >= 1 && h.stage <= 4;

    if (h.stage === 5 || h.stage === 0) {
      bar.innerHTML = `
        <div class="gold-label">${escapeHtml(h.bannerMessage)}</div>
        <div class="btn-row">
          ${me && me.chips < snap.config.bigBlind ? `<button id="btn-h-rebuy" class="btn btn-purple">REBUY $${snap.config.startingChips}</button>` : ''}
          ${isHost ? `<button id="btn-h-next" class="btn btn-green btn-lg">DEAL NEXT HAND</button>` : `<span class="muted">WAITING FOR HOST TO DEAL...</span>`}
        </div>`;
      if ($('btn-h-next')) $('btn-h-next').onclick = () => send({ type: 'next_round' });
      if ($('btn-h-rebuy')) $('btn-h-rebuy').onclick = () => send({ type: 'rebuy' });
      return;
    }

    if (!myTurn) {
      const actor = snap.seats.find(s => s.seatIndex === h.currentTurnSeat);
      bar.innerHTML = `<div class="gold-label">WAITING FOR ${actor ? escapeHtml(actor.name) : '...'} TO ACT</div>
        <div class="muted small">YOUR CHIPS: $${me ? me.chips : 0}</div>`;
      return;
    }

    const toCall = Math.max(0, h.currentBet - me.currentBet);
    const minTarget = h.currentBet === 0 ? snap.config.bigBlind : h.currentBet + Math.max(h.minRaise, snap.config.bigBlind);
    const maxTarget = me.currentBet + me.chips;
    state.raiseTo = Math.max(Math.min(minTarget, maxTarget), Math.min(maxTarget, state.raiseTo));
    const canRaise = maxTarget > h.currentBet;
    const verb = h.currentBet === 0 ? 'BET' : 'RAISE';

    bar.innerHTML = `
      <div class="btn-row">
        <button id="btn-fold" class="btn btn-red">FOLD</button>
        <button id="btn-call" class="btn btn-blue">${toCall === 0 ? 'CHECK' : `CALL $${Math.min(toCall, me.chips)}`}</button>
        <button id="btn-raise" class="btn btn-orange" ${canRaise ? '' : 'disabled'}>${verb} $${state.raiseTo}</button>
        <button id="btn-allin" class="btn btn-purple" ${me.chips > 0 ? '' : 'disabled'}>ALL-IN</button>
      </div>
      <div class="btn-row" style="align-items:center">
        <input id="rng-raise" type="range" min="${Math.min(minTarget, maxTarget)}" max="${maxTarget}" step="${Math.max(5, snap.config.smallBlind)}" value="${state.raiseTo}" />
        <button id="btn-halfpot" class="btn btn-dark btn-sm">1/2 POT</button>
        <button id="btn-pot" class="btn btn-dark btn-sm">POT</button>
        <span class="gold-label">CHIPS: $${me.chips}</span>
      </div>`;

    $('btn-fold').onclick = () => send({ type: 'holdem_action', action: 0 });
    $('btn-call').onclick = () => send({ type: 'holdem_action', action: toCall === 0 ? 1 : 2 });
    $('btn-raise').onclick = () => send({ type: 'holdem_action', action: h.currentBet === 0 ? 3 : 4, amount: state.raiseTo });
    $('btn-allin').onclick = () => send({ type: 'holdem_action', action: 5 });
    $('rng-raise').oninput = (e) => {
      state.raiseTo = Number(e.target.value);
      $('btn-raise').textContent = `${verb} $${state.raiseTo}`;
    };
    $('btn-halfpot').onclick = () => {
      state.raiseTo = Math.min(maxTarget, Math.max(minTarget, h.currentBet + Math.floor(h.totalPot / 2)));
      renderHoldem(snap, me, isHost);
    };
    $('btn-pot').onclick = () => {
      state.raiseTo = Math.min(maxTarget, Math.max(minTarget, h.currentBet + h.totalPot));
      renderHoldem(snap, me, isHost);
    };
  }

  function renderBaccarat(snap, me, isHost) {
    const b = snap.baccarat;
    const pWin = b.stage === 2 && b.lastOutcome === 0;
    const bWin = b.stage === 2 && b.lastOutcome === 1;

    $('bac-player-panel').classList.toggle('gold-border', pWin);
    $('bac-banker-panel').classList.toggle('gold-border', bWin);
    $('bac-player-score').textContent = b.playerCards.length ? b.playerScore : '-';
    $('bac-banker-score').textContent = b.bankerCards.length ? b.bankerScore : '-';
    $('bac-shoe').textContent = `SHOE: ${b.shoeRemaining}/416`;
    $('bac-banner').textContent = b.bannerMessage;

    $('bac-player-cards').innerHTML = b.playerCards.map(c => cardHtml(c, { glow: pWin })).join('');
    $('bac-banker-cards').innerHTML = b.bankerCards.map(c => cardHtml(c, { glow: bWin })).join('');

    const bets = me?.baccaratBets || { player:0, banker:0, tie:0, playerPair:0, bankerPair:0, lockedIn:false };
    $('z-amt-0').textContent = `$${bets.player}`;
    $('z-amt-1').textContent = `$${bets.banker}`;
    $('z-amt-2').textContent = `$${bets.tie}`;
    $('z-amt-3').textContent = `$${bets.playerPair}`;
    $('z-amt-4').textContent = `$${bets.bankerPair}`;

    const canBet = me && b.stage === 0 && !bets.lockedIn && me.chips >= state.selectedChip;
    document.querySelectorAll('.bac-zone').forEach(z => z.disabled = !canBet);

    $('bac-history').innerHTML = b.history.map(h => {
      const cls = h.outcome === 0 ? 'bead-p' : h.outcome === 1 ? 'bead-b' : 'bead-t';
      const ch = h.outcome === 0 ? 'P' : h.outcome === 1 ? 'B' : 'T';
      return `<div class="bead ${cls}">${ch}</div>`;
    }).join('');

    $('bac-seats').innerHTML = snap.seats.map(s => {
      const meCls = s.playerId === snap.viewerPlayerId ? 'gold-label' : '';
      return `<div class="bac-seat-item ${meCls}">${bubbleHtml(s)}${escapeHtml(s.name)}: $${s.chips} <span class="muted">${escapeHtml(s.lastAction || '')}</span></div>`;
    }).join('');

    const totalWager = bets.player + bets.banker + bets.tie + bets.playerPair + bets.bankerPair;
    $('bac-my-chips').textContent = `CHIPS: $${me ? me.chips : 0}`;
    $('bac-my-wager').textContent = `WAGER: $${totalWager}`;

    const ctrl = $('bac-controls');
    if (b.stage === 0) {
      ctrl.innerHTML = `
        <button id="btn-b-clear" class="btn btn-red" ${!bets.lockedIn && totalWager > 0 ? '' : 'disabled'}>CLEAR</button>
        <button id="btn-b-lock" class="btn btn-green" ${!bets.lockedIn ? '' : 'disabled'}>${bets.lockedIn ? 'LOCKED!' : 'LOCK BETS'}</button>
        ${isHost ? `<button id="btn-b-deal" class="btn btn-orange">DEAL NOW</button>` : ''}`;
      $('btn-b-clear').onclick = () => send({ type: 'baccarat_clear' });
      $('btn-b-lock').onclick = () => send({ type: 'baccarat_lock' });
      if ($('btn-b-deal')) $('btn-b-deal').onclick = () => send({ type: 'next_round' });
    } else {
      ctrl.innerHTML = `
        ${me && me.chips < 10 ? `<button id="btn-b-rebuy" class="btn btn-purple">REBUY</button>` : ''}
        ${isHost ? `<button id="btn-b-next" class="btn btn-green">NEW BACCARAT ROUND</button>` : `<span class="gold-label">WAITING FOR HOST...</span>`}`;
      if ($('btn-b-next')) $('btn-b-next').onclick = () => send({ type: 'next_round' });
      if ($('btn-b-rebuy')) $('btn-b-rebuy').onclick = () => send({ type: 'rebuy' });
    }
  }

  function renderGuts(snap, me, isHost) {
    const g = snap.guts;
    $('guts-pot').textContent = `GUTS POT: $${g.pot} (ANTE $${g.ante})`;
    $('guts-banner').textContent = g.bannerMessage;

    const viewerSeat = snap.viewerSeatIndex >= 0 ? snap.viewerSeatIndex : 0;
    $('guts-seats').innerHTML = snap.seats.map(s => {
      const slot = (s.seatIndex - viewerSeat + 10) % 10;
      const [lx, ly] = OVAL_POS[slot];
      const isWin = g.winnerSeats.includes(s.seatIndex);
      const isLocal = s.playerId === snap.viewerPlayerId;
      const sub = s.showdownHandName || s.lastAction || (s.gutsChoice === 1 ? 'IN' : s.gutsChoice === 2 ? 'OUT' : 'DECIDING...');
      return `<div class="oval-seat ${isWin ? 'winner' : ''}" style="left:${lx}%;top:${ly}%">
        ${bubbleHtml(s)}
        <div class="seat-top">
          <span>${escapeHtml(s.name)}</span>
          <span class="gold-label">$${s.chips}</span>
        </div>
        <div class="seat-cards">
          ${s.holeCards.map(c => cardHtml(c, { compact: !isLocal, glow: isWin })).join('')}
        </div>
        <div class="small muted" style="text-align:center;margin-top:3px">${escapeHtml(sub)}</div>
      </div>`;
    }).join('');

    const bar = $('guts-action-bar');
    if (g.stage === 0) {
      const canDecide = me && !me.sittingOut && me.gutsChoice === 0;
      bar.innerHTML = `
        <div class="btn-row">
          <button id="btn-g-in" class="btn btn-green btn-lg" ${canDecide ? '' : 'disabled'}>GO IN!</button>
          <button id="btn-g-out" class="btn btn-red btn-lg" ${canDecide ? '' : 'disabled'}>GO OUT</button>
        </div>
        <div class="btn-row">
          ${isHost ? `<button id="btn-g-reveal" class="btn btn-orange">REVEAL NOW</button>` : `<span class="gold-label">YOUR CHIPS: $${me ? me.chips : 0}</span>`}
        </div>`;
      $('btn-g-in').onclick = () => send({ type: 'guts_decision', choice: 1 });
      $('btn-g-out').onclick = () => send({ type: 'guts_decision', choice: 2 });
      if ($('btn-g-reveal')) $('btn-g-reveal').onclick = () => send({ type: 'next_round' });
    } else {
      bar.innerHTML = `
        <div class="gold-label">${escapeHtml(g.bannerMessage)}</div>
        <div class="btn-row">
          ${me && me.chips < g.ante ? `<button id="btn-g-rebuy" class="btn btn-purple">REBUY</button>` : ''}
          ${isHost ? `<button id="btn-g-next" class="btn btn-green btn-lg">NEXT GUTS ROUND</button>` : `<span class="muted">WAITING FOR HOST...</span>`}
        </div>`;
      if ($('btn-g-next')) $('btn-g-next').onclick = () => send({ type: 'next_round' });
      if ($('btn-g-rebuy')) $('btn-g-rebuy').onclick = () => send({ type: 'rebuy' });
    }
  }

  function renderBlackjack(snap, me, isHost) {
    const bj = snap.blackjack;
    $('bj-dealer-title').textContent = bj.dealerBlackjack ? 'DEALER: NATURAL 21!'
      : bj.dealerBusted ? `DEALER: BUST (${bj.dealerTotal})`
      : bj.dealerCards.length ? `DEALER: ${bj.dealerTotal}` : 'DEALER';
    $('bj-shoe').textContent = `SHOE: ${bj.shoeRemaining}/312`;
    $('bj-dealer-cards').innerHTML = bj.dealerCards.map(c => cardHtml(c)).join('');
    $('bj-banner').textContent = bj.bannerMessage;

    $('bj-seats').innerHTML = snap.seats.map(s => {
      const isTurn = bj.stage === 1 && bj.currentTurnSeat === s.seatIndex;
      const isMe = s.playerId === snap.viewerPlayerId;
      const cls = ['panel'];
      if (isTurn) cls.push('gold-border');
      return `<div class="${cls.join(' ')} bj-seat-box" style="padding:8px">
        ${bubbleHtml(s)}
        <div class="seat-top">
          <span class="${isMe ? 'gold-label' : ''}">${escapeHtml(s.name)}</span>
          <span class="gold-label">$${s.chips}</span>
        </div>
        <div class="small muted">BET: $${s.blackjackBet} ${s.blackjackCards.length ? `// TOT: ${s.blackjackTotal}` : ''}</div>
        <div class="seat-cards" style="margin:6px 0">${s.blackjackCards.map(c => cardHtml(c, { compact: true })).join('')}</div>
        <div class="small" style="color:${s.blackjackNet > 0 ? 'var(--green)' : 'var(--orange)'}">${escapeHtml(s.lastAction || '')}</div>
      </div>`;
    }).join('');

    const bar = $('bj-action-bar');
    if (bj.stage === 0) {
      const canBet = me && !me.blackjackLocked && me.chips >= state.selectedChip;
      bar.innerHTML = `
        <div class="bac-chips-info">
          <span class="gold-label">CHIPS: $${me ? me.chips : 0}</span>
          <span class="white-label">BET: $${me ? me.blackjackBet : 0}</span>
        </div>
        <div class="chip-picker">
          ${[10,25,100,500].map((v,i) => `<button data-bjchip="${v}" class="chip ${['chip-blue','chip-green','chip-red','chip-purple'][i]} ${state.selectedChip===v?'active':''}">$${v}</button>`).join('')}
        </div>
        <div class="btn-row">
          <button id="btn-bj-bet" class="btn btn-blue" ${canBet ? '' : 'disabled'}>+ $${state.selectedChip}</button>
          <button id="btn-bj-clear" class="btn btn-red" ${me && !me.blackjackLocked && me.blackjackBet > 0 ? '' : 'disabled'}>CLEAR</button>
          <button id="btn-bj-lock" class="btn btn-green" ${me && !me.blackjackLocked ? '' : 'disabled'}>${me?.blackjackLocked ? 'LOCKED!' : 'LOCK BET'}</button>
          ${isHost ? `<button id="btn-bj-deal" class="btn btn-orange">DEAL NOW</button>` : ''}
        </div>`;
      bar.querySelectorAll('[data-bjchip]').forEach(b => b.onclick = () => { state.selectedChip = Number(b.dataset.bjchip); renderBlackjack(snap, me, isHost); });
      $('btn-bj-bet').onclick = () => send({ type: 'bj_bet', amount: state.selectedChip });
      $('btn-bj-clear').onclick = () => send({ type: 'bj_clear' });
      $('btn-bj-lock').onclick = () => send({ type: 'bj_lock' });
      if ($('btn-bj-deal')) $('btn-bj-deal').onclick = () => send({ type: 'next_round' });
    } else if (bj.stage === 1) {
      const myTurn = me && bj.currentTurnSeat === me.seatIndex;
      if (myTurn) {
        const canDbl = me.blackjackCards.length === 2 && me.chips >= me.blackjackBet;
        bar.innerHTML = `
          <div class="btn-row">
            <button id="btn-bj-hit" class="btn btn-green btn-lg">HIT</button>
            <button id="btn-bj-stand" class="btn btn-red btn-lg">STAND</button>
            <button id="btn-bj-dbl" class="btn btn-orange btn-lg" ${canDbl ? '' : 'disabled'}>DOUBLE DOWN</button>
          </div>
          <div class="gold-label">YOUR TOTAL: ${me.blackjackTotal} // BET: $${me.blackjackBet}</div>`;
        $('btn-bj-hit').onclick = () => send({ type: 'bj_action', action: 0 });
        $('btn-bj-stand').onclick = () => send({ type: 'bj_action', action: 1 });
        $('btn-bj-dbl').onclick = () => send({ type: 'bj_action', action: 2 });
      } else {
        bar.innerHTML = `<div class="gold-label">${escapeHtml(bj.bannerMessage)}</div>`;
      }
    } else {
      bar.innerHTML = `
        <div class="gold-label">${escapeHtml(bj.bannerMessage)}</div>
        <div class="btn-row">
          ${me && me.chips < 10 ? `<button id="btn-bj-rebuy" class="btn btn-purple">REBUY</button>` : ''}
          ${isHost ? `<button id="btn-bj-next" class="btn btn-green btn-lg">NEW 21 ROUND</button>` : `<span class="muted">WAITING FOR HOST...</span>`}
        </div>`;
      if ($('btn-bj-next')) $('btn-bj-next').onclick = () => send({ type: 'next_round' });
      if ($('btn-bj-rebuy')) $('btn-bj-rebuy').onclick = () => send({ type: 'rebuy' });
    }
  }

  function checkNewSamChats(snap) {
    if (!snap || !snap.seats) return;
    let maxId = state.lastSpokenChatId;
    const sorted = [...snap.seats].sort((a, b) => (a.lastChatId || 0) - (b.lastChatId || 0));
    for (const s of sorted) {
      if (s.lastChatId > state.lastSpokenChatId && s.lastChatMessage) {
        if (s.lastChatId > maxId) maxId = s.lastChatId;
        if (state.samEnabled && window.SamVoice) {
          window.SamVoice.speak(s.lastChatMessage, s.lastChatPitch || 64);
        }
        // Schedule re-render after 6.1s so the Balatro speech bubble auto-clears
        setTimeout(() => { if (state.snapshot) render(); }, 6200);
      }
    }
    state.lastSpokenChatId = maxId;
  }

  function bubbleHtml(seat) {
    if (!seat || !seat.lastChatMessage || !seat.lastChatUnixMs) return '';
    const ageMs = Date.now() - seat.lastChatUnixMs;
    if (ageMs < -5000 || ageMs > 6000) return '';
    const chars = [...seat.lastChatMessage].map((ch, i) =>
      `<span class="wch" style="animation-delay:${(i * 0.045).toFixed(3)}s">${ch === ' ' ? '&nbsp;' : escapeHtml(ch)}</span>`
    ).join('');
    return `<div class="balatro-bubble">${chars}</div>`;
  }

  function escapeHtml(s) {
    return String(s).replace(/[&<>"']/g, c => ({ '&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;' }[c]));
  }

  // ---------- Event Bindings ----------
  $('btn-crt').onclick = () => {
    state.crtOn = !state.crtOn;
    $('crt-overlay').classList.toggle('crt-on', state.crtOn);
    $('btn-crt').textContent = state.crtOn ? 'CRT: ON' : 'CRT: OFF';
  };

  $('btn-hide-ip').onclick = () => {
    state.hideIp = !state.hideIp;
    render();
    refreshPublicRooms();
  };

  window.addEventListener('keydown', (e) => {
    if (e.key === 'F1') { e.preventDefault(); $('btn-crt').click(); }
    if (e.key === 'F2') { e.preventDefault(); $('btn-hide-ip').click(); }
  });

  $('btn-home-bots').onclick = () => {
    state.homeBotsOn = !state.homeBotsOn;
    if (state.homeBotsOn && state.homeBotCount === 0) state.homeBotCount = 1;
    $('btn-home-bots').textContent = state.homeBotsOn ? 'BOTS: ON' : 'BOTS: OFF';
    $('btn-home-bots').className = `btn ${state.homeBotsOn ? 'btn-purple' : 'btn-dark'}`;
    $('lbl-home-bots').textContent = `COUNT: ${state.homeBotsOn ? state.homeBotCount : 0}`;
  };
  $('btn-home-bot-dec').onclick = () => {
    if (!state.homeBotsOn) return;
    state.homeBotCount = Math.max(0, state.homeBotCount - 1);
    $('lbl-home-bots').textContent = `COUNT: ${state.homeBotCount}`;
  };
  $('btn-home-bot-inc').onclick = () => {
    if (!state.homeBotsOn) return;
    state.homeBotCount = Math.min(9, state.homeBotCount + 1);
    $('lbl-home-bots').textContent = `COUNT: ${state.homeBotCount}`;
  };

  $('btn-create-room').onclick = () => {
    const playerName = ($('inp-name').value || 'HOST').trim().toUpperCase();
    const roomName = ($('inp-room-name').value || `${playerName}'S TABLE`).trim().toUpperCase();
    const mode = Number($('sel-mode').value);
    const isPrivate = $('chk-private').checked;
    const initialBots = state.homeBotsOn ? state.homeBotCount : 0;
    ensureSocket(() => send({ type: 'create_room', playerName, roomName, mode, isPrivate, initialBots }));
  };

  $('btn-join-code').onclick = () => joinByCode($('inp-join-code').value);
  $('btn-refresh-rooms').onclick = refreshPublicRooms;

  $('btn-leave').onclick = () => {
    send({ type: 'leave_room' });
    state.roomCode = null;
    state.snapshot = null;
    render();
    refreshPublicRooms();
  };

  $('btn-copy-link').onclick = async () => {
    if (!state.roomCode) return;
    const url = `${location.origin}/?room=${state.roomCode}`;
    try {
      await navigator.clipboard.writeText(url);
      $('btn-copy-link').textContent = 'COPIED!';
      setTimeout(() => $('btn-copy-link').textContent = 'COPY INVITE', 1400);
    } catch {
      prompt('Share this invite link with friends:', url);
    }
  };

  document.querySelectorAll('.btn-mode').forEach(b => {
    b.onclick = () => send({ type: 'switch_mode', mode: Number(b.dataset.mode) });
  });
  $('btn-switch-mode').onclick = () => {
    const next = ((state.snapshot?.config.mode ?? 0) + 1) % 5;
    send({ type: 'switch_mode', mode: next });
  };
  $('btn-to-lobby').onclick = () => send({ type: 'return_lobby' });

  document.querySelectorAll('.btn-blind').forEach(b => {
    b.onclick = () => {
      const [sb, bb] = b.dataset.blinds.split(',').map(Number);
      send({ type: 'set_blinds', smallBlind: sb, bigBlind: bb });
    };
  });

  $('btn-bot-dec').onclick = () => {
    const cur = state.snapshot?.seats.filter(s => s.isBot).length || 0;
    send({ type: 'set_bot_count', count: Math.max(0, cur - 1) });
  };
  $('btn-bot-inc').onclick = () => {
    const cur = state.snapshot?.seats.filter(s => s.isBot).length || 0;
    send({ type: 'set_bot_count', count: cur + 1 });
  };
  $('btn-bot-none').onclick = () => send({ type: 'set_bot_count', count: 0 });
  $('btn-bot-fill').onclick = () => send({ type: 'set_bot_count', count: 9 });
  $('btn-start-game').onclick = () => send({ type: 'start_game' });

  $('btn-lobby-chat').onclick = () => {
    const inp = $('inp-lobby-chat');
    if (inp.value.trim()) {
      send({ type: 'chat', text: inp.value.trim(), pitch: state.samPitch });
      inp.value = '';
    }
  };
  $('inp-lobby-chat').addEventListener('keydown', (e) => {
    if (e.key === 'Enter') $('btn-lobby-chat').click();
  });

  $('rng-sam-pitch').oninput = (e) => {
    state.samPitch = Number(e.target.value);
    $('lbl-sam-pitch').textContent = state.samPitch;
  };
  $('btn-sam-toggle').onclick = () => {
    state.samEnabled = !state.samEnabled;
    $('btn-sam-toggle').textContent = state.samEnabled ? 'SAM: ON' : 'SAM: OFF';
    $('btn-sam-toggle').className = `btn btn-sm ${state.samEnabled ? 'btn-blue' : 'btn-red'}`;
  };
  $('btn-global-chat').onclick = () => {
    const inp = $('inp-global-chat');
    if (inp.value.trim()) {
      send({ type: 'chat', text: inp.value.trim(), pitch: state.samPitch });
      inp.value = '';
    }
  };
  $('inp-global-chat').addEventListener('keydown', (e) => {
    if (e.key === 'Enter') $('btn-global-chat').click();
  });

  document.querySelectorAll('.chip').forEach(btn => {
    btn.onclick = () => {
      state.selectedChip = Number(btn.dataset.chip);
      document.querySelectorAll('.chip').forEach(c => c.classList.toggle('active', c === btn));
      render();
    };
  });

  document.querySelectorAll('.bac-zone').forEach(btn => {
    btn.onclick = () => send({ type: 'baccarat_bet', betType: Number(btn.dataset.bet), amount: state.selectedChip });
  });

  // Auto-fill room code from ?room=CODE invite URL
  const params = new URLSearchParams(location.search);
  const inviteCode = params.get('room');
  if (inviteCode) {
    $('inp-join-code').value = inviteCode.toUpperCase();
    $('home-status').textContent = `INVITE DETECTED FOR ROOM ${inviteCode.toUpperCase()} — ENTER NAME & CLICK JOIN!`;
  }

  render();
  refreshPublicRooms();
})();
