using BalatroPoker.Core.Cards;

namespace BalatroPoker.Game.Graphics;

public static class CardSprites
{
    // 7x7 pixel suit icons
    private static readonly byte[] SpadeIcon =
    {
        0b0001000,
        0b0011100,
        0b0111110,
        0b1111111,
        0b1111111,
        0b0101010,
        0b0011100
    };

    private static readonly byte[] HeartIcon =
    {
        0b0110110,
        0b1111111,
        0b1111111,
        0b1111111,
        0b0111110,
        0b0011100,
        0b0001000
    };

    private static readonly byte[] DiamondIcon =
    {
        0b0001000,
        0b0011100,
        0b0111110,
        0b1111111,
        0b0111110,
        0b0011100,
        0b0001000
    };

    private static readonly byte[] ClubIcon =
    {
        0b0011100,
        0b0011100,
        0b1101011,
        0b1111111,
        0b1101011,
        0b0001000,
        0b0011100
    };

    public static void DrawCard(
        PixelCanvas canvas,
        Card card,
        int x,
        int y,
        bool compact = false,
        bool highlightGold = false,
        float bobPhase = 0f)
    {
        int w = compact ? 24 : 40;
        int h = compact ? 34 : 56;
        int bobY = bobPhase != 0f ? (int)MathF.Round(MathF.Sin(bobPhase) * 1.5f) : 0;
        int cy = y + bobY;

        // Chunky pixel drop shadow
        canvas.FillRoundedRect(x + 2, cy + 3, w, h, PixelCanvas.Rgba(8, 10, 16, 175));

        // Optional golden Balatro win aura
        if (highlightGold)
        {
            canvas.FillRoundedRect(x - 2, cy - 2, w + 4, h + 4, PixelCanvas.Rgba(255, 206, 68));
        }

        uint border = PixelCanvas.Rgba(24, 24, 34);
        canvas.FillRoundedRect(x, cy, w, h, border);

        if (card.FaceDown)
        {
            DrawCardBack(canvas, x, cy, w, h);
            return;
        }

        // Cream pixel parchment face
        uint face = PixelCanvas.Rgba(248, 242, 228);
        uint innerShade = PixelCanvas.Rgba(232, 224, 206);
        canvas.FillRoundedRect(x + 1, cy + 1, w - 2, h - 2, face);
        canvas.FillRect(x + 2, cy + h - 3, w - 4, 2, innerShade);

        // Balatro-inspired high-contrast 4-color / classic vibrant suit palette
        uint suitColor = card.Suit switch
        {
            Suit.Hearts => PixelCanvas.Rgba(226, 52, 66),
            Suit.Diamonds => PixelCanvas.Rgba(238, 108, 44),
            Suit.Clubs => PixelCanvas.Rgba(40, 118, 92),
            _ => PixelCanvas.Rgba(34, 42, 62)
        };

        string rankStr = card.ShortRank;
        if (compact)
        {
            PixelFont.DrawText(canvas, rankStr, x + 3, cy + 3, suitColor, 1, shadow: false);
            DrawSuitIcon(canvas, card.Suit, x + w / 2 - 3, cy + h / 2, suitColor, 1);
        }
        else
        {
            // Top-left index
            PixelFont.DrawText(canvas, rankStr, x + 4, cy + 4, suitColor, 1, shadow: false);
            DrawSuitIcon(canvas, card.Suit, x + 4, cy + 13, suitColor, 1);

            // Big center emblem
            DrawSuitIcon(canvas, card.Suit, x + w / 2 - 7, cy + h / 2 - 7, suitColor, 2);

            // Bottom-right rank
            int rw = PixelFont.MeasureWidth(rankStr, 1);
            PixelFont.DrawText(canvas, rankStr, x + w - rw - 4, cy + h - 11, suitColor, 1, shadow: false);
        }
    }

    private static void DrawCardBack(PixelCanvas canvas, int x, int y, int w, int h)
    {
        uint outerWhite = PixelCanvas.Rgba(240, 236, 228);
        uint backRed = PixelCanvas.Rgba(186, 42, 58);
        uint backDark = PixelCanvas.Rgba(128, 24, 42);
        uint goldTrim = PixelCanvas.Rgba(244, 198, 76);

        canvas.FillRoundedRect(x + 1, y + 1, w - 2, h - 2, outerWhite);
        canvas.FillRect(x + 3, y + 3, w - 6, h - 6, backRed);

        // Pixel diamond lattice pattern
        for (int py = y + 4; py < y + h - 4; py += 2)
        {
            for (int px = x + 4; px < x + w - 4; px += 2)
            {
                if (((px + py) & 3) == 0)
                    canvas.SetPixel(px, py, backDark);
            }
        }

        canvas.DrawRect(x + 4, y + 4, w - 8, h - 8, goldTrim, 1);
        // Center diamond medallion
        int cx = x + w / 2;
        int cy = y + h / 2;
        canvas.FillRect(cx - 2, cy - 2, 5, 5, goldTrim);
        canvas.FillRect(cx - 1, cy - 1, 3, 3, backRed);
    }

    public static void DrawSuitIcon(PixelCanvas canvas, Suit suit, int x, int y, uint color, int scale = 1)
    {
        byte[] mask = suit switch
        {
            Suit.Spades => SpadeIcon,
            Suit.Hearts => HeartIcon,
            Suit.Diamonds => DiamondIcon,
            _ => ClubIcon
        };

        for (int r = 0; r < 7; r++)
        {
            byte row = mask[r];
            for (int c = 0; c < 7; c++)
            {
                if ((row & (1 << (6 - c))) != 0)
                {
                    canvas.FillRect(x + c * scale, y + r * scale, scale, scale, color);
                }
            }
        }
    }

    public static void DrawChip(PixelCanvas canvas, int cx, int cy, int radius, uint mainColor, string? label = null)
    {
        canvas.FillCircle(cx + 1, cy + 2, radius, PixelCanvas.Rgba(10, 12, 18, 170));
        canvas.FillCircle(cx, cy, radius, PixelCanvas.Rgba(24, 24, 34));
        canvas.FillCircle(cx, cy, radius - 1, mainColor);
        // White casino notches
        uint notch = PixelCanvas.Rgba(248, 244, 232);
        canvas.FillRect(cx - 1, cy - radius + 1, 3, 2, notch);
        canvas.FillRect(cx - 1, cy + radius - 2, 3, 2, notch);
        canvas.FillRect(cx - radius + 1, cy - 1, 2, 3, notch);
        canvas.FillRect(cx + radius - 2, cy - 1, 2, 3, notch);
        canvas.FillCircle(cx, cy, Math.Max(2, radius - 3), PixelCanvas.Rgba(248, 244, 232));
        canvas.FillCircle(cx, cy, Math.Max(1, radius - 4), mainColor);

        if (!string.IsNullOrEmpty(label))
        {
            PixelFont.DrawTextCentered(canvas, label, cx, cy - 3, PixelCanvas.Rgba(255, 255, 255), 1, shadow: true);
        }
    }
}
