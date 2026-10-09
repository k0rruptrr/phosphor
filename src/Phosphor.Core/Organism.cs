namespace Phosphor.Core;

/// <summary>
/// Необязательно. Сцена умеет «прорасти» из последнего кадра предыдущей сцены (эстафета дирижёра).
/// Вызывается сразу после Init и до первого Update. previous — последний кадр старой сцены; его размер
/// может отличаться от текущей сетки (другая высота ячейки) — масштабируй координаты сам.
/// Яркость ячейки = max(Fg) для непробельных символов и max(Bg) для фона. Только rng из Init.
/// </summary>
public interface IHandoffReceiver
{
    void Seed(CharGrid previous);
}

/// <summary>
/// Общее «самочувствие» экрана, которое ведёт дирижёр. Все значения 0..1, меняются плавно.
/// Попадает в FrameInfo — фильтры могут модулироваться им так же, как звуком ("src": "breath" и т.д.).
/// </summary>
public readonly record struct Organism(
    float Breath,   // медленное дыхание: синус с периодом ~90 с, 0..1
    float Night,    // 1 глубокой ночью (01–05), 0 днём, плавные сумерки
    float Fatigue,  // «усталость» текущей сцены: растёт с её возрастом, сбрасывается при смене
    float Transition) // 0 вне перехода, 0→1→0 во время смены сцены
{
    public float Get(string source) => source switch
    {
        "breath" => Breath,
        "night" => Night,
        "fatigue" => Fatigue,
        "transition" => Transition,
        _ => 0f,
    };

    public static readonly string[] Sources = { "breath", "night", "fatigue", "transition" };
}
