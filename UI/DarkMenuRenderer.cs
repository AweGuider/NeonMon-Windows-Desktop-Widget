namespace NeonMon.UI;

internal sealed class DarkMenuRenderer : ToolStripProfessionalRenderer
{
    private static readonly Color Background = Color.FromArgb(7, 16, 21);
    private static readonly Color Foreground = Color.FromArgb(224, 246, 249);
    private static readonly Color Muted = Color.FromArgb(104, 147, 157);
    private static readonly Color Cyan = Color.FromArgb(49, 247, 210);
    private static readonly Color Hover = Color.FromArgb(22, 52, 56);
    private static readonly Color Border = Color.FromArgb(32, 78, 88);
    private static readonly Color Separator = Color.FromArgb(22, 44, 50);

    public DarkMenuRenderer()
        : base(new DarkColors())
    {
        RoundedEdges = false;
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs args)
    {
        args.TextColor = args.Item.Enabled ? Foreground : Muted;
        base.OnRenderItemText(args);
    }

    protected override void OnRenderArrow(ToolStripArrowRenderEventArgs args)
    {
        args.ArrowColor = args.Item?.Enabled == false ? Muted : Foreground;
        base.OnRenderArrow(args);
    }

    protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs args)
    {
        using var pen = new Pen(Cyan, Math.Max(1.5f, args.ImageRectangle.Height / 9f));
        var box = args.ImageRectangle;
        args.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        args.Graphics.DrawLines(pen, new[]
        {
            new PointF(box.Left + box.Width * 0.22f, box.Top + box.Height * 0.52f),
            new PointF(box.Left + box.Width * 0.42f, box.Top + box.Height * 0.72f),
            new PointF(box.Left + box.Width * 0.78f, box.Top + box.Height * 0.3f)
        });
    }

    private sealed class DarkColors : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => Background;
        public override Color ImageMarginGradientBegin => Background;
        public override Color ImageMarginGradientMiddle => Background;
        public override Color ImageMarginGradientEnd => Background;
        public override Color MenuBorder => Border;
        public override Color MenuItemBorder => Hover;
        public override Color MenuItemSelected => Hover;
        public override Color MenuItemSelectedGradientBegin => Hover;
        public override Color MenuItemSelectedGradientEnd => Hover;
        public override Color MenuItemPressedGradientBegin => Hover;
        public override Color MenuItemPressedGradientEnd => Hover;
        public override Color CheckBackground => Background;
        public override Color CheckSelectedBackground => Hover;
        public override Color CheckPressedBackground => Hover;
        public override Color SeparatorDark => Separator;
        public override Color SeparatorLight => Separator;
    }
}
