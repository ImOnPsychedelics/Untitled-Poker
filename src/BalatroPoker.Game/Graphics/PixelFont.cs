namespace BalatroPoker.Game.Graphics;

public static class PixelFont
{
    // 5x7 crisp bitmap font (5 bits wide per row, 7 rows per glyph)
    private static readonly Dictionary<char, byte[]> Glyphs = new()
    {
        ['A'] = new byte[] { 0b01110, 0b10001, 0b10001, 0b11111, 0b10001, 0b10001, 0b10001 },
        ['B'] = new byte[] { 0b11110, 0b10001, 0b10001, 0b11110, 0b10001, 0b10001, 0b11110 },
        ['C'] = new byte[] { 0b01110, 0b10001, 0b10000, 0b10000, 0b10000, 0b10001, 0b01110 },
        ['D'] = new byte[] { 0b11100, 0b10010, 0b10001, 0b10001, 0b10001, 0b10010, 0b11100 },
        ['E'] = new byte[] { 0b11111, 0b10000, 0b10000, 0b11110, 0b10000, 0b10000, 0b11111 },
        ['F'] = new byte[] { 0b11111, 0b10000, 0b10000, 0b11110, 0b10000, 0b10000, 0b10000 },
        ['G'] = new byte[] { 0b01110, 0b10001, 0b10000, 0b10111, 0b10001, 0b10001, 0b01110 },
        ['H'] = new byte[] { 0b10001, 0b10001, 0b10001, 0b11111, 0b10001, 0b10001, 0b10001 },
        ['I'] = new byte[] { 0b01110, 0b00100, 0b00100, 0b00100, 0b00100, 0b00100, 0b01110 },
        ['J'] = new byte[] { 0b00111, 0b00010, 0b00010, 0b00010, 0b10010, 0b10010, 0b01100 },
        ['K'] = new byte[] { 0b10001, 0b10010, 0b10100, 0b11000, 0b10100, 0b10010, 0b10001 },
        ['L'] = new byte[] { 0b10000, 0b10000, 0b10000, 0b10000, 0b10000, 0b10000, 0b11111 },
        ['M'] = new byte[] { 0b10001, 0b11011, 0b10101, 0b10101, 0b10001, 0b10001, 0b10001 },
        ['N'] = new byte[] { 0b10001, 0b11001, 0b10101, 0b10011, 0b10001, 0b10001, 0b10001 },
        ['O'] = new byte[] { 0b01110, 0b10001, 0b10001, 0b10001, 0b10001, 0b10001, 0b01110 },
        ['P'] = new byte[] { 0b11110, 0b10001, 0b10001, 0b11110, 0b10000, 0b10000, 0b10000 },
        ['Q'] = new byte[] { 0b01110, 0b10001, 0b10001, 0b10001, 0b10101, 0b10010, 0b01101 },
        ['R'] = new byte[] { 0b11110, 0b10001, 0b10001, 0b11110, 0b10100, 0b10010, 0b10001 },
        ['S'] = new byte[] { 0b01111, 0b10000, 0b10000, 0b01110, 0b00001, 0b00001, 0b11110 },
        ['T'] = new byte[] { 0b11111, 0b00100, 0b00100, 0b00100, 0b00100, 0b00100, 0b00100 },
        ['U'] = new byte[] { 0b10001, 0b10001, 0b10001, 0b10001, 0b10001, 0b10001, 0b01110 },
        ['V'] = new byte[] { 0b10001, 0b10001, 0b10001, 0b10001, 0b10001, 0b01010, 0b00100 },
        ['W'] = new byte[] { 0b10001, 0b10001, 0b10001, 0b10101, 0b10101, 0b11011, 0b10001 },
        ['X'] = new byte[] { 0b10001, 0b10001, 0b01010, 0b00100, 0b01010, 0b10001, 0b10001 },
        ['Y'] = new byte[] { 0b10001, 0b10001, 0b01010, 0b00100, 0b00100, 0b00100, 0b00100 },
        ['Z'] = new byte[] { 0b11111, 0b00001, 0b00010, 0b00100, 0b01000, 0b10000, 0b11111 },

        ['0'] = new byte[] { 0b01110, 0b10001, 0b10011, 0b10101, 0b11001, 0b10001, 0b01110 },
        ['1'] = new byte[] { 0b00100, 0b01100, 0b00100, 0b00100, 0b00100, 0b00100, 0b01110 },
        ['2'] = new byte[] { 0b01110, 0b10001, 0b00001, 0b00110, 0b01000, 0b10000, 0b11111 },
        ['3'] = new byte[] { 0b11110, 0b00001, 0b00001, 0b01110, 0b00001, 0b00001, 0b11110 },
        ['4'] = new byte[] { 0b00010, 0b00110, 0b01010, 0b10010, 0b11111, 0b00010, 0b00010 },
        ['5'] = new byte[] { 0b11111, 0b10000, 0b11110, 0b00001, 0b00001, 0b10001, 0b01110 },
        ['6'] = new byte[] { 0b01110, 0b10000, 0b11110, 0b10001, 0b10001, 0b10001, 0b01110 },
        ['7'] = new byte[] { 0b11111, 0b00001, 0b00010, 0b00100, 0b01000, 0b01000, 0b01000 },
        ['8'] = new byte[] { 0b01110, 0b10001, 0b10001, 0b01110, 0b10001, 0b10001, 0b01110 },
        ['9'] = new byte[] { 0b01110, 0b10001, 0b10001, 0b01111, 0b00001, 0b00001, 0b01110 },

        [' '] = new byte[] { 0, 0, 0, 0, 0, 0, 0 },
        ['.'] = new byte[] { 0, 0, 0, 0, 0, 0b01100, 0b01100 },
        [','] = new byte[] { 0, 0, 0, 0, 0, 0b00100, 0b01000 },
        [':'] = new byte[] { 0, 0b00100, 0b00100, 0, 0b00100, 0b00100, 0 },
        ['!'] = new byte[] { 0b00100, 0b00100, 0b00100, 0b00100, 0b00100, 0, 0b00100 },
        ['?'] = new byte[] { 0b01110, 0b10001, 0b00010, 0b00100, 0b00100, 0, 0b00100 },
        ['-'] = new byte[] { 0, 0, 0, 0b11111, 0, 0, 0 },
        ['+'] = new byte[] { 0, 0b00100, 0b00100, 0b11111, 0b00100, 0b00100, 0 },
        ['/'] = new byte[] { 0b00001, 0b00010, 0b00100, 0b00100, 0b01000, 0b10000, 0 },
        ['$'] = new byte[] { 0b00100, 0b01111, 0b10100, 0b01110, 0b00101, 0b11110, 0b00100 },
        ['#'] = new byte[] { 0b01010, 0b11111, 0b01010, 0b01010, 0b11111, 0b01010, 0 },
        ['('] = new byte[] { 0b00010, 0b00100, 0b01000, 0b01000, 0b01000, 0b00100, 0b00010 },
        [')'] = new byte[] { 0b01000, 0b00100, 0b00010, 0b00010, 0b00010, 0b00100, 0b01000 },
        ['['] = new byte[] { 0b01110, 0b01000, 0b01000, 0b01000, 0b01000, 0b01000, 0b01110 },
        [']'] = new byte[] { 0b01110, 0b00010, 0b00010, 0b00010, 0b00010, 0b00010, 0b01110 },
        ['\''] = new byte[] { 0b00100, 0b00100, 0, 0, 0, 0, 0 },
        ['&'] = new byte[] { 0b01100, 0b10010, 0b01100, 0b10101, 0b10010, 0b01101, 0 },
        ['%'] = new byte[] { 0b11001, 0b11010, 0b00100, 0b01000, 0b10110, 0b00110, 0 },
        ['>'] = new byte[] { 0b10000, 0b01000, 0b00100, 0b00010, 0b00100, 0b01000, 0b10000 },
        ['<'] = new byte[] { 0b00010, 0b00100, 0b01000, 0b10000, 0b01000, 0b00100, 0b00010 },
        ['_'] = new byte[] { 0, 0, 0, 0, 0, 0, 0b11111 },
        ['|'] = new byte[] { 0b00100, 0b00100, 0b00100, 0b00100, 0b00100, 0b00100, 0b00100 },
    };

