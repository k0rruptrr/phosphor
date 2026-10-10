namespace Phosphor.App.Settings;

/// <summary>
/// Ползунок в тёмной теме (нативный TrackBar светлый). Мышь: клик и перетаскивание. Клавиши: ←/→ шаг 1% диапазона
/// (Shift — 10%), Home/End. Integer — значения целые (шаг 1). ValueChanged — только от пользователя.
/// </summary>
internal sealed class SliderBox : Control
{
    private const int Pad = 8;
    private float _min, _max = 1f, _value;
    private bool _drag;

    public SliderBox()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.Selectable, true);
        BackColor = Theme.Bg;
        TabStop = true;
        Cursor = Cursors.Hand;
    }

    public event EventHandler? ValueChanged;

    /// <summary>Пока min &gt; max, диапазон некорректен: значение не клэмпится (см. SetRange — менять диапазон целиком).</summary>
    public float Minimum { get => _min; set { _min = value; Apply(_value, false); } }

    public float Maximum { get => _max; set { _max = value; Apply(_value, false); } }

    /// <summary>
    /// Диапазон и значение разом (без промежуточного клэмпа между Minimum и Maximum). min &gt; max — меняем местами, с предупреждением в Debug.
    /// Событие не вызывается.
    /// </summary>
    public void SetRange(float min, float max, float value)
    {
        if (min > max)
        {
            System.Diagnostics.Debug.WriteLine($"SliderBox.SetRange: min {min} > max {max}, границы поменяны местами");
            (min, max) = (max, min);
        }
        _min = min;
        _max = max;
        Apply(value, false);
    }

    /// <summary>Целые значения (шаг 1).</summary>
    public bool Integer { get; set; }

    /// <summary>Значение программно (без события). Пользователь меняет через мышь и клавиши.</summary>
    public float Value { get => _value; set => Apply(value, false); }

    private void Apply(float v, bool raise)
    {
        // Некорректный диапазон (min > max) — не клэмпим: Math.Clamp на нём бросает исключение. Значение поправится при корректном диапазоне.
        float nv = _min <= _max ? Math.Clamp(v, _min, _max) : v;
        if (Integer) nv = MathF.Round(nv);
        if (nv == _value) return;
        _value = nv;
        Invalidate();
        if (raise) ValueChanged?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        int y = Height / 2;
        var track = new Rectangle(Pad, y - 2, Math.Max(1, Width - 2 * Pad), 4);
        using (var b = new SolidBrush(Theme.Border)) g.FillRectangle(b, track);
        float f = _max > _min ? (_value - _min) / (_max - _min) : 0f;
        int tx = track.X + (int)Math.Round(f * track.Width);
        using (var b = new SolidBrush(Theme.Accent)) g.FillRectangle(b, track.X, track.Y, tx - track.X, track.Height);
        using (var b = new SolidBrush(Focused ? Theme.Accent : Theme.Text)) g.FillRectangle(b, tx - 4, y - 8, 9, 16);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        Focus();
        _drag = true;
        Capture = true;
        SetFromX(e.X);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_drag) SetFromX(e.X);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        _drag = false;
        Capture = false;
    }

    private void SetFromX(int x)
    {
        float f = Math.Clamp((x - Pad) / (float)Math.Max(1, Width - 2 * Pad), 0f, 1f);
        Apply(_min + f * (_max - _min), true);
    }

    protected override bool IsInputKey(Keys key) =>
        key is Keys.Left or Keys.Right or Keys.Home or Keys.End || base.IsInputKey(key);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        float step = Integer ? (e.Shift ? 10f : 1f) : (_max - _min) / 100f * (e.Shift ? 10f : 1f);
        switch (e.KeyCode)
        {
            case Keys.Left: Apply(_value - step, true); break;
            case Keys.Right: Apply(_value + step, true); break;
            case Keys.Home: Apply(_min, true); break;
            case Keys.End: Apply(_max, true); break;
            default: return;
        }
        e.Handled = true;
    }

    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }

    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
}

/// <summary>
/// Прокручиваемый список строк правой колонки (своя полоса, без нативной). Строки одной высоты RowH;
/// ListScroll держит видимую часть (Top — первая видимая строка). Колесо и полоса двигают содержимое.
/// </summary>
internal sealed class ParamsPanel : Panel
{
    public const int RowH = 28;
    private const int BarW = 10;

    private readonly Panel _content = new();
    private readonly List<Control> _rows = new();
    private readonly ListScroll _scroll = new();
    private bool _dragBar;

    public ParamsPanel()
    {
        BackColor = Theme.Bg;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
        _content.BackColor = Theme.Bg;
        Controls.Add(_content);
        MouseWheel += (_, e) => ScrollWheel(e.Delta);
    }

    /// <summary>Удалить все строки (и их контролы) и вернуться к началу.</summary>
    public void Clear()
    {
        foreach (var r in _rows)
        {
            _content.Controls.Remove(r);
            r.Dispose();
        }
        _rows.Clear();
        _scroll.SetCount(0);
        _scroll.ScrollTo(0);
        Relayout();
    }

    /// <summary>Добавить строку в конец. Строка — Panel; её контролы получают колесо мыши через этот список.</summary>
    public void AddRow(Control row)
    {
        row.Location = new Point(0, _rows.Count * RowH);
        row.Size = new Size(Math.Max(1, _content.Width), RowH);
        row.BackColor = Theme.Bg;
        _content.Controls.Add(row);
        _rows.Add(row);
        HookWheel(row);
        Relayout();
    }

    private void HookWheel(Control c)
    {
        c.MouseWheel += (_, e) => ScrollWheel(e.Delta);
        foreach (Control child in c.Controls) HookWheel(child);
    }

