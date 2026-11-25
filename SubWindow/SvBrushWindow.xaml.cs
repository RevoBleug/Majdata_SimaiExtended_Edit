using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using Newtonsoft.Json;

namespace MajdataEdit.SvBrush
{
    /// <summary>主窗口交给曲线刷窗口的「当前选区」快照。</summary>
    public sealed class SvSelectionSnapshot
    {
        public string Text = string.Empty;
        public double Seconds;
        public long StartOffset;
        public long EndOffset;
        public string Message = string.Empty;
        public TextPointer? Start;
        public TextPointer? End;

        /// <summary>每一格的起始时刻（秒）。拿不到就留空，Still 会退化成常数。</summary>
        public double[] CellTimes = Array.Empty<double>();

        public bool IsValid => Message.Length == 0 && Text.Length > 0 && Start != null && End != null;
    }

    /// <summary>一条存下来的形状 + 参数。</summary>
    public sealed class SvBrushPreset
    {
        public string Name = string.Empty;
        public int Shape;
        public SvShapeParams Params = new();
    }

    /// <summary>
    /// 曲线刷窗口：把选中的一段谱面按某种形状铺满 SV。
    /// 独立窗口（非模态），会跟着编辑器的选区自动更新。
    /// </summary>
    public partial class SvBrushWindow : Window
    {
        private const string PresetFile = "SvBrushPresets.json";

        private static readonly string[] ShapeNames =
        {
            "Linear 线性",
            "Exponential 指数",
            "Bezier 贝塞尔",
            "Hermite 缓入缓出",
            "Sinusoidal 正弦波",
            "Circular 圆弧",
            "Chinchilla 缓动组",
            "Custom 手输序列",
            "Random 随机",
            "Stutter 顿挫",
            "Teleport 瞬移顿挫",
            "Still 等距"
        };

        private readonly Func<SvSelectionSnapshot> _readSelection;
        private readonly Action<string> _apply;
        private readonly DispatcherTimer _reloadTimer;
        private readonly List<SvShapeType> _listedShapes = new();
        private readonly List<SvBrushPreset> _presets = new();

        private SvShapeParams _p = new();
        private string _selection = string.Empty;
        private double _cellSeconds;
        private List<string> _cells = new();
        private double[] _cellTimes = Array.Empty<double>();
        private SvBrushBuildResult? _lastResult;
        private bool _suppress;
        private bool _hasLiveTarget;
        private long _loadedStart = -1;
        private long _loadedEnd = -2;

        public SvBrushWindow(Func<SvSelectionSnapshot> readSelection, Action<string> apply)
        {
            InitializeComponent();

            _readSelection = readSelection;
            _apply = apply;

            _reloadTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(220) };
            _reloadTimer.Tick += (_, _) =>
            {
                _reloadTimer.Stop();
                ReloadSelection();
            };

            FoldCheck.IsChecked = true;

            LoadPresets();
            SetTab(0);
            ReloadSelection();
        }

        /// <summary>编辑器选区变了（由主窗口调，内部会自己防抖）。</summary>
        public void NotifyEditorSelectionChanged()
        {
            _reloadTimer.Stop();
            _reloadTimer.Start();
        }

