using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace DesktopPet.Rendering.D3D11;

/// <summary>
/// Renders a subtitle string into an RGBA byte array using GDI+.
/// Font size is fixed in physical pixels so text stays legible at any window
/// size. The texture height grows automatically to fit multiple lines.
/// </summary>
internal sealed class SubtitleRenderer
{
    private const int   MinWidth     = 64;
    private const int   MaxWidth     = 2048;
    private const int   MaxHeight    = 512;
    private const float QuadWidthRatio = 0.90f;
    private const float FontSizePx   = 20f;

    private byte[]? _pixels;

    public int TexWidth  { get; private set; }
    public int TexHeight { get; private set; }

    /// <summary>
    /// Renders <paramref name="text"/> into an RGBA pixel array.
    /// Texture width = window width × 90%; height expands to fit all lines.
    /// Returns null when text is empty.
    /// </summary>
    public byte[]? Render(string text, int windowWidth, int windowHeight)
    {
        // 过滤 GDI+ 无法渲染的 Emoji（Surrogate pairs 及常见符号区间），避免乱码方块。
        text = StripEmoji(text);

        if (string.IsNullOrWhiteSpace(text))
        {
            TexWidth = TexHeight = 0;
            return null;
        }

        var texW = Math.Clamp((int)(windowWidth * QuadWidthRatio), MinWidth, MaxWidth);

        // ── 用 dummy Graphics 预量文字高度，决定纹理需要多高 ─────────────────
        using var dummy  = new Bitmap(1, 1);
        using var dummyG = Graphics.FromImage(dummy);
        using var font   = new Font("Microsoft YaHei UI", FontSizePx, FontStyle.Regular, GraphicsUnit.Pixel);

        var pad      = 10f;
        var textW    = texW - pad * 4;
        using var fmt = BuildFormat();
        var measured = dummyG.MeasureString(text, font, (int)Math.Max(1f, textW), fmt);

        var texH = Math.Clamp((int)(measured.Height + pad * 3), 1, MaxHeight);

        TexWidth  = texW;
        TexHeight = texH;

        // ── 渲染到 Bitmap ─────────────────────────────────────────────────────
        using var bitmap = new Bitmap(texW, texH, PixelFormat.Format32bppArgb);
        using var g      = Graphics.FromImage(bitmap);

        g.SmoothingMode     = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        g.Clear(Color.Transparent);

        // 半透明背景（alpha=185，在亮色背景下也能保证文字可读）
        var bgRect = new RectangleF(pad / 2, pad / 2, texW - pad, texH - pad);
        using var bgBrush = new SolidBrush(Color.FromArgb(185, 0, 0, 0));
        FillRoundedRectangle(g, bgBrush, bgRect, 12f);

        // 文字（多行自动换行，不省略）
        using var textBrush = new SolidBrush(Color.White);
        var textRect = new RectangleF(pad * 2, pad, texW - pad * 4, texH - pad * 2);
        g.DrawString(text, font, textBrush, textRect, fmt);

        // ── BGRA → RGBA ───────────────────────────────────────────────────────
        var needed = texW * texH * 4;
        if (_pixels is null || _pixels.Length < needed)
            _pixels = new byte[needed];
        else
            Array.Clear(_pixels, 0, needed);

        var bmpData = bitmap.LockBits(
            new Rectangle(0, 0, texW, texH),
            ImageLockMode.ReadOnly,
            PixelFormat.Format32bppArgb);
        try
        {
            unsafe
            {
                var src   = (byte*)bmpData.Scan0;
                var count = texW * texH;
                for (var i = 0; i < count; i++)
                {
                    _pixels[i * 4 + 0] = src[i * 4 + 2]; // R
                    _pixels[i * 4 + 1] = src[i * 4 + 1]; // G
                    _pixels[i * 4 + 2] = src[i * 4 + 0]; // B
                    _pixels[i * 4 + 3] = src[i * 4 + 3]; // A
                }
            }
        }
        finally
        {
            bitmap.UnlockBits(bmpData);
        }

        return _pixels;
    }

    /// <summary>
    /// 移除 GDI+ 无法渲染的 Emoji 及特殊符号，避免出现乱码方块。
    /// 过滤范围：Surrogate pair（U+1F000+）、常见符号/箭头/杂项图形块。
    /// </summary>
    private static string StripEmoji(string text)
    {
        var sb = new System.Text.StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];

            // Surrogate pair → 完整码点在 BMP 以外（U+10000~U+10FFFF），全部跳过
            if (char.IsHighSurrogate(c))
            {
                if (i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                    i++; // 跳过低代理
                continue;
            }

            // BMP 内的 Emoji / 特殊符号区间
            var cp = (int)c;
            if (cp is
                >= 0x2000 and <= 0x27FF or   // 通用标点、箭头、数学运算符、杂项技术
                >= 0x2900 and <= 0x2BFF or   // 补充箭头、杂项符号
                >= 0x3000 and <= 0x303F or   // CJK 符号（保留标点，跳过装饰）
                >= 0xFE00 and <= 0xFE0F or   // 变体选择符
                0x200D                        // Zero-width joiner
               )
            {
                continue;
            }

            sb.Append(c);
        }
        return sb.ToString();
    }

    private static StringFormat BuildFormat() => new()
    {
        Alignment     = StringAlignment.Center,
        LineAlignment = StringAlignment.Center,  // 垂直居中
        Trimming      = StringTrimming.None,     // 不省略，允许自动换行
    };

    private static void FillRoundedRectangle(Graphics g, Brush brush, RectangleF rect, float radius)
    {
        using var path = new GraphicsPath();
        var d = radius * 2;
        path.AddArc(rect.X,                   rect.Y,                    d, d, 180, 90);
        path.AddArc(rect.X + rect.Width - d,  rect.Y,                    d, d, 270, 90);
        path.AddArc(rect.X + rect.Width - d,  rect.Y + rect.Height - d,  d, d,   0, 90);
        path.AddArc(rect.X,                   rect.Y + rect.Height - d,  d, d,  90, 90);
        path.CloseFigure();
        g.FillPath(brush, path);
    }
}
