namespace Phosphor.Core;

/// <summary>Вид параметра сцены — от него зависит контрол в окне настроек и как значение пишется в scenes.params.</summary>
public enum SceneParamKind { Float, Int, Bool, Choice }

/// <summary>
/// Описание параметра сцены для окна настроек. Имя — ключ в scenes.params.&lt;id&gt; (то же, что сцена читает через
/// SceneOptions.GetFloat/GetInt/GetBool/GetString). Default обязан совпадать со значением по умолчанию в коде сцены.
/// Bool: Min=0, Max=1, Default 0/1. Choice: Options — допустимые строки, Default — индекс значения по умолчанию.
/// </summary>
public sealed record SceneParamSpec(
    string Name,
    SceneParamKind Kind,
    float Default,
    float Min,
    float Max,
    string Description,
    string[]? Options = null);

/// <summary>Сцена, которая описывает свои параметры (необязательно). Список не меняется между вызовами.</summary>
public interface ISceneParams
{
    IReadOnlyList<SceneParamSpec> Params { get; }
}
