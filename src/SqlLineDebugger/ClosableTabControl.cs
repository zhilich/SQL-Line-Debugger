namespace SqlLineDebugger;

internal sealed class ClosableTabControl : TabControl
{
    public event Action<TabPage>? CloseRequested;

    public ClosableTabControl()
    {
        DrawMode = TabDrawMode.OwnerDrawFixed;
        Padding = new Point(28, 4);
    }

    internal Rectangle CloseBounds(int index)
    {
        var tab = GetTabRect(index);
        int size = Math.Min(tab.Height - 4, (int)Math.Round(18 * DeviceDpi / 96.0));
        return new Rectangle(tab.Right - size - 4, tab.Top + (tab.Height - size) / 2, size, size);
    }

    protected override void OnDrawItem(DrawItemEventArgs e)
    {
        var bounds = GetTabRect(e.Index);
        var close = CloseBounds(e.Index);
        using var background = new SolidBrush(e.Index == SelectedIndex ? SystemColors.Window : SystemColors.Control);
        e.Graphics.FillRectangle(background, bounds);
        var text = Rectangle.FromLTRB(bounds.Left + 6, bounds.Top, close.Left - 3, bounds.Bottom);
        TextRenderer.DrawText(e.Graphics, TabPages[e.Index].Text, Font, text, SystemColors.ControlText,
            TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        int inset = Math.Max(4, close.Width / 3);
        using var pen = new Pen(SystemColors.ControlText, Math.Max(1, DeviceDpi / 96f));
        e.Graphics.DrawLine(pen, close.Left + inset, close.Top + inset, close.Right - inset, close.Bottom - inset);
        e.Graphics.DrawLine(pen, close.Left + inset, close.Bottom - inset, close.Right - inset, close.Top + inset);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        for (int i = 0; i < TabCount; i++)
        {
            if (e.Button == MouseButtons.Left && CloseBounds(i).Contains(e.Location))
            {
                CloseRequested?.Invoke(TabPages[i]);
                return;
            }
        }
        base.OnMouseDown(e);
    }
}
