namespace BalatroPoker.Game.Graphics;

public enum SwirlTheme
{
    MenuRoyal = 0,
    HoldemEmerald = 1,
    BaccaratCrimson = 2
}

public sealed class PixelCanvas
{
    public const int Width = 640;
    public const int Height = 360;

    public uint[] Pixels { get; } = new uint[Width * Height];

    public static uint Rgba(byte r, byte g, byte b, byte a = 255) =>
        ((uint)a << 24) | ((uint)r << 16) | ((uint)g << 8) | b;

    public void Clear(uint color) => Array.Fill(Pixels, color);

    public void SetPixel(int x, int y, uint color)
    {
        if ((uint)x >= Width || (uint)y >= Height) return;
        Pixels[y * Width + x] = color;
    }

    public void BlendPixel(int x, int y, uint color)
    {
        if ((uint)x >= Width || (uint)y >= Height) return;
        uint a = (color >> 24) & 0xFF;
        if (a == 0) return;
        if (a == 255)
        {
            Pixels[y * Width + x] = color;
            return;
        }

        uint dst = Pixels[y * Width + x];
        uint inv = 255 - a;
        uint r = (((color >> 16) & 0xFF) * a + ((dst >> 16) & 0xFF) * inv) >> 8;
        uint g = (((color >> 8) & 0xFF) * a + ((dst >> 8) & 0xFF) * inv) >> 8;
        uint b = ((color & 0xFF) * a + (dst & 0xFF) * inv) >> 8;
        Pixels[y * Width + x] = 0xFF000000u | (r << 16) | (g << 8) | b;
    }

    public void FillRect(int x, int y, int w, int h, uint color)
    {
        int x0 = Math.Max(0, x);
        int y0 = Math.Max(0, y);
        int x1 = Math.Min(Width, x + w);
        int y1 = Math.Min(Height, y + h);
        if (x0 >= x1 || y0 >= y1) return;

        uint a = (color >> 24) & 0xFF;
        if (a == 255)
        {
            for (int row = y0; row < y1; row++)
            {
                Array.Fill(Pixels, color, row * Width + x0, x1 - x0);
            }
        }
        else if (a > 0)
        {
            for (int row = y0; row < y1; row++)
            {
                for (int col = x0; col < x1; col++)
                    BlendPixel(col, row, color);
            }
        }
    }

    public void DrawRect(int x, int y, int w, int h, uint color, int thickness = 1)
    {
        FillRect(x, y, w, thickness, color);
        FillRect(x, y + h - thickness, w, thickness, color);
        FillRect(x, y, thickness, h, color);
        FillRect(x + w - thickness, y, thickness, h, color);
    }

    public void FillRoundedRect(int x, int y, int w, int h, uint color)
    {
        if (w <= 4 || h <= 4)
        {
            FillRect(x, y, w, h, color);
            return;
        }
        FillRect(x + 2, y, w - 4, h, color);
        FillRect(x + 1, y + 1, w - 2, h - 2, color);
        FillRect(x, y + 2, w, h - 4, color);
    }

    public void DrawBalatroPanel(int x, int y, int w, int h, uint fill, uint border, uint? highlight = null)
    {
        // Drop shadow
        FillRoundedRect(x + 2, y + 3, w, h, Rgba(10, 12, 18, 175));
        // Outer dark outline
        FillRoundedRect(x, y, w, h, border);
        // Inner fill
        FillRoundedRect(x + 2, y + 2, w - 4, h - 4, fill);
        // Subtle top bevel highlight
        if (highlight.HasValue)
        {
            FillRect(x + 4, y + 2, w - 8, 1, highlight.Value);
        }
    }

    public void FillCircle(int cx, int cy, int r, uint color)
    {
        int r2 = r * r;
        for (int dy = -r; dy <= r; dy++)
        {
            int py = cy + dy;
            if ((uint)py >= Height) continue;
            for (int dx = -r; dx <= r; dx++)
            {
                if (dx * dx + dy * dy <= r2)
                {
                    int px = cx + dx;
                    if ((uint)px < Width)
                    {
                        if ((color >> 24) == 0xFF) Pixels[py * Width + px] = color;
                        else BlendPixel(px, py, color);
                    }
                }
            }
        }
    }

