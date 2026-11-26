// Classic 1982 SAM (Software Automatic Mouth) inspired 3-formant 8-bit Web Audio synthesizer.
// Zero LLM, zero external CDN — pure local formant synthesis with adjustable pitch (25..130).
window.SamVoice = (() => {
  const SAMPLE_RATE = 22050;
  let audioCtx = null;

  const PHONEMES = {
    AA:[730,1090,2440,1.0,0.00,95,false], AE:[660,1720,2410,1.0,0.00,90,false],
    EH:[530,1840,2480,1.0,0.00,85,false], IY:[270,2290,3010,1.0,0.00,90,false],
    IH:[390,1990,2550,1.0,0.00,75,false], OW:[450, 840,2410,1.0,0.00,100,false],
    UW:[300, 870,2240,1.0,0.00,95,false], UH:[520,1190,2390,1.0,0.00,80,false],
    EY:[420,2100,2650,1.0,0.00,100,false],
    M:[280,900,2200,0.65,0.00,65,false],  N:[280,1700,2600,0.65,0.00,60,false],
    NG:[280,2300,2750,0.65,0.00,70,false],L:[360,1050,2800,0.75,0.00,60,false],
    R:[420,1300,1650,0.78,0.00,65,false], W:[300,610,2200,0.78,0.00,65,false],
    Y:[270,2150,2900,0.78,0.00,60,false], V:[280,1100,2400,0.55,0.25,60,false],
    Z:[280,1650,2600,0.50,0.45,65,false], B:[220,800,2100,0.55,0.15,50,true],
    D:[220,1700,2600,0.55,0.18,50,true],  G:[220,2100,2700,0.55,0.18,50,true],
    JH:[250,1850,2650,0.50,0.35,65,true],
    S:[400,2000,3800,0.00,0.65,75,false], SH:[350,1800,2800,0.00,0.70,80,false],
    F:[350,1200,2600,0.00,0.45,65,false], TH:[350,1500,2600,0.00,0.40,65,false],
    H:[500,1500,2500,0.00,0.32,55,false], P:[200,800,2100,0.00,0.40,50,true],
    T:[200,1800,3000,0.00,0.50,50,true],  K:[200,2100,2900,0.00,0.45,55,true],
    CH:[250,1900,3000,0.00,0.60,70,true],
    SP:[0,0,0,0,0,55,false], BR:[0,0,0,0,0,120,false]
  };

  const DIGITS = ['ZERO','ONE','TWO','THREE','FOUR','FIVE','SIX','SEVEN','EIGHT','NINE'];

  function textToPhonemes(text) {
    const out = [];
    const s = String(text || '').toUpperCase();
    let i = 0;
    while (i < s.length) {
      const c = s[i];
      if (/\s/.test(c)) { out.push('SP'); i++; continue; }
      if ('.!?,'.includes(c)) { out.push('BR'); i++; continue; }
      if (c >= '0' && c <= '9') { out.push(...textToPhonemes(DIGITS[c.charCodeAt(0) - 48]), 'SP'); i++; continue; }
      if (c === '$') { out.push(...textToPhonemes('DOLLARS')); i++; continue; }

      const two = s.slice(i, i + 2);
      const pairMap = {
        SH:'SH', CH:'CH', TH:'TH', PH:'F', NG:'NG', CK:'K',
        OO:'UW', EE:'IY', EA:'IY', AI:'EY', AY:'EY', OU:'OW', OW:'OW', OA:'OW', QU:'K'
      };
      if (pairMap[two]) {
        out.push(pairMap[two]);
        if (two === 'QU') out.push('W');
        i += 2;
        continue;
      }

      const next = s[i + 1] || '';
      const prev = s[i - 1] || '';
      const isLetter = (ch) => ch >= 'A' && ch <= 'Z';
      let p = null;
      if (c === 'A') p = 'AE';
      else if (c === 'B') p = 'B';
      else if (c === 'C') p = 'EIY'.includes(next) ? 'S' : 'K';
      else if (c === 'D') p = 'D';
      else if (c === 'E') p = (!isLetter(next) && isLetter(prev)) ? null : 'EH';
      else if (c === 'F') p = 'F';
      else if (c === 'G') p = 'EIY'.includes(next) ? 'JH' : 'G';
      else if (c === 'H') p = 'H';
      else if (c === 'I') p = 'IH';
      else if (c === 'J') p = 'JH';
      else if (c === 'K') p = 'K';
      else if (c === 'L') p = 'L';
      else if (c === 'M') p = 'M';
      else if (c === 'N') p = 'N';
      else if (c === 'O') p = 'OW';
      else if (c === 'P') p = 'P';
      else if (c === 'Q') p = 'K';
      else if (c === 'R') p = 'R';
      else if (c === 'S') p = 'S';
      else if (c === 'T') p = 'T';
      else if (c === 'U') p = 'UH';
      else if (c === 'V') p = 'V';
      else if (c === 'W') p = 'W';
      else if (c === 'X') p = 'K';
      else if (c === 'Y') p = isLetter(prev) ? 'IY' : 'Y';
      else if (c === 'Z') p = 'Z';

      if (p) {
        out.push(p);
        if (c === 'X') out.push('S');
      }
      i++;
    }
    return out;
  }

  function speak(text, pitch = 64) {
    try {
      const list = textToPhonemes(text);
      if (!list.length) return;
      if (!audioCtx) audioCtx = new (window.AudioContext || window.webkitAudioContext)();
      if (audioCtx.state === 'suspended') audioCtx.resume();

      const clampedPitch = Math.max(25, Math.min(130, Number(pitch) || 64));
      const baseF0 = 55 + clampedPitch * 1.15;
      const formantShift = 0.85 + (clampedPitch / 64) * 0.15;

      let totalSamples = 0;
      for (const ph of list) {
        const spec = PHONEMES[ph];
        if (spec) totalSamples += Math.floor((SAMPLE_RATE * spec[5]) / 1000);
      }
      if (!totalSamples) return;

      const buf = audioCtx.createBuffer(1, totalSamples, SAMPLE_RATE);
      const ch = buf.getChannelData(0);
      let outIdx = 0;
      let glottalPhase = 0, p1 = 0, p2 = 0, p3 = 0;
      let lfsr = 0xACE1;

      for (let idx = 0; idx < list.length; idx++) {
        const spec = PHONEMES[list[idx]];
        if (!spec) continue;
        const [rawF1, rawF2, rawF3, vAmp, nAmp, durMs, isStop] = spec;
        const count = Math.floor((SAMPLE_RATE * durMs) / 1000);
        const f1 = rawF1 * formantShift;
        const f2 = rawF2 * formantShift;
        const f3 = rawF3 * formantShift;
        const f0 = baseF0 * (1.03 - 0.10 * (idx / Math.max(1, list.length)));

        for (let n = 0; n < count; n++) {
          const tNorm = n / Math.max(1, count);
          let env = 1;
          if (tNorm < 0.12) env = tNorm / 0.12;
          else if (tNorm > 0.85) env = (1 - tNorm) / 0.15;
          if (isStop && tNorm < 0.45) env *= 0.08;

          let sample = 0;
          if (vAmp > 0) {
            glottalPhase += f0 / SAMPLE_RATE;
            if (glottalPhase >= 1) {
              glottalPhase -= 1;
              p1 = 0; p2 = 0; p3 = 0;
            }
            const pulseEnv = Math.exp(-glottalPhase * 3.2);
            p1 += (2 * Math.PI * f1) / SAMPLE_RATE;
            p2 += (2 * Math.PI * f2) / SAMPLE_RATE;
            p3 += (2 * Math.PI * f3) / SAMPLE_RATE;
            sample += (0.55 * Math.sin(p1) + 0.30 * Math.sin(p2) + 0.15 * Math.sin(p3)) * pulseEnv * vAmp;
          }
          if (nAmp > 0) {
            const bit = ((lfsr >> 0) ^ (lfsr >> 2) ^ (lfsr >> 3) ^ (lfsr >> 5)) & 1;
            lfsr = (lfsr >> 1) | (bit << 15);
            const noise = ((lfsr & 0xFF) / 127.5) - 1;
            sample += noise * nAmp * 0.55;
          }
          sample *= env * 0.65;
          ch[outIdx++] = Math.round(Math.max(-1, Math.min(1, sample)) * 32) / 32;
        }
      }

      const src = audioCtx.createBufferSource();
      src.buffer = buf;
      src.connect(audioCtx.destination);
      src.start();
    } catch { }
  }

  return { speak };
})();