        /// <summary>重新从编辑器读一次选区。</summary>
        public void ReloadSelection()
        {
            SvSelectionSnapshot snap;
            try
            {
                snap = _readSelection();
            }
            catch (Exception ex)
            {
                SummaryText.Text = "读取选区失败：" + ex.Message;
                return;
            }

            if (!snap.IsValid)
            {
                _hasLiveTarget = false;
                ApplyButton.IsEnabled = false;

                // 已经有内容时不要清空，否则用户点一下编辑器参数就全没了
                if (_lastResult == null || _cells.Count == 0)
                {
                    _selection = string.Empty;
                    _cells = new List<string>();
                    _lastResult = null;
                    SummaryText.Text = string.IsNullOrEmpty(snap.Message)
                        ? "未选中内容。请回到编辑器选中一段谱面（至少一整格）后，这里会自动载入。"
                        : snap.Message;
                }

                return;
            }

            _hasLiveTarget = true;
            ApplyButton.IsEnabled = true;

            // 还是同一个区域就不重来，避免把正在调的参数冲掉
            if (snap.StartOffset == _loadedStart && snap.EndOffset == _loadedEnd) return;

            _loadedStart = snap.StartOffset;
            _loadedEnd = snap.EndOffset;

            _selection = snap.Text;
            _cells = SvCellText.SplitCells(_selection);
            _cellTimes = snap.CellTimes;

            var cells = Math.Max(1, _cells.Count);
            _cellSeconds = snap.Seconds > 0 ? snap.Seconds / cells : 0;

            LoadInitialValues();

            FoldCheck.IsEnabled = _cellSeconds > 0;
            if (_cellSeconds <= 0 && FoldCheck.IsChecked == true) FoldCheck.IsChecked = false;

            BuildParamPanel();
            Recalc();
        }

        /// <summary>把选区里已有的 SV 反解成起止值，当作初始参数。</summary>
        private void LoadInitialValues()
        {
            var hasAny = false;
            foreach (var c in _cells)
                if (SvCellText.ReadSvValue(c).HasValue)
                {
                    hasAny = true;
                    break;
                }

            if (!hasAny) return;

            var values = SvCellText.ReadExisting(_cells);
            if (values.Length == 0) return;

            _p.StartSV = Math.Round(values[0], 4);
            _p.EndSV = Math.Round(values[^1], 4);

            var sum = 0.0;
            foreach (var v in values) sum += v;
            _p.AvgSV = Math.Round(sum / values.Length, 4);
        }

        private SvShapeType CurrentType
        {
            get
            {
                var i = ShapeList.SelectedIndex;
                if (i >= 0 && i < _listedShapes.Count) return _listedShapes[i];
                return SvShapeType.Linear;
            }
        }

        // ------------------------------------------------------------------ 标签页

