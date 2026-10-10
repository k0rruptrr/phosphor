namespace Phosphor.Core;

/// <summary>
/// Где и как хост кладёт бирку на экран. Anchor: "tl","tc","tr","ml","mc","mr","bl","bc","br" (верх/середина/низ × лево/центр/право).
/// Size — высота бирки, доля высоты экрана. Margin — отступ от края, доля высоты. Opacity 0..1.
/// Over: true — поверх эффектов (чистая, после шлейфа, до корпуса экрана); false — под эффектами (рвётся VHS, плывёт в SECAM).
/// Drift — защита от выгорания: медленный сдвиг на несколько пикселей. Stroke — чёрная обводка текста, px на 1080 строк (0 — нет).
/// Font — семейство для текстовой бирки ("" — шрифт сетки text.font).
/// </summary>
public readonly record struct BadgePlacement(
    string Anchor, float Size, float Margin, float Opacity, bool Over, bool Drift, float Stroke, string Font);

/// <summary>
/// Бирка монитора (часы, логотип): небольшая надпись или картинка в точке экрана, одна на монитор, на всех сценах.
/// Реализуется в Phosphor.Overlays вместе с IOverlayParams (Configure до первого использования).
/// Текстовая бирка: ImagePath == null, хост рендерит сетку Cols×Rows (только глифы, фон прозрачный) шрифтом Placement.Font
/// с высотой клетки = Size·H / Rows. Картиночная: ImagePath — PNG с альфой, Cols/Rows игнорируются, высота = Size·H, ширина по пропорции.
/// Хост перерисовывает текстуру бирки только при смене Version (часы — раз в секунду, логотип — никогда).
/// </summary>
public interface IBadge
{
    string Id { get; }
    int Cols { get; }
    int Rows { get; }
    string? ImagePath { get; }
    BadgePlacement Placement { get; }
    /// <summary>Меняется, когда содержимое Draw изменилось.</summary>
    long Version { get; }
    void Update(float dt, in OverlayContext ctx);
    /// <summary>Нарисовать текст в grid размера Cols×Rows. Только символы из GlyphSet; Bg не важен (рисуются только глифы).</summary>
    void Draw(CharGrid grid);
}
