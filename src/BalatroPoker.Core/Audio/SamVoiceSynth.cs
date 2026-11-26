namespace BalatroPoker.Core.Audio;

/// <summary>
/// Classic 1982 SAM (Software Automatic Mouth) inspired 3-formant 8-bit retro speech synthesizer.
/// Pure C#, zero external dependencies, zero LLM — supports custom pitch (25..130, default 64).
/// </summary>
public static class SamVoiceSynth
{
    public const int SampleRate = 22050;
    public const int DefaultPitch = 64;
    public const int MinPitch = 25;
    public const int MaxPitch = 130;

    private readonly record struct PhonemeSpec(
        float F1, float F2, float F3,
        float VoicedAmp, float NoiseAmp,
        int DurationMs, bool IsStop = false);

    private static readonly Dictionary<string, PhonemeSpec> Phonemes = new()
    {
        // Vowels
        ["AA"] = new(730, 1090, 2440, 1.0f, 0.00f, 95),
        ["AE"] = new(660, 1720, 2410, 1.0f, 0.00f, 90),
        ["EH"] = new(530, 1840, 2480, 1.0f, 0.00f, 85),
        ["IY"] = new(270, 2290, 3010, 1.0f, 0.00f, 90),
        ["IH"] = new(390, 1990, 2550, 1.0f, 0.00f, 75),
        ["OW"] = new(450,  840, 2410, 1.0f, 0.00f, 100),
        ["UW"] = new(300,  870, 2240, 1.0f, 0.00f, 95),
        ["UH"] = new(520, 1190, 2390, 1.0f, 0.00f, 80),
        ["AY"] = new(700, 1450, 2500, 1.0f, 0.00f, 110),
        ["EY"] = new(420, 2100, 2650, 1.0f, 0.00f, 100),

        // Voiced consonants & liquids
        ["M"]  = new(280,  900, 2200, 0.65f, 0.00f, 65),
        ["N"]  = new(280, 1700, 2600, 0.65f, 0.00f, 60),
        ["NG"] = new(280, 2300, 2750, 0.65f, 0.00f, 70),
        ["L"]  = new(360, 1050, 2800, 0.75f, 0.00f, 60),
        ["R"]  = new(420, 1300, 1650, 0.78f, 0.00f, 65),
        ["W"]  = new(300,  610, 2200, 0.78f, 0.00f, 65),
        ["Y"]  = new(270, 2150, 2900, 0.78f, 0.00f, 60),
        ["V"]  = new(280, 1100, 2400, 0.55f, 0.25f, 60),
        ["Z"]  = new(280, 1650, 2600, 0.50f, 0.45f, 65),
        ["B"]  = new(220,  800, 2100, 0.55f, 0.15f, 50, true),
        ["D"]  = new(220, 1700, 2600, 0.55f, 0.18f, 50, true),
        ["G"]  = new(220, 2100, 2700, 0.55f, 0.18f, 50, true),
        ["JH"] = new(250, 1850, 2650, 0.50f, 0.35f, 65, true),

        // Unvoiced fricatives & plosives
        ["S"]  = new(400, 2000, 3800, 0.00f, 0.65f, 75),
        ["SH"] = new(350, 1800, 2800, 0.00f, 0.70f, 80),
        ["F"]  = new(350, 1200, 2600, 0.00f, 0.45f, 65),
        ["TH"] = new(350, 1500, 2600, 0.00f, 0.40f, 65),
        ["H"]  = new(500, 1500, 2500, 0.00f, 0.32f, 55),
        ["P"]  = new(200,  800, 2100, 0.00f, 0.40f, 50, true),
        ["T"]  = new(200, 1800, 3000, 0.00f, 0.50f, 50, true),
        ["K"]  = new(200, 2100, 2900, 0.00f, 0.45f, 55, true),
        ["CH"] = new(250, 1900, 3000, 0.00f, 0.60f, 70, true),

        // Pauses
        ["SP"] = new(0, 0, 0, 0.00f, 0.00f, 55),
        ["BR"] = new(0, 0, 0, 0.00f, 0.00f, 120),
    };