    private void ScrollWheel(int delta)
    {
        _scroll.ScrollBy(-delta / 120 * 3);
        ApplyScroll();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        Relayout();
    }

    /// <summary>Пересчитать ширину содержимого, число видимых строк и положение.</summary>
    private void Relayout()
    {
        _content.SetBounds(0, 0, Math.Max(100, Width - BarW - 6), Math.Max(Height, _rows.Count * RowH));
        foreach (var r in _rows) r.Width = _content.Width;
        _scroll.SetCount(_rows.Count);
        _scroll.SetRows(Math.Max(1, Height / RowH));
        ApplyScroll();
    }

    private void ApplyScroll()
    {
        _content.Top = -_scroll.Top * RowH;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (_rows.Count <= _scroll.Rows) return;
        var track = new Rectangle(Width - BarW, 0, BarW, Height);
        using (var b = new SolidBrush(Theme.Bg)) e.Graphics.FillRectangle(b, track);
        var (y, h) = _scroll.Thumb(Height);
        using (var b = new SolidBrush(Theme.Border)) e.Graphics.FillRectangle(b, new Rectangle(track.X + 2, y, track.Width - 4, h));
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left || _rows.Count <= _scroll.Rows) return;
        _dragBar = true;
        Capture = true;
        DragBar(e.Y);
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
        Capture = false;
    }

    private void DragBar(int y)
    {
        var (_, h) = _scroll.Thumb(Height);
        _scroll.ScrollTo(_scroll.TopForTrack(y - h / 2, Height - h));
        ApplyScroll();
    }
}

/// <summary>
/// Кнопка «по умолчанию»: рисуется сама (дуга ~300° и стрелка, 1.5 px, цвет текста; при наведении — янтарь).
/// Выключена (тусклая), когда значение уже равно умолчанию.
/// </summary>
internal sealed class ResetButton : Button
{
    private bool _hover;

    public ResetButton()
    {
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        base.OnMouseEnter(e);
        _hover = true;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hover = false;
        Invalidate();
    }

    protected override void OnEnabledChanged(EventArgs e)
    {
        base.OnEnabledChanged(e);
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using (var bg = new SolidBrush(Theme.Panel)) g.FillRectangle(bg, ClientRectangle);
        using (var border = new Pen(Theme.Border)) g.DrawRectangle(border, 0, 0, Width - 1, Height - 1);

        Color color = !Enabled ? Color.FromArgb(0x4a, 0x4a, 0x4a) : _hover ? Theme.Accent : Theme.Text;
        float cx = Width / 2f, cy = Height / 2f;
        float r = Math.Min(Width, Height) / 2f - 5f;
        const float start = -70f, sweep = 300f;
        using var pen = new Pen(color, 1.5f);
        g.DrawArc(pen, cx - r, cy - r, 2 * r, 2 * r, start, sweep);

        // Стрелка в конце дуги (обход по часовой стрелке на экране): две короткие линии назад от точки
        double end = (start + sweep) * Math.PI / 180.0;
        var tip = new PointF(cx + r * (float)Math.Cos(end), cy + r * (float)Math.Sin(end));
        var back = new PointF((float)Math.Sin(end), -(float)Math.Cos(end)); // минус касательная
        const float len = 4.5f;
        foreach (double deg in new[] { 40.0, -40.0 })
        {
            double a = deg * Math.PI / 180.0;
            float bx = back.X * (float)Math.Cos(a) - back.Y * (float)Math.Sin(a);
            float by = back.X * (float)Math.Sin(a) + back.Y * (float)Math.Cos(a);
            g.DrawLine(pen, tip, new PointF(tip.X + bx * len, tip.Y + by * len));
        }
    }
}

/// <summary>Маленький диалог ввода имени (тёмный). Проверка — колбэком: текст ошибки или null.</summary>
internal sealed class NameDialog : Form
{
    private readonly TextBox _box = new();
    private readonly Label _error = new();
    private readonly Func<string, string?> _validate;

    private NameDialog(string hint, string initial, Func<string, string?> validate)
    {
        _validate = validate;
        Text = "PHOSPHOR — новый пресет";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(380, 150);
        BackColor = Theme.Bg;
        ForeColor = Theme.Text;
        Font = Theme.Mono;

        var label = new Label { Text = hint, AutoSize = true, Location = new Point(16, 16) };
        _box.Location = new Point(16, 44);
        _box.Size = new Size(348, 24);
        _box.Text = initial;
        _error.AutoSize = true;
        _error.Location = new Point(16, 78);
        var ok = new Button { Text = "Сохранить", Location = new Point(150, 108), Size = new Size(100, 30) };
        var cancel = new Button { Text = "Отмена", Location = new Point(256, 108), Size = new Size(108, 30), DialogResult = DialogResult.Cancel };
        ok.Click += (_, _) => TryAccept();

        Controls.Add(label);
        Controls.Add(_box);
        Controls.Add(_error);
        Controls.Add(ok);
        Controls.Add(cancel);
        Theme.Apply(this);
        _error.ForeColor = Theme.Accent;
        AcceptButton = ok;
        CancelButton = cancel;
    }

    /// <summary>Имя (с обрезкой пробелов) или null, если отменено.</summary>
    public static string? Ask(IWin32Window owner, string hint, string initial, Func<string, string?> validate)
    {
        using var d = new NameDialog(hint, initial, validate);
        return d.ShowDialog(owner) == DialogResult.OK ? d._box.Text.Trim() : null;
    }

    private void TryAccept()
    {
        var problem = _validate(_box.Text.Trim());
        if (problem is not null)
        {
            _error.Text = problem;
            return;
        }
        DialogResult = DialogResult.OK;
    }
}
