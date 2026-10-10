using System.Diagnostics;
using System.Drawing.Imaging;
using System.Globalization;
using Phosphor.Core;
using Phosphor.Gfx;
using Phosphor.Platform;
using WinTimer = System.Windows.Forms.Timer;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Phosphor.App.Tests")]

namespace Phosphor.App.Settings;

// В Phosphor.App есть свой тип Font: в этом пространстве имён берём System.Drawing.Font
using Font = System.Drawing.Font;

/// <summary>
/// Окно настроек (/c). Обычное окно Windows Forms в тёмной теме. Вся логика состояний — в SettingsModel;
/// здесь только раскладка и перенос значений контролов в PhosphorConfig при «Сохранить».
/// </summary>
internal sealed class SettingsForm : Form
{
    private const int TabsX = 16, TabsY = 16, TabsW = 600, TabsH = 540;
    private const int PrevX = 632, PrevY = 40;
    // Строки параметров (ширина панели 480, содержимое 464): подписи | контрол | значение | «по умолчанию»
    private const int MaxLabelCol = 264, ValueX = 376, ValueW = 56, ControlEnd = 368, ResetX = 436;
    /// <summary>Ширина колонки подписей текущей группы строк (см. LabelColumn).</summary>
    private int _labelCol = 150;

    private readonly SettingsModel _model;
    private readonly string _cfgPath;
    private readonly Log _log;

    private readonly FlatTabs _tabs = new();
    private readonly Label _previewCaption = new();
    private readonly SettingsPreview _preview;
    private readonly PathLabel _status = new();
    private readonly ToolTip _tip = new();
    private readonly Button _save = new(), _cancel = new();

    // Сцены
    private readonly PickList _scenes;
    private readonly CheckBox _sceneFavOnly = new();
    private readonly NumBox _favWeight = new();
    // Фильтры
    private readonly PickList _presets;
    private readonly ComboBox _activePreset = new();
    private readonly CheckBox _presetFavOnly = new();
    // Перебор
    private readonly CheckBox _shufScenes = new(), _shufPresets = new(), _perMonitor = new(), _beatSync = new();
    private readonly NumBox _interval = new(), _transSec = new(), _overlayPct = new();
    private readonly ComboBox _transition = new();
    private readonly SliderBox _trail = new();
    private readonly Label _trailValue = new();
    // Параметры под превью (сцена на вкладке «Сцены», пресет на «Фильтрах»)
    private readonly Label _paramsTitle = new();
    private readonly ParamsPanel _params = new();
    private readonly Button _sceneResetAll = new();
    private readonly Button _presetSaveAs = new(), _presetBuiltin = new(), _presetDelete = new();
    // Диммер
    private readonly CheckBox _dimEnabled = new(), _dimHw = new();
    private readonly NumBox _dimDelay = new(), _dimFade = new(), _dimHwLevel = new(), _dimSwLevel = new();
    private readonly Button _probe = new();
    private readonly Label _probeStatus = new();
    private readonly ListView _monitors = new();
    // Сеть
    private readonly CheckBox _netEnabled = new();
    private readonly TextBox _city = new();

    private string _previewScene = "";
    /// <summary>Пресет, который редактируется и показывается в превью (выбран в списке или активный). null — shuffle.</summary>
    private string? _editPreset;
    /// <summary>Ключ, по которому построены строки параметров (вкладка|сцена|пресет): меняется — строки пересобираются.</summary>
    private string _paramsKey = "";
    /// <summary>Программная установка значений: события комбобоксов и списков не пишут в конфиг.</summary>
    private bool _loading;

    private static readonly Choice[] TransitionChoices =
    {
        new("mixed", "mixed — выбирает дирижёр"),
        new("fade", "fade — затухание"),
        new("vhs-noise", "vhs-noise — шум кассеты"),
        new("cut", "cut — жёсткая склейка"),
    };

    private SettingsForm(SettingsModel model, string cfgPath, Log log, bool livePreview)
    {
        _model = model;
        _cfgPath = cfgPath;
        _log = log;
        _preview = new SettingsPreview(livePreview, model.Config, log);

        _scenes = new PickList(_model.GetScene, id => { _model.CycleScene(id); RefreshScenesAfterChange(id); });
        _presets = new PickList(_model.GetPreset, name => { _model.CyclePreset(name); UpdatePreview(); }, _model.IsPresetModified);

        Text = "PHOSPHOR — настройки";
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(1136, 616);
        BackColor = Theme.Bg;
        ForeColor = Theme.Text;
        Font = Theme.Mono;
        CancelButton = _cancel;
        AcceptButton = _save;

        BuildTabs();
        BuildPreviewColumn();
        BuildParamsColumn();
        BuildBottom();
        Controls.Add(_tabs);
        Controls.Add(_previewCaption);
        Controls.Add(_preview);
        Controls.Add(_status);
        Controls.Add(_save);
        Controls.Add(_cancel);
        Theme.Apply(this);
        _save.FlatAppearance.BorderColor = Theme.Accent;
        _save.ForeColor = Theme.Accent;

        _loading = true;
        LoadControls();
        _loading = false;
        _status.Text = "Конфиг: " + _cfgPath;
        _tip.SetToolTip(_status, _cfgPath);
        FormClosing += OnFormClosing;
        _scenes.SelectionChanged += (_, _) => OnScenesSelection();
        _presets.SelectionChanged += (_, _) => OnPresetsSelection();
        _tabs.SelectedIndexChanged += (_, _) => UpdatePreview();
        _editPreset = ActivePresetName();
        if (_editPreset is not null) _presets.SelectId(_editPreset);
        UpdatePreview();
    }

    // ---------- Раскладка ----------

    private void BuildTabs()
    {
        _tabs.Location = new Point(TabsX, TabsY);
        _tabs.Size = new Size(TabsW, TabsH);
        _tabs.ItemWidth = 96;
        _tabs.Add("Сцены", Page(BuildScenesPage));
        _tabs.Add("Фильтры", Page(BuildFiltersPage));
        _tabs.Add("Перебор", Page(BuildShufflePage));
        _tabs.Add("Диммер", Page(BuildDimmerPage));
        _tabs.Add("Сеть", Page(BuildNetworkPage));
        _tabs.Add("О программе", Page(BuildAboutPage));
    }

    private static Panel Page(Action<Panel> fill)
    {
        var p = new Panel { BackColor = Theme.Bg };
        fill(p);
        return p;
    }

    private void BuildScenesPage(Panel p)
    {
        _sceneFavOnly.Text = "только любимые";
        _sceneFavOnly.AutoSize = true;
        _sceneFavOnly.Location = new Point(12, 12);
        _sceneFavOnly.CheckedChanged += (_, _) => _model.Config.Scenes.FavoritesOnly = _sceneFavOnly.Checked;
        p.Controls.Add(_sceneFavOnly);

        Place(p, Lbl("любимые чаще в"), 220, 15);
        Num(p, _favWeight, 1, 10, 0, 352, 11, 80);
        Place(p, Lbl("раз"), 440, 15);

        Button(p, "все вкл", 12, 44, 120, () => { _model.SetAllScenes(true); RefreshScenesAfterChange(null); });
        Button(p, "все выкл", 140, 44, 120, () => { _model.SetAllScenes(false); RefreshScenesAfterChange(null); });
        Place(p, Lbl("клик или Space: выкл → вкл → ★", Theme.Muted), 276, 50);

        _scenes.Location = new Point(12, 84);
        _scenes.Size = new Size(568, 412);
        _scenes.Fill(_model.SceneIds);
        p.Controls.Add(_scenes);
    }