    /// <summary>
    /// Converts ASCII English text into a sequence of SAM phonemes.
    /// </summary>
    public static List<string> TextToPhonemes(string text)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(text)) return result;

        string s = text.ToUpperInvariant();
        int i = 0;
        while (i < s.Length)
        {
            char c = s[i];
            if (char.IsWhiteSpace(c))
            {
                result.Add("SP");
                i++;
                continue;
            }
            if (c is '.' or '!' or '?' or ',')
            {
                result.Add("BR");
                i++;
                continue;
            }
            if (char.IsDigit(c))
            {
                string word = c switch
                {
                    '0' => "ZERO", '1' => "ONE", '2' => "TWO", '3' => "THREE", '4' => "FOUR",
                    '5' => "FIVE", '6' => "SIX", '7' => "SEVEN", '8' => "EIGHT", '9' => "NINE",
                    _ => ""
                };
                result.AddRange(TextToPhonemes(word));
                result.Add("SP");
                i++;
                continue;
            }
            if (c == '$')
            {
                result.AddRange(TextToPhonemes("DOLLARS"));
                i++;
                continue;
            }

            string two = i + 1 < s.Length ? s.Substring(i, 2) : "";
            string? matched = two switch
            {
                "SH" => "SH",
                "CH" => "CH",
                "TH" => "TH",
                "PH" => "F",
                "NG" => "NG",
                "CK" => "K",
                "OO" => "UW",
                "EE" => "IY",
                "EA" => "IY",
                "AI" or "AY" => "EY",
                "OU" or "OW" => "OW",
                "OA" => "OW",
                "QU" => "K",
                _ => null
            };

            if (matched is not null)
            {
                result.Add(matched);
                if (two == "QU") result.Add("W");
                i += 2;
                continue;
            }

            string? single = c switch
            {
                'A' => "AE",
                'B' => "B",
                'C' => (i + 1 < s.Length && s[i + 1] is 'E' or 'I' or 'Y') ? "S" : "K",
                'D' => "D",
                'E' => (i == s.Length - 1 || !char.IsLetter(s[i + 1])) && i > 0 && char.IsLetter(s[i - 1]) ? null : "EH",
                'F' => "F",
                'G' => (i + 1 < s.Length && s[i + 1] is 'E' or 'I' or 'Y') ? "JH" : "G",
                'H' => "H",
                'I' => "IH",
                'J' => "JH",
                'K' => "K",
                'L' => "L",
                'M' => "M",
                'N' => "N",
                'O' => "OW",
                'P' => "P",
                'Q' => "K",
                'R' => "R",
                'S' => "S",
                'T' => "T",
                'U' => "UH",
                'V' => "V",
                'W' => "W",
                'X' => "K",
                'Y' => (i == 0 || !char.IsLetter(s[i - 1])) ? "Y" : "IY",
                'Z' => "Z",
                _ => null
            };

            if (single is not null)
            {
                result.Add(single);
                if (c == 'X') result.Add("S");
            }
            i++;
        }

        return result;
    }

    /// <summary>
    /// Synthesizes crunchy 8-bit quantized SAM speech as 16-bit mono PCM at 22050 Hz.
    /// </summary>
    public static short[] SynthesizePcm16(string text, int pitch = DefaultPitch)
    {
        var list = TextToPhonemes(text);
        if (list.Count == 0) return Array.Empty<short>();

        pitch = Math.Clamp(pitch, MinPitch, MaxPitch);
        // Classic SAM pitch mapping: pitch 64 ~= 118 Hz fundamental; higher pitch -> higher voice
        float baseF0 = 55f + pitch * 1.15f;
        // Slight formant shift with pitch for authentic SAM robotic character
        float formantShift = 0.85f + (pitch / 64f) * 0.15f;

        var samples = new List<short>(list.Count * 1600);
        float glottalPhase = 0f;
        float p1 = 0f, p2 = 0f, p3 = 0f;
        uint lfsr = 0xACE1u;

        for (int idx = 0; idx < list.Count; idx++)
        {
            if (!Phonemes.TryGetValue(list[idx], out var spec))
                continue;

            int count = (SampleRate * spec.DurationMs) / 1000;
            float f1 = spec.F1 * formantShift;
            float f2 = spec.F2 * formantShift;
            float f3 = spec.F3 * formantShift;

            // Subtle downward inflection at end of sentence
            float phraseProgress = idx / (float)Math.Max(1, list.Count);
            float f0 = baseF0 * (1.03f - 0.10f * phraseProgress);

            for (int n = 0; n < count; n++)
            {
                float tNorm = n / (float)Math.Max(1, count);
                float env = 1f;
                if (tNorm < 0.12f) env = tNorm / 0.12f;
                else if (tNorm > 0.85f) env = (1f - tNorm) / 0.15f;

                if (spec.IsStop && tNorm < 0.45f)
                    env *= 0.08f; // Plosive closure before burst

                float sample = 0f;

                if (spec.VoicedAmp > 0f)
                {
                    glottalPhase += f0 / SampleRate;
                    if (glottalPhase >= 1f)
                    {
                        glottalPhase -= 1f;
                        // Hard-sync formant phases on glottal pulse (the secret to SAM's iconic buzz!)
                        p1 = 0f;
                        p2 = 0f;
                        p3 = 0f;
                    }

                    // Decaying glottal pulse envelope within each pitch period
                    float pulseEnv = MathF.Exp(-glottalPhase * 3.2f);
                    p1 += (2f * MathF.PI * f1) / SampleRate;
                    p2 += (2f * MathF.PI * f2) / SampleRate;
                    p3 += (2f * MathF.PI * f3) / SampleRate;

                    float voiced = (0.55f * MathF.Sin(p1) + 0.30f * MathF.Sin(p2) + 0.15f * MathF.Sin(p3)) * pulseEnv;
                    sample += voiced * spec.VoicedAmp;
                }

                if (spec.NoiseAmp > 0f)
                {
                    // 16-bit LFSR retro noise
                    uint bit = ((lfsr >> 0) ^ (lfsr >> 2) ^ (lfsr >> 3) ^ (lfsr >> 5)) & 1u;
                    lfsr = (lfsr >> 1) | (bit << 15);
                    float noise = ((lfsr & 0xFF) / 127.5f) - 1f;
                    sample += noise * spec.NoiseAmp * 0.55f;
                }

                sample *= env * 0.72f;
                // Quantize to 6-bit/8-bit steps for authentic retro SAM crunch!
                float q = MathF.Round(Math.Clamp(sample, -1f, 1f) * 32f) / 32f;
                samples.Add((short)(q * 28000f));
            }
        }

        return samples.ToArray();
    }
}
