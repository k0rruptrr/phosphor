using System.Runtime.InteropServices;

namespace Phosphor.App.Settings;

// В Phosphor.App есть свой тип Font: в этом пространстве имён берём System.Drawing.Font
using Font = System.Drawing.Font;

/// <summary>Тема окна настроек: тёмная, моноширинная. Без сторонних библиотек.</summary>
internal static class Theme
{
    public static readonly Color Bg = Color.FromArgb(0x12, 0x12, 0x12);
    public static readonly Color Panel = Color.FromArgb(0x1b, 0x1b, 0x1b);
    public static readonly Color Text = Color.FromArgb(0xd8, 0xd8, 0xd8);
    public static readonly Color Muted = Color.FromArgb(0x7a, 0x7a, 0x7a);
    public static readonly Color Accent = Color.FromArgb(0xff, 0xb0, 0x00);
    public static readonly Color Border = Color.FromArgb(0x38, 0x38, 0x38);
    public static readonly Color Select = Color.FromArgb(0x2c, 0x22, 0x0c);

    private static Font? _mono;
    private static Font? _glyph;

    /// <summary>Consolas 10pt (моноширинный).</summary>
    public static Font Mono => _mono ??= new Font("Consolas", 10f, FontStyle.Regular, GraphicsUnit.Point);

    /// <summary>Шрифт для ★ ● ○ (Consolas их не содержит).</summary>
    public static Font Glyph => _glyph ??= new Font("Segoe UI Symbol", 10f, FontStyle.Regular, GraphicsUnit.Point);

    /// <summary>Рекурсивно красит контролы в тему. Таблицы и списки — отдельно (см. SettingsForm).</summary>
    public static void Apply(Control root)
    {
        foreach (Control c in root.Controls)
        {
            Style(c);
            Apply(c);
        }
    }

    private static void Style(Control c)
    {
        c.Font = c.Tag is Font own ? own : Mono;
        switch (c)
        {
            case Form:
            case TabPage:
                c.BackColor = Bg;
                c.ForeColor = Text;
                break;
            case Button b:
                b.FlatStyle = FlatStyle.Flat;
                b.BackColor = Panel;
                b.ForeColor = Text;
                b.FlatAppearance.BorderColor = Border;
                b.FlatAppearance.MouseOverBackColor = Select;
                b.FlatAppearance.MouseDownBackColor = Select;
                b.UseVisualStyleBackColor = false;
                break;
            case CheckBox k:
                k.FlatStyle = FlatStyle.Flat;
                k.BackColor = Bg;
                k.ForeColor = Text;
                k.FlatAppearance.BorderColor = Border;
                k.FlatAppearance.CheckedBackColor = Panel;
                k.FlatAppearance.MouseDownBackColor = Select;
                k.FlatAppearance.MouseOverBackColor = Select;
                break;
            case ComboBox cb:
                cb.FlatStyle = FlatStyle.Flat;
                cb.BackColor = Panel;
                cb.ForeColor = Text;
                cb.DrawMode = DrawMode.OwnerDrawFixed;
                cb.DrawItem -= DrawComboItem;
                cb.DrawItem += DrawComboItem;
                break;
            case NumericUpDown n:
                n.BorderStyle = BorderStyle.FixedSingle;
                n.BackColor = Panel;
                n.ForeColor = Text;
                break;
            case TextBox t:
                t.BorderStyle = BorderStyle.FixedSingle;
                t.BackColor = Panel;
                t.ForeColor = Text;
                break;
            case Label l:
                l.BackColor = Color.Transparent;
                l.ForeColor = Text;
                break;
        }
    }

    private static void DrawComboItem(object? sender, DrawItemEventArgs e)
    {
        if (sender is not ComboBox cb) return;
        bool sel = (e.State & DrawItemState.Selected) != 0;
        using var bg = new SolidBrush(sel ? Select : Panel);
        e.Graphics.FillRectangle(bg, e.Bounds);
        if (e.Index >= 0)
            TextRenderer.DrawText(e.Graphics, cb.GetItemText(cb.Items[e.Index]), Mono, e.Bounds,
                sel ? Accent : Text, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
    }

    /// <summary>Тёмная полоса прокрутки и списков (Explorer DarkMode, Windows 10 1809+).</summary>
    public static void DarkScrollbars(Control c)
    {
        try
        {
            SetWindowTheme(c.Handle, "DarkMode_Explorer", null);
            // Нативная полоса прокрутки перечитывает тему только после WM_THEMECHANGED и пересчёта рамки
            SendMessage(c.Handle, 0x031A, 0, 0);
            SetWindowPos(c.Handle, 0, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0004 | 0x0010 | 0x0020);
        }
        catch (Exception) { /* старая система — остаётся светлая полоса */ }
    }

    /// <summary>Тёмный режим для нативных полос прокрутки (uxtheme, без документации; Windows 10 1809+). Только для процесса окна настроек.</summary>
    public static void ForceDarkNativeControls()
    {
        try { SetPreferredAppMode(2); } // 2 = ForceDark
        catch (Exception) { /* старая система */ }
    }

    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
    private static extern int SetWindowTheme(nint hwnd, string pszSubAppName, string? pszSubIdList);

    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern nint SendMessage(nint hwnd, uint msg, nint wParam, nint lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(nint hwnd, nint insertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("uxtheme.dll", EntryPoint = "#135")]
    private static extern int SetPreferredAppMode(int mode);
}
