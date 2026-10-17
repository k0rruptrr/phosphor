using Phosphor.Core;
using Phosphor.Gfx;
using Phosphor.Platform;

namespace Phosphor.App;

/// <summary>
/// Дирижёр-«организм»: решает, когда и на что менять сцены и пресеты на всех мониторах, и ведёт общее
/// самочувствие экрана (дыхание, ночь, усталость), которое видят фильтры (модуляция "src") и оверлей.
/// Сцены про дирижёра не знают. Эстафета — через IHandoffReceiver новой сцены.
/// </summary>
internal sealed class Director
{
    /// <summary>Сцены, у которых свой интерфейс/HUD — оверлей поверх им только мешает.</summary>
    private static readonly HashSet<string> OwnHud = new(StringComparer.OrdinalIgnoreCase)
        { "boids", "voxel", "teletext", "teletype", "weatherboard" };

    private readonly Shared _s;
    private readonly List<MonitorSlot> _slots;
    private readonly List<string> _presets;
    private readonly bool _lockScene, _lockPreset;
    private readonly string? _forcedOverlay;
    private readonly float[] _nextSwitch;
    private float _time;

    // Стрелки: у каждого монитора стек прошлых (сцена, пресет) для «назад»
    private const int HistoryCap = 32;
    private readonly List<(string Scene, string Preset)>[] _history;

    // Дроп: долгое затишье, потом громко и удар
    private float _slowLevel, _quietFor, _dropCooldown;

    // Отладка/проверка в --shot: принудительная смена на кадре N (env PHOSPHOR_SWITCH_AT / _TO / _KIND)
    private readonly long _forceAt;
    private readonly string? _forceTo;
    private readonly TransitionKind? _forceKind;

    public Director(Shared s, List<MonitorSlot> slots, bool lockScene, bool lockPreset)
    {
        _s = s;
        _slots = slots;
        _lockScene = lockScene;
        _lockPreset = lockPreset;
        _presets = s.Config.Filters.Presets.Keys.ToList();
        _forcedOverlay = Environment.GetEnvironmentVariable("PHOSPHOR_OVERLAY") is { Length: > 0 } ov ? ov : null;
        _nextSwitch = new float[slots.Count];
        for (int i = 0; i < slots.Count; i++) _nextSwitch[i] = NextInterval() * (0.5f + 0.5f * i / Math.Max(1, slots.Count)); // мониторы не меняются синхронно
        _history = slots.Select(_ => new List<(string Scene, string Preset)>()).ToArray();

        _forceAt = long.TryParse(Environment.GetEnvironmentVariable("PHOSPHOR_SWITCH_AT"), out var at) ? at : -1;
        _forceTo = Environment.GetEnvironmentVariable("PHOSPHOR_SWITCH_TO");
        _forceKind = Enum.TryParse<TransitionKind>(Environment.GetEnvironmentVariable("PHOSPHOR_SWITCH_KIND"), true, out var k) ? k : null;
    }

    /// <summary>Какой оверлей повесить на сцену (или null).</summary>
    public string? PickOverlay(string sceneId)
    {
        if (_forcedOverlay is not null) return _forcedOverlay;
        var cfg = _s.Config.Overlay;
        if (!cfg.Enabled || OwnHud.Contains(sceneId) || !_s.Overlays.ContainsKey(cfg.Id)) return null;
        return _s.Rng.NextDouble() < cfg.Probability ? cfg.Id : null;
    }

    private float NextInterval()
    {
        float i = _s.Config.Shuffle.IntervalSec;
        return i * (0.75f + 0.5f * (float)_s.Rng.NextDouble());
    }

    /// <summary>Самочувствие экрана для слота i.</summary>
    public Organism OrganismFor(int i, DateTime now) =>
        new(BreathAt(_time), NightFactor(now), FatigueOf(_slots[i]), _slots[i].TransitionPulse);