    private void BuildFiltersPage(Panel p)
    {
        Place(p, Lbl("активный фильтр"), 12, 17);
        _activePreset.Location = new Point(LabelCol(FilterLabels), 12);
        _activePreset.Size = new Size(300, 24);
        _activePreset.DropDownStyle = ComboBoxStyle.DropDownList;
        _activePreset.SelectedIndexChanged += (_, _) => { if (!_loading) OnActivePresetChanged(); };
        p.Controls.Add(_activePreset);

        _presetFavOnly.Text = "только любимые";
        _presetFavOnly.AutoSize = true;
        _presetFavOnly.Location = new Point(12, 48);
        _presetFavOnly.CheckedChanged += (_, _) => _model.Config.Filters.FavoritesOnly = _presetFavOnly.Checked;
        p.Controls.Add(_presetFavOnly);

        Button(p, "все вкл", 12, 80, 120, () => { _model.SetAllPresets(true); RefreshPresetsAfterChange(); });
        Button(p, "все выкл", 140, 80, 120, () => { _model.SetAllPresets(false); RefreshPresetsAfterChange(); });
        Place(p, Lbl("клик или Space: выкл → вкл → ★", Theme.Muted), 276, 86);

        _presets.Location = new Point(12, 120);
        _presets.Size = new Size(568, 376);
        _presets.Fill(_model.PresetNames);
        p.Controls.Add(_presets);
    }

    private void BuildShufflePage(Panel p)
    {
        Check(p, _shufScenes, "менять сцены", 12, 12);
        Check(p, _shufPresets, "менять фильтры", 12, 46);

        Place(p, Lbl("интервал, с"), 12, 88);
        Num(p, _interval, 5, 3600, 0, LabelCol(ShuffleLabels), 84, 90);
        Place(p, Lbl("переход"), 12, 124);
        _transition.Location = new Point(LabelCol(ShuffleLabels), 120);
        _transition.Size = new Size(260, 24);
        _transition.DropDownStyle = ComboBoxStyle.DropDownList;
        _transition.Items.AddRange(TransitionChoices);
        p.Controls.Add(_transition);
        Place(p, Lbl("длительность перехода, с"), 12, 160);
        Num(p, _transSec, 0, 10, 1, LabelCol(ShuffleLabels), 156, 90);

        Check(p, _perMonitor, "разные сцены на мониторах", 12, 198);
        Check(p, _beatSync, "смена в такт музыке", 12, 232);

        Place(p, Lbl("вероятность HUD-оверлея, %"), 12, 270);
        Num(p, _overlayPct, 0, 100, 0, LabelCol(ShuffleLabels), 266, 90);

        Place(p, Lbl("шлейф (моушн-блюр)"), 12, 310);
        _trail.Minimum = 0f;
        _trail.Maximum = 0.9f;
        _trail.Size = new Size(260, 24);
        _trail.Location = new Point(LabelCol(ShuffleLabels), 306);
        _trail.ValueChanged += (_, _) => { _model.Config.Screen.Trail = _trail.Value; ShowTrail(); UpdatePreview(); };
        p.Controls.Add(_trail);
        _trailValue.AutoSize = true;
        _trailValue.Location = new Point(LabelCol(ShuffleLabels) + 270, 310);
        p.Controls.Add(_trailValue);
    }

    private void ShowTrail() => _trailValue.Text = _trail.Value.ToString("0.00", CultureInfo.InvariantCulture);

    private void BuildDimmerPage(Panel p)
    {
        Check(p, _dimEnabled, "гасить экран, пока работает заставка", 12, 12);

        Place(p, Lbl("через, мин"), 12, 52);
        Num(p, _dimDelay, 0, 240, 0, LabelCol(DimmerLabels), 48, 90);
        Place(p, Lbl("длительность затемнения, с"), 12, 88);
        Num(p, _dimFade, 0, 600, 0, LabelCol(DimmerLabels), 84, 90);

        Check(p, _dimHw, "управлять подсветкой (DDC/CI)", 12, 126);
        Place(p, Lbl("целевая яркость подсветки, %"), 12, 162);
        Num(p, _dimHwLevel, 0, 100, 0, LabelCol(DimmerLabels), 158, 90);
        Place(p, Lbl("яркость картинки без DDC, %"), 12, 198);
        Num(p, _dimSwLevel, 0, 100, 0, LabelCol(DimmerLabels), 194, 90);

        _probe.Text = "проверить мониторы";
        _probe.Location = new Point(12, 238);
        _probe.Size = new Size(200, 28);
        _probe.Click += (_, _) => StartProbe();
        p.Controls.Add(_probe);
        _probeStatus.AutoSize = true;
        _probeStatus.Location = new Point(224, 244);
        _probeStatus.Text = "только чтение: яркость не меняется";
        _probeStatus.ForeColor = Theme.Muted;
        p.Controls.Add(_probeStatus);

        _monitors.Location = new Point(12, 280);
        _monitors.Size = new Size(568, 210);
        StyleMonitorList();
        p.Controls.Add(_monitors);
    }

