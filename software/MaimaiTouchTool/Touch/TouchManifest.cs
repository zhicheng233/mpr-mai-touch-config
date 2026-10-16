namespace MaimaiTouchTool.Touch;

public sealed record TouchAsset(string Label, string BasePath, string PressedPath);
public sealed record TouchBoardPosition(string Label, double X, double Y, double Width, double Height, char Layer);

public static class TouchManifest
{
    private static readonly string[] Labels = ["A1", "A2", "A3", "A4", "A5", "A6", "A7", "A8", "B1", "B2", "B3", "B4", "B5", "B6", "B7", "B8", "C1", "C2", "D1", "D2", "D3", "D4", "D5", "D6", "D7", "D8", "E1", "E2", "E3", "E4", "E5", "E6", "E7", "E8"];
    public static IReadOnlyList<TouchAsset> Assets { get; } =
    [
        new("A1", "avares://MaimaiTouchTool/Assets/Touch/A1.svg", "avares://MaimaiTouchTool/Assets/Touch/A1_touch.svg"),
        new("A2", "avares://MaimaiTouchTool/Assets/Touch/A2.svg", "avares://MaimaiTouchTool/Assets/Touch/A2_touch.svg"),
        new("A3", "avares://MaimaiTouchTool/Assets/Touch/A3.svg", "avares://MaimaiTouchTool/Assets/Touch/A3_touch.svg"),
        new("A4", "avares://MaimaiTouchTool/Assets/Touch/A4.svg", "avares://MaimaiTouchTool/Assets/Touch/A4_touch.svg"),
        new("A5", "avares://MaimaiTouchTool/Assets/Touch/A5.svg", "avares://MaimaiTouchTool/Assets/Touch/A5_touch.svg"),
        new("A6", "avares://MaimaiTouchTool/Assets/Touch/A6.svg", "avares://MaimaiTouchTool/Assets/Touch/A6_touch.svg"),
        new("A7", "avares://MaimaiTouchTool/Assets/Touch/A7.svg", "avares://MaimaiTouchTool/Assets/Touch/A7_touch.svg"),
        new("A8", "avares://MaimaiTouchTool/Assets/Touch/A8.svg", "avares://MaimaiTouchTool/Assets/Touch/A8_touch.svg"),
        new("B1", "avares://MaimaiTouchTool/Assets/Touch/B1.svg", "avares://MaimaiTouchTool/Assets/Touch/B1_touch.svg"),
        new("B2", "avares://MaimaiTouchTool/Assets/Touch/B2.svg", "avares://MaimaiTouchTool/Assets/Touch/B2_touch.svg"),
        new("B3", "avares://MaimaiTouchTool/Assets/Touch/B3.svg", "avares://MaimaiTouchTool/Assets/Touch/B3_touch.svg"),
        new("B4", "avares://MaimaiTouchTool/Assets/Touch/B4.svg", "avares://MaimaiTouchTool/Assets/Touch/B4_touch.svg"),
        new("B5", "avares://MaimaiTouchTool/Assets/Touch/B5.svg", "avares://MaimaiTouchTool/Assets/Touch/B5_touch.svg"),
        new("B6", "avares://MaimaiTouchTool/Assets/Touch/B6.svg", "avares://MaimaiTouchTool/Assets/Touch/B6_touch.svg"),
        new("B7", "avares://MaimaiTouchTool/Assets/Touch/B7.svg", "avares://MaimaiTouchTool/Assets/Touch/B7_touch.svg"),
        new("B8", "avares://MaimaiTouchTool/Assets/Touch/B8.svg", "avares://MaimaiTouchTool/Assets/Touch/B8_touch.svg"),
        new("C1", "avares://MaimaiTouchTool/Assets/Touch/C1.svg", "avares://MaimaiTouchTool/Assets/Touch/C1_touch.svg"),
        new("C2", "avares://MaimaiTouchTool/Assets/Touch/C2.svg", "avares://MaimaiTouchTool/Assets/Touch/C2_touch.svg"),
        new("D1", "avares://MaimaiTouchTool/Assets/Touch/D1.svg", "avares://MaimaiTouchTool/Assets/Touch/D1_touch.svg"),
        new("D2", "avares://MaimaiTouchTool/Assets/Touch/D2.svg", "avares://MaimaiTouchTool/Assets/Touch/D2_touch.svg"),
        new("D3", "avares://MaimaiTouchTool/Assets/Touch/D3.svg", "avares://MaimaiTouchTool/Assets/Touch/D3_touch.svg"),
        new("D4", "avares://MaimaiTouchTool/Assets/Touch/D4.svg", "avares://MaimaiTouchTool/Assets/Touch/D4_touch.svg"),
        new("D5", "avares://MaimaiTouchTool/Assets/Touch/D5.svg", "avares://MaimaiTouchTool/Assets/Touch/D5_touch.svg"),
        new("D6", "avares://MaimaiTouchTool/Assets/Touch/D6.svg", "avares://MaimaiTouchTool/Assets/Touch/D6_touch.svg"),
        new("D7", "avares://MaimaiTouchTool/Assets/Touch/D7.svg", "avares://MaimaiTouchTool/Assets/Touch/D7_touch.svg"),
        new("D8", "avares://MaimaiTouchTool/Assets/Touch/D8.svg", "avares://MaimaiTouchTool/Assets/Touch/D8_touch.svg"),
        new("E1", "avares://MaimaiTouchTool/Assets/Touch/E1.svg", "avares://MaimaiTouchTool/Assets/Touch/E1_touch.svg"),
        new("E2", "avares://MaimaiTouchTool/Assets/Touch/E2.svg", "avares://MaimaiTouchTool/Assets/Touch/E2_touch.svg"),
        new("E3", "avares://MaimaiTouchTool/Assets/Touch/E3.svg", "avares://MaimaiTouchTool/Assets/Touch/E3_touch.svg"),
        new("E4", "avares://MaimaiTouchTool/Assets/Touch/E4.svg", "avares://MaimaiTouchTool/Assets/Touch/E4_touch.svg"),
        new("E5", "avares://MaimaiTouchTool/Assets/Touch/E5.svg", "avares://MaimaiTouchTool/Assets/Touch/E5_touch.svg"),
        new("E6", "avares://MaimaiTouchTool/Assets/Touch/E6.svg", "avares://MaimaiTouchTool/Assets/Touch/E6_touch.svg"),
        new("E7", "avares://MaimaiTouchTool/Assets/Touch/E7.svg", "avares://MaimaiTouchTool/Assets/Touch/E7_touch.svg"),
        new("E8", "avares://MaimaiTouchTool/Assets/Touch/E8.svg", "avares://MaimaiTouchTool/Assets/Touch/E8_touch.svg")
    ];
    public const double BoardWidth = 960;
    public const double BoardHeight = 912;
    public const double BoardCenterX = 480;
    public const double BoardCenterY = 456;

