using System.Windows.Forms;
using Phosphor.App.Settings;

namespace Phosphor.App.Tests;

/// <summary>Список сцен/пресетов: колесо, полоса, клавиши. Раньше колесо не двигало список (Scrollable=false) — эти тесты это ловят.</summary>
public class PickListTests
{
    private static readonly string[] Ids = Enumerable.Range(0, 40).Select(i => $"s{i:00}").ToArray();

    // Высота 220 при строке 22 px = 10 видимых строк
    private static PickList Make(out List<string> cycled)
    {
        var log = new List<string>();
        cycled = log;
        var list = new PickList(_ => Pick.On, id => log.Add(id));
        list.Size = new System.Drawing.Size(300, 220);
        list.Fill(Ids);
        return list;
    }

    [Fact]
    public void Wheel_ScrollsTheList()
    {
        var list = Make(out _);
        Assert.Equal(0, list.TopIndex);
        list.ScrollWheel(-120); // один щелчок вниз = 3 строки
        Assert.Equal(3, list.TopIndex);
        list.ScrollWheel(120);
        Assert.Equal(0, list.TopIndex);
    }

    [Fact]
    public void Wheel_StopsAtLastRow()
    {
        var list = Make(out _);
        list.ScrollWheel(-120 * 100);
        Assert.Equal(30, list.TopIndex); // 40 - 10 видимых
        list.ScrollWheel(120 * 100);
        Assert.Equal(0, list.TopIndex);
    }

    [Fact]
    public void Wheel_DoesNotChangeSelection()
    {
        var list = Make(out _);
        list.KeyNav(Keys.Down);
        list.ScrollWheel(-120 * 5);
        Assert.Equal(0, list.SelectedIndex);
    }

    [Fact]
    public void Drag_BarMovesTheList()
    {
        var list = Make(out _);
        list.DragBar(2 + 216); // низ дорожки (Track: Y=2, высота 216)
        Assert.Equal(30, list.TopIndex);
        list.DragBar(2);
        Assert.Equal(0, list.TopIndex);
        list.DragBar(2 + 216 / 2);
        Assert.InRange(list.TopIndex, 12, 18);
    }

    [Fact]
    public void Keys_DownKeepsSelectionVisible()
    {
        var list = Make(out _);
        for (int i = 0; i < 15; i++) list.KeyNav(Keys.Down);
        Assert.Equal(14, list.SelectedIndex);
        Assert.True(list.SelectedIndex >= list.TopIndex && list.SelectedIndex < list.TopIndex + 10);
        Assert.Equal(5, list.TopIndex);
    }

    [Fact]
    public void Keys_PageDownAndEnd()
    {
        var list = Make(out _);
        list.KeyNav(Keys.Down); // 0
        for (int i = 0; i < 14; i++) list.KeyNav(Keys.Down); // 14
        list.KeyNav(Keys.PageDown); // 24
        Assert.Equal(24, list.SelectedIndex);
        Assert.True(list.SelectedIndex >= list.TopIndex && list.SelectedIndex < list.TopIndex + 10);
        list.KeyNav(Keys.End);
        Assert.Equal(39, list.SelectedIndex);
        Assert.Equal(30, list.TopIndex);
        list.KeyNav(Keys.Home);
        Assert.Equal(0, list.SelectedIndex);
        Assert.Equal(0, list.TopIndex);
    }

    [Fact]
    public void Keys_UpFromNothingSelectsFirst()
    {
        var list = Make(out _);
        list.KeyNav(Keys.Up);
        Assert.Equal(0, list.SelectedIndex);
    }

    [Fact]
    public void SelectionChanged_RaisedOnlyOnChange()
    {
        var list = Make(out _);
        int raised = 0;
        list.SelectionChanged += (_, _) => raised++;
        list.KeyNav(Keys.Down);  // -1 -> 0: событие
        list.KeyNav(Keys.Down);  // 0 -> 1: событие
        list.KeyNav(Keys.Up);    // 1 -> 0: событие
        list.KeyNav(Keys.Up);    // у верхней границы: без изменения
        Assert.Equal(3, raised);
    }

    [Fact]
    public void Space_CyclesSelectedRow()
    {
        var list = Make(out var cycled);
        list.KeyNav(Keys.Down);
        list.KeyNav(Keys.Space);
        Assert.Equal(new[] { "s00" }, cycled);
    }

    [Fact]
    public void ShortList_HasNoScrolling()
    {
        var list = new PickList(_ => Pick.On, _ => { });
        list.Size = new System.Drawing.Size(300, 220);
        list.Fill(new[] { "a", "b", "c" });
        list.ScrollWheel(-120 * 5);
        Assert.Equal(0, list.TopIndex);
    }
}

public class ListScrollTests
{
    [Fact]
    public void Thumb_TopAndBottom()
    {
        var s = new ListScroll();
        s.SetCount(40);
        s.SetRows(10);
        Assert.Equal((0, 54), s.Thumb(216));
        s.ScrollTo(30);
        Assert.Equal((216 - 54, 54), s.Thumb(216));
    }

    [Fact]
    public void TopForTrack_MapsEnds()
    {
        var s = new ListScroll();
        s.SetCount(40);
        s.SetRows(10);
        Assert.Equal(0, s.TopForTrack(0, 162));
        Assert.Equal(30, s.TopForTrack(162, 162));
        Assert.Equal(30, s.TopForTrack(500, 162));
    }

    [Fact]
    public void ShortList_ThumbFillsTrack()
    {
        var s = new ListScroll();
        s.SetCount(3);
        s.SetRows(10);
        Assert.Equal((0, 216), s.Thumb(216));
        Assert.Equal(0, s.TopForTrack(100, 216));
    }

    [Fact]
    public void Select_EnsuresVisible()
    {
        var s = new ListScroll();
        s.SetCount(40);
        s.SetRows(10);
        s.Select(25);
        Assert.Equal(16, s.Top);
        s.Select(2);
        Assert.Equal(2, s.Top);
    }
}