    private static float BreathAt(float time) => 0.5f + 0.5f * MathF.Sin(time * MathF.Tau / 90f);

    private float FatigueOf(MonitorSlot slot) =>
        Math.Clamp(slot.Current.Age / Math.Max(5f, _s.Config.Shuffle.IntervalSec), 0f, 1f);

    /// <summary>
    /// Отчёт о самочувствии для ключа "phosphor". Только чтение состояния: поведение дирижёра не меняется.
    /// Fatigue — максимум по мониторам; Night — true, когда NightFactor ≥ 0.5.
    /// </summary>
    public PhosphorSnapshot Report(float fps, float bpm, TimeSpan uptime, long frames, DateTime now)
    {
        var scenes = new List<string>(_slots.Count);
        var presets = new List<string>(_slots.Count);
        float fatigue = 0f;
        foreach (var slot in _slots)
        {
            scenes.Add(slot.Current.Scene.Id);
            presets.Add(slot.Current.PresetName);
            fatigue = MathF.Max(fatigue, FatigueOf(slot));
        }
        return new PhosphorSnapshot(fps, scenes, presets, bpm, fatigue, BreathAt(_time), NightFactor(now) >= 0.5f,
            uptime, frames, DateTimeOffset.Now);
    }

    /// <summary>1 с 01:00 до 05:00, плавно нарастает с 22:00 и спадает к 07:00, днём 0.</summary>
    public static float NightFactor(DateTime now)
    {
        float h = now.Hour + now.Minute / 60f;
        if (h >= 1 && h < 5) return 1f;
        if (h >= 22) return (h - 22) / 3f;
        if (h < 1) return (h + 2) / 3f;
        if (h >= 5 && h < 7) return 1f - (h - 5) / 2f;
        return 0f;
    }

    public void Update(float dt, long frame, in AudioFrame audio)
    {
        _time += dt;
        var now = DateTime.Now;

        if (frame == _forceAt)
        {
            for (int i = 0; i < _slots.Count; i++) SwitchScene(i, now, audio, _forceTo, _forceKind);
            return;
        }

        // Дроп в музыке → у всех мониторов смена пресета со сбоем картинки
        _dropCooldown = MathF.Max(0, _dropCooldown - dt);
        if (audio.Active)
        {
            _slowLevel += (audio.Level - _slowLevel) * MathF.Min(1f, dt / 4f);
            _quietFor = _slowLevel < 0.3f ? _quietFor + dt : 0f;
            if (_s.Config.Shuffle.PresetOnDrop && !_lockPreset && _dropCooldown <= 0 && _quietFor > 4f && audio.Level > 0.6f && audio.Beat)
            {
                _s.Log.Info("дирижёр: дроп! смена пресетов");
                foreach (var slot in _slots) slot.BeginPresetSwitch(PickPreset(slot.Current.PresetName, now, audio), 0.6f);
                _dropCooldown = 20f;
                _quietFor = 0;
            }
        }
        else { _slowLevel = 0; _quietFor = 0; }

        if (!_s.Config.Shuffle.Scenes && !_s.Config.Shuffle.Presets) return;
        if (BeatSync(dt, now, audio)) return;
        for (int i = 0; i < _slots.Count; i++)
        {
            if (_slots[i].InTransition) continue;
            _nextSwitch[i] -= dt;
            if (_nextSwitch[i] > 0) continue;
            _nextSwitch[i] = NextInterval();
            if (_s.Config.Shuffle.Scenes && !_lockScene) SwitchScene(i, now, audio, null, null);
            else if (!_lockPreset) _slots[i].BeginPresetSwitch(PickPreset(_slots[i].Current.PresetName, now, audio), 0.6f);
        }
    }