        private void Tab_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button button) return;
            if (button.Tag is not string tag) return;
            if (!int.TryParse(tag, out var tab)) return;
            SetTab(tab);
        }

        private void SetTab(int tab)
        {
            var buttons = new[] { TabStandard, TabSpecial, TabPreset };
            for (var i = 0; i < buttons.Length; i++)
            {
                var selected = i == tab;
                buttons[i].Background = selected
                    ? (Brush)FindResource("ButtonsBackground")
                    : Brushes.Transparent;
                buttons[i].Foreground = selected
                    ? (Brush)FindResource("ButtonForeground")
                    : new SolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0x9A));
            }

            var presetTab = tab == 2;

            ShapeList.Visibility = presetTab ? Visibility.Collapsed : Visibility.Visible;
            ParamScroll.Visibility = presetTab ? Visibility.Collapsed : Visibility.Visible;
            PresetList.Visibility = presetTab ? Visibility.Visible : Visibility.Collapsed;
            PresetPanel.Visibility = presetTab ? Visibility.Visible : Visibility.Collapsed;

            if (presetTab)
            {
                RefreshPresetList();
                return;
            }

            _suppress = true;
            ShapeList.Items.Clear();
            _listedShapes.Clear();

            // 0~8 标准形状，9 以后是特殊类型
            var from = tab == 0 ? 0 : 9;
            var to = tab == 0 ? 8 : ShapeNames.Length - 1;
            for (var i = from; i <= to; i++)
            {
                _listedShapes.Add((SvShapeType)i);
                ShapeList.Items.Add(ShapeNames[i]);
            }

            ShapeList.SelectedIndex = 0;
            _suppress = false;

            BuildParamPanel();
            Recalc();
        }

        private void SelectShape(SvShapeType type)
        {
            var tab = (int)type >= 9 ? 1 : 0;
            SetTab(tab);
            var index = _listedShapes.IndexOf(type);
            if (index >= 0) ShapeList.SelectedIndex = index;
        }

        // ------------------------------------------------------------------ 参数面板

        private sealed class Row
        {
            public string Label = string.Empty;
            public string Kind = "num";
            public string[] Choices = Array.Empty<string>();
            public Func<string> Get = () => string.Empty;
            public Action<string> Set = _ => { };
            public string Tip = string.Empty;
        }

        private Row Num(string label, Func<double> get, Action<double> set, string tip = "")
        {
            return new Row
            {
                Label = label,
                Kind = "num",
                Get = () => get().ToString("0.####", CultureInfo.InvariantCulture),
                Set = s =>
                {
                    if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) set(v);
                },
                Tip = tip
            };
        }

        private Row Choice(string label, string[] choices, Func<int> get, Action<int> set, string tip = "")
        {
            return new Row
            {
                Label = label,
                Kind = "choice",
                Choices = choices,
                Get = () => get().ToString(CultureInfo.InvariantCulture),
                Set = s =>
                {
                    if (int.TryParse(s, out var i)) set(i);
                },
                Tip = tip
            };
        }

        private Row Text(string label, Func<string> get, Action<string> set, string tip = "")
        {
            return new Row
            {
                Label = label,
                Kind = "text",
                Get = get,
                Set = set,
                Tip = tip
            };
        }

        private void BuildParamPanel()
        {
            _suppress = true;
            ParamPanel.Children.Clear();

            // 一句话说明放在最上面
            var hint = HintFor(CurrentType);
            if (!string.IsNullOrEmpty(hint))
                ParamPanel.Children.Add(new TextBlock
                {
                    Text = hint,
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 11.5,
                    LineHeight = 17,
                    Margin = new Thickness(0, 0, 0, 12),
                    Foreground = new SolidColorBrush(Color.FromRgb(0x8A, 0x8A, 0x8A))
                });

            foreach (var row in RowsFor(CurrentType))
            {
                var line = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Margin = new Thickness(0, 0, 0, 7)
                };

                line.Children.Add(new TextBlock
                {
                    Text = row.Label,
                    Width = 96,
                    FontSize = 12,
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = new SolidColorBrush(Color.FromRgb(0xB9, 0xB9, 0xB9))
                });

                if (row.Kind == "choice")
                {
                    var combo = new ComboBox
                    {
                        Width = 148,
                        Height = 23,
                        FontSize = 12,
                        Background = (Brush)FindResource("ButtonsBackground"),
                        Foreground = (Brush)FindResource("ButtonForeground"),
                        Template = (ControlTemplate)FindResource("DarkComboBox")
                    };
                    foreach (var c in row.Choices) combo.Items.Add(c);
                    combo.SelectedIndex = int.TryParse(row.Get(), out var idx)
                        ? Math.Clamp(idx, 0, row.Choices.Length - 1)
                        : 0;
                    combo.SelectionChanged += (_, _) =>
                    {
                        if (_suppress) return;
                        row.Set(combo.SelectedIndex.ToString(CultureInfo.InvariantCulture));
                        Recalc();
                    };
                    line.Children.Add(combo);
                }
                else
                {
                    var box = new TextBox
                    {
                        Width = row.Kind == "text" ? 240 : 110,
                        Height = 23,
                        FontSize = 12,
                        VerticalContentAlignment = VerticalAlignment.Center,
                        Background = (Brush)FindResource("ButtonsBackground"),
                        Foreground = (Brush)FindResource("ButtonForeground"),
                        BorderBrush = new SolidColorBrush(Color.FromRgb(0x3C, 0x3C, 0x3C)),
                        Text = row.Get()
                    };
                    box.TextChanged += (_, _) =>
                    {
                        if (_suppress) return;
                        row.Set(box.Text);
                        Recalc();
                    };
                    line.Children.Add(box);
                }

                if (!string.IsNullOrEmpty(row.Tip)) line.ToolTip = row.Tip;
                ParamPanel.Children.Add(line);
            }

            _suppress = false;
        }

        private IEnumerable<Row> RowsFor(SvShapeType type)
        {
            switch (type)
            {
                case SvShapeType.Linear:
                    yield return Num("起始 SV", () => _p.StartSV, v => _p.StartSV = v);
                    yield return Num("终止 SV", () => _p.EndSV, v => _p.EndSV = v);
                    break;

                case SvShapeType.Exponential:
                    yield return Choice("方向", new[] { "加速", "减速" }, () => _p.SpeedUp ? 0 : 1, i => _p.SpeedUp = i == 0);
                    yield return Num("强度", () => _p.Intensity, v => _p.Intensity = v, "越大转折越陡，常用 10~80");
                    yield return Num("目标平均 SV", () => _p.AvgSV, v => _p.AvgSV = v);
                    yield return Num("垂直平移", () => _p.VerticalShift, v => _p.VerticalShift = v, "整条曲线一起加减");
                    break;

                case SvShapeType.Bezier:
                    yield return Num("x1", () => _p.BezierX1, v => _p.BezierX1 = v);
                    yield return Num("y1", () => _p.BezierY1, v => _p.BezierY1 = v);
                    yield return Num("x2", () => _p.BezierX2, v => _p.BezierX2 = v);
                    yield return Num("y2", () => _p.BezierY2, v => _p.BezierY2 = v);
                    yield return Num("目标平均 SV", () => _p.AvgSV, v => _p.AvgSV = v);
                    yield return Num("垂直平移", () => _p.VerticalShift, v => _p.VerticalShift = v);
                    break;

                case SvShapeType.Hermite:
                    yield return Num("起始 SV", () => _p.StartSV, v => _p.StartSV = v);
                    yield return Num("终止 SV", () => _p.EndSV, v => _p.EndSV = v);
                    yield return Num("锐度", () => _p.Sharpness, v => _p.Sharpness = v, "50 为对称缓动，越大越接近「瞬间切换」");
                    break;

                case SvShapeType.Sinusoidal:
                    yield return Num("起始振幅", () => _p.StartSV, v => _p.StartSV = v);
                    yield return Num("终止振幅", () => _p.EndSV, v => _p.EndSV = v);
                    yield return Num("周期数", () => _p.Periods, v => _p.Periods = v, "整段里来回几次");
                    yield return Num("相位", () => _p.PeriodsShift, v => _p.PeriodsShift = v, "0.25 = 从波峰起步");
                    yield return Num("锐度", () => _p.Sharpness, v => _p.Sharpness = v, "50 为纯正弦，越大波峰越尖");
                    yield return Num("垂直平移", () => _p.VerticalShift, v => _p.VerticalShift = v);
                    break;

                case SvShapeType.Circular:
                    yield return Choice("方向", new[] { "加速", "减速" }, () => _p.SpeedUp ? 0 : 1, i => _p.SpeedUp = i == 0);
                    yield return Num("圆弧占比 %", () => _p.ArcPercent, v => _p.ArcPercent = v, "越小越接近直线，100 为整半圆");
                    yield return Num("目标平均 SV", () => _p.AvgSV, v => _p.AvgSV = v);
                    yield return Num("垂直平移", () => _p.VerticalShift, v => _p.VerticalShift = v);
                    break;

                case SvShapeType.Chinchilla:
                    yield return Choice("方向", new[] { "加速", "减速" }, () => _p.SpeedUp ? 0 : 1, i => _p.SpeedUp = i == 0);
                    yield return Choice("缓动类型",
                        new[] { "指数", "多项式", "圆弧", "正弦幂", "反正弦幂", "倒数幂", "Peter Stock" },
                        () => _p.ChinchillaType, i => _p.ChinchillaType = i);
                    yield return Num("强度", () => _p.ChinchillaIntensity, v => _p.ChinchillaIntensity = v, "常用 0.2~0.9");
                    yield return Num("目标平均 SV", () => _p.AvgSV, v => _p.AvgSV = v);
                    yield return Num("垂直平移", () => _p.VerticalShift, v => _p.VerticalShift = v);
                    break;

                case SvShapeType.Custom:
                    yield return Text("值列表", () => _p.CustomValues, s => _p.CustomValues = s, "逗号分隔，会自动拉伸到选区格数");
                    break;

                case SvShapeType.Random:
                    yield return Choice("分布", new[] { "均匀", "正态" }, () => _p.RandomType, i => _p.RandomType = i);
                    yield return Num("幅度", () => _p.RandomScale, v => _p.RandomScale = v);
                    yield return Num("种子", () => _p.RandomSeed, v => _p.RandomSeed = (int)v, "同一个种子结果可复现");
                    yield return Num("目标平均 SV", () => _p.AvgSV, v => _p.AvgSV = v);
                    break;

                case SvShapeType.Stutter:
                    yield return Num("顿挫 SV", () => _p.StutterValue, v => _p.StutterValue = v, "卡住/猛冲那一下的值");
                    yield return Num("占比 %", () => _p.StutterPercent, v => _p.StutterPercent = v);
                    yield return Num("每区间次数", () => _p.StuttersPerSection, v => _p.StuttersPerSection = Math.Max(1, (int)v));
                    yield return Num("目标平均 SV", () => _p.AvgSV, v => _p.AvgSV = v);
                    yield return Choice("顿挫落在末段", new[] { "否", "是" }, () => _p.ControlLastSV ? 1 : 0, i => _p.ControlLastSV = i == 1);
                    break;

                case SvShapeType.TeleportStutter:
                    yield return Num("主 SV", () => _p.TeleportMainSV, v => _p.TeleportMainSV = v, "瞬移之后匀速滑行的速度");
                    yield return Num("瞬移距离", () => _p.TeleportDistance, v => _p.TeleportDistance = v, "ScrollDist 单位，约 2 个单位 ≈ 一屏");
                    yield return Choice("用百分比代替距离", new[] { "否", "是" }, () => _p.TeleportUsePercent ? 1 : 0, i => _p.TeleportUsePercent = i == 1);
                    yield return Num("瞬移占比 %", () => _p.TeleportPercent, v => _p.TeleportPercent = v);
                    yield return Num("目标平均 SV", () => _p.AvgSV, v => _p.AvgSV = v);
                    break;

                case SvShapeType.Still:
                    yield return Num("间距", () => _p.StillSpacing, v => _p.StillSpacing = v,
                        "ScrollDist 单位，约 2 个单位 = 一屏。自动模式关闭时才用这个值。");
                    yield return Choice("自动跟随进入速度", new[] { "是", "否" }, () => _p.StillAuto ? 0 : 1, i => _p.StillAuto = i == 0);
                    yield return Num("进入 SV", () => _p.AvgSV, v => _p.AvgSV = v, "选区开始时的速度，用来让接缝连续");
                    break;
            }
        }

        private static string HintFor(SvShapeType type)
        {
            switch (type)
            {
                case SvShapeType.Linear:
                    return "速度从起始值均匀变到终止值，最简单的一种。";
                case SvShapeType.Exponential:
                    return "速度按指数曲线变化，起步慢、后面突然窜出去（或反过来）。目标平均 SV 会锁定整段的总位移。";
                case SvShapeType.Bezier:
                    return "用一条贝塞尔曲线定义音符的行走轨迹，再求导成速度。所以调的是音符往哪走，不是速度多快。";
                case SvShapeType.Hermite:
                    return "在起止值之间走一条 S 形曲线，两头慢中间快，没有回弹。";
                case SvShapeType.Sinusoidal:
                    return "速度在正负之间摆动，音符会来回走。周期数是往返几次，相位是起步位置。";
                case SvShapeType.Circular:
                    return "用半圆的斜率做加减速，两头变化慢、中间快。圆弧占比越小越接近直线。";
                case SvShapeType.Chinchilla:
                    return "七种缓动函数共用一套参数，手感最好的一组。强度受 0~1 百分比曲线约束，常用 0.5 左右。";
                case SvShapeType.Custom:
                    return "手输一串值，工具把它线性拉伸到选区格数。例：2, 1, 2 就是一次往返。";
                case SvShapeType.Random:
                    return "在目标平均值附近随机抖动。种子相同结果就相同，方便复现。";
                case SvShapeType.Stutter:
                    return "按音符之间的区间工作：每段里先用一个值再用另一个值，两者按时间加权后等于目标平均 SV，"
                           + "所以整段位移不变、音符位置不跑偏，只是走得一顿一顿。";
                case SvShapeType.TeleportStutter:
                    return "按音符之间的区间工作：每段第一格给一个很大的 SV 把音符一次性推出去，剩下匀速走完。"
                           + "段末位移仍然守恒，看起来是闪现 + 平移。";
                case SvShapeType.Still:
                    return "让音符在屏幕上保持固定间距，不管主谱面原来的 SV 怎么变。"
                           + "算的是每条音符区间内的 SV 积分，让它们全部相等；这是闭式解，不是近似。"
                           + "注意它改的是整条曲线，所以选区之外的音符也会跟着改。";
                default:
                    return string.Empty;
            }
        }

        // ------------------------------------------------------------------ 计算与写入

        private void Recalc()
        {
            if (_suppress) return;

            if (_cells.Count == 0)
            {
                _lastResult = null;
                ApplyButton.IsEnabled = false;
                return;
            }

            var decimals = 3;
            if (int.TryParse(DecimalsBox.Text, out var d)) decimals = Math.Clamp(d, 0, 8);

            var fold = FoldCheck.IsChecked == true && _cellSeconds > 0;

            try
            {
                _lastResult = SvBrushBuilder.Build(_selection, _cellSeconds, CurrentType, _p, decimals, fold, _cellTimes);
            }
            catch (Exception ex)
            {
                _lastResult = null;
                SummaryText.Text = "生成失败：" + ex.Message;
                return;
            }

            var r = _lastResult;
            var text = $"选区 {r.Cells} 格 · 其中 {r.NoteCells} 格有音符 · "
                       + (r.CellSeconds > 0 ? $"{r.CellSeconds:0.###} 秒/格" : "格长未知");

            if (r.Cells > 0)
            {
                text += $"\n最大 SV {r.MaxAbsSv:0.###}";
                if (r.CellSeconds > 0)
                {
                    text += $" · 单格最大位移 {r.MaxCellMoveScreens:0.###} 屏";
                    text += r.MaxCellMoveScreens > 1
                        ? " · 会跳屏（音符一帧跨过整个屏幕，看不到过程）"
                        : " · 不跳屏";
                }
            }

            if (r.GradientCount > 0) text += $"\n已折叠 {r.GradientCount} 处渐变";

            SummaryText.Text = text;
        }

        private void ShapeList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppress) return;
            BuildParamPanel();
            Recalc();
        }

        private void Option_Changed(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded) return;
            Recalc();
        }

        private void Decimals_Changed(object sender, TextChangedEventArgs e)
        {
            if (!IsLoaded) return;
            Recalc();
        }

        private void Apply_Click(object sender, RoutedEventArgs e)
        {
            if (_lastResult == null || _lastResult.Cells == 0 || !_hasLiveTarget)
            {
                MessageBox.Show(this, "当前没有可写入的选区。请先在编辑器里选中一段谱面。", "SV 曲线刷");
                return;
            }

            if (_lastResult.MaxCellMoveScreens > 4)
            {
                var ok = MessageBox.Show(this,
                    $"单格位移达到 {_lastResult.MaxCellMoveScreens:0.#} 屏，音符会在一格之内跨过整个屏幕。\n确定要写入吗？",
                    "SV 曲线刷", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
                if (ok != MessageBoxResult.OK) return;
            }

            // 写完让下次刷新重新读一遍：内容变了，反解出来的起止值也要跟着变
            _loadedStart = -1;
            _loadedEnd = -2;

            _apply(_lastResult.Text);
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        // ------------------------------------------------------------------ 预设

        private void LoadPresets()
        {
            try
            {
                if (!File.Exists(PresetFile)) return;
                var list = JsonConvert.DeserializeObject<List<SvBrushPreset>>(File.ReadAllText(PresetFile));
                if (list == null) return;
                _presets.Clear();
                _presets.AddRange(list);
            }
            catch
            {
                // 预设文件坏了就当没有，不要因此让窗口打不开
            }
        }

        private void SavePresets()
        {
            try
            {
                File.WriteAllText(PresetFile, JsonConvert.SerializeObject(_presets, Formatting.Indented));
            }
            catch (Exception ex)
            {
                PresetStatus.Text = "保存预设失败：" + ex.Message;
            }
        }

        private void RefreshPresetList()
        {
            var keep = PresetList.SelectedIndex;
            PresetList.Items.Clear();
            foreach (var preset in _presets)
                PresetList.Items.Add(preset.Name);

            if (PresetList.Items.Count > 0)
                PresetList.SelectedIndex = Math.Clamp(keep, 0, PresetList.Items.Count - 1);
        }

        private SvBrushPreset? SelectedPreset()
        {
            var i = PresetList.SelectedIndex;
            if (i < 0 || i >= _presets.Count) return null;
            return _presets[i];
        }

        private void PresetList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var preset = SelectedPreset();
            if (preset == null) return;

            var shapeIndex = Math.Clamp(preset.Shape, 0, ShapeNames.Length - 1);
            PresetStatus.Text = $"已选中：{preset.Name}（{ShapeNames[shapeIndex]}）";
        }

        private void SavePreset_Click(object sender, RoutedEventArgs e)
        {
            var name = PresetNameBox.Text.Trim();
            if (name.Length == 0) name = "预设 " + (_presets.Count + 1);

            _presets.RemoveAll(o => o.Name == name);
            _presets.Add(new SvBrushPreset
            {
                Name = name,
                Shape = (int)CurrentType,
                Params = _p.Clone()
            });

            SavePresets();
            RefreshPresetList();
            PresetList.SelectedIndex = _presets.Count - 1;
            PresetStatus.Text = $"已保存：{name}";
        }

        private void ApplyPreset_Click(object sender, RoutedEventArgs e)
        {
            var preset = SelectedPreset();
            if (preset == null)
            {
                PresetStatus.Text = "先在左边选一条预设。";
                return;
            }

            var shapeIndex = Math.Clamp(preset.Shape, 0, ShapeNames.Length - 1);
            var parameters = preset.Params.Clone();

            // 先切到对应形状，再整体换参数（RowsFor 里的 lambda 是读字段的，换完必须重建面板）
            SelectShape((SvShapeType)shapeIndex);
            _p = parameters;

            BuildParamPanel();
            Recalc();

            PresetStatus.Text = $"已应用：{preset.Name}";
        }

        private void DeletePreset_Click(object sender, RoutedEventArgs e)
        {
            var preset = SelectedPreset();
            if (preset == null)
            {
                PresetStatus.Text = "先在左边选一条预设。";
                return;
            }

            _presets.Remove(preset);
            SavePresets();
            RefreshPresetList();
            PresetStatus.Text = $"已删除：{preset.Name}";
        }
    }
}
