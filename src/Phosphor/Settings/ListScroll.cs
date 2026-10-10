namespace Phosphor.App.Settings;

/// <summary>
/// Прокрутка списка без UI: первая видимая строка, выделение, видимость выделенной строки, бегунок полосы.
/// Чистая логика — тестируется без окна. Индексы — 0..Count-1; Top — индекс первой видимой строки.
/// </summary>
public sealed class ListScroll
{
    public int Count { get; private set; }

    /// <summary>Сколько строк помещается в окне.</summary>
    public int Rows { get; private set; } = 1;

    /// <summary>Индекс первой видимой строки.</summary>
    public int Top { get; private set; }

    /// <summary>Выделенная строка или -1.</summary>
    public int Selected { get; private set; } = -1;

    public int MaxTop => Math.Max(0, Count - Rows);

    public void SetCount(int count)
    {
        Count = Math.Max(0, count);
        if (Selected >= Count) Selected = Count - 1;
        Top = Math.Clamp(Top, 0, MaxTop);
    }

    public void SetRows(int rows)
    {
        Rows = Math.Max(1, rows);
        Top = Math.Clamp(Top, 0, MaxTop);
        if (Selected >= 0) EnsureVisible(Selected);
    }

    public void ScrollTo(int top) => Top = Math.Clamp(top, 0, MaxTop);

    /// <summary>Колесо и полоса: сдвиг первой видимой строки (выделение не меняется).</summary>
    public void ScrollBy(int delta) => ScrollTo(Top + delta);

    /// <summary>Выделить строку (с ограничением по краям) и показать её.</summary>
    public void Select(int index)
    {
        if (Count == 0) { Selected = -1; return; }
        Selected = Math.Clamp(index, 0, Count - 1);
        EnsureVisible(Selected);
    }

    /// <summary>Сдвинуть выделение на delta строк; без выделения первый шаг ставит на первую строку.</summary>
    public void Move(int delta) => Select(Selected < 0 ? 0 : Selected + delta);

    public void EnsureVisible(int index)
    {
        if (index < Top) Top = index;
        else if (index >= Top + Rows) Top = index - Rows + 1;
        Top = Math.Clamp(Top, 0, MaxTop);
    }

    /// <summary>Бегунок полосы: (Y, высота) в пикселях дорожки длиной trackLen. Без прокрутки — весь трек.</summary>
    public (int Y, int H) Thumb(int trackLen)
    {
        if (Count <= Rows || trackLen <= 0) return (0, Math.Max(0, trackLen));
        int h = Math.Clamp(trackLen * Rows / Count, 20, trackLen);
        int y = (int)Math.Round((trackLen - h) * (double)Top / MaxTop);
        return (y, h);
    }

    /// <summary>Позиция бегунка (0..trackLen) → первая видимая строка.</summary>
    public int TopForTrack(int y, int trackLen)
    {
        if (Count <= Rows || trackLen <= 0) return 0;
        double f = Math.Clamp(y / (double)trackLen, 0, 1);
        return (int)Math.Round(f * MaxTop);
    }
}