    /// <summary>
    /// Ручная команда с клавиш заставки — на все мониторы. Работает и при lockScene/lockPreset (ручной выбор важнее фиксации).
    /// Слот в переходе пропускается. После команды таймер слота перезапускается, чтобы не сменить сразу.
    /// </summary>
    public void Command(UserCommand c, in AudioFrame audio)
    {
        var now = DateTime.Now;
        _s.Log.Info(c switch
        {
            UserCommand.NextScene => "клавиши: следующая сцена",
            UserCommand.PrevScene => "клавиши: предыдущая сцена",
            UserCommand.NextPreset => "клавиши: следующий пресет",
            _ => "клавиши: предыдущий пресет",
        });
        var presetIds = Selection.PresetPool(_s.Config).Select(p => p.Id).ToList();
        for (int i = 0; i < _slots.Count; i++)
        {
            var slot = _slots[i];
            if (slot.InTransition) continue;
            switch (c)
            {
                case UserCommand.NextScene:
                    _history[i].Add((slot.Current.Scene.Id, slot.Current.PresetName));
                    if (_history[i].Count > HistoryCap) _history[i].RemoveAt(0);
                    SwitchScene(i, now, audio, null, null);
                    break;
                case UserCommand.PrevScene:
                    if (_history[i].Count == 0) break;
                    var (scene, preset) = _history[i][^1];
                    _history[i].RemoveAt(_history[i].Count - 1);
                    SwitchScene(i, now, audio, scene, null, 0, preset);
                    break;
                default:
                    // Шаг по пулу пресетов; текущего в пуле нет — первый
                    if (presetIds.Count == 0) break;
                    int idx = presetIds.FindIndex(p => p.Equals(slot.Current.PresetName, StringComparison.OrdinalIgnoreCase));
                    int n = presetIds.Count;
                    int next = idx < 0 ? 0 : (idx + (c == UserCommand.NextPreset ? 1 : n - 1)) % n;
                    slot.BeginPresetSwitch(presetIds[next], 0.6f);
                    break;
            }
            _nextSwitch[i] = NextInterval();
        }
    }

    /// <summary>
    /// Под музыку сцены меняются не по таймеру, а в такт: каждые PhraseBeats ударов — каскад по мониторам,
    /// один монитор на удар, переход длиной в долю такта. Возвращает true, пока ведёт такт (таймер стоит).
    /// </summary>
    private bool BeatSync(float dt, DateTime now, in AudioFrame audio)
    {
        var cfg = _s.Config.Shuffle;
        if (!cfg.BeatSync || _lockScene || !cfg.Scenes) return false;
        _sinceBeat = audio.Beat ? 0 : _sinceBeat + dt;
        // Удары идут, темп известен — ведём; пропали больше чем на 4 с — отдаём таймеру
        bool grooving = audio.Active && audio.Bpm > 0 && _sinceBeat < 4f;
        if (!grooving) { _cascade = -1; return false; }
        if (!audio.Beat) return true;

        float beatSec = 60f / audio.Bpm;
        if (_cascade < 0 && ++_beats >= Math.Clamp(cfg.PhraseBeats, 8, 256))
        {
            _beats = 0;
            _cascade = 0;
            _s.Log.Info($"дирижёр: фраза ({cfg.PhraseBeats} ударов, {audio.Bpm:F0} bpm) — смена по мониторам в такт");
        }
        if (_cascade >= 0)
        {
            // Этот монитор ещё в переходе — пропускаем его, каскад не ждёт
            if (!_slots[_cascade].InTransition) SwitchScene(_cascade, now, audio, null, null, beatSec);
            if (++_cascade >= _slots.Count) _cascade = -1;
        }
        return true; // таймер стоит, пока ведёт музыка, и продолжит с того же места
    }

    private int _beats, _cascade = -1;
    private float _sinceBeat = 999f;