    public static int MeasureWidth(string text, int scale = 1) =>
        string.IsNullOrEmpty(text) ? 0 : (text.Length * 6 - 1) * scale;

    public static void DrawText(PixelCanvas canvas, string text, int x, int y, uint color, int scale = 1, bool shadow = true)
    {
        if (string.IsNullOrEmpty(text)) return;
        uint shadowCol = PixelCanvas.Rgba(12, 12, 18, 220);

        int cursorX = x;
        foreach (char raw in text)
        {
            char ch = char.ToUpperInvariant(raw);
            if (!Glyphs.TryGetValue(ch, out byte[]? rows))
                rows = Glyphs['?'];

            for (int r = 0; r < 7; r++)
            {
                byte bits = rows[r];
                for (int c = 0; c < 5; c++)
                {
                    if ((bits & (1 << (4 - c))) != 0)
                    {
                        if (shadow)
                            canvas.FillRect(cursorX + c * scale + scale, y + r * scale + scale, scale, scale, shadowCol);
                        canvas.FillRect(cursorX + c * scale, y + r * scale, scale, scale, color);
                    }
                }
            }
            cursorX += 6 * scale;
        }
    }

    public static void DrawTextCentered(PixelCanvas canvas, string text, int centerX, int y, uint color, int scale = 1, bool shadow = true)
    {
        int w = MeasureWidth(text, scale);
        DrawText(canvas, text, centerX - w / 2, y, color, scale, shadow);
    }

    /// <summary>
    /// Renders Balatro-style wavy bouncing text (each character oscillates on a gentle sine wave).
    /// </summary>
    public static void DrawWavyBalatroText(PixelCanvas canvas, string text, int x, int y, uint color, float timeSec, int scale = 1)
    {
        if (string.IsNullOrEmpty(text)) return;
        uint shadowCol = PixelCanvas.Rgba(12, 12, 18, 220);
        int cursorX = x;

        for (int idx = 0; idx < text.Length; idx++)
        {
            char ch = char.ToUpperInvariant(text[idx]);
            if (!Glyphs.TryGetValue(ch, out byte[]? rows))
                rows = Glyphs['?'];

            int waveY = (int)MathF.Round(MathF.Sin(timeSec * 7.5f + idx * 0.55f) * 1.4f);

            for (int r = 0; r < 7; r++)
            {
                byte bits = rows[r];
                for (int c = 0; c < 5; c++)
                {
                    if ((bits & (1 << (4 - c))) != 0)
                    {
                        canvas.FillRect(cursorX + c * scale + scale, y + waveY + r * scale + scale, scale, scale, shadowCol);
                        canvas.FillRect(cursorX + c * scale, y + waveY + r * scale, scale, scale, color);
                    }
                }
            }
            cursorX += 6 * scale;
        }
    }
}