    // ===== Live Test 布局调参（改这里的数字即可，改完重新编译启动）=====
    // Scale = 该环键的缩放（越大键越大）；Radius = 该环中心离圆心的距离（越大越靠外）。
    // A/E 的角度从 0°(正上方) 顺时针每 45° 一个；D/B 整体偏移 22.5°，插在 A/E 之间。
    private const double ScaleA = 0.70, RadiusA = 350; // 外圈 A：大扇形
    private const double ScaleD = 0.55, RadiusD = 260; // D 环
    private const double ScaleE = 0.45, RadiusE = 185; // E 环
    private const double ScaleB = 0.42, RadiusB = 115; // 内圈 B
    private const double ScaleC = 0.50;                // 中央 C

    // ===== 可选：每键坐标覆盖（由 tools/preview-board.html 的「生成 C# 片段」导出）=====
    // 把导出结果里的 new("..", x, y, w, h, '..'), 行粘贴到下面方括号里即可。
    // 非空时：命中这些 label 的键用它自己的坐标，其余键仍按上面的参数计算。
    // 注意：必须声明在 Layout 之前，否则静态初始化顺序会让 CreateLayout 读到 null。
    private static readonly TouchBoardPosition[] KeyOverrides =
    [
        new("A1", 521.02, 80.27, 187.36, 177.88, 'A'),
        new("A2", 683.85, 229.63, 177.88, 187.36, 'A'),
        new("A3", 680.85, 493.02, 177.88, 187.36, 'A'),
        new("A4", 520.02, 652.85, 187.36, 177.88, 'A'),
        new("A5", 256.63, 653.85, 187.36, 177.88, 'A'),
        new("A6", 102.27, 492.02, 177.88, 187.36, 'A'),
        new("A7", 104.27, 231.63, 177.88, 187.36, 'A'),
        new("A8", 257.63, 77.27, 187.36, 177.88, 'A'),
        new("B1", 487.88, 259.79, 103.94, 98.63, 'B'),
        new("B2", 577.58, 348.19, 98.63, 103.94, 'B'),
        new("B3", 576.58, 462.88, 98.63, 103.94, 'B'),
        new("B4", 483, 553, 103.94, 98.63, 'B'),
        new("B5", 371.19, 553.58, 103.94, 98.63, 'B'),
        new("B6", 284.79, 460.88, 98.63, 103.94, 'B'),
        new("B7", 284.79, 346.19, 98.63, 103.94, 'B'),
        new("B8", 372.19, 259.79, 103.94, 98.63, 'B'),
        new("C1", 486, 383.19, 66.38, 145.63, 'C'),
        new("C2", 410, 383, 66.38, 145.63, 'C'),
        new("D1", 433.99, 76.78, 94.03, 126.44, 'D'),
        new("D2", 637.81, 158.6, 142, 141.18, 'D'),
        new("D3", 737, 409, 126.44, 94.03, 'D'),
        new("D4", 626.87, 599.87, 155.88, 155.88, 'D'),
        new("D5", 433.99, 706.78, 94.03, 126.44, 'D'),
        new("D6", 181.19, 612.22, 142, 141.18, 'D'),
        new("D7", 102.78, 407.99, 126.44, 94.03, 'D'),
        new("D8", 180.25, 154.25, 155.88, 155.88, 'D'),
        new("E1", 429.79, 186.79, 100.43, 100.43, 'E'),
        new("E2", 599.35, 265.63, 71.02, 71.02, 'E'),
        new("E3", 648.79, 405.79, 100.43, 100.43, 'E'),
        new("E4", 599.35, 575.35, 71.02, 71.02, 'E'),
        new("E5", 429.79, 624.79, 100.43, 100.43, 'E'),
        new("E6", 289.63, 575.35, 71.02, 71.02, 'E'),
        new("E7", 210.79, 405.79, 100.43, 100.43, 'E'),
        new("E8", 290.63, 265.63, 71.02, 71.02, 'E'),
    ];

