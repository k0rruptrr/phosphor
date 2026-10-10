using System.Diagnostics;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using Phosphor.Core;
using Phosphor.Gfx;
using Phosphor.Platform;
using Phosphor.Text;
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
    private const int PrevX = 632, PrevY = 40, PrevW = 480, PrevH = 270;
    /// <summary>Отступ правой колонки от правого края окна и высота снизу до её кнопок (512 при окне 616).</summary>
    private const int ColRight = 24, BottomBarH = 104;
    // Строки параметров (ширина панели 480, содержимое 464): подписи | контрол | значение | «по умолчанию»
    private const int MaxLabelCol = 264, ValueX = 376, ValueW = 56, ControlEnd = 368, ResetX = 436;
    /// <summary>Колонка подписей в левой части страниц «Экран» и «Оверлеи» (шире: там нет колонки под превью).</summary>
    private const int LeftLabelCol = 330;
    /// <summary>Ширина колонки подписей текущей группы строк (см. LabelColumn).</summary>
    private int _labelCol = 150;
    /// <summary>Геометрия строк параметров: колонка под превью (по умолчанию) или левая часть страницы (экран, оверлеи).</summary>
    private int _valueX = ValueX, _resetX = ResetX, _controlEnd = ControlEnd;

    private readonly SettingsModel _model;
    private readonly string _cfgPath;
    private readonly Log _log;

    private readonly FlatTabs _tabs = new();
    private readonly Label _previewCaption = new();
    private readonly Label _previewHint = new();
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
    // Оверлеи
    private readonly PickList _overlays;
    // Перебор
    private readonly CheckBox _shufScenes = new(), _shufPresets = new(), _perMonitor = new(), _beatSync = new();
    private readonly NumBox _interval = new(), _transSec = new();
    private readonly ComboBox _transition = new();
    // Параметры под превью (сцена на вкладке «Сцены», пресет на «Эффектах», оверлей на «Оверлеях», экран на «Экране»)
    private readonly Label _paramsTitle = new();
    private readonly ParamsPanel _params = new();
    private readonly Button _sceneResetAll = new();
    private readonly Button _screenResetAll = new();
    // Левая часть страниц «Экран» и «Оверлеи»: строки параметров без скролла, кнопка сброса внизу
    private readonly ParamsPanel _screenRows = new();
    private readonly ParamsPanel _overlayRows = new();
    private readonly Button _overlayResetAll = new();
    /// <summary>Строки вкладки «Оверлеи», которые активны не всегда (поля бирки при другом источнике): строка и условие.</summary>
    private readonly List<(Control Row, Func<bool> Active)> _gates = new();
    private readonly RadioButton _screenCrt = new(), _screenFlat = new();
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
    // Шрифт сетки
    private readonly ComboBox _font = new();
    private readonly Label _fontSample = new();
    /// <summary>Перечень шрифтов на момент открытия окна (для образца и подписей).</summary>
    private IReadOnlyList<FontEntry> _fontList = Array.Empty<FontEntry>();
    private readonly Dictionary<string, Font> _sampleFonts = new(StringComparer.OrdinalIgnoreCase);
    private PrivateFontCollection? _privateFonts;
    private static readonly Lazy<HashSet<string>> s_installedFamilies = new(() =>
        new InstalledFontCollection().Families.Select(f => f.Name).ToHashSet(StringComparer.OrdinalIgnoreCase));

    private string _previewScene = "";
    /// <summary>Пресет, который редактируется и показывается в превью (выбран в списке или активный). null — shuffle.</summary>
    private string? _editPreset;
    /// <summary>Оверлей, выбранный в списке «Оверлеи»: его параметры под превью и --overlay для превью.</summary>
    private string? _editOverlay = SettingsModel.OverlayIds[0];
    /// <summary>Ключ, по которому построены строки параметров (вкладка|сцена|пресет|оверлей): меняется — строки пересобираются.</summary>
    private string _paramsKey = "";
    /// <summary>Программная установка значений: события комбобоксов и списков не пишут в конфиг.</summary>
    private bool _loading;

    /// <summary>Индексы вкладок (порядок BuildTabs). Имена для --tab — TabIds.</summary>
    private const int TabScenes = 0, TabEffects = 1, TabOverlays = 2, TabScreen = 3;
    private static readonly string[] TabIds = { "scenes", "filters", "overlays", "screen", "shuffle", "dimmer", "network", "font", "about" };

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
        _overlays = new PickList(_model.GetOverlay, id => { _model.ToggleOverlay(id); _overlays.Invalidate(); UpdatePreview(); })
        {
            Caption = SettingsModel.OverlayTitle,
        };

        Text = "PHOSPHOR — настройки";
        try { Icon = System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch (Exception) { }
        // Окно растягивается по высоте: колонки параметров растут; минимум — стандартный размер
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(1136, 616);
        MinimumSize = Size;
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
        _overlays.SelectionChanged += (_, _) => OnOverlaySelection();
        _tabs.SelectedIndexChanged += (_, _) => { LayoutRightColumn(); UpdatePreview(); };
        _editPreset = ActivePresetName();
        if (_editPreset is not null) _presets.SelectId(_editPreset);
        LayoutRightColumn();
        UpdatePreview();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        LayoutRightColumn();
    }

    /// <summary>
    /// Правая колонка: превью 16:9 (480×270; на «Эффектах» 400×225), под ним подпись, колонка параметров
    /// (растёт по высоте окна) и кнопки колонки внизу. Ширина колонки постоянна, правый край — от края окна.
    /// </summary>
    private void LayoutRightColumn()
    {
        int x = ClientSize.Width - ColRight - PrevW;
        var size = PreviewSizeFor(_tabs.SelectedIndex);
        _previewCaption.Location = new Point(x, 16);
        _preview.SetBounds(x, PrevY, size.Width, size.Height);
        int hintY = PrevY + size.Height + 10;
        _previewHint.Location = new Point(x, hintY);
        int titleY = hintY + 24;
        _paramsTitle.Location = new Point(x, titleY);
        int btnY = ClientSize.Height - BottomBarH;
        int top = titleY + 24;
        _params.SetBounds(x, top, PrevW, Math.Max(40, btnY - 6 - top));
        _sceneResetAll.Location = new Point(x, btnY);
        _presetSaveAs.Location = new Point(x, btnY);
        _presetBuiltin.Location = new Point(x + 158, btnY);
        _presetDelete.Location = new Point(x + 356, btnY);
    }

    /// <summary>Размер превью: на «Эффектах» — 400×225, чтобы под ним было больше места для параметров; иначе 480×270.</summary>
    private static Size PreviewSizeFor(int tab) => tab == TabEffects ? new Size(400, 225) : new Size(PrevW, PrevH);

    // ---------- Раскладка ----------

    private void BuildTabs()
    {
        _tabs.Location = new Point(TabsX, TabsY);
        _tabs.Size = new Size(TabsW, TabsH);
        _tabs.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left;
        _tabs.Add("Сцены", Page(BuildScenesPage));
        _tabs.Add("Эффекты", Page(BuildFiltersPage));
        _tabs.Add("Оверлеи", Page(BuildOverlaysPage));
        _tabs.Add("Экран", Page(BuildScreenPage));
        _tabs.Add("Перебор", Page(BuildShufflePage));
        _tabs.Add("Диммер", Page(BuildDimmerPage));
        _tabs.Add("Сеть", Page(BuildNetworkPage));
        _tabs.Add("Шрифт", Page(BuildFontPage));
        // «О программе» не влезает в полоску из девяти вкладок (обрезается) — короткое «Инфо»
        _tabs.Add("Инфо", Page(BuildAboutPage));
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
        // Список растёт по высоте страницы (якоря внутри страниц вкладок не годятся — см. FitPage)
        p.Resize += (_, _) => _scenes.SetBounds(12, 84, 568, Math.Max(40, p.ClientSize.Height - 90));
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
        p.Resize += (_, _) => _presets.SetBounds(12, 120, 568, Math.Max(40, p.ClientSize.Height - 126));
        _presets.Fill(_model.PresetNames);
        p.Controls.Add(_presets);
    }

    private void BuildOverlaysPage(Panel p)
    {
        // Четыре строки списка (HUD, OSD, часы, логотип) — видны все; ниже — параметры выбранной
        _overlays.Location = new Point(12, 8);
        _overlays.Size = new Size(568, 88);
        _overlays.Fill(SettingsModel.OverlayIds);
        _overlays.SelectId(_editOverlay ?? SettingsModel.OverlayIds[0]);
        p.Controls.Add(_overlays);
        Place(p, Lbl("клик или Space: выкл → вкл. Ниже — параметры выбранной строки.", Theme.Muted), 12, 100);
        // Параметры выбранной строки — прокручиваемая колонка; кнопка сброса закреплена внизу вне прокрутки
        _overlayRows.Location = new Point(12, 118);
        _overlayRows.Size = new Size(588, 354);
        _overlayRows.BackColor = Theme.Bg;
        p.Controls.Add(_overlayRows);
        _overlayResetAll.Text = "все по умолчанию";
        _overlayResetAll.Location = new Point(12, 478);
        _overlayResetAll.Size = new Size(210, 26);
        p.Resize += (_, _) =>
        {
            int h = p.ClientSize.Height;
            _overlayRows.SetBounds(12, 118, 588, Math.Max(40, h - 118 - 40));
            _overlayResetAll.Location = new Point(12, h - 34);
        };
        _overlayResetAll.Click += (_, _) => OnResetAll();
        p.Controls.Add(_overlayResetAll);
    }

    /// <summary>
    /// Страница «Экран»: пояснение (одной строкой, приглушённо), переключатель «кинескоп / плоский экран»,
    /// группы параметров (ScreenConfig.Specs) без скролла, «вернуть всё» внизу. Колонка под превью не нужна.
    /// </summary>
    private void BuildScreenPage(Panel p)
    {
        var note = Lbl("Корпус монитора: одинаков для всех эффектов. Эффекты — сигнал, экран — железо.", Theme.Muted);
        note.Tag = new Font(Theme.Mono.FontFamily, 8.5f, FontStyle.Regular, GraphicsUnit.Point); // Theme.Apply берёт шрифт из Tag
        Place(p, note, 12, 10);

        _screenCrt.Text = "кинескоп";
        _screenFlat.Text = "плоский экран";
        _screenCrt.AutoSize = _screenFlat.AutoSize = true;
        _screenCrt.Location = new Point(12, 36);
        _screenFlat.Location = new Point(150, 36);
        _screenCrt.Checked = _model.ScreenEnabled;
        _screenFlat.Checked = !_model.ScreenEnabled;
        _screenCrt.CheckedChanged += (_, _) => { if (_screenCrt.Checked) OnScreenKindChanged(); };
        _screenFlat.CheckedChanged += (_, _) => { if (_screenFlat.Checked) OnScreenKindChanged(); };
        p.Controls.Add(_screenCrt);
        p.Controls.Add(_screenFlat);

        _screenRows.Location = new Point(12, 64);
        _screenRows.Size = new Size(588, 404);
        _screenRows.BackColor = Theme.Bg;
        p.Controls.Add(_screenRows);

        _screenResetAll.Text = "вернуть всё";
        _screenResetAll.Location = new Point(12, 478);
        _screenResetAll.Size = new Size(210, 26);
        p.Resize += (_, _) =>
        {
            int h = p.ClientSize.Height;
            _screenRows.SetBounds(12, 64, 588, Math.Max(40, h - 64 - 40));
            _screenResetAll.Location = new Point(12, h - 34);
        };
        _screenResetAll.Click += (_, _) => { _model.ResetScreen(); RebuildScreenRows(); UpdatePreview(); };
        p.Controls.Add(_screenResetAll);
    }

    /// <summary>Переключатель «кинескоп / плоский экран»: меняет Screen.Enabled; значения полей не трогаются.</summary>
    private void OnScreenKindChanged()
    {
        if (_loading) return;
        _model.ScreenEnabled = _screenCrt.Checked;
        RebuildScreenRows();
        UpdatePreview();
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
    }

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

    private void BuildFontPage(Panel p)
    {
        Place(p, Lbl("шрифт сетки"), 12, 17);
        _font.Location = new Point(LabelCol(FontLabels), 12);
        _font.Size = new Size(330, 24);
        _font.DropDownStyle = ComboBoxStyle.DropDownList;
        _font.SelectedIndexChanged += (_, _) => { if (!_loading) OnFontChanged(); };
        p.Controls.Add(_font);

        Place(p, Lbl("образец", Theme.Muted), 12, 56);
        _fontSample.AutoSize = false;
        _fontSample.Location = new Point(12, 80);
        _fontSample.Size = new Size(568, 56);
        _fontSample.Text = "PHOSPHOR 0123 ░▒▓█ アイウ";
        p.Controls.Add(_fontSample);

        Place(p, Lbl("Шрифт применяется в превью сразу.", Theme.Muted), 12, 156);
        Place(p, Lbl("Ширина ячейки — по шрифту, высота — cellHeightPx.", Theme.Muted), 12, 180);
        Place(p, Lbl("«· встроенный» — из папки fonts рядом с exe (OFL).", Theme.Muted), 12, 204);
        Place(p, Lbl("Не найден — Consolas и предупреждение в логе.", Theme.Muted), 12, 228);
    }

    private void FillFontCombo(string current)
    {
        _fontList = FontCatalog.List();
        _font.BeginUpdate();
        _font.Items.Clear();
        foreach (var e in _fontList) _font.Items.Add(new Choice(e.Family, e.Builtin ? $"{e.Family} · встроенный" : e.Family));
        int idx = _font.Items.Cast<Choice>().ToList().FindIndex(ch => string.Equals(ch.Value, current, StringComparison.OrdinalIgnoreCase));
        if (idx < 0)
        {
            // Значение из конфига не в перечне: путь к файлу или отсутствующий шрифт — показываем, как есть
            bool isPath = current.Contains('\\') || current.Contains('/') || current.Contains(':');
            string text = isPath ? Path.GetFileName(current) + " · файл" : current + " · не найден";
            _font.Items.Insert(0, new Choice(current, text));
            idx = 0;
        }
        _font.SelectedIndex = idx;
        _font.EndUpdate();
        UpdateFontSample();
    }

    private void OnFontChanged()
    {
        if (_font.SelectedItem is not Choice ch) return;
        _model.Config.Text.Font = ch.Value;
        UpdateFontSample();
        UpdatePreview();
    }

    private void UpdateFontSample()
    {
        _fontSample.Font = _font.SelectedItem is Choice ch ? SampleFont(ch.Value) : Theme.Mono;
    }

    /// <summary>Образец шрифтом семейства. Установленные — из системы; встроенные и пользовательские — из файла (PrivateFontCollection).</summary>
    private Font SampleFont(string family)
    {
        if (_sampleFonts.TryGetValue(family, out var cached)) return cached;
        var entry = _fontList.FirstOrDefault(e => string.Equals(e.Family, family, StringComparison.OrdinalIgnoreCase));
        if (entry is null) return Theme.Mono; // не в перечне — образец обычным шрифтом окна
        try
        {
            Font font;
            if (s_installedFamilies.Value.Contains(family))
            {
                font = new Font(family, 14f, FontStyle.Regular, GraphicsUnit.Point);
            }
            else
            {
                _privateFonts ??= new PrivateFontCollection();
                _privateFonts.AddFontFile(entry.Path);
                font = new Font(_privateFonts.Families[^1], 14f, FontStyle.Regular, GraphicsUnit.Point);
            }
            _sampleFonts[family] = font;
            return font;
        }
        catch (Exception e)
        {
            _log.Warn($"образец шрифта '{family}': {e.Message}");
            return Theme.Mono;
        }
    }

    private static readonly string[] FontLabels = { "шрифт сетки" };

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
        _preview.Size = new Size(PrevW, PrevH);
        _previewHint.Text = "без звука, как в заставке";
        _previewHint.AutoSize = true;
        _previewHint.ForeColor = Theme.Muted;
        Controls.Add(_previewHint);
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
        _sceneResetAll.Click += (_, _) => OnResetAll();

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
        _save.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
        _save.Click += (_, _) => Save();
        _cancel.Text = "Отмена";
        _cancel.Size = new Size(110, 32);
        _cancel.Location = new Point(1136 - 16 - 110, 568);
        _cancel.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
        _cancel.DialogResult = DialogResult.Cancel;
        // Путь обрезается многоточием в середине и не заходит под кнопку «Сохранить»; полный путь — в подсказке
        _status.Location = new Point(16, 576);
        _status.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
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

        _dimEnabled.Checked = c.Dimmer.Enabled;
        _dimDelay.Value = Clamp(c.Dimmer.DelayMin, 0, 240);
        _dimFade.Value = Clamp(c.Dimmer.FadeSec, 0, 600);
        _dimHw.Checked = c.Dimmer.Hardware;
        _dimHwLevel.Value = Clamp(c.Dimmer.HardwareLevel, 0, 100);
        _dimSwLevel.Value = Clamp(c.Dimmer.SoftwareLevel * 100, 0, 100);

        _netEnabled.Checked = c.Network.Enabled;
        _city.Text = c.Network.City;

        FillFontCombo(c.Text.Font);
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

        c.Dimmer.Enabled = _dimEnabled.Checked;
        c.Dimmer.DelayMin = (float)_dimDelay.Value;
        c.Dimmer.FadeSec = (float)_dimFade.Value;
        c.Dimmer.Hardware = _dimHw.Checked;
        c.Dimmer.HardwareLevel = (int)_dimHwLevel.Value;
        c.Dimmer.SoftwareLevel = (float)_dimSwLevel.Value / 100f;

        c.Network.Enabled = _netEnabled.Checked;
        var city = _city.Text.Trim();
        if (city.Length > 0) c.Network.City = city;

        if (_font.SelectedItem is Choice font) c.Text.Font = font.Value;
    }

    private void Save()
    {
        var problem = _model.Problem();
        if (problem is not null)
        {
            MessageBox.Show(this, problem, "PHOSPHOR", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            _tabs.SelectedIndex = problem.Contains("сцен") ? TabScenes : TabEffects;
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
        // --overlay — только на вкладке «Оверлеи»: превью показывает выбранный оверлей
        string? overlay = _tabs.SelectedIndex == TabOverlays ? _editOverlay : null;
        if (_previewScene.Length > 0) _preview.Request(_previewScene, preset, overlay);
        if (CurrentParamsKey() != _paramsKey) RebuildParams();
    }

    private string CurrentParamsKey() => $"{_tabs.SelectedIndex}|{_previewScene}|{_editPreset}|{_editOverlay}";

    /// <summary>Выбор строки в списке «Оверлеи»: её параметры — под превью, превью показывает её.</summary>
    private void OnOverlaySelection()
    {
        if (_overlays.SelectedId is null) return;
        _editOverlay = _overlays.SelectedId;
        UpdatePreview();
    }

    /// <summary>«Все по умолчанию» под превью: сцена или оверлей — в зависимости от вкладки.</summary>
    private void OnResetAll()
    {
        if (_tabs.SelectedIndex == TabScenes)
        {
            if (_previewScene.Length == 0) return;
            _model.ResetSceneParams(_previewScene);
        }
        else if (_tabs.SelectedIndex == TabOverlays)
        {
            if (_editOverlay is null) return;
            _model.ResetOverlay(_editOverlay);
            _overlays.Invalidate();
        }
        else return;
        RebuildParams();
        UpdatePreview();
    }

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
        int tab = _tabs.SelectedIndex;
        bool scenesTab = tab == TabScenes;
        bool filtersTab = tab == TabEffects;
        bool overlaysTab = tab == TabOverlays;
        bool screenTab = tab == TabScreen;
        // Колонка под превью — только для сцен и эффектов; оверлеи и экран строятся в левой части своих страниц
        _paramsTitle.Visible = _params.Visible = scenesTab || filtersTab;
        _sceneResetAll.Visible = scenesTab;
        foreach (var b in new[] { _presetSaveAs, _presetBuiltin, _presetDelete }) b.Visible = filtersTab;
        SetColumnGeometry(left: false);
        if (scenesTab) BuildSceneRows();
        else if (filtersTab) BuildFilterRows();
        else if (overlaysTab) RebuildOverlayRows();
        else if (screenTab) RebuildScreenRows();
    }

    /// <summary>Геометрия строк: колонка под превью (ширина 464) или левая часть страницы (ширина 568).</summary>
    private void SetColumnGeometry(bool left)
    {
        _leftColumn = left;
        if (left) { _valueX = 478; _resetX = 540; _controlEnd = 468; }
        else { _valueX = ValueX; _resetX = ResetX; _controlEnd = ControlEnd; }
    }

    /// <summary>Группы параметров экрана: подзаголовок и строки; у «плоского экрана» корпус приглушён (значения сохраняются).</summary>
    private void RebuildScreenRows()
    {
        _screenRows.Clear();
        SetColumnGeometry(left: true);
        _labelCol = LabelColumn(ScreenConfig.Specs.Select(s => LabelText(s.Name, s.Description)), LeftLabelCol);
        var groups = new (string Title, string[] Names, bool Body)[]
        {
            ("Геометрия", new[] { "curvature", "cornerRadius", "vignette", "overscan" }, true),
            ("Стекло", new[] { "glare" }, true),
            ("Корпус", new[] { "bezel", "bezelColor", "bezelSize", "bezelGlow" }, true),
            ("Шлейф", new[] { "trail" }, false),
        };
        bool crt = _model.ScreenEnabled;
        foreach (var (title, names, body) in groups)
        {
            var head = HeaderRow(title);
            if (body && !crt) DimLabels(head);
            _screenRows.AddRow(head, 26);
            foreach (var name in names)
            {
                var spec = ScreenConfig.Specs.First(s => s.Name == name);
                var row = ScreenParamRow(spec);
                // Плоский экран: корпус неактивен (тусклый, не нажимается), шлейф — всегда активен
                if (body && !crt) { row.Enabled = false; DimLabels(row); }
                _screenRows.AddRow(row);
            }
            _screenRows.AddRow(NewRow(), 6);
        }
        SetColumnGeometry(left: false);
    }

    /// <summary>Неактивная группа: подписи приглушённым цветом (WinForms сам их не затеняет).</summary>
    private static void DimLabels(Control host)
    {
        foreach (Control c in host.Controls)
        {
            if (c is Label l) l.ForeColor = Theme.Muted;
            DimLabels(c);
        }
    }

    /// <summary>Левая часть «Оверлеи»: шаг строки параметров (меньше 28, чтобы влезли бирки) и высота подсказки.</summary>
    private const int OverlayRowStep = 27, OverlayNoteH = 18, OverlayHeadH = 22;
    /// <summary>Колонка подписей «Оверлеи»: длинные описания бирок (до ~50 знаков) без «…»; поля уже, но не меньше 88 px.</summary>
    private const int OverlayLabelCol = 380;
    /// <summary>Правый край выпадающих списков в левой части (колонка значения 478..534).</summary>
    private const int OverlayComboEnd = 534;
    /// <summary>true — строки строятся в левой части страницы (оверлеи, экран): геометрия ComboSize.</summary>
    private bool _leftColumn;

    private void RebuildOverlayRows()
    {
        _overlayRows.Clear();
        _gates.Clear();
        string id = _editOverlay ?? SettingsModel.OverlayIds[0];
        bool hud = id == "hud";
        bool vcr = id == "vcr";
        bool clock = id == "clock";
        bool logo = id == "logo";
        var specs = _model.OverlaySpecs(id) ?? Array.Empty<SceneParamSpec>();

        var labels = new List<string>();
        if (hud) labels.Add("вероятность, %");
        labels.AddRange(specs.Select(s => LabelText(s.Name, s.Description)));
        if (vcr) labels.Add("шрифт OSD");
        if (clock || logo) labels.Add("шрифт");
        if (logo) { labels.Add("текст"); labels.Add("файл PNG"); }
        SetColumnGeometry(left: true);
        _labelCol = LabelColumn(labels, OverlayLabelCol);

        _overlayRows.AddRow(HeaderRow($"Параметры «{SettingsModel.OverlayTitle(id)}»"), OverlayHeadH + 2);
        if (hud) _overlayRows.AddRow(OverlayProbabilityRow(), OverlayRowStep);
        if (specs.Count == 0 && !vcr) _overlayRows.AddRow(NoteRow("у этого оверлея нет настроек"), OverlayRowStep);
        foreach (var spec in specs) _overlayRows.AddRow(OverlayParamRow(id, spec), OverlayRowStep);
        if (vcr) _overlayRows.AddRow(OsdFontRow(), OverlayRowStep);
        if (clock) _overlayRows.AddRow(FontRow("clock", "шрифт", "шрифт для стиля «text»; «как у сетки» — шрифт сетки"), OverlayRowStep);
        if (logo)
        {
            // Порядок как в ТЗ: шрифт, текст, файл PNG и подсказка под ним. Неактивные поля — по источнику (source)
            var font = FontRow("logo", "шрифт", "шрифт для источника «текст»; «как у сетки» — шрифт сетки");
            _overlayRows.AddRow(font, OverlayRowStep); AddGate(font, () => _model.OverlayFieldActive("logo", "font"));
            var text = LogoTextRow();
            _overlayRows.AddRow(text, OverlayRowStep); AddGate(text, () => _model.OverlayFieldActive("logo", "text"));
            var file = PngFileRow();
            _overlayRows.AddRow(file, OverlayRowStep); AddGate(file, () => _model.OverlayFieldActive("logo", "file"));
            _overlayRows.AddRow(NoteRow("PNG с прозрачностью; лучше белый или светлый"), OverlayNoteH);
        }
        SetColumnGeometry(left: false);
        UpdateOverlayGates();
    }

    /// <summary>Строка, которая активна только при условии (см. OverlayFieldActive): неактивная — приглушена и не нажимается.</summary>
    private void AddGate(Control row, Func<bool> active) => _gates.Add((row, active));

    /// <summary>Пересчитать активность строк-ворот по текущему конфигу (после правки источника и т.п.).</summary>
    private void UpdateOverlayGates()
    {
        foreach (var (row, active) in _gates)
        {
            if (row.IsDisposed) continue;
            bool on = active();
            row.Enabled = on;
            foreach (Control c in row.Controls)
                if (c is Label l) l.ForeColor = on ? Theme.Text : Theme.Muted;
        }
    }

    /// <summary>Строка «текст» бирки логотипа (logo.text): однострочное поле. Пусто — ключ удаляется; «PHOSPHOR» — умолчание, ключ тоже не нужен.</summary>
    private Panel LogoTextRow()
    {
        const string def = SettingsModel.LogoDefaultText;
        var (row, value, reset) = ParamRow("logo.text", "текст", $"текст логотипа для источника «текст»; по умолчанию {def}");
        string cur = _model.OverlayString("logo", "text");
        var box = new TextBox { Location = new Point(_labelCol, 3), Size = ControlSize(24) };
        box.Text = cur.Length > 0 ? cur : def;
        reset.Enabled = cur.Length > 0;
        box.TextChanged += (_, _) =>
        {
            _model.SetOverlayString("logo", "text", box.Text == def ? "" : box.Text);
            reset.Enabled = _model.OverlayString("logo", "text").Length > 0;
            OnParamEdited();
        };
        reset.Click += (_, _) =>
        {
            _model.SetOverlayString("logo", "text", "");
            box.Text = def;
            reset.Enabled = false;
            OnParamEdited();
        };
        row.Controls.Add(box);
        FinishParamRow(row, value, reset, $"по умолчанию: {def}");
        return row;
    }

    /// <summary>Строка «файл PNG» бирки логотипа (logo.file): путь и кнопка «…» (OpenFileDialog, *.png). Пусто — файла нет.</summary>
    private Panel PngFileRow()
    {
        var (row, value, _) = ParamRow("logo.file", "файл PNG", "PNG для источника «файл»; путь пишется в overlay.params.logo.file", withReset: false);
        // Поле пути до кнопки «…» (справа, в пределах строки 568)
        const int browseX = 536;
        var box = new TextBox { Location = new Point(_labelCol, 3), Size = new Size(Math.Max(60, browseX - 6 - _labelCol), 24) };
        box.Text = _model.OverlayString("logo", "file");
        var browse = new Button { Text = "…", Location = new Point(browseX, 2), Size = new Size(26, 24) };
        box.TextChanged += (_, _) =>
        {
            _model.SetOverlayString("logo", "file", box.Text.Trim());
            OnParamEdited();
        };
        browse.Click += (_, _) =>
        {
            using var dlg = new OpenFileDialog { Filter = "PNG (*.png)|*.png", Title = "PNG логотипа", CheckFileExists = true };
            string cur = box.Text.Trim();
            if (cur.Length > 0 && File.Exists(cur)) dlg.InitialDirectory = Path.GetDirectoryName(cur);
            if (dlg.ShowDialog(this) == DialogResult.OK) box.Text = dlg.FileName; // TextChanged запишет путь
        };
        row.Controls.Add(box);
        row.Controls.Add(browse);
        Theme.Apply(row);
        value.ForeColor = Theme.Muted;
        _tip.SetToolTip(browse, "выбрать PNG");
        return row;
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
    private static int LabelColumn(IEnumerable<string> texts, int maxCol = MaxLabelCol)
    {
        int max = 0;
        foreach (var t in texts) max = Math.Max(max, TextRenderer.MeasureText(t, Theme.Mono).Width);
        return Math.Min(maxCol, max + 16);
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
    private (Panel Row, Label Value, ResetButton Reset) ParamRow(string name, string text, string description, bool withReset = true)
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
        var value = new Label { AutoSize = false, Size = new Size(ValueW, 20), Location = new Point(_valueX, 4), TextAlign = ContentAlignment.MiddleRight };
        var reset = new ResetButton { Size = new Size(28, 22), Location = new Point(_resetX, 3) };
        row.Controls.Add(label);
        row.Controls.Add(value);
        if (withReset) row.Controls.Add(reset);
        return (row, value, reset);
    }

    /// <summary>Место под контрол параметра: от колонки подписей до колонки значения.</summary>
    private Size ControlSize(int height) => new(Math.Max(60, _controlEnd - _labelCol), height);

    /// <summary>
    /// Место под выпадающий список. В левой части у списка нет текста значения (колонка значения пуста),
    /// поэтому он тянется до её правого края: «как у сетки» и имена шрифтов не обрезаются.
    /// </summary>
    private Size ComboSize(int height) => new(Math.Max(60, (_leftColumn ? OverlayComboEnd : _controlEnd) - _labelCol), height);

    private static string FormatNumber(double v, double min, double max, bool integer)
    {
        if (integer) return ((int)Math.Round(v)).ToString(CultureInfo.InvariantCulture);
        double span = Math.Abs(max - min);
        string fmt = span <= 2 ? "0.00" : span <= 50 ? "0.0" : "0";
        return v.ToString(fmt, CultureInfo.InvariantCulture);
    }

    /// <summary>Для тестов: строка параметра сцены тем же путём, что у окна (окно не показывается).</summary>
    internal Panel ProbeSceneParamRow(string sceneId, SceneParamSpec spec)
    {
        _labelCol = LabelColumn(new[] { LabelText(spec.Name, spec.Description) });
        return SceneParamRow(sceneId, spec);
    }

    /// <summary>Для тестов: строка параметра стадии пресета тем же путём, что у окна (окно не показывается).</summary>
    internal Panel ProbeFilterParamRow(string preset, int stage, ParamSpec spec)
    {
        _labelCol = LabelColumn(new[] { FilterLabel(spec) });
        return FilterParamRow(preset, stage, spec);
    }

    /// <summary>Для тестов: строка параметра экрана (ScreenConfig.Specs) тем же путём, что у окна.</summary>
    internal Panel ProbeScreenParamRow(SceneParamSpec spec)
    {
        _labelCol = LabelColumn(new[] { LabelText(spec.Name, spec.Description) });
        return ScreenParamRow(spec);
    }

    /// <summary>Для тестов: строка параметра оверлея тем же путём, что у окна.</summary>
    internal Panel ProbeOverlayParamRow(string overlayId, SceneParamSpec spec)
    {
        _labelCol = LabelColumn(new[] { LabelText(spec.Name, spec.Description) });
        return OverlayParamRow(overlayId, spec);
    }

    /// <summary>Для тестов: все строки левой части «Оверлеи» для выбранной строки тем же путём, что у окна. Возвращает число контролов.</summary>
    internal int ProbeOverlayRows(string overlayId)
    {
        _editOverlay = overlayId;
        RebuildOverlayRows();
        return _overlayRows.Rows.Count;
    }

    /// <summary>
    /// Для тестов: колонка параметров вкладки (scenes | filters | overlays | screen) для элемента item (сцена, пресет, оверлей;
    /// для screen не нужен) тем же путём, что у окна. Окно не показывается; раскладка выполняется явно.
    /// </summary>
    internal ParamsPanel ProbeColumn(string tab, string item)
    {
        int idx = Array.IndexOf(TabIds, tab);
        _tabs.SelectedIndex = idx;
        switch (idx)
        {
            case TabScenes: _previewScene = item; break;
            case TabEffects: _editPreset = item; break;
            case TabOverlays: _editOverlay = item; break;
        }
        UpdatePreview();
        LayoutForTest();
        return idx switch
        {
            TabOverlays => _overlayRows,
            TabScreen => _screenRows,
            _ => _params,
        };
    }

    /// <summary>Для тестов: раскладка всего дерева контролов (размеры страниц и колонок по якорям).</summary>
    internal void LayoutForTest() => LayoutAll(this);

    private static void LayoutAll(Control c)
    {
        c.PerformLayout();
        foreach (Control child in c.Controls) LayoutAll(child);
    }

    /// <summary>Для тестов: активность строк-ворот (после ProbeOverlayRows), в порядке построения.</summary>
    internal IReadOnlyList<bool> ProbeOverlayGates() => _gates.Select(g => g.Row.Enabled).ToList();

    /// <summary>Для тестов: строка «вероятность, %» (HUD) и строка шрифта OSD (VCR) тем же путём, что у окна.</summary>
    internal Panel ProbeOverlayProbabilityRow()
    {
        _labelCol = LabelColumn(new[] { "вероятность, %" });
        return OverlayProbabilityRow();
    }

    internal Panel ProbeOsdFontRow()
    {
        _labelCol = LabelColumn(new[] { "шрифт OSD" });
        return OsdFontRow();
    }

    /// <summary>Для тестов: форма без показа. Вызывающий освобождает форму и журнал.</summary>
    internal static SettingsForm CreateForProbe(SettingsModel model, Log log) => new(model, "(тест)", log, livePreview: false);

    private Panel SceneParamRow(string sceneId, SceneParamSpec spec) =>
        SpecRow(spec, _model.SceneParamValue(sceneId, spec),
            x => _model.SetSceneParam(sceneId, spec, x),
            () => _model.ResetSceneParam(sceneId, spec));

    private Panel ScreenParamRow(SceneParamSpec spec) =>
        SpecRow(spec, _model.ScreenValue(spec.Name),
            x => _model.SetScreenParam(spec.Name, x),
            () => _model.ResetScreenParam(spec.Name));

    private Panel OverlayParamRow(string overlayId, SceneParamSpec spec) =>
        SpecRow(spec, _model.OverlayParamValue(overlayId, spec),
            x => _model.SetOverlayParam(overlayId, spec, x),
            () => _model.ResetOverlayParam(overlayId, spec));

    /// <summary>
    /// Строка параметра по описанию (сцена, экран, оверлей): контрол по виду (Bool — галка, Choice — комбобокс, иначе ползунок),
    /// write пишет значение в модель, restore возвращает умолчание. Превью обновляется через OnParamEdited.
    /// </summary>
    private Panel SpecRow(SceneParamSpec spec, double v, Action<double> write, Action restore)
    {
        var (row, value, reset) = ParamRow(spec.Name, LabelText(spec.Name, spec.Description), spec.Description);
        string def;
        Control control;
        switch (spec.Kind)
        {
            case SceneParamKind.Bool:
                var check = new CheckBox { Text = "", AutoSize = true, Location = new Point(_labelCol, 4), Checked = v >= 0.5 };
                value.Text = check.Checked ? "вкл" : "выкл";
                check.CheckedChanged += (_, _) =>
                {
                    write(check.Checked ? 1 : 0);
                    value.Text = check.Checked ? "вкл" : "выкл";
                    reset.Enabled = Differs(check.Checked ? 1 : 0, spec.Default);
                    OnParamEdited();
                };
                reset.Click += (_, _) => { restore(); check.Checked = spec.Default >= 0.5; };
                control = check;
                def = spec.Default >= 0.5 ? "вкл" : "выкл";
                reset.Enabled = Differs(v, spec.Default);
                break;
            case SceneParamKind.Choice:
                var combo = new ParamCombo { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(_labelCol, 3), Size = ComboSize(24) };
                combo.Items.AddRange(spec.Options ?? Array.Empty<string>());
                combo.SelectedIndex = Math.Clamp((int)Math.Round(v), 0, Math.Max(0, combo.Items.Count - 1));
                combo.SelectedIndexChanged += (_, _) =>
                {
                    if (combo.SelectedIndex < 0) return;
                    write(combo.SelectedIndex);
                    reset.Enabled = Differs(combo.SelectedIndex, spec.Default);
                    OnParamEdited();
                };
                reset.Click += (_, _) => { restore(); combo.SelectedIndex = (int)Math.Round(spec.Default); };
                control = combo;
                def = spec.Options is { Length: > 0 } so ? so[Math.Clamp((int)Math.Round(spec.Default), 0, so.Length - 1)] : "";
                reset.Enabled = Differs(combo.SelectedIndex, spec.Default);
                value.Text = "";
                break;
            default:
                bool integer = spec.Kind == SceneParamKind.Int;
                var slider = new SliderBox { Integer = integer, Location = new Point(_labelCol, 2), Size = ControlSize(24) };
                slider.SetRange(spec.Min, spec.Max, (float)v);
                value.Text = FormatNumber(slider.Value, spec.Min, spec.Max, integer);
                slider.ValueChanged += (_, _) =>
                {
                    write(slider.Value);
                    value.Text = FormatNumber(slider.Value, spec.Min, spec.Max, integer);
                    reset.Enabled = Differs(slider.Value, spec.Default);
                    OnParamEdited();
                };
                reset.Click += (_, _) =>
                {
                    restore();
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

    /// <summary>Первая строка HUD: вероятность оверлея для очередной сцены (Overlay.Probability), проценты 0..100.</summary>
    private Panel OverlayProbabilityRow()
    {
        const string name = "overlay.probability";
        var (row, value, reset) = ParamRow(name, "вероятность, %", "вероятность HUD-оверлея для очередной сцены (сцены со своим HUD — без него)");
        double def = SettingsModel.DefaultOverlayProbability * 100;
        var box = new NumBox { Location = new Point(_labelCol, 2), Size = ControlSize(24) };
        box.DecimalPlaces = 0;
        box.SetRange(0, 100);
        box.Value = Clamp(_model.OverlayProbability * 100, 0, 100);

        void Refresh()
        {
            value.Text = box.Value.ToString("0", CultureInfo.InvariantCulture);
            reset.Enabled = Differs(_model.OverlayProbability * 100, def);
        }

        box.ValueEdited += (_, _) =>
        {
            double pct = (double)box.Value;
            if (!Differs(pct, _model.OverlayProbability * 100)) return;
            _model.OverlayProbability = pct / 100.0;
            Refresh();
            OnParamEdited();
        };
        reset.Click += (_, _) =>
        {
            _model.OverlayProbability = SettingsModel.DefaultOverlayProbability;
            box.Value = Clamp(def, 0, 100);
            Refresh();
            OnParamEdited();
        };
        Refresh();
        row.Controls.Add(box);
        FinishParamRow(row, value, reset, "по умолчанию: " + FormatNumber(def, 0, 100, true));
        return row;
    }

    /// <summary>Последняя строка OSD видика: шрифт (overlay.params.vcr.font). Первый пункт — «как у сетки» (пусто).</summary>
    private Panel OsdFontRow() => FontRow("vcr", "шрифт OSD", "шрифт OSD видика; «как у сетки» — шрифт сетки");

    /// <summary>Строка шрифта оверлея или бирки (overlay.params.&lt;id&gt;.font): список шрифтов, первый пункт — «как у сетки» (пусто).</summary>
    private Panel FontRow(string overlayId, string label, string description)
    {
        var (row, value, reset) = ParamRow($"{overlayId}.font", label, description);
        var combo = new ParamCombo { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(_labelCol, 3), Size = ComboSize(24) };
        combo.Items.Add(new Choice("", "как у сетки"));
        foreach (var e in FontCatalog.List())
            combo.Items.Add(new Choice(e.Family, e.Builtin ? $"{e.Family} · встроенный" : e.Family));
        string cur = _model.OverlayString(overlayId, "font");
        int idx = cur.Length == 0 ? 0 : combo.Items.Cast<Choice>().ToList().FindIndex(ch => string.Equals(ch.Value, cur, StringComparison.OrdinalIgnoreCase));
        if (idx < 0)
        {
            // Шрифт из конфига не найден в перечне — показываем как есть (как у шрифта сетки)
            combo.Items.Insert(1, new Choice(cur, cur + " · не найден"));
            idx = 1;
        }
        combo.SelectedIndex = idx;
        value.Text = "";

        void Refresh() => reset.Enabled = _model.OverlayString(overlayId, "font").Length > 0;
        combo.SelectedIndexChanged += (_, _) =>
        {
            if (combo.SelectedItem is not Choice ch) return;
            _model.SetOverlayString(overlayId, "font", ch.Value);
            Refresh();
            OnParamEdited();
        };
        reset.Click += (_, _) =>
        {
            _model.SetOverlayString(overlayId, "font", "");
            combo.SelectedIndex = 0;
            Refresh();
            OnParamEdited();
        };
        Refresh();
        row.Controls.Add(combo);
        FinishParamRow(row, value, reset, "по умолчанию: как у сетки");
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
            var combo = new ParamCombo { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(_labelCol, 3), Size = ComboSize(24) };
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
            var slider = new SliderBox { Location = new Point(_labelCol, 2), Size = ControlSize(24) };
            slider.SetRange(spec.Min, spec.Max, (float)v);
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
        if (_tabs.SelectedIndex == TabEffects) RefreshFilterTitle();
        if (_tabs.SelectedIndex == TabOverlays) UpdateOverlayGates();
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

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            foreach (var f in _sampleFonts.Values) f.Dispose();
            _sampleFonts.Clear();
            _privateFonts?.Dispose();
        }
        base.Dispose(disposing);
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
        int idx = Array.IndexOf(TabIds, name);
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
    private static readonly string[] ShuffleLabels = { "интервал, с", "переход", "длительность перехода, с" };
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
        n.SetRange(min, max);
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
    public static int RenderShot(string pngPath, string tab, PhosphorConfig config, IEnumerable<string> scenes, Log log, string? overlay = null)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Theme.ForceDarkNativeControls();
        using var form = new SettingsForm(new SettingsModel(config, scenes), cfgPath: "(снимок)", log, livePreview: false);
        form.SelectTab(tab);
        // Оверлей для снимка вкладки «Оверлеи» (hud по умолчанию, vcr — OSD видика)
        if (overlay is not null) form._overlays.SelectId(overlay);
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

    /// <summary>Поля слева и справа от подписи вкладки. Если все вкладки в ширину не влезают — TabPaddingTight.</summary>
    public const int TabPadding = 12, TabPaddingTight = 6;
    private readonly List<Rectangle> _rects = new();

    /// <summary>Ширина каждой вкладки — по тексту (TextRenderer.MeasureText) + поля.</summary>
    private void RebuildRects()
    {
        _rects.Clear();
        int textSum = 0;
        foreach (var it in _items) textSum += TextRenderer.MeasureText(it.Title, Theme.Mono).Width;
        int pad = textSum + _items.Count * 2 * TabPadding > Width ? TabPaddingTight : TabPadding;
        int x = 0;
        foreach (var it in _items)
        {
            int w = TextRenderer.MeasureText(it.Title, Theme.Mono).Width + 2 * pad;
            _rects.Add(new Rectangle(x, 0, w, HeaderHeight));
            x += w;
        }
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        RebuildRects();
        Invalidate();
    }


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
        RebuildRects();
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
            var r = _rects[i];
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
        int i = _rects.FindIndex(r => r.Contains(e.X, e.Y));
        if (i >= 0) Select(i);
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
        _dec.Click += (_, _) => Edit(Value - _step);
        _inc.Click += (_, _) => Edit(Value + _step);
        _text.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Up) { Edit(Value + _step); e.Handled = e.SuppressKeyPress = true; }
            else if (e.KeyCode == Keys.Down) { Edit(Value - _step); e.Handled = e.SuppressKeyPress = true; }
        };
        _text.Leave += (_, _) => Edit(Value);
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

    /// <summary>Диапазон разом (без промежуточного клэмпа между Minimum и Maximum). min &gt; max — меняем местами, с предупреждением в Debug.</summary>
    public void SetRange(decimal min, decimal max)
    {
        if (min > max)
        {
            System.Diagnostics.Debug.WriteLine($"NumBox.SetRange: min {min} > max {max}, границы поменяны местами");
            (min, max) = (max, min);
        }
        _min = min;
        _max = max;
        Value = Value;
    }

    public decimal Value
    {
        get => decimal.TryParse(_text.Text, NumberStyles.Number, CultureInfo.CurrentCulture, out var v) ? Clamp(v) : _min;
        set => _text.Text = Math.Round(Clamp(value), _decimals).ToString("F" + _decimals, CultureInfo.CurrentCulture);
    }

    /// <summary>Значение изменено пользователем (кнопки, стрелки, уход из поля). Программная установка Value — не вызывает.</summary>
    public event EventHandler? ValueEdited;

    private void Edit(decimal v)
    {
        Value = v;
        ValueEdited?.Invoke(this, EventArgs.Empty);
    }

    private decimal Clamp(decimal v) => Math.Min(_max, Math.Max(_min, v));

    /// <summary>Колесо: при фокусе в поле (ввод числа) меняет значение; иначе — прокручивает колонку (WheelRoute).</summary>
    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WheelRoute.WmMouseWheel)
        {
            int delta = WheelRoute.Delta(m);
            if (ContainsFocus) Edit(Value + Math.Sign(delta) * _step);
            else WheelRoute.Forward(this, delta);
            return;
        }
        base.WndProc(ref m);
    }

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

    /// <summary>Подпись строки (по умолчанию — сам id). Id при этом остаётся техническим ключом.</summary>
    public Func<string, string>? Caption { get; init; }

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
            TextRenderer.DrawText(g, Caption?.Invoke(id) ?? id, Theme.Mono, new Rectangle(PadL + 30, rect.Y, rowsW - PadL - 36, RowH),
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
    /// <summary>Оверлей для превью (--overlay), только пока открыта вкладка «Оверлеи».</summary>
    private string? _overlay;
    private Process? _proc;
    private string? _runningArgs;
    private string _lastJson = "";
    /// <summary>Размер панели изменился: окно превью процесса задаётся при старте — перезапустить.</summary>
    private bool _forceRestart;

    public SettingsPreview(bool live, PhosphorConfig config, Log log)
    {
        _live = live;
        _config = config;
        _log = log;
        BackColor = Theme.Panel;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
        _debounce.Tick += (_, _) => { _debounce.Stop(); Start(); };
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        _forceRestart = true;
        if (_live && _scene is not null)
        {
            _debounce.Stop();
            _debounce.Start();
        }
    }

    public void Request(string scene, string? preset, string? overlay = null)
    {
        _scene = scene;
        _preset = preset;
        _overlay = overlay;
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
        var args = SettingsModel.PreviewArguments(Handle, _scene, _preset, _tempConfig, _overlay);
        if (!cfgChanged && !_forceRestart && args == _runningArgs && _proc is { HasExited: false }) return;
        _forceRestart = false;
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