    private void SwitchScene(int i, DateTime now, in AudioFrame audio, string? forcedScene, TransitionKind? forcedKind, float beatSec = 0, string? forcedPreset = null)
    {
        var slot = _slots[i];
        string scene = forcedScene ?? PickScene(i);
        string preset = forcedPreset ?? (_lockPreset || !_s.Config.Shuffle.Presets ? slot.Current.PresetName : PickPreset(slot.Current.PresetName, now, audio));
        var probe = _s.Scenes.Create(scene);
        bool handoff = probe is IHandoffReceiver;
        var kind = forcedKind ?? PickKind(handoff);
        float dur = _s.Config.Shuffle.TransitionSec * (handoff && kind == TransitionKind.Fade ? 1.6f : 1f);
        // В такт: эстафета — плавно за 2 удара, остальное — помехой ровно в один удар
        if (beatSec > 0) dur = Math.Clamp(beatSec * (handoff && kind == TransitionKind.Fade ? 2f : 1f), 0.25f, 1.6f);
        slot.BeginSceneSwitch(scene, preset, kind, dur, PickOverlay(scene), handoff);
    }

    private string PickScene(int i)
    {
        // Веса: любимые чаще (Selection.ScenePool). Не повторять текущую и не дублировать то, что сейчас на других мониторах
        var pool = Selection.ScenePool(_s.Config, _s.Scenes.Ids);
        var busy = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var sl in _slots) { busy.Add(sl.Current.Scene.Id); if (sl.Incoming is not null) busy.Add(sl.Incoming.Scene.Id); }
        var exclude = busy;
        if (!pool.Any(p => !busy.Contains(p.Id)))
            exclude = new HashSet<string>(new[] { _slots[i].Current.Scene.Id }, StringComparer.OrdinalIgnoreCase);
        return Selection.Pick(pool, _s.Rng.NextDouble(), exclude);
    }

    /// <summary>Пресет по настроению: ночью — фосфорные терминалы, под музыку — rave; без музыки rave редкий.
    /// Веса настроения умножаются на вес из Selection.PresetPool; пресеты вне пула не выбираются.</summary>
    private string PickPreset(string current, DateTime now, in AudioFrame audio)
    {
        if (_presets.Count <= 1) return current;
        float night = NightFactor(now);
        var poolWeight = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var (id, weight) in Selection.PresetPool(_s.Config)) poolWeight[id] = weight;
        double total = 0;
        var w = new double[_presets.Count];
        for (int k = 0; k < _presets.Count; k++)
        {
            var p = _presets[k];
            double weight = 1;
            if (p.Equals(current, StringComparison.OrdinalIgnoreCase)) weight = 0;
            else if (p.Equals("rave", StringComparison.OrdinalIgnoreCase)) weight = audio.Active ? 4 : 0.15;
            else if (p is "terminal" or "amber") weight = 1 + 2 * night;
            weight *= poolWeight.TryGetValue(p, out var pw) ? pw : 0;
            w[k] = weight;
            total += weight;
        }
        if (total <= 0) return current;
        double r = _s.Rng.NextDouble() * total;
        for (int k = 0; k < w.Length; k++) { r -= w[k]; if (r <= 0) return _presets[k]; }
        return _presets[^1];
    }

    private TransitionKind PickKind(bool handoff)
    {
        var t = _s.Config.Shuffle.Transition;
        if (t.Equals("fade", StringComparison.OrdinalIgnoreCase)) return TransitionKind.Fade;
        if (t.Equals("vhs-noise", StringComparison.OrdinalIgnoreCase)) return TransitionKind.VhsNoise;
        if (t.Equals("cut", StringComparison.OrdinalIgnoreCase)) return TransitionKind.Cut;
        // "mixed": эстафету лучше видно на плавном переходе — силуэт старой сцены проступает в новой
        if (handoff) return _s.Rng.NextDouble() < 0.75 ? TransitionKind.Fade : TransitionKind.VhsNoise;
        return _s.Rng.NextDouble() < 0.5 ? TransitionKind.Fade : TransitionKind.VhsNoise;
    }
}