    public static IReadOnlyList<TouchBoardPosition> Layout { get; } = CreateLayout();

    private static IReadOnlyList<TouchBoardPosition> CreateLayout()
    {
        var layout = new List<TouchBoardPosition>(Labels.Length);
        AddRing(layout, 'A', RadiusA, 22.5, 45, ScaleA);
        AddRing(layout, 'B', RadiusB, 22.5, 45, ScaleB);
        layout.Add(Position("C1", BoardCenterX + 170.2 * ScaleC / 2, BoardCenterY, 170.2 * ScaleC, 373.4 * ScaleC, 'C'));
        layout.Add(Position("C2", BoardCenterX - 170.2 * ScaleC / 2, BoardCenterY, 170.2 * ScaleC, 373.4 * ScaleC, 'C'));
        AddRing(layout, 'D', RadiusD, 22.5, 45, ScaleD);
        AddRing(layout, 'E', RadiusE, 0, 45, ScaleE);
        if (KeyOverrides is not null)
        {
            foreach (var ov in KeyOverrides)
            {
                var index = layout.FindIndex(p => p.Label == ov.Label);
                if (index >= 0) layout[index] = ov; else layout.Add(ov);
            }
        }
        return layout;
    }

    private static void AddRing(ICollection<TouchBoardPosition> layout, char layer, double radius, double startAngle, double step, double scale)
    {
        for (var index = 0; index < 8; index++)
        {
            var label = $"{layer}{index + 1}";
            var angle = (startAngle + step * index) * Math.PI / 180;
            var centerX = BoardCenterX + radius * Math.Sin(angle);
            var centerY = BoardCenterY - radius * Math.Cos(angle);
            var (width, height) = NaturalSize(label);
            layout.Add(Position(label, centerX, centerY, width * scale, height * scale, layer));
        }
    }

    private static (double Width, double Height) NaturalSize(string label) => label switch
    {
        "A1" or "A4" or "A5" or "A8" => (480.4, 456.1),
        "A2" or "A3" or "A6" or "A7" => (456.1, 480.4),
        "B1" or "B4" or "B5" or "B8" => (266.5, 252.9),
        "B2" or "B3" or "B6" or "B7" => (252.9, 266.5),
        "C1" or "C2" => (170.2, 373.4),
        "D1" or "D5" => (241.1, 324.2),
        "D3" or "D7" => (324.2, 241.1),
        "D2" or "D6" => (364.1, 362.0),
        "D4" or "D8" => (399.7, 399.7),
        "E2" or "E4" or "E6" or "E8" => (182.1, 182.1),
        "E1" or "E3" or "E5" or "E7" => (257.5, 257.5),
        _ => throw new ArgumentOutOfRangeException(nameof(label), label, "Unknown touch key label.")
    };

    private static TouchBoardPosition Position(string label, double centerX, double centerY, double width, double height, char layer) =>
        new(label, centerX - width / 2, centerY - height / 2, width, height, layer);

    public static void Validate()
    {
        if (Assets.Count != 34 || Layout.Count != 34 || Assets.Select(x => x.Label).SequenceEqual(Labels) == false || Layout.Select(x => x.Label).SequenceEqual(Labels) == false || Assets.Any(x => string.IsNullOrWhiteSpace(x.BasePath) || string.IsNullOrWhiteSpace(x.PressedPath) || !x.BasePath.EndsWith($"/{x.Label}.svg", StringComparison.Ordinal) || !x.PressedPath.EndsWith($"/{x.Label}_touch.svg", StringComparison.Ordinal))) throw new InvalidOperationException("The fixed 34-key touch manifest is invalid.");
        if (Layout.Any(x => x.Width <= 0 || x.Height <= 0 || double.IsNaN(x.X) || double.IsNaN(x.Y) || double.IsInfinity(x.X) || double.IsInfinity(x.Y))) throw new InvalidOperationException("The touch board layout contains invalid geometry.");
    }
    public static TouchAsset Get(string label) => Assets.First(x => x.Label == label);
    public static Avalonia.Svg.Skia.SvgSource Load(string path) => Avalonia.Svg.Skia.SvgSource.Load(path, baseUri: null)!;
}
