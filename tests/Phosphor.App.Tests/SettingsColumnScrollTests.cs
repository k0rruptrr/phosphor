using System.Runtime.InteropServices;
using System.Windows.Forms;
using Phosphor.App.Settings;
using Phosphor.Core;

namespace Phosphor.App.Tests;

/// <summary>
/// Колонки параметров окна настроек в стандартном (минимальном) размере: до конца и к началу прокручивается,
/// полоса — только когда строк больше места; колесо над контролом без фокуса прокручивает колонку и не меняет значение;
/// ресайз окна растит колонку. Окно не показывается (CreateForProbe), раскладка — явная (LayoutForTest).
/// </summary>
public class SettingsColumnScrollTests
{
    private const uint WmMouseWheel = 0x020A;

    [DllImport("user32.dll")]
    private static extern nint SendMessage(nint hWnd, uint msg, nint wParam, nint lParam);

    /// <summary>WinForms-контролы — на STA-потоке. Исключение пробрасывается с исходным стеком.</summary>
    private static void OnSta(Action action)
    {
        Exception? error = null;
        var t = new Thread(() =>
        {
            try { action(); }
            catch (Exception e) { error = e; }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (error is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
    }

    private static PhosphorConfig Defaults() => PhosphorConfig.Parse(PhosphorConfig.DefaultJson);

    /// <summary>Сцена с 14 ползунками и выбором: колонка заведомо выше окна минимального размера.</summary>
    private static readonly IReadOnlyList<SceneParamSpec> FakeParams = Enumerable.Range(0, 14)
        .Select(i => new SceneParamSpec($"p{i:00}", SceneParamKind.Float, 0.5f, 0, 1, $"параметр {i}"))
        .Append(new SceneParamSpec("mode", SceneParamKind.Choice, 0, 0, 2, "режим", new[] { "a", "b", "c" }))
        .ToList();

    private static SettingsForm FakeForm(Phosphor.App.Log log, out SettingsModel model)
    {
        model = new SettingsModel(Defaults(), new[] { "fake" },
            new SettingsSpecs(_ => null, id => id == "fake" ? FakeParams : null));
        return SettingsForm.CreateForProbe(model, log);
    }

    /// <summary>Первый контрол типа T в дереве (в порядке обхода).</summary>
    private static T? Find<T>(Control c) where T : Control
    {
        foreach (Control child in c.Controls)
        {
            if (child is T t) return t;
            var inner = Find<T>(child);
            if (inner is not null) return inner;
        }
        return null;
    }

    /// <summary>Колесо одним щелчком (120) — сообщением WM_MOUSEWHEEL прямо в окно контрола.</summary>
    private static void SendWheel(Control target, short delta)
    {
        nint wParam = (nint)((long)(ushort)delta << 16);
        SendMessage(target.Handle, WmMouseWheel, wParam, 0);
    }

    /// <summary>
    /// Конец колонки: последняя строка целиком видна; начало: первая строка видна.
    /// Полоса видна ровно тогда, когда содержимое выше области.
    /// </summary>
    private static void CheckColumn(ParamsPanel p, string where, List<string> failures)
    {
        if (p.Rows.Count == 0)
        {
            failures.Add($"{where}: нет строк");
            return;
        }
        p.ScrollTo(int.MaxValue);
        if (!p.IsRowFullyVisible(p.Rows[^1]))
            failures.Add($"{where}: в конце не видна последняя строка (прокрутка {p.Offset}, макс. {p.MaxOffset}, высота {p.Height})");
        p.ScrollTo(0);
        if (!p.IsRowFullyVisible(p.Rows[0]))
            failures.Add($"{where}: в начале не видна первая строка");
        if (p.BarVisible != p.ContentHeight > p.Height)
            failures.Add($"{where}: полоса прокрутки не соответствует содержимому");
        if (p.ContentHeight <= p.Height && p.MaxOffset != 0)
            failures.Add($"{where}: содержимое помещается, но прокрутка есть");
    }

    [Fact]
    public void Scenes_EveryScene_ColumnScrollsToEndAndBack_AtMinimumSize()
    {
        var failures = new List<string>();
        OnSta(() =>
        {
            using var log = new Phosphor.App.Log();
            var model = new SettingsModel(Defaults(), Catalog.Scenes().Ids, SettingsSpecs.FromCatalog());
            using var form = SettingsForm.CreateForProbe(model, log);
            foreach (var id in model.SceneIds)
                CheckColumn(form.ProbeColumn("scenes", id), $"сцена {id}", failures);
        });
        Assert.Empty(failures);
    }

    [Fact]
    public void Effects_EveryBuiltinPreset_ColumnScrollsToEndAndBack_AtMinimumSize()
    {
        var failures = new List<string>();
        OnSta(() =>
        {
            using var log = new Phosphor.App.Log();
            var model = new SettingsModel(Defaults(), Catalog.Scenes().Ids, SettingsSpecs.FromCatalog());
            using var form = SettingsForm.CreateForProbe(model, log);
            foreach (var name in model.PresetNames)
                CheckColumn(form.ProbeColumn("filters", name), $"пресет {name}", failures);
        });
        Assert.Empty(failures);
    }

    [Fact]
    public void Overlays_EveryOverlayAndBadge_ColumnScrollsToEndAndBack_AtMinimumSize()
    {
        var failures = new List<string>();
        OnSta(() =>
        {
            using var log = new Phosphor.App.Log();
            var model = new SettingsModel(Defaults(), Catalog.Scenes().Ids, SettingsSpecs.FromCatalog());
            using var form = SettingsForm.CreateForProbe(model, log);
            foreach (var id in SettingsModel.OverlayIds)
                CheckColumn(form.ProbeColumn("overlays", id), $"оверлей {id}", failures);
        });
        Assert.Empty(failures);
    }

    [Fact]
    public void Screen_ColumnScrollsToEndAndBack_InBothKinds()
    {
        var failures = new List<string>();
        OnSta(() =>
        {
            using var log = new Phosphor.App.Log();
            var model = new SettingsModel(Defaults(), Catalog.Scenes().Ids, SettingsSpecs.FromCatalog());
            using var form = SettingsForm.CreateForProbe(model, log);
            CheckColumn(form.ProbeColumn("screen", ""), "экран: кинескоп", failures);
            model.ScreenEnabled = false;
            CheckColumn(form.ProbeColumn("screen", ""), "экран: плоский", failures);
        });
        Assert.Empty(failures);
    }

    [Fact]
    public void Wheel_OverSliderWithoutFocus_ScrollsColumn_AndKeepsValue()
    {
        OnSta(() =>
        {
            using var log = new Phosphor.App.Log();
            using var form = FakeForm(log, out _);
            var panel = form.ProbeColumn("scenes", "fake");
            panel.ScrollTo(0);
            var slider = Find<SliderBox>(panel) ?? throw new InvalidOperationException("нет ползунка");
            float value = slider.Value;
            int offset = panel.Offset;

            SendWheel(slider, -120);

            Assert.False(slider.Focused);
            Assert.Equal(value, slider.Value);
            Assert.Equal(offset + ParamsPanel.WheelStep, panel.Offset);
        });
    }

    [Fact]
    public void Wheel_OverComboWithoutFocus_ScrollsColumn_AndKeepsSelection()
    {
        OnSta(() =>
        {
            using var log = new Phosphor.App.Log();
            using var form = FakeForm(log, out _);
            var panel = form.ProbeColumn("scenes", "fake");
            panel.ScrollTo(0);
            var combo = Find<ParamCombo>(panel) ?? throw new InvalidOperationException("нет выпадающего списка");
            int selected = combo.SelectedIndex;
            int offset = panel.Offset;

            SendWheel(combo, -120);

            Assert.False(combo.Focused);
            Assert.Equal(selected, combo.SelectedIndex);
            Assert.Equal(offset + ParamsPanel.WheelStep, panel.Offset);
        });
    }

    [Fact]
    public void Wheel_OverLabelOfRow_ScrollsColumnExactlyOneStep()
    {
        OnSta(() =>
        {
            using var log = new Phosphor.App.Log();
            using var form = FakeForm(log, out _);
            var panel = form.ProbeColumn("scenes", "fake");
            panel.ScrollTo(0);
            var label = Find<Label>(panel.Rows[0]) ?? throw new InvalidOperationException("нет подписи");

            SendWheel(label, -120);

            Assert.Equal(ParamsPanel.WheelStep, panel.Offset);
        });
    }

    [Fact]
    public void Resize_TallerWindow_GrowsParamColumnByTheSameAmount()
    {
        OnSta(() =>
        {
            using var log = new Phosphor.App.Log();
            using var form = FakeForm(log, out _);
            var panel = form.ProbeColumn("scenes", "fake");
            int before = panel.Height;

            form.Height += 300;
            form.LayoutForTest();

            int grown = panel.Height - before;
            Assert.InRange(grown, 290, 310);
        });
    }
}