    /// <summary>Таблица мониторов рисуется сама: нативные белые заголовки и линии не нужны.</summary>
    private void StyleMonitorList()
    {
        _monitors.View = View.Details;
        _monitors.OwnerDraw = true;
        _monitors.FullRowSelect = true;
        _monitors.GridLines = false;
        _monitors.HeaderStyle = ColumnHeaderStyle.Nonclickable;
        _monitors.BorderStyle = BorderStyle.FixedSingle;
        _monitors.BackColor = Theme.Panel;
        _monitors.ForeColor = Theme.Text;
        _monitors.Font = Theme.Mono;
        _monitors.Columns.Add("монитор", 170);
        _monitors.Columns.Add("DDC/CI", 80);
        _monitors.Columns.Add("min–cur–max", 130);
        _monitors.Columns.Add("причина", 170);
        _monitors.DrawColumnHeader += (_, e) =>
        {
            using (var b = new SolidBrush(Theme.Bg)) e.Graphics.FillRectangle(b, e.Bounds);
            TextRenderer.DrawText(e.Graphics, e.Header!.Text, Theme.Mono, new Rectangle(e.Bounds.X + 6, e.Bounds.Y, e.Bounds.Width - 8, e.Bounds.Height),
                Theme.Muted, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
        };
        _monitors.DrawItem += (_, e) =>
        {
            using (var b = new SolidBrush(Theme.Panel)) e.Graphics.FillRectangle(b, e.Bounds);
            e.DrawDefault = false;
        };
        _monitors.DrawSubItem += (_, e) =>
        {
            using (var b = new SolidBrush(Theme.Panel)) e.Graphics.FillRectangle(b, e.Bounds);
            TextRenderer.DrawText(e.Graphics, e.SubItem?.Text ?? "", Theme.Mono, new Rectangle(e.Bounds.X + 6, e.Bounds.Y, e.Bounds.Width - 8, e.Bounds.Height),
                Theme.Text, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
        };
        _monitors.HandleCreated += (_, _) => Theme.DarkScrollbars(_monitors);
        // Последняя колонка растягивается до края: иначе справа остаётся светлый кусок заголовка
        _monitors.Resize += (_, _) =>
        {
            int used = 0;
            for (int i = 0; i < _monitors.Columns.Count - 1; i++) used += _monitors.Columns[i].Width;
            _monitors.Columns[^1].Width = Math.Max(60, _monitors.ClientSize.Width - used);
        };
    }

    private void BuildNetworkPage(Panel p)
    {
        Check(p, _netEnabled, "сеть: погода, новости, спутники, телетекст", 12, 12);
        Place(p, Lbl("город"), 12, 52);
        _city.Location = new Point(LabelCol(NetworkLabels), 48);
        _city.Size = new Size(260, 24);
        p.Controls.Add(_city);
        Place(p, Lbl("Данные кешируются; без сети сцены работают на кеше.", Theme.Muted), 12, 88);
    }

    private void BuildAboutPage(Panel p)
    {
        var name = Lbl("PHOSPHOR " + AppInfo.Display);
        name.Tag = new Font(Theme.Mono.FontFamily, 13f, FontStyle.Bold, GraphicsUnit.Point); // Theme.Apply берёт шрифт из Tag
        name.ForeColor = Theme.Accent;
        Place(p, name, 12, 12);
        Place(p, Lbl("конфиг: " + _cfgPath, Theme.Muted), 12, 60);
        Place(p, Lbl("лог: " + _log.Path, Theme.Muted), 12, 84);
        Button(p, "открыть лог", 12, 124, 160, () => OpenFile(_log.Path));
        Button(p, "открыть phosphor.json", 184, 124, 220, () => OpenFile(_cfgPath));
    }

    private void BuildPreviewColumn()
    {
        _previewCaption.AutoSize = true;
        _previewCaption.Location = new Point(PrevX, 16);
        _previewCaption.ForeColor = Theme.Muted;
        _preview.Location = new Point(PrevX, PrevY);
        _preview.Size = new Size(480, 270);
        var hint = Lbl("без звука, как в заставке", Theme.Muted);
        hint.Location = new Point(PrevX, PrevY + 282);
        Controls.Add(hint);
    }

    /// <summary>Под превью: параметры сцены (вкладка «Сцены») или пресета (вкладка «Фильтры»). На других вкладках пусто.</summary>
    private void BuildParamsColumn()
    {
        _paramsTitle.AutoSize = true;
        _paramsTitle.Location = new Point(PrevX, 350);
        _paramsTitle.ForeColor = Theme.Muted;
        // Список параметров — до y=506; кнопки одной строкой внизу колонки (y=512)
        _params.Location = new Point(PrevX, 372);
        _params.Size = new Size(480, 134);

        _sceneResetAll.Text = "все по умолчанию";
        _sceneResetAll.Location = new Point(PrevX, 512);
        _sceneResetAll.Size = new Size(210, 26);
        _sceneResetAll.Click += (_, _) => { if (_previewScene.Length > 0) { _model.ResetSceneParams(_previewScene); RebuildParams(); UpdatePreview(); } };

        _presetSaveAs.Text = "Сохранить как…";
        _presetSaveAs.Location = new Point(PrevX, 512);
        _presetSaveAs.Size = new Size(150, 26);
        _presetSaveAs.Click += (_, _) => OnPresetSaveAs();

        _presetBuiltin.Text = "Вернуть встроенный";
        _presetBuiltin.Location = new Point(PrevX + 158, 512);
        _presetBuiltin.Size = new Size(190, 26);
        _presetBuiltin.Click += (_, _) => OnPresetBuiltin();

        _presetDelete.Text = "Удалить";
        _presetDelete.Location = new Point(PrevX + 356, 512);
        _presetDelete.Size = new Size(124, 26);
        _presetDelete.Click += (_, _) => OnPresetDelete();

        Controls.Add(_paramsTitle);
        Controls.Add(_params);
        Controls.Add(_sceneResetAll);
        Controls.Add(_presetSaveAs);
        Controls.Add(_presetBuiltin);
        Controls.Add(_presetDelete);
    }

    private void BuildBottom()
    {
        _save.Text = "Сохранить";
        _save.Size = new Size(120, 32);
        _save.Location = new Point(1136 - 16 - 110 - 8 - 120, 568);
        _save.Click += (_, _) => Save();
        _cancel.Text = "Отмена";
        _cancel.Size = new Size(110, 32);
        _cancel.Location = new Point(1136 - 16 - 110, 568);
        _cancel.DialogResult = DialogResult.Cancel;
        // Путь обрезается многоточием в середине и не заходит под кнопку «Сохранить»; полный путь — в подсказке
        _status.Location = new Point(16, 576);
        _status.Size = new Size(_save.Left - 16 - 12, 20);
        _status.ForeColor = Theme.Muted;
    }

    // ---------- Значения ↔ контролы ----------

    private void LoadControls()
    {
        var c = _model.Config;
        _sceneFavOnly.Checked = c.Scenes.FavoritesOnly;
        _favWeight.Value = Clamp(c.Shuffle.FavoriteWeight, 1, 10);
        _presetFavOnly.Checked = c.Filters.FavoritesOnly;

        FillActiveCombo(c.Filters.Active);

        _shufScenes.Checked = c.Shuffle.Scenes;
        _shufPresets.Checked = c.Shuffle.Presets;
        _interval.Value = Clamp(c.Shuffle.IntervalSec, 5, 3600);
        _transition.SelectedIndex = Math.Max(0, Array.FindIndex(TransitionChoices, ch => string.Equals(ch.Value, c.Shuffle.Transition, StringComparison.OrdinalIgnoreCase)));
        _transSec.Value = Clamp(c.Shuffle.TransitionSec, 0, 10);
        _perMonitor.Checked = c.Shuffle.PerMonitor;
        _beatSync.Checked = c.Shuffle.BeatSync;
        _overlayPct.Value = Clamp(c.Overlay.Probability * 100, 0, 100);
        _trail.Value = Math.Clamp(c.Screen.Trail, 0f, 0.9f);
        ShowTrail();

        _dimEnabled.Checked = c.Dimmer.Enabled;
        _dimDelay.Value = Clamp(c.Dimmer.DelayMin, 0, 240);
        _dimFade.Value = Clamp(c.Dimmer.FadeSec, 0, 600);
        _dimHw.Checked = c.Dimmer.Hardware;
        _dimHwLevel.Value = Clamp(c.Dimmer.HardwareLevel, 0, 100);
        _dimSwLevel.Value = Clamp(c.Dimmer.SoftwareLevel * 100, 0, 100);

        _netEnabled.Checked = c.Network.Enabled;
        _city.Text = c.Network.City;
    }

    private void ReadControls()
    {
        var c = _model.Config;
        c.Scenes.FavoritesOnly = _sceneFavOnly.Checked;
        c.Shuffle.FavoriteWeight = (float)_favWeight.Value;
        c.Filters.FavoritesOnly = _presetFavOnly.Checked;
        if (_activePreset.SelectedItem is Choice active) c.Filters.Active = active.Value;

        c.Shuffle.Scenes = _shufScenes.Checked;
        c.Shuffle.Presets = _shufPresets.Checked;
        c.Shuffle.IntervalSec = (float)_interval.Value;
        if (_transition.SelectedItem is Choice tr) c.Shuffle.Transition = tr.Value;
        c.Shuffle.TransitionSec = (float)_transSec.Value;
        c.Shuffle.PerMonitor = _perMonitor.Checked;
        c.Shuffle.BeatSync = _beatSync.Checked;
        c.Overlay.Probability = (double)_overlayPct.Value / 100.0;
        c.Screen.Trail = _trail.Value;

        c.Dimmer.Enabled = _dimEnabled.Checked;
        c.Dimmer.DelayMin = (float)_dimDelay.Value;
        c.Dimmer.FadeSec = (float)_dimFade.Value;
        c.Dimmer.Hardware = _dimHw.Checked;
        c.Dimmer.HardwareLevel = (int)_dimHwLevel.Value;
        c.Dimmer.SoftwareLevel = (float)_dimSwLevel.Value / 100f;

        c.Network.Enabled = _netEnabled.Checked;
        var city = _city.Text.Trim();
        if (city.Length > 0) c.Network.City = city;
    }

    private void Save()
    {
        var problem = _model.Problem();
        if (problem is not null)
        {
            MessageBox.Show(this, problem, "PHOSPHOR", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            _tabs.SelectedIndex = problem.Contains("сцен") ? 0 : 1;
            return;
        }
        ReadControls();
        try
        {
            _model.Save(_cfgPath);
            _log.Info("настройки: сохранено " + _cfgPath);
        }
        catch (Exception e)
        {
            _log.Error("настройки: не удалось сохранить: " + e.Message);
            MessageBox.Show(this, "Не удалось сохранить:\n" + e.Message, "PHOSPHOR", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        DialogResult = DialogResult.OK;
        Close();
    }

    // ---------- Списки и превью ----------

    private void RefreshScenesAfterChange(string? id)
    {
        _scenes.Invalidate();
        if (id is not null)
        {
            _previewScene = id;
            UpdatePreview();
        }
    }

    private void RefreshPresetsAfterChange()
    {
        _presets.Invalidate();
        UpdatePreview();
    }

    private void OnScenesSelection()
    {
        if (_scenes.SelectedId is null) return;
        _previewScene = _scenes.SelectedId;
        UpdatePreview();
    }

    private void UpdatePreview()
    {
        if (_previewScene.Length == 0 && _model.SceneIds.Count > 0)
            _previewScene = _model.SceneIds.FirstOrDefault(id => _model.GetScene(id) != Pick.Off) ?? _model.SceneIds[0];
        // Редактируемый пресет (из списка или активный) — его и показываем; shuffle — как раньше
        string? chosen = _editPreset ?? (_activePreset.SelectedItem as Choice)?.Value;
        string? preset = _model.PreviewPreset(chosen);
        _previewCaption.Text = $"превью · {_previewScene} · {preset ?? "—"}";
        if (_previewScene.Length > 0) _preview.Request(_previewScene, preset);
        if (CurrentParamsKey() != _paramsKey) RebuildParams();
    }

    private string CurrentParamsKey() => $"{_tabs.SelectedIndex}|{_previewScene}|{_editPreset}";

    private string? ActivePresetName()
    {
        string? active = _model.Config.Filters.Active;
        return string.Equals(active, FiltersConfig.ShuffleActive, StringComparison.OrdinalIgnoreCase) ? null : _model.PresetKey(active ?? "");
    }

    /// <summary>
    /// Комбобокс «активный фильтр»: shuffle и все пресеты. Выбор: прежний (если ещё есть), иначе preferred, иначе первый.
    /// </summary>
    private void FillActiveCombo(string? preferred)
    {
        bool was = _loading;
        _loading = true;
        try
        {
            string? cur = (_activePreset.SelectedItem as Choice)?.Value;
            _activePreset.Items.Clear();
            _activePreset.Items.Add(new Choice(FiltersConfig.ShuffleActive, "случайный (shuffle)"));
            foreach (var name in _model.PresetNames) _activePreset.Items.Add(new Choice(name, name));
            int idx = IndexOfChoice(cur);
            if (idx < 0) idx = IndexOfChoice(preferred);
            _activePreset.SelectedIndex = Math.Max(0, idx);
        }
        finally { _loading = was; }
    }

    private int IndexOfChoice(string? value) =>
        value is null ? -1 : _activePreset.Items.Cast<Choice>().ToList().FindIndex(ch => string.Equals(ch.Value, value, StringComparison.OrdinalIgnoreCase));

    /// <summary>Выбор в комбобоксе «активный»: пресет становится редактируемым (и выделяется в списке).</summary>
    private void OnActivePresetChanged()
    {
        string? chosen = (_activePreset.SelectedItem as Choice)?.Value;
        if (chosen is null || string.Equals(chosen, FiltersConfig.ShuffleActive, StringComparison.OrdinalIgnoreCase))
        {
            _editPreset = null;
            UpdatePreview();
            return;
        }
        _presets.SelectId(chosen);
        _editPreset = _presets.SelectedId ?? chosen;
        UpdatePreview();
    }

    /// <summary>Выделение в списке «Фильтры»: пресет становится редактируемым.</summary>
    private void OnPresetsSelection()
    {
        _editPreset = _presets.SelectedId;
        UpdatePreview();
    }

    private void RebuildParams()
    {
        _paramsKey = CurrentParamsKey();
        _params.Clear();
        bool scenesTab = _tabs.SelectedIndex == 0;
        bool filtersTab = _tabs.SelectedIndex == 1;
        _paramsTitle.Visible = _params.Visible = scenesTab || filtersTab;
        _sceneResetAll.Visible = scenesTab;
        foreach (var b in new[] { _presetSaveAs, _presetBuiltin, _presetDelete }) b.Visible = filtersTab;
        if (scenesTab) BuildSceneRows();
        else if (filtersTab) BuildFilterRows();
    }

    private void BuildSceneRows()
    {
        string id = _previewScene;
        _paramsTitle.Text = $"Параметры сцены «{id}»";
        var specs = id.Length > 0 ? _model.SceneSpecs(id) : null;
        _sceneResetAll.Enabled = specs is { Count: > 0 };
        if (specs is null || specs.Count == 0)
        {
            _labelCol = LabelColumn(Array.Empty<string>());
            _params.AddRow(NoteRow("у этой сцены нет настроек"));
            return;
        }
        _labelCol = LabelColumn(specs.Select(s => LabelText(s.Name, s.Description)));
        foreach (var spec in specs) _params.AddRow(SceneParamRow(id, spec));
    }

    private void BuildFilterRows()
    {
        string? name = _editPreset;
        RefreshFilterTitle();
        bool any = name is not null;
        _presetSaveAs.Enabled = any;
        _presetBuiltin.Enabled = any && _model.IsBuiltinPreset(name!);
        _presetDelete.Enabled = any && !_model.IsBuiltinPreset(name!) && _model.PresetNames.Count > 1;
        if (name is null)
        {
            _labelCol = LabelColumn(Array.Empty<string>());
            _params.AddRow(NoteRow("выберите пресет в списке (или активный — не shuffle)"));
            return;
        }
        var stages = _model.PresetStages(name);
        _labelCol = LabelColumn(stages.SelectMany(st => st.Params).Select(FilterLabel));
        foreach (var st in stages)
        {
            _params.AddRow(HeaderRow($"{st.Id}  ·  {StageHumanName(st.Id)}"));
            if (st.Params.Count == 0)
            {
                _params.AddRow(NoteRow("без настроек"));
                continue;
            }
            foreach (var spec in st.Params) _params.AddRow(FilterParamRow(name, st.Index, spec));
        }
    }

    /// <summary>Подпись параметра для пользователя: Description из спецификации, без него — техническое имя.</summary>
    private static string LabelText(string name, string description) => description.Trim().Length > 0 ? description.Trim() : name;

    /// <summary>Подпись параметра фильтра: короткий Label, без него — как у сцен (Description, иначе имя).</summary>
    private static string FilterLabel(ParamSpec spec) =>
        !string.IsNullOrWhiteSpace(spec.Label) ? spec.Label.Trim() : LabelText(spec.Name, spec.Description);

    /// <summary>
    /// Ширина колонки подписей: по самой длинной подписи, но не больше 55% панели (ползунок остаётся).
    /// Подписи длиннее колонки обрезаются с «…», полный текст — в подсказке.
    /// </summary>
    private static int LabelColumn(IEnumerable<string> texts)
    {
        int max = 0;
        foreach (var t in texts) max = Math.Max(max, TextRenderer.MeasureText(t, Theme.Mono).Width);
        return Math.Min(MaxLabelCol, max + 16);
    }

    private static string Ellipsize(string text, int maxW)
    {
        if (TextRenderer.MeasureText(text, Theme.Mono).Width <= maxW) return text;
        string s = text;
        while (s.Length > 1 && TextRenderer.MeasureText(s + "…", Theme.Mono).Width > maxW) s = s[..^1];
        return s + "…";
    }

    /// <summary>Разница значений с точностью, с которой значения хранятся в конфиге (3 знака).</summary>
    private static bool Differs(double a, double b) => Math.Abs(Math.Round(a, 3) - Math.Round(b, 3)) > 1e-9;

    private void RefreshFilterTitle()
    {
        string? name = _editPreset;
        _paramsTitle.Text = name is null
            ? "Параметры фильтра"
            : $"Параметры фильтра «{name}»" + (_model.IsPresetModified(name) ? "  изменён*" : "");
    }

    /// <summary>Человеческое имя стадии для заголовка (если известно).</summary>
    private static string StageHumanName(string id) => id switch
    {
        "crt" => "кинескоп",
        "phosphor" => "люминофор",
        "vhs" => "видеокассета",
        "tv" => "телевизор (PAL / SECAM / NTSC)",
        "chroma" => "хрома-аберрация",
        "mono" => "монохром (символы)",
        _ => "",
    };

    private static Panel NewRow() => new() { BackColor = Theme.Bg };

    private static Panel NoteRow(string text)
    {
        var row = NewRow();
        var note = new Label { Text = text, AutoSize = true, Location = new Point(0, 4) };
        row.Controls.Add(note);
        Theme.Apply(row);
        note.ForeColor = Theme.Muted;
        return row;
    }

    private static Panel HeaderRow(string text)
    {
        var row = NewRow();
        var head = new Label { Text = text, AutoSize = true, Location = new Point(0, 4) };
        head.Tag = new Font(Theme.Mono, FontStyle.Bold);
        row.Controls.Add(head);
        Theme.Apply(row);
        head.ForeColor = Theme.Accent;
        return row;
    }

    /// <summary>Строка параметра: подпись, контрол, значение, «↺» (по умолчанию). Контрол и значение задаёт вызывающий.</summary>
    /// <summary>
    /// Строка: подпись text (обрезается с «…»; в подсказке — полное description и техническое имя), место под контрол
    /// (слева от значения, x = _labelCol), значение и кнопка «по умолчанию» справа.
    /// </summary>
    private (Panel Row, Label Value, ResetButton Reset) ParamRow(string name, string text, string description)
    {
        var row = NewRow();
        string full = description.Trim();
        var label = new Label
        {
            Text = Ellipsize(text, _labelCol - 12),
            AutoSize = false,
            Size = new Size(_labelCol - 8, 20),
            Location = new Point(0, 4),
            TextAlign = ContentAlignment.MiddleLeft,
        };
        _tip.SetToolTip(label, full.Length > 0 ? $"{full}\n({name})" : name);
        var value = new Label { AutoSize = false, Size = new Size(ValueW, 20), Location = new Point(ValueX, 4), TextAlign = ContentAlignment.MiddleRight };
        var reset = new ResetButton { Size = new Size(28, 22), Location = new Point(ResetX, 3) };
        row.Controls.Add(label);
        row.Controls.Add(value);
        row.Controls.Add(reset);
        return (row, value, reset);
    }

    /// <summary>Место под контрол параметра: от колонки подписей до колонки значения.</summary>
    private Size ControlSize(int height) => new(Math.Max(60, ControlEnd - _labelCol), height);

    private static string FormatNumber(double v, double min, double max, bool integer)
    {
        if (integer) return ((int)Math.Round(v)).ToString(CultureInfo.InvariantCulture);
        double span = Math.Abs(max - min);
        string fmt = span <= 2 ? "0.00" : span <= 50 ? "0.0" : "0";
        return v.ToString(fmt, CultureInfo.InvariantCulture);
    }

    private Panel SceneParamRow(string sceneId, SceneParamSpec spec)
    {
        var (row, value, reset) = ParamRow(spec.Name, LabelText(spec.Name, spec.Description), spec.Description);
        double v = _model.SceneParamValue(sceneId, spec);
        string def;
        Control control;
        switch (spec.Kind)
        {
            case SceneParamKind.Bool:
                var check = new CheckBox { Text = "", AutoSize = true, Location = new Point(_labelCol, 4), Checked = v >= 0.5 };
                value.Text = check.Checked ? "вкл" : "выкл";
                check.CheckedChanged += (_, _) =>
                {
                    _model.SetSceneParam(sceneId, spec, check.Checked ? 1 : 0);
                    value.Text = check.Checked ? "вкл" : "выкл";
                    reset.Enabled = Differs(check.Checked ? 1 : 0, spec.Default);
                    OnParamEdited();
                };
                reset.Click += (_, _) => { _model.ResetSceneParam(sceneId, spec); check.Checked = spec.Default >= 0.5; };
                control = check;
                def = spec.Default >= 0.5 ? "вкл" : "выкл";
                reset.Enabled = Differs(v, spec.Default);
                break;
            case SceneParamKind.Choice:
                var combo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(_labelCol, 3), Size = ControlSize(24) };
                combo.Items.AddRange(spec.Options ?? Array.Empty<string>());
                combo.SelectedIndex = Math.Clamp((int)Math.Round(v), 0, Math.Max(0, combo.Items.Count - 1));
                combo.SelectedIndexChanged += (_, _) =>
                {
                    if (combo.SelectedIndex < 0) return;
                    _model.SetSceneParam(sceneId, spec, combo.SelectedIndex);
                    reset.Enabled = Differs(combo.SelectedIndex, spec.Default);
                    OnParamEdited();
                };
                reset.Click += (_, _) => { _model.ResetSceneParam(sceneId, spec); combo.SelectedIndex = (int)Math.Round(spec.Default); };
                control = combo;
                def = spec.Options is { Length: > 0 } so ? so[Math.Clamp((int)Math.Round(spec.Default), 0, so.Length - 1)] : "";
                reset.Enabled = Differs(combo.SelectedIndex, spec.Default);
                value.Text = "";
                break;
            default:
                bool integer = spec.Kind == SceneParamKind.Int;
                var slider = new SliderBox { Minimum = spec.Min, Maximum = spec.Max, Integer = integer, Location = new Point(_labelCol, 2), Size = ControlSize(24) };
                slider.Value = (float)v;
                value.Text = FormatNumber(slider.Value, spec.Min, spec.Max, integer);
                slider.ValueChanged += (_, _) =>
                {
                    _model.SetSceneParam(sceneId, spec, slider.Value);
                    value.Text = FormatNumber(slider.Value, spec.Min, spec.Max, integer);
                    reset.Enabled = Differs(slider.Value, spec.Default);
                    OnParamEdited();
                };
                reset.Click += (_, _) =>
                {
                    _model.ResetSceneParam(sceneId, spec);
                    slider.Value = spec.Default;
                    value.Text = FormatNumber(slider.Value, spec.Min, spec.Max, integer);
                    reset.Enabled = false;
                    OnParamEdited();
                };
                control = slider;
                def = FormatNumber(spec.Default, spec.Min, spec.Max, integer);
                reset.Enabled = Differs(slider.Value, spec.Default);
                break;
        }
        row.Controls.Add(control);
        FinishParamRow(row, value, reset, "по умолчанию: " + def);
        return row;
    }

    private Panel FilterParamRow(string preset, int stage, ParamSpec spec)
    {
        var (row, value, reset) = ParamRow(spec.Name, FilterLabel(spec), spec.Description);
        double v = _model.PresetParamValue(preset, stage, spec);
        Control control;
        string def;
        if (spec.Options is { Length: > 0 } opts)
        {
            var combo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(_labelCol, 3), Size = ControlSize(24) };
            combo.Items.AddRange(opts);
            combo.SelectedIndex = Math.Clamp((int)Math.Round(v), 0, opts.Length - 1);
            combo.SelectedIndexChanged += (_, _) =>
            {
                if (combo.SelectedIndex < 0) return;
                _model.SetPresetParam(preset, stage, spec, combo.SelectedIndex);
                reset.Enabled = Differs(combo.SelectedIndex, spec.Default);
                OnParamEdited();
            };
            reset.Click += (_, _) => { _model.SetPresetParam(preset, stage, spec, spec.Default); combo.SelectedIndex = (int)Math.Round(spec.Default); };
            control = combo;
            def = opts[Math.Clamp((int)Math.Round(spec.Default), 0, opts.Length - 1)];
            reset.Enabled = Differs(combo.SelectedIndex, spec.Default);
            value.Text = "";
        }
        else
        {
            var slider = new SliderBox { Minimum = spec.Min, Maximum = spec.Max, Location = new Point(_labelCol, 2), Size = ControlSize(24) };
            slider.Value = (float)v;
            value.Text = FormatNumber(slider.Value, spec.Min, spec.Max, false);
            slider.ValueChanged += (_, _) =>
            {
                _model.SetPresetParam(preset, stage, spec, slider.Value);
                value.Text = FormatNumber(slider.Value, spec.Min, spec.Max, false);
                reset.Enabled = Differs(slider.Value, spec.Default);
                OnParamEdited();
            };
            reset.Click += (_, _) =>
            {
                _model.SetPresetParam(preset, stage, spec, spec.Default);
                slider.Value = spec.Default;
                value.Text = FormatNumber(slider.Value, spec.Min, spec.Max, false);
                reset.Enabled = false;
                OnParamEdited();
            };
            control = slider;
            def = FormatNumber(spec.Default, spec.Min, spec.Max, false);
            reset.Enabled = Differs(slider.Value, spec.Default);
        }
        row.Controls.Add(control);
        FinishParamRow(row, value, reset, "по умолчанию: " + def);
        return row;
    }

    /// <summary>Общая отделка строки: стиль темы, цвет значения, подсказка у «по умолчанию».</summary>
    private void FinishParamRow(Panel row, Label value, ResetButton reset, string resetTip)
    {
        Theme.Apply(row);
        value.ForeColor = Theme.Muted;
        _tip.SetToolTip(reset, resetTip);
    }

    /// <summary>Любая правка параметра: конфиг уже обновлён в модели; превью перезапустится (debounce), список и заголовок — в актуальное состояние.</summary>
    private void OnParamEdited()
    {
        _presets.Invalidate();
        if (_tabs.SelectedIndex == 1) RefreshFilterTitle();
        UpdatePreview();
    }


    private void OnPresetSaveAs()
    {
        if (_editPreset is null) return;
        string source = _editPreset;
        string? name = NameDialog.Ask(this, "Имя нового пресета (латиница, цифры, дефис):", source + "-2", _model.ValidatePresetName);
        if (name is null) return;
        var problem = _model.SaveAsPreset(source, name);
        if (problem is not null)
        {
            MessageBox.Show(this, problem, "PHOSPHOR", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        RefreshPresetLists(name);
    }

    private void OnPresetBuiltin()
    {
        if (_editPreset is null) return;
        if (!_model.RestoreBuiltinPreset(_editPreset)) return;
        _presets.Invalidate();
        RebuildParams();
        UpdatePreview();
    }

    private void OnPresetDelete()
    {
        if (_editPreset is null) return;
        string name = _editPreset;
        if (MessageBox.Show(this, $"Удалить пресет «{name}»?", "PHOSPHOR", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        if (!_model.DeletePreset(name))
        {
            MessageBox.Show(this, "Этот пресет нельзя удалить (встроенный или последний).", "PHOSPHOR", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        string? first = _model.PresetNames.FirstOrDefault();
        RefreshPresetLists(first);
    }

    /// <summary>После «Сохранить как» / «Удалить»: обновить комбобокс и список, выделить select.</summary>
    private void RefreshPresetLists(string? select)
    {
        FillActiveCombo(_model.Config.Filters.Active);
        _presets.Fill(_model.PresetNames);
        if (select is not null) _presets.SelectId(select);
        _editPreset = _presets.SelectedId;
        _paramsKey = "";
        UpdatePreview();
    }

    /// <summary>Закрытие без «Сохранить»: если пресеты изменены и не записаны в файл — спросить.</summary>
    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (DialogResult != DialogResult.OK && _model.HasUnwrittenPresetChanges())
        {
            var answer = MessageBox.Show(this, "Изменения пресетов не записаны в phosphor.json. Закрыть без сохранения?",
                "PHOSPHOR", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (answer == DialogResult.No)
            {
                e.Cancel = true;
                return;
            }
        }
        _preview.Stop();
    }

    private void StartProbe()
    {
        _probe.Enabled = false;
        _probeStatus.ForeColor = Theme.Muted;
        _probeStatus.Text = "проверяю… (только чтение)";
        _monitors.Items.Clear();
        var journal = Path.Combine(Path.GetTempPath(), "phosphor-settings-probe.json");
        Task.Run(() =>
        {
            var found = new List<BacklightInfo>();
            bool ready = false;
            try
            {
                // Без Request: яркость не меняется. Журнала по этому пути нет — восстанавливать нечего.
                using var bl = Backlight.Start(journal, m => _log.Info("настройки/подсветка: " + m));
                var sw = Stopwatch.StartNew();
                while (!bl.Ready && sw.Elapsed < TimeSpan.FromSeconds(10)) Thread.Sleep(100);
                ready = bl.Ready;
                found.AddRange(bl.Monitors);
            }
            catch (Exception e)
            {
                _log.Warn("настройки/подсветка: " + e.Message);
            }
            if (IsDisposed || !IsHandleCreated) return;
            BeginInvoke(() => FillMonitors(found, ready));
        });
    }

    private void FillMonitors(List<BacklightInfo> found, bool ready)
    {
        if (IsDisposed) return;
        _probe.Enabled = true;
        _monitors.BeginUpdate();
        _monitors.Items.Clear();
        foreach (var m in found)
        {
            var item = new ListViewItem(m.DeviceName);
            item.SubItems.Add(m.Hardware ? "да" : "нет");
            item.SubItems.Add($"{m.Min}–{m.Original}–{m.Max}");
            item.SubItems.Add(m.Reason ?? (m.Hardware ? "" : "нет ответа DDC/CI"));
            _monitors.Items.Add(item);
        }
        _monitors.EndUpdate();
        _probeStatus.ForeColor = Theme.Muted;
        _probeStatus.Text = !ready ? "опрос не уложился в 10 с" : found.Count == 0 ? "мониторов не найдено" : $"мониторов: {found.Count} (яркость не менялась)";
    }

    private void SelectTab(string name)
    {
        int idx = Array.IndexOf(SettingsTabs.All, name);
        if (idx >= 0) _tabs.SelectedIndex = idx;
    }

    private static void OpenFile(string path)
    {
        if (!File.Exists(path))
        {
            MessageBox.Show("Файл не найден:\n" + path, "PHOSPHOR", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    // ---------- Колонка полей: X по самой длинной подписи страницы ----------

    private static readonly string[] FilterLabels = { "активный фильтр" };
    private static readonly string[] ShuffleLabels = { "интервал, с", "переход", "длительность перехода, с", "вероятность HUD-оверлея, %" };
    private static readonly string[] DimmerLabels = { "через, мин", "длительность затемнения, с", "целевая яркость подсветки, %", "яркость картинки без DDC, %" };
    private static readonly string[] NetworkLabels = { "город" };

    /// <summary>X поля = отступ 12 + ширина самой длинной подписи (TextRenderer.MeasureText, Consolas 10pt) + зазор 12.</summary>
    private static int LabelCol(IEnumerable<string> labels)
    {
        int max = 0;
        foreach (var s in labels) max = Math.Max(max, TextRenderer.MeasureText(s, Theme.Mono).Width);
        return 12 + max + 12;
    }

    // ---------- Мелкие помощники раскладки ----------

    private static Label Lbl(string text, Color? color = null) => new()
    {
        Text = text,
        AutoSize = true,
        ForeColor = color ?? Theme.Text,
    };

    private static void Place(Control host, Control c, int x, int y)
    {
        c.Location = new Point(x, y);
        host.Controls.Add(c);
    }

    private static void Check(Control host, CheckBox k, string text, int x, int y)
    {
        k.Text = text;
        k.AutoSize = true;
        Place(host, k, x, y);
    }

    private static void Num(Control host, NumBox n, decimal min, decimal max, int decimals, int x, int y, int w)
    {
        n.DecimalPlaces = decimals;
        n.Minimum = min;
        n.Maximum = max;
        n.Size = new Size(w, 24);
        Place(host, n, x, y);
    }

    private static void Button(Panel p, string text, int x, int y, int w, Action onClick)
    {
        var b = new Button { Text = text, Location = new Point(x, y), Size = new Size(w, 28) };
        b.Click += (_, _) => onClick();
        p.Controls.Add(b);
    }

    private static decimal Clamp(float v, decimal min, decimal max) => Math.Clamp((decimal)v, min, max);
    private static decimal Clamp(double v, decimal min, decimal max) => Math.Clamp((decimal)v, min, max);

    private sealed record Choice(string Value, string Text)
    {
        public override string ToString() => Text;
    }

    // ---------- Точки входа ----------

    /// <summary>Окно настроек для /c (с owner, если открыто из панели управления: /c:HWND).</summary>
    public static void Open(PhosphorConfig config, IEnumerable<string> scenes, string cfgPath, nint ownerHwnd, Log log)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Theme.ForceDarkNativeControls();
        using var form = new SettingsForm(new SettingsModel(config, scenes), cfgPath, log, livePreview: true);
        if (ownerHwnd != 0) form.ShowDialog(new WinOwner(ownerHwnd));
        else form.ShowDialog();
    }

    /// <summary>--settings-shot: PNG окна на вкладке tab без превью-процесса (вместо него — тёмная панель). Для проверки вёрстки.</summary>
    public static int RenderShot(string pngPath, string tab, PhosphorConfig config, IEnumerable<string> scenes, Log log)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Theme.ForceDarkNativeControls();
        using var form = new SettingsForm(new SettingsModel(config, scenes), cfgPath: "(снимок)", log, livePreview: false);
        form.SelectTab(tab);
        // Показ за экраном: создаются дескрипторы всех контролов для DrawToBitmap, без мигания на рабочем столе
        form.StartPosition = FormStartPosition.Manual;
        form.Location = new Point(-32000, -32000);
        form.Show();
        Application.DoEvents();
        form.PerformLayout();
        var bmp = new Bitmap(form.Width, form.Height);
        form.DrawToBitmap(bmp, new Rectangle(0, 0, form.Width, form.Height));
        var outPath = Path.GetFullPath(pngPath);
        Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
        bmp.Save(outPath, ImageFormat.Png);
        bmp.Dispose();
        log.Info($"settings-shot: вкладка {tab} → {outPath}");
        return 0;
    }

    private sealed class WinOwner : IWin32Window
    {
        public WinOwner(nint hwnd) => Handle = hwnd;
        public nint Handle { get; }
    }
}

/// <summary>Подпись с многоточием в середине пути (PathEllipsis). Полный текст — в подсказке у владельца.</summary>
internal sealed class PathLabel : Label
{
    public PathLabel()
    {
        AutoSize = false;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        using (var bg = new SolidBrush(Theme.Bg)) e.Graphics.FillRectangle(bg, ClientRectangle);
        TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, ForeColor,
            TextFormatFlags.PathEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
    }
}

/// <summary>Вкладки своей отрисовки: полоска заголовков и страницы-панели. Без нативного TabControl (светлая рамка).</summary>
internal sealed class FlatTabs : Panel
{
    private const int HeaderHeight = 28;

    private readonly List<(string Title, Panel Page)> _items = new();
    private int _selected = -1;

    public FlatTabs()
    {
        Padding = new Padding(0, HeaderHeight, 0, 0);
        BackColor = Theme.Bg;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw, true);
    }

    public event EventHandler? SelectedIndexChanged;

    public int ItemWidth { get; set; } = 96;

    public int SelectedIndex
    {
        get => _selected;
        set => Select(value);
    }

    public void Add(string title, Panel page)
    {
        page.Dock = DockStyle.Fill;
        page.Visible = false;
        _items.Add((title, page));
        Controls.Add(page);
        if (_selected < 0) Select(0);
    }

    private void Select(int i)
    {
        if (i < 0 || i >= _items.Count || i == _selected) return;
        if (_selected >= 0) _items[_selected].Page.Visible = false;
        _selected = i;
        _items[i].Page.Visible = true;
        Invalidate();
        SelectedIndexChanged?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        using (var bg = new SolidBrush(Theme.Bg)) g.FillRectangle(bg, 0, 0, Width, HeaderHeight);
        for (int i = 0; i < _items.Count; i++)
        {
            var r = new Rectangle(i * ItemWidth, 0, ItemWidth, HeaderHeight);
            bool sel = i == _selected;
            using (var b = new SolidBrush(sel ? Theme.Panel : Theme.Bg)) g.FillRectangle(b, r);
            TextRenderer.DrawText(g, _items[i].Title, Theme.Mono, r, sel ? Theme.Accent : Theme.Muted,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
            if (sel)
                using (var line = new SolidBrush(Theme.Accent)) g.FillRectangle(line, r.X, r.Bottom - 2, r.Width, 2);
        }
        using (var pen = new Pen(Theme.Border)) g.DrawLine(pen, 0, HeaderHeight - 1, Width, HeaderHeight - 1);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left || e.Y < 0 || e.Y >= HeaderHeight) return;
        int i = e.X / ItemWidth;
        if (i >= 0 && i < _items.Count) Select(i);
    }
}

/// <summary>Числовое поле без нативного spin-контрола: текст + кнопки -/+ (стрелки ↑↓ с клавиатуры).</summary>
internal sealed class NumBox : Panel
{
    private readonly TextBox _text = new();
    private readonly Button _dec = new(), _inc = new();
    private decimal _min, _max = 100, _step = 1;
    private int _decimals;

    public NumBox()
    {
        BackColor = Theme.Bg;
        _text.BorderStyle = BorderStyle.FixedSingle;
        _text.TextAlign = HorizontalAlignment.Right;
        _dec.Text = "-";
        _inc.Text = "+";
        _dec.Click += (_, _) => Value -= _step;
        _inc.Click += (_, _) => Value += _step;
        _text.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Up) { Value += _step; e.Handled = e.SuppressKeyPress = true; }
            else if (e.KeyCode == Keys.Down) { Value -= _step; e.Handled = e.SuppressKeyPress = true; }
        };
        _text.Leave += (_, _) => Value = Value;
        Controls.Add(_text);
        Controls.Add(_dec);
        Controls.Add(_inc);
        Resize += (_, _) => Relayout();
        Relayout();
    }

    public int DecimalPlaces
    {
        get => _decimals;
        set { _decimals = Math.Max(0, value); _step = _decimals > 0 ? 0.5m : 1m; Value = Value; }
    }

    public decimal Minimum { get => _min; set { _min = value; Value = Value; } }

    public decimal Maximum { get => _max; set { _max = value; Value = Value; } }

    public decimal Value
    {
        get => decimal.TryParse(_text.Text, NumberStyles.Number, CultureInfo.CurrentCulture, out var v) ? Clamp(v) : _min;
        set => _text.Text = Math.Round(Clamp(value), _decimals).ToString("F" + _decimals, CultureInfo.CurrentCulture);
    }

    private decimal Clamp(decimal v) => Math.Min(_max, Math.Max(_min, v));

    private void Relayout()
    {
        const int bw = 24;
        _inc.SetBounds(Width - bw, 0, bw, Height);
        _dec.SetBounds(Width - 2 * bw, 0, bw, Height);
        _text.SetBounds(0, 2, Math.Max(20, Width - 2 * bw - 4), 20);
    }
}

/// <summary>
/// Список с тремя состояниями: свой контрол (рисование и прокрутка здесь, без нативного ListView).
/// Колесо, полоса с перетаскиванием, Up/Down/PageUp/PageDown/Home/End выделяют строку и держат её в видимой части; клик или Space циклит состояние.
/// </summary>
internal sealed class PickList : Control
{
    private const int RowH = 22, BarW = 10, PadL = 10;

    private readonly Func<string, Pick> _stateOf;
    private readonly Action<string> _cycle;
    private readonly Func<string, bool>? _modified;
    private readonly List<string> _ids = new();
    private readonly ListScroll _scroll = new();
    private bool _dragBar;

    /// <param name="modified">Пометка «изменён*» у строки (пресеты); null — без пометки.</param>
    public PickList(Func<string, Pick> stateOf, Action<string> cycle, Func<string, bool>? modified = null)
    {
        _stateOf = stateOf;
        _cycle = cycle;
        _modified = modified;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.Selectable, true);
        BackColor = Theme.Panel;
        ForeColor = Theme.Text;
        Font = Theme.Mono;
        TabStop = true;
    }

    /// <summary>Выделенный id или null.</summary>
    public string? SelectedId => _scroll.Selected >= 0 && _scroll.Selected < _ids.Count ? _ids[_scroll.Selected] : null;

    /// <summary>Индекс первой видимой строки (для проверок).</summary>
    public int TopIndex => _scroll.Top;

    /// <summary>Выделенная строка (индекс) или -1.</summary>
    public int SelectedIndex => _scroll.Selected;

    /// <summary>Выделение сменилось (клик, клавиши).</summary>
    public event EventHandler? SelectionChanged;

    public void Fill(IEnumerable<string> ids)
    {
        _ids.Clear();
        _ids.AddRange(ids);
        _scroll.SetCount(_ids.Count);
        Sync();
        Invalidate();
    }

    /// <summary>Выделить строку по id (без учёта регистра), если она есть.</summary>
    public void SelectId(string id)
    {
        int i = _ids.FindIndex(x => string.Equals(x, id, StringComparison.OrdinalIgnoreCase));
        if (i < 0) return;
        SetSelection(i);
        Invalidate();
    }

    private void Sync() => _scroll.SetRows(Math.Max(1, Height / RowH));

    private bool HasBar => _ids.Count > _scroll.Rows;

    private Rectangle Track => new(Width - BarW - 2, 2, BarW, Math.Max(10, Height - 4));

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        Sync();
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Sync();
        var g = e.Graphics;
        g.Clear(Theme.Panel);
        int rowsW = Width - (HasBar ? BarW + 6 : 0);
        for (int r = 0; r < _scroll.Rows; r++)
        {
            int idx = _scroll.Top + r;
            if (idx >= _ids.Count) break;
            var rect = new Rectangle(0, r * RowH, rowsW, RowH);
            bool sel = idx == _scroll.Selected;
            using (var b = new SolidBrush(sel ? Theme.Select : Theme.Panel)) g.FillRectangle(b, rect);
            if (sel && Focused)
                using (var pen = new Pen(Theme.Accent)) g.DrawRectangle(pen, rect.X, rect.Y, rect.Width - 1, rect.Height - 1);

            var id = _ids[idx];
            var pick = _stateOf(id);
            var (glyph, glyphColor) = pick switch
            {
                Pick.Off => ("○", Theme.Muted),
                Pick.On => ("●", Theme.Text),
                _ => ("★", Theme.Accent),
            };
            var flags = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine;
            TextRenderer.DrawText(g, glyph, Theme.Glyph, new Rectangle(PadL, rect.Y, 24, RowH), glyphColor, flags);
            TextRenderer.DrawText(g, id, Theme.Mono, new Rectangle(PadL + 30, rect.Y, rowsW - PadL - 36, RowH),
                pick == Pick.Off ? Theme.Muted : Theme.Text, flags);
            if (_modified?.Invoke(id) == true)
                TextRenderer.DrawText(g, "изменён*", Theme.Mono, new Rectangle(rowsW - 90, rect.Y, 84, RowH), Theme.Accent,
                    TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
        }

        if (HasBar)
        {
            var track = Track;
            using (var tb = new SolidBrush(Theme.Bg)) g.FillRectangle(tb, track);
            var (y, h) = _scroll.Thumb(track.Height);
            using (var th = new SolidBrush(Theme.Border)) g.FillRectangle(th, new Rectangle(track.X + 2, track.Y + y, track.Width - 4, h));
        }
        using (var pen = new Pen(Theme.Border)) g.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        if (e.Button != MouseButtons.Left) return;
        if (HasBar && Track.Contains(e.Location))
        {
            _dragBar = true;
            DragBar(e.Y);
            return;
        }
        Sync();
        int row = e.Y / RowH;
        int idx = _scroll.Top + row;
        if (e.Y < 0 || row >= _scroll.Rows || idx >= _ids.Count) return;
        SetSelection(idx);
        _cycle(_ids[idx]);
        Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_dragBar) DragBar(e.Y);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        _dragBar = false;
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        ScrollWheel(e.Delta);
    }

    /// <summary>Колесо: delta — как в WM_MOUSEWHEEL (120 на щелчок), 3 строки на щелчок.</summary>
    public void ScrollWheel(int delta)
    {
        Sync();
        _scroll.ScrollBy(-delta / 120 * 3);
        Invalidate();
    }

    /// <summary>Перетаскивание бегунка: y — координата мыши в контроле.</summary>
    public void DragBar(int y)
    {
        Sync();
        var track = Track;
        var (_, h) = _scroll.Thumb(track.Height);
        int len = track.Height - h;
        _scroll.ScrollTo(_scroll.TopForTrack(y - track.Y - h / 2, len));
        Invalidate();
    }

    protected override bool IsInputKey(Keys key) =>
        key is Keys.Up or Keys.Down or Keys.PageUp or Keys.PageDown or Keys.Home or Keys.End or Keys.Space
        || base.IsInputKey(key);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (KeyNav(e.KeyCode)) e.Handled = true;
    }

    protected override void OnGotFocus(EventArgs e)
    {
        base.OnGotFocus(e);
        Invalidate();
    }

    protected override void OnLostFocus(EventArgs e)
    {
        base.OnLostFocus(e);
        Invalidate();
    }

    /// <summary>Клавиши списка. Возвращает true, если клавиша обработана.</summary>
    public bool KeyNav(Keys key)
    {
        Sync();
        int cur = _scroll.Selected < 0 ? 0 : _scroll.Selected;
        switch (key)
        {
            case Keys.Up: SetSelection(cur - 1); break;
            case Keys.Down: SetSelection(_scroll.Selected < 0 ? 0 : cur + 1); break;
            case Keys.PageUp: SetSelection(cur - _scroll.Rows); break;
            case Keys.PageDown: SetSelection(_scroll.Selected < 0 ? 0 : cur + _scroll.Rows); break;
            case Keys.Home: SetSelection(0); break;
            case Keys.End: SetSelection(_ids.Count - 1); break;
            case Keys.Space:
                if (SelectedId is { } id) _cycle(id);
                break;
            default:
                return false;
        }
        Invalidate();
        return true;
    }

    private void SetSelection(int index)
    {
        int before = _scroll.Selected;
        _scroll.Select(index);
        if (_scroll.Selected != before) SelectionChanged?.Invoke(this, EventArgs.Empty);
    }
}

/// <summary>Превью: дочерний процесс «Phosphor.exe /p HWND --scene ... --preset ...» в панели (как Windows embed заставки).
/// Смена выбора — с задержкой 300 мс; старый процесс убивается. Без live (--settings-shot) — тёмная панель с надписью.</summary>
internal sealed class SettingsPreview : Panel
{
    private readonly bool _live;
    private readonly PhosphorConfig _config;
    private readonly Log _log;
    private readonly WinTimer _debounce = new() { Interval = 300 };
    /// <summary>Текущий конфиг во временном файле превью (%TEMP%\phosphor-preview-&lt;pid&gt;.json).</summary>
    private readonly string _tempConfig = Path.Combine(Path.GetTempPath(), $"phosphor-preview-{Environment.ProcessId}.json");
    private string? _scene;
    private string? _preset;
    private Process? _proc;
    private string? _runningArgs;
    private string _lastJson = "";

    public SettingsPreview(bool live, PhosphorConfig config, Log log)
    {
        _live = live;
        _config = config;
        _log = log;
        BackColor = Theme.Panel;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
        _debounce.Tick += (_, _) => { _debounce.Stop(); Start(); };
    }

    public void Request(string scene, string? preset)
    {
        _scene = scene;
        _preset = preset;
        if (!_live) { Invalidate(); return; }
        _debounce.Stop();
        _debounce.Start();
    }

    /// <summary>
    /// Перезапуск превью: если конфиг изменился с прошлого запуска — пишем текущий конфиг во временный файл и
    /// запускаем процесс с --config (любое изменение в окне видно в превью). Иначе — только если сменилась сцена или пресет.
    /// </summary>
    private void Start()
    {
        if (IsDisposed || _scene is null) return;
        string json = SettingsJson.Serialize(_config);
        bool cfgChanged = json != _lastJson;
        var args = SettingsModel.PreviewArguments(Handle, _scene, _preset, _tempConfig);
        if (!cfgChanged && args == _runningArgs && _proc is { HasExited: false }) return;
        if (cfgChanged)
        {
            try
            {
                File.WriteAllText(_tempConfig, json, new System.Text.UTF8Encoding(false));
                _lastJson = json;
            }
            catch (Exception e)
            {
                _log.Warn("превью: не записан временный конфиг: " + e.Message);
                return;
            }
        }
        Kill();
        var exe = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "Phosphor.exe");
        try
        {
            _proc = Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = false });
            _runningArgs = args;
            _log.Info("превью: " + args);
        }
        catch (Exception e)
        {
            _log.Warn("превью: не запустилось: " + e.Message);
            _runningArgs = null;
        }
        Invalidate();
    }

    /// <summary>Остановить превью и удалить временный конфиг.</summary>
    public void Stop()
    {
        _debounce.Stop();
        Kill();
        try { File.Delete(_tempConfig); }
        catch (Exception) { /* уже нет или занят — не важно */ }
    }

    private void Kill()
    {
        var p = _proc;
        _proc = null;
        _runningArgs = null;
        if (p is null) return;
        try
        {
            if (!p.HasExited) { p.Kill(entireProcessTree: true); p.WaitForExit(1000); }
        }
        catch (Exception) { /* процесс уже завершился */ }
        finally { p.Dispose(); }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        // Процесс рисует поверх панели; надпись видна, пока его нет (или в --settings-shot)
        if (_proc is { HasExited: false } && _live) return;
        var text = _live ? "превью запускается…" : "превью";
        TextRenderer.DrawText(e.Graphics, text, Theme.Mono, ClientRectangle, Theme.Muted,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        using var pen = new Pen(Theme.Border);
        e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _debounce.Dispose();
            Kill();
        }
        base.Dispose(disposing);
    }
}
