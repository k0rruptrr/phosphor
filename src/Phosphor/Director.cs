using Phosphor.Core;
using Phosphor.Gfx;

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
    private readonly List<string> _scenes;
    private readonly List<string> _presets;
    private readonly bool _lockScene, _lockPreset;
    private readonly string? _forcedOverlay;
    private readonly float[] _nextSwitch;
    private float _time;

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
        _scenes = s.Config.Scenes.Enabled.Where(s.Scenes.Has).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (_scenes.Count == 0) _scenes.AddRange(s.Scenes.Ids);
        _presets = s.Config.Filters.Presets.Keys.ToList();
        _forcedOverlay = Environment.GetEnvironmentVariable("PHOSPHOR_OVERLAY") is { Length: > 0 } ov ? ov : null;
        _nextSwitch = new float[slots.Count];
        for (int i = 0; i < slots.Count; i++) _nextSwitch[i] = NextInterval() * (0.5f + 0.5f * i / Math.Max(1, slots.Count)); // мониторы не меняются синхронно

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
    public Organism OrganismFor(int i, DateTime now)
    {
        float breath = 0.5f + 0.5f * MathF.Sin(_time * MathF.Tau / 90f);
        float fatigue = Math.Clamp(_slots[i].Current.Age / Math.Max(5f, _s.Config.Shuffle.IntervalSec), 0f, 1f);
        return new Organism(breath, NightFactor(now), fatigue, _slots[i].TransitionPulse);
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

    private void SwitchScene(int i, DateTime now, in AudioFrame audio, string? forcedScene, TransitionKind? forcedKind, float beatSec = 0)
    {
        var slot = _slots[i];
        string scene = forcedScene ?? PickScene(i);
        string preset = _lockPreset || !_s.Config.Shuffle.Presets ? slot.Current.PresetName : PickPreset(slot.Current.PresetName, now, audio);
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
        // Не повторять текущую и не дублировать то, что сейчас на других мониторах
        var busy = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var sl in _slots) { busy.Add(sl.Current.Scene.Id); if (sl.Incoming is not null) busy.Add(sl.Incoming.Scene.Id); }
        var options = _scenes.Where(s => !busy.Contains(s)).ToList();
        if (options.Count == 0) options = _scenes.Where(s => !s.Equals(_slots[i].Current.Scene.Id, StringComparison.OrdinalIgnoreCase)).ToList();
        if (options.Count == 0) options = _scenes;
        return options[_s.Rng.Next(options.Count)];
    }

    /// <summary>Пресет по настроению: ночью — фосфорные терминалы, под музыку — rave; без музыки rave редкий.</summary>
    private string PickPreset(string current, DateTime now, in AudioFrame audio)
    {
        if (_presets.Count <= 1) return current;
        float night = NightFactor(now);
        double total = 0;
        var w = new double[_presets.Count];
        for (int k = 0; k < _presets.Count; k++)
        {
            var p = _presets[k];
            double weight = 1;
            if (p.Equals(current, StringComparison.OrdinalIgnoreCase)) weight = 0;
            else if (p.Equals("rave", StringComparison.OrdinalIgnoreCase)) weight = audio.Active ? 4 : 0.15;
            else if (p is "terminal" or "amber") weight = 1 + 2 * night;
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
