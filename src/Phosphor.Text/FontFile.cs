using StbTrueTypeSharp;
using System.Runtime.InteropServices;

namespace Phosphor.Text;

/// <summary>Загруженный файл шрифта (первый face для TTC). Байты закреплены в памяти до Dispose.</summary>
internal sealed unsafe class FontFile : IDisposable
{
    private GCHandle _handle;

    public StbTrueType.stbtt_fontinfo Info { get; }
    /// <summary>hhea ascent/descent в единицах шрифта (descent отрицательный).</summary>
    public int Ascent { get; }
    public int Descent { get; }

    private FontFile(GCHandle handle, StbTrueType.stbtt_fontinfo info, int ascent, int descent)
    {
        _handle = handle;
        Info = info;
        Ascent = ascent;
        Descent = descent;
    }

    public static FontFile Load(string path)
    {
        byte[] data = File.ReadAllBytes(path);
        var handle = GCHandle.Alloc(data, GCHandleType.Pinned);
        try
        {
            byte* p = (byte*)handle.AddrOfPinnedObject();
            int offset = StbTrueType.stbtt_GetFontOffsetForIndex(p, 0);
            if (offset < 0) throw new InvalidDataException($"Не удалось найти шрифт в '{path}'");

            var info = new StbTrueType.stbtt_fontinfo();
            if (StbTrueType.stbtt_InitFont(info, p, offset) == 0)
                throw new InvalidDataException($"stbtt_InitFont не смог разобрать '{path}'");

            int asc, desc, gap;
            StbTrueType.stbtt_GetFontVMetrics(info, &asc, &desc, &gap);
            return new FontFile(handle, info, asc, desc);
        }
        catch (InvalidDataException)
        {
            handle.Free();
            throw;
        }
        catch (Exception e)
        {
            // stb_truetype бросает общий Exception (нет cmap и т.п.) — приводим к InvalidDataException для вызывающих
            handle.Free();
            throw new InvalidDataException($"Не удалось разобрать '{path}': {e.Message}", e);
        }
    }

    /// <summary>Advance width глифа c в единицах шрифта; 0, если глифа нет.</summary>
    public int Advance(char c)
    {
        int adv, lsb;
        StbTrueType.stbtt_GetCodepointHMetrics(Info, c, &adv, &lsb);
        return adv;
    }

    /// <summary>Есть ли в шрифте глиф для c (glyph index 0 — «.notdef»).</summary>
    public bool Has(char c) => StbTrueType.stbtt_FindGlyphIndex(Info, c) != 0;

    /// <summary>Строка из таблицы name (UTF-16BE). Сначала английская (платформа Windows, 0x409), затем Mac. null — нет.</summary>
    public string? Name(int nameId)
    {
        string? mac = null;
        foreach (var (platform, encoding, lang) in new[] { (3, 1, 0x409), (1, 0, 0) })
        {
            int len = 0;
            byte* s = (byte*)StbTrueType.stbtt_GetFontNameString(Info, &len, platform, encoding, lang, nameId);
            if (s == null || len <= 0) continue;
            string text = platform == 3
                ? System.Text.Encoding.BigEndianUnicode.GetString(s, len)
                : System.Text.Encoding.Latin1.GetString(s, len);
            if (platform == 3) return text.Trim();
            mac ??= text.Trim();
        }
        return mac;
    }

    public void Dispose()
    {
        if (_handle.IsAllocated) _handle.Free();
    }
}