    public void FillEllipse(int cx, int cy, int rx, int ry, uint color)
    {
        float invRx2 = 1f / (rx * rx);
        float invRy2 = 1f / (ry * ry);
        int y0 = Math.Max(0, cy - ry);
        int y1 = Math.Min(Height - 1, cy + ry);
        int x0 = Math.Max(0, cx - rx);
        int x1 = Math.Min(Width - 1, cx + rx);

        for (int y = y0; y <= y1; y++)
        {
            float dy = y - cy;
            float dyTerm = dy * dy * invRy2;
            int row = y * Width;
            for (int x = x0; x <= x1; x++)
            {
                float dx = x - cx;
                if (dx * dx * invRx2 + dyTerm <= 1f)
                {
                    Pixels[row + x] = color;
                }
            }
        }
    }

    /// <summary>
    /// Renders a Balatro-inspired psychedelic swirling pixel background at 2x2 chunky pixel resolution.
    /// </summary>
    public void DrawBalatroSwirlBackground(float timeSec, SwirlTheme theme)
    {
        // Three theme colors (deep, mid, accent)
        (byte r1, byte g1, byte b1, byte r2, byte g2, byte b2, byte r3, byte g3, byte b3) = theme switch
        {
            SwirlTheme.HoldemEmerald => ((byte)14, (byte)54, (byte)42, (byte)24, (byte)106, (byte)78, (byte)12, (byte)32, (byte)36),
            SwirlTheme.BaccaratCrimson => ((byte)66, (byte)16, (byte)28, (byte)126, (byte)28, (byte)44, (byte)36, (byte)14, (byte)26),
            _ => ((byte)22, (byte)36, (byte)68, (byte)168, (byte)42, (byte)56, (byte)24, (byte)68, (byte)114)
        };

        float t = timeSec * 0.65f;
        const int halfW = Width / 2;
        const int halfH = Height / 2;

        for (int hy = 0; hy < halfH; hy++)
        {
            float ny = (hy - halfH * 0.5f) / (halfH * 0.5f);
            int row0 = (hy * 2) * Width;
            int row1 = row0 + Width;

            for (int hx = 0; hx < halfW; hx++)
            {
                float nx = (hx - halfW * 0.5f) / (halfH * 0.5f);

                // Domain-warped vortex swirl (inspired by Balatro's iconic background shader)
                float dist = MathF.Sqrt(nx * nx + ny * ny);
                float angle = MathF.Atan2(ny, nx) + dist * 2.2f - t * 0.7f;

                float ux = MathF.Cos(angle) * dist;
                float uy = MathF.Sin(angle) * dist;

                float v1 = MathF.Sin(ux * 3.1f + MathF.Cos(uy * 2.7f + t));
                float v2 = MathF.Cos(uy * 3.4f - MathF.Sin(ux * 2.5f - t * 0.8f));
                float wave = (v1 + v2) * 0.25f + 0.5f;

                // Quantize slightly into retro pixel bands for authentic pixel-art feel
                float band = MathF.Floor(wave * 7f) / 7f;

                byte r, g, b;
                if (band < 0.5f)
                {
                    float k = band * 2f;
                    r = (byte)(r3 + (r1 - r3) * k);
                    g = (byte)(g3 + (g1 - g3) * k);
                    b = (byte)(b3 + (b1 - b3) * k);
                }
                else
                {
                    float k = (band - 0.5f) * 2f;
                    r = (byte)(r1 + (r2 - r1) * k);
                    g = (byte)(g1 + (g2 - g1) * k);
                    b = (byte)(b1 + (b2 - b1) * k);
                }

                uint col = 0xFF000000u | ((uint)r << 16) | ((uint)g << 8) | b;
                int x2 = hx * 2;
                Pixels[row0 + x2] = col;
                Pixels[row0 + x2 + 1] = col;
                Pixels[row1 + x2] = col;
                Pixels[row1 + x2 + 1] = col;
            }
        }
    }

    public void ApplyCrtEffect()
    {
        for (int y = 0; y < Height; y++)
        {
            bool scanline = (y & 1) == 1;
            float vy = (y - Height * 0.5f) / (Height * 0.5f);
            float vy2 = vy * vy;
            int row = y * Width;

            for (int x = 0; x < Width; x++)
            {
                float vx = (x - Width * 0.5f) / (Width * 0.5f);
                float vig = 1f - (vx * vx * 0.16f + vy2 * 0.18f);
                if (scanline) vig *= 0.88f;
                int factor = (int)(Math.Clamp(vig, 0.55f, 1f) * 256);

                uint c = Pixels[row + x];
                uint r = (((c >> 16) & 0xFF) * (uint)factor) >> 8;
                uint g = (((c >> 8) & 0xFF) * (uint)factor) >> 8;
                uint b = ((c & 0xFF) * (uint)factor) >> 8;
                Pixels[row + x] = 0xFF000000u | (r << 16) | (g << 8) | b;
            }
        }
    }
}
