namespace BalatroPoker.Game.Graphics;

public readonly record struct UiRect(int X, int Y, int W, int H)
{
    public bool Contains(int px, int py) => px >= X && px < X + W && py >= Y && py < Y + H;
}

public sealed class ParticleSystem
{
    private struct Particle
    {
        public float X, Y, Vx, Vy, Life;
        public uint Color;
        public int Size;
    }

    private readonly List<Particle> _particles = new();
    private readonly Random _rng = new();

    public void SpawnWinBurst(int cx, int cy, int count = 36)
    {
        uint[] palette =
        {
            PixelCanvas.Rgba(255, 214, 70),
            PixelCanvas.Rgba(255, 92, 92),
            PixelCanvas.Rgba(78, 205, 255),
            PixelCanvas.Rgba(106, 236, 138),
            PixelCanvas.Rgba(255, 255, 255)
        };

        for (int i = 0; i < count; i++)
        {
            float ang = (float)(_rng.NextDouble() * Math.PI * 2);
            float spd = 35f + (float)_rng.NextDouble() * 110f;
            _particles.Add(new Particle
            {
                X = cx,
                Y = cy,
                Vx = MathF.Cos(ang) * spd,
                Vy = MathF.Sin(ang) * spd - 35f,
                Life = 0.7f + (float)_rng.NextDouble() * 0.8f,
                Color = palette[_rng.Next(palette.Length)],
                Size = _rng.Next(2, 4)
            });
        }
    }

    public void UpdateAndDraw(PixelCanvas canvas, float dt)
    {
        for (int i = _particles.Count - 1; i >= 0; i--)
        {
            var p = _particles[i];
            p.Life -= dt;
            if (p.Life <= 0)
            {
                _particles.RemoveAt(i);
                continue;
            }
            p.Vy += 150f * dt;
            p.X += p.Vx * dt;
            p.Y += p.Vy * dt;
            _particles[i] = p;
            canvas.FillRect((int)p.X, (int)p.Y, p.Size, p.Size, p.Color);
        }
    }
}

public static class BalatroUi
{
    public static readonly uint ColBgDark = PixelCanvas.Rgba(24, 28, 38);
    public static readonly uint ColPanel = PixelCanvas.Rgba(34, 40, 54);
    public static readonly uint ColPanelBorder = PixelCanvas.Rgba(16, 18, 26);
    public static readonly uint ColWhite = PixelCanvas.Rgba(248, 246, 240);
    public static readonly uint ColMuted = PixelCanvas.Rgba(172, 180, 198);
    public static readonly uint ColGold = PixelCanvas.Rgba(255, 206, 68);
    public static readonly uint ColRed = PixelCanvas.Rgba(226, 66, 76);
    public static readonly uint ColBlue = PixelCanvas.Rgba(62, 142, 232);
    public static readonly uint ColGreen = PixelCanvas.Rgba(58, 178, 112);
    public static readonly uint ColOrange = PixelCanvas.Rgba(238, 134, 48);
    public static readonly uint ColPurple = PixelCanvas.Rgba(148, 86, 212);

    public static bool DrawButton(
        PixelCanvas canvas,
        UiRect rect,
        string label,
        uint baseColor,
        int mouseX,
        int mouseY,
        bool mouseDown,
        bool mouseJustReleased,
        bool enabled = true,
        int textScale = 1)
    {
        bool hover = enabled && rect.Contains(mouseX, mouseY);
        bool pressed = hover && mouseDown;
        int yOff = pressed ? 1 : 0;

        uint fill = !enabled
            ? PixelCanvas.Rgba(68, 72, 84)
            : hover
                ? Brighten(baseColor, 22)
                : baseColor;

        uint bottomShade = Darken(fill, 45);
        uint topGlow = Brighten(fill, 35);

        // Drop shadow
        canvas.FillRoundedRect(rect.X + 1, rect.Y + 3, rect.W, rect.H, PixelCanvas.Rgba(10, 12, 18, 180));
        // Outline
        canvas.FillRoundedRect(rect.X, rect.Y + yOff, rect.W, rect.H, ColPanelBorder);
        // Body
        canvas.FillRoundedRect(rect.X + 2, rect.Y + 2 + yOff, rect.W - 4, rect.H - 4, fill);
        // 3D arcade bottom edge & top bevel
        canvas.FillRect(rect.X + 3, rect.Y + rect.H - 4 + yOff, rect.W - 6, 2, bottomShade);
        canvas.FillRect(rect.X + 3, rect.Y + 2 + yOff, rect.W - 6, 1, topGlow);

        uint textCol = enabled ? ColWhite : PixelCanvas.Rgba(140, 146, 160);
        int textY = rect.Y + (rect.H - 7 * textScale) / 2 + yOff;
        PixelFont.DrawTextCentered(canvas, label, rect.X + rect.W / 2, textY, textCol, textScale, shadow: true);

        return hover && mouseJustReleased;
    }

    public static bool DrawTextInput(
        PixelCanvas canvas,
        UiRect rect,
        string label,
        string value,
        bool focused,
        int mouseX,
        int mouseY,
        bool mouseJustPressed,
        float timeSec)
    {
        PixelFont.DrawText(canvas, label, rect.X, rect.Y - 10, ColMuted, 1);

        uint border = focused ? ColGold : ColPanelBorder;
        canvas.DrawBalatroPanel(rect.X, rect.Y, rect.W, rect.H, ColBgDark, border);

        string display = value;
        if (focused && ((int)(timeSec * 2.5f) & 1) == 0)
            display += "_";

        PixelFont.DrawText(canvas, display, rect.X + 6, rect.Y + (rect.H - 7) / 2, ColWhite, 1);
        return mouseJustPressed && rect.Contains(mouseX, mouseY);
    }

    private static uint Brighten(uint col, int delta)
    {
        byte r = (byte)Math.Min(255, ((col >> 16) & 0xFF) + delta);
        byte g = (byte)Math.Min(255, ((col >> 8) & 0xFF) + delta);
        byte b = (byte)Math.Min(255, (col & 0xFF) + delta);
        return PixelCanvas.Rgba(r, g, b);
    }

    private static uint Darken(uint col, int delta)
    {
        byte r = (byte)Math.Max(0, (int)((col >> 16) & 0xFF) - delta);
        byte g = (byte)Math.Max(0, (int)((col >> 8) & 0xFF) - delta);
        byte b = (byte)Math.Max(0, (int)(col & 0xFF) - delta);
        return PixelCanvas.Rgba(r, g, b);
    }
}
