using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace MajdataEdit.SvBrush
{
    /// <summary>
    /// 曲线刷支持的形状。全部按"每格一个 SV 值"的方式生成，
    /// 输出时会原样保留选区里的逗号，所以不会改变任何时间。
    /// </summary>
    public enum SvShapeType
    {
        Linear,
        Exponential,
        Bezier,
        Hermite,
        Sinusoidal,
        Circular,
        Chinchilla,
        Custom,
        Random,
        Stutter,
        TeleportStutter,
        Still
    }

    /// <summary>所有形状共用的参数集合，各形状只读自己关心的那几个。</summary>
    public sealed class SvShapeParams
    {
        // Linear / Hermite / Sinusoidal 用
        public double StartSV = 2.0;
        public double EndSV = 1.0;

        // Exponential / Circular / Chinchilla 用
        public bool SpeedUp = true;
        public double Intensity = 30.0;

        // Bezier 用（控制点坐标，范围 0~1）
        public double BezierX1;
        public double BezierY1;
        public double BezierX2;
        public double BezierY2 = 1.0;

        // Sinusoidal 用
        public double Periods = 1.0;
        public double PeriodsShift = 0.25;
        public double Sharpness = 50.0;

        // Circular 用
        public double ArcPercent = 50.0;

        // Chinchilla 用：0 Exponential / 1 Polynomial / 2 Circular / 3 Sine Power
        //                 4 Arc Sine Power / 5 Inverse Power / 6 Peter Stock
        public int ChinchillaType;
        public double ChinchillaIntensity = 0.5;

        // Custom 用：手输的一串值，逗号分隔
        public string CustomValues = "2, 1.5, 1";

        // Random 用
        public int RandomType; // 0 均匀 / 1 正态
        public double RandomScale = 2.0;
        public int RandomSeed = 1;

        // Stutter 用
        public double StutterValue = 1.5;
        public double StutterPercent = 50.0;
        public int StuttersPerSection = 1;
        public bool ControlLastSV;

        // TeleportStutter 用
        public double TeleportMainSV = 0.5;
        public double TeleportDistance = 2.0; // ScrollDist 单位
        public bool TeleportUsePercent;
        public double TeleportPercent = 50.0;

        // Still 用
        public double StillSpacing = 2.0; // ScrollDist 单位，约 2 个单位 = 一屏
        public bool StillAuto = true;

        // 公共
        public double AvgSV = 1.0;
        public double VerticalShift;
        public bool DontNormalize;

        public SvShapeParams Clone()
        {
            return (SvShapeParams)MemberwiseClone();
        }
    }

    /// <summary>
    /// 形状 → 每格 SV 值。纯函数，不碰界面也不碰文本。
    /// </summary>
    public static class SvShapeGenerator
    {
        /// <summary>
        /// 生成 cells 个 SV 值。
        /// hasNote 用来告诉 Stutter / TeleportStutter / Still 哪些格子里有音符
        /// （它们按"音符区间"工作）；cellTimes 是每格的起始时刻（秒），Still 需要精确值。
        /// </summary>
        public static double[] Generate(SvShapeType type, SvShapeParams p, int cells,
            Func<int, bool>? hasNote = null, double[]? cellTimes = null)
        {
            if (cells <= 0) return Array.Empty<double>();
            hasNote ??= _ => false;

            switch (type)
            {
                case SvShapeType.Linear:
                    return Linear(p.StartSV, p.EndSV, cells);

                case SvShapeType.Exponential:
                    return Exponential(p.SpeedUp, cells, p.AvgSV, p.Intensity, p.VerticalShift);

                case SvShapeType.Bezier:
                    return Bezier(p.BezierX1, p.BezierY1, p.BezierX2, p.BezierY2, p.AvgSV, cells, p.VerticalShift);

                case SvShapeType.Hermite:
                    return Hermite(p.StartSV, p.EndSV, cells, p.AvgSV, p.VerticalShift, p.Sharpness);

                case SvShapeType.Sinusoidal:
                    return Sinusoidal(p.StartSV, p.EndSV, p.Periods, p.PeriodsShift, cells, p.VerticalShift, p.Sharpness);

                case SvShapeType.Circular:
                    return Circular(p.SpeedUp, p.ArcPercent, p.AvgSV, p.VerticalShift, cells, p.DontNormalize);

                case SvShapeType.Chinchilla:
                    return Chinchilla(p, cells);

                case SvShapeType.Custom:
                    return Custom(ParseValues(p.CustomValues), cells);

                case SvShapeType.Random:
                    var rnd = new Random(p.RandomSeed);
                    return RandomSet(p.RandomType, p.RandomScale, cells, p.AvgSV, p.VerticalShift, p.DontNormalize, rnd);

                case SvShapeType.Stutter:
                    return Stutter(p, cells, hasNote);

                case SvShapeType.TeleportStutter:
                    return TeleportStutter(p, cells, hasNote);

                case SvShapeType.Still:
                    return Still(p, cells, hasNote, cellTimes);

                default:
                    return Linear(p.StartSV, p.EndSV, cells);
            }
        }

        // ---------------------------------------------------------------- 基础工具

        public static void NormalizeToAverage(double[] values, double targetAverage)
        {
            if (values.Length == 0) return;
            double sum = 0;
            foreach (var v in values) sum += v;
            var avg = sum / values.Length;
            if (Math.Abs(avg) < 1e-9) return;
            var k = targetAverage / avg;
            for (var i = 0; i < values.Length; i++) values[i] *= k;
        }

        /// <summary>把一串自定义值重采样到 cells 个（线性插值）。</summary>
        public static double[] Custom(IReadOnlyList<double> src, int cells)
        {
            var result = new double[cells];
            if (src.Count == 0) return result;
            if (src.Count == 1)
            {
                for (var i = 0; i < cells; i++) result[i] = src[0];
                return result;
            }

            for (var i = 0; i < cells; i++)
            {
                var x = cells == 1 ? 0.0 : i / (double)(cells - 1) * (src.Count - 1);
                var i0 = (int)Math.Floor(x);
                if (i0 >= src.Count - 1) i0 = src.Count - 2;
                var f = x - i0;
                result[i] = src[i0] * (1 - f) + src[i0 + 1] * f;
            }

            return result;
        }

        public static double[] ParseValues(string? text)
        {
            var list = new List<double>();
            if (string.IsNullOrWhiteSpace(text)) return list.ToArray();
            foreach (var part in text!.Split(new[] { ',', '，', ' ', '\t', ';', '；' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                    list.Add(v);
            }

            return list.ToArray();
        }

        // ---------------------------------------------------------------- 各形状

        /// <summary>速度线性变化：起 → 终。</summary>
        public static double[] Linear(double start, double end, int n)
        {
            var r = new double[n];
            if (n == 1)
            {
                r[0] = start;
                return r;
            }

            for (var i = 0; i < n; i++) r[i] = start + (end - start) * i / (n - 1);
            return r;
        }

        /// <summary>
        /// 指数曲线：先算一条 exp 形状，再整体缩放到目标平均值。
        /// intensity 越大越陡；SpeedUp=false 时前后镜像。
        /// </summary>
        public static double[] Exponential(bool speedUp, int n, double avg, double intensity, double shift)
        {
            avg -= shift;
            var a = intensity / 5.0; // 和 plumoguSV 一致，把 intensity 压到好用的范围
            if (Math.Abs(a) < 1e-6) a = 1e-6;

            var r = new double[n];
            for (var i = 0; i < n; i++)
            {
                var x = speedUp
                    ? (i + 0.5) * a / n
                    : (n - i - 0.5) * a / n;
                r[i] = Math.Exp(x) / Math.E / a;
            }

            NormalizeToAverage(r, avg);
            for (var i = 0; i < n; i++) r[i] += shift;
            return r;
        }

        /// <summary>
        /// 贝塞尔：先按 cubic-bezier(x1,y1,x2,y2) 定义一条"位置曲线"，再求导得到每格速度。
        /// </summary>
        public static double[] Bezier(double x1, double y1, double x2, double y2, double avg, int n, double shift)
        {
            avg -= shift;
            var times = new double[n + 1];
            for (var i = 0; i <= n; i++) times[i] = 0.5;

            var targets = new double[n + 1];
            for (var i = 0; i <= n; i++) targets[i] = i / (double)n;

            // 二分式迭代反解参数 t，使 x(t) 落在等距的目标位置上
            for (var iter = 0; iter < 24; iter++)
            {
                var step = Math.Pow(0.5, iter + 1);
                for (var i = 0; i <= n; i++)
                {
                    var x = CubicBezier(x1, x2, times[i]);
                    if (x < targets[i]) times[i] += step;
                    else if (x > targets[i]) times[i] -= step;
                }
            }

            var yPos = new double[n + 1];
            for (var i = 0; i <= n; i++) yPos[i] = CubicBezier(y1, y2, times[i]);

            var r = new double[n];
            for (var i = 0; i < n; i++) r[i] = (yPos[i + 1] - yPos[i]) * n;

            NormalizeToAverage(r, avg);
            for (var i = 0; i < n; i++) r[i] += shift;
            return r;
        }

        /// <summary>
        /// Hermite：在起止值之间走一条 S 形缓动（smoothstep）。
        /// sharpness 50 为对称缓动，&gt;50 更"方"（前段快/后段慢），&lt;50 更接近直线。
        /// </summary>
        public static double[] Hermite(double start, double end, int n, double avg, double shift, double sharpness)
        {
            avg -= shift;
            // sharpness 50 → 指数 1（标准 smoothstep）
            var k = sharpness <= 1 ? 1.0 : Math.Pow(sharpness / 50.0, 1.5);

            var r = new double[n];
            for (var i = 0; i < n; i++)
            {
                var t = n == 1 ? 1.0 : i / (double)(n - 1);
                var e = t * t * (3 - 2 * t);
                // 用 k 在"直线"和"smoothstep"之间、以及更陡的曲线之间插值
                e = t + (e - t) * k;
                e = Math.Clamp(e, 0, 1);
                r[i] = start + (end - start) * e;
            }

            // 起止值是有意义的，只在明确要求时才归一化，这里保持用户输入
            if (Math.Abs(avg - end) > 1e-9 && Math.Abs(start - end) < 1e-9)
                NormalizeToAverage(r, avg);

            for (var i = 0; i < n; i++) r[i] += shift;
            return r;
        }

        /// <summary>
        /// 正弦：振幅从 startSV 线性变到 endSV，周期数 periods，锐度控制波峰尖不尖。
        /// 采样密度跟随格数，不再单独给"每半周期几个点"。
        /// </summary>
        public static double[] Sinusoidal(double startAmp, double endAmp, double periods, double periodsShift,
            int n, double shift, double sharpness)
        {
            if (periods <= 0) periods = 1;

            double p;
            if (sharpness > 50) p = Math.Sqrt((sharpness - 50) * 2);
            else p = Math.Pow(sharpness / 50.0, 2);
            if (p < 1e-6) p = 1e-6;

            var quarterPeriods = 4 * periods;
            var perQuarter = n / quarterPeriods;
            if (perQuarter < 1) perQuarter = 1;

            var r = new double[n];
            for (var i = 0; i < n; i++)
            {
                var amp = n == 1 ? startAmp : startAmp + (endAmp - startAmp) * i / (double)(n - 1);
                var angle = Math.PI / 2 * (i / perQuarter + 4 * periodsShift);
                var s = Math.Sin(angle);
                var v = amp * Math.Pow(Math.Abs(s), p);
                r[i] = v * Math.Sign(s) + shift;
            }

            return r;
        }

        /// <summary>
        /// 圆弧：用半圆的斜率做加减速，arcPercent 越小越接近直线。
        /// </summary>
        public static double[] Circular(bool speedUp, double arcPercent, double avg, double shift, int n, bool dontNormalize)
        {
            avg -= shift;
            var startAngle = Math.PI * (arcPercent / 100.0);

            var angles = Linear(startAngle, 0, n + 1);
            var yPos = new double[n + 1];
            for (var i = 0; i <= n; i++)
            {
                var x = Math.Cos(Math.Round(angles[i], 8));
                var inner = 1 - x * x;
                if (inner < 0) inner = 0;
                yPos[i] = -avg * Math.Sqrt(inner);
            }

            var r = new double[n];
            for (var i = 0; i < n; i++) r[i] = (yPos[i + 1] - yPos[i]) * n;

            if (!speedUp) Array.Reverse(r);
            if (!dontNormalize) NormalizeToAverage(r, avg);
            for (var i = 0; i < n; i++) r[i] += shift;
            return r;
        }

        /// <summary>
        /// Chinchilla：把 0→1 的百分比曲线按 7 种缓动函数重映射，再求导得到速度。
        /// 这是 plumoguSV 里手感最好的一组。
        /// </summary>
        public static double[] Chinchilla(SvShapeParams p, int n)
        {
            var baseAvg = p.AvgSV - p.VerticalShift;
            var steps = n + 1;
            var percents = Linear(0, 1, steps);
            var scaled = new double[steps];
            for (var i = 0; i < steps; i++) scaled[i] = ScalePercent(p, percents[i]);

            var r = new double[n];
            for (var i = 0; i < n; i++) r[i] = (scaled[i + 1] - scaled[i]) * n;

            NormalizeToAverage(r, baseAvg);
            for (var i = 0; i < n; i++) r[i] += p.VerticalShift;
            return r;
        }

        private static double ScalePercent(SvShapeParams p, double percent)
        {
            var slowDown = !p.SpeedUp;
            var w = slowDown ? 1 - percent : percent;
            var a = p.ChinchillaIntensity;
            double np;

            switch (p.ChinchillaType)
            {
                case 0: // Exponential
                    np = w * Math.Exp(a * (w - 1));
                    break;
                case 1: // Polynomial
                    np = Math.Pow(w, a + 1);
                    break;
                case 2: // Circular
                    if (Math.Abs(a) < 1e-9) return percent;
                    var b = 1 / Math.Pow(a, a + 1);
                    var radicand = Math.Pow(b + 1, 2) + b * b - Math.Pow(w + b, 2);
                    np = b + 1 - Math.Sqrt(Math.Max(0, radicand));
                    break;
                case 3: // Sine Power
                    var exp3 = Math.Log(a + 1);
                    var base3 = Math.Sin(Math.PI * (w - 1) / 2) + 1;
                    np = w * Math.Pow(Math.Max(0, base3), exp3);
                    break;
                case 4: // Arc Sine Power
                    var exp4 = Math.Log(a + 1);
                    var base4 = 2 * Math.Asin(Math.Clamp(w, -1, 1)) / Math.PI;
                    np = w * Math.Pow(Math.Max(0, base4), exp4);
                    break;
                case 5: // Inverse Power
                    var den = 1 + Math.Pow(w, -a);
                    np = 2 * w / den;
                    break;
                default: // Peter Stock
                    if (Math.Abs(a) < 1e-9) return percent;
                    var c = a / (1 - a);
                    np = w * w * (1 + c) / (w + c);
                    break;
            }

            if (slowDown) np = 1 - np;
            return Math.Clamp(np, 0, 1);
        }

        public static double[] RandomSet(int type, double scale, int n, double avg, double shift,
            bool dontNormalize, Random rnd)
        {
            avg -= shift;
            var r = new double[n];
            for (var i = 0; i < n; i++)
            {
                if (type == 1)
                {
                    // Box-Muller
                    var u1 = 1.0 - rnd.NextDouble();
                    var u2 = rnd.NextDouble();
                    var norm = Math.Sqrt(-2 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2);
                    r[i] = scale * norm;
                }
                else
                {
                    r[i] = scale * 2 * (0.5 - rnd.NextDouble());
                }
            }

            if (!dontNormalize) NormalizeToAverage(r, avg);
            for (var i = 0; i < n; i++) r[i] += shift;
            return r;
        }

        /// <summary>
        /// 顿挫：在一个区间里只用两个值交替，先快后慢（或先慢后快），
        /// 且两个值按时间占比加权后的平均值等于目标均值 —— 所以整段位移不变，只是"结巴"。
        /// </summary>
        public static double[] Stutter(SvShapeParams p, int n, Func<int, bool> hasNote)
        {
            var r = new double[n];
            for (var i = 0; i < n; i++) r[i] = p.AvgSV;

            var pp = Math.Clamp(p.StutterPercent / 100.0, 0.01, 0.99);
            var other = (p.AvgSV - p.StutterValue * pp) / (1 - pp);

            foreach (var (s, e) in Sections(n, hasNote))
            {
                var len = e - s;
                if (len <= 1) continue;

                var per = Math.Max(1, p.StuttersPerSection);
                var sub = Math.Max(1, len / per);
                for (var k = 0; k < per; k++)
                {
                    var ss = s + k * sub;
                    var se = k == per - 1 ? e : Math.Min(e, ss + sub);
                    var slen = se - ss;
                    if (slen <= 0) continue;

                    var hold = (int)Math.Round(slen * (p.ControlLastSV ? 1 - pp : pp));
                    hold = Math.Clamp(hold, 1, Math.Max(1, slen - 1));

                    for (var i = 0; i < slen; i++)
                    {
                        var isFirst = i < hold;
                        if (p.ControlLastSV) r[ss + i] = isFirst ? other : p.StutterValue;
                        else r[ss + i] = isFirst ? p.StutterValue : other;
                    }
                }
            }

            return r;
        }

        /// <summary>
        /// 瞬移顿挫：区间第一格给一个很大的 SV 把音符一次性推出去，剩下匀速。
        /// 位移按目标均值守恒，所以段末音符仍然落在原来的位置。
        /// </summary>
        public static double[] TeleportStutter(SvShapeParams p, int n, Func<int, bool> hasNote)
        {
            var r = new double[n];
            for (var i = 0; i < n; i++) r[i] = p.AvgSV;

            foreach (var (s, e) in Sections(n, hasNote))
            {
                var len = e - s;
                if (len <= 1) continue;

                var main = p.TeleportMainSV;
                double head;
                if (p.TeleportUsePercent)
                {
                    // 用"占总位移百分比"的方式：第一格承担 p% 的位移
                    var share = Math.Clamp(p.TeleportPercent / 100.0, 0.01, 0.99);
                    head = p.AvgSV * len * share;
                }
                else
                {
                    // 用绝对距离（ScrollDist 单位）：第一格多走 TeleportDistance
                    head = main + p.TeleportDistance;
                }

                var rest = p.AvgSV * len - head;
                var restPer = len - 1 > 0 ? rest / (len - 1) : 0;

                r[s] = head;
                for (var i = s + 1; i < e; i++) r[i] = restPer;
            }

            return r;
        }

        /// <summary>
        /// 等距（Still）：让音符在屏幕上保持固定间距，不管主谱面的 SV 怎么变。
        ///
        /// 原理：两个音符的屏幕间距 = |P(T₂) − P(T₁)| × speed，也就是这段区间里 SV 的积分。
        /// 要间距恒定，就让每条音符区间的积分都等于目标间距 D；区间内每格 SV 恒定，
        /// 于是 SV_k = D / (T_{k+1} − T_k)，闭式解，不用迭代。
        ///
        /// 注意这是"全局曲线"，所以选区外的音符也会跟着改 —— 单曲线模型的必然结果。
        /// </summary>
        public static double[] Still(SvShapeParams p, int n, Func<int, bool> hasNote, double[]? cellTimes)
        {
            var r = new double[n];
            var entry = p.AvgSV;

            var notes = new List<int>();
            for (var i = 0; i < n; i++)
                if (hasNote(i))
                    notes.Add(i);

            // 拿不到音符时刻就没法算，退回常数
            if (notes.Count < 2 || cellTimes == null || cellTimes.Length < n)
            {
                for (var i = 0; i < n; i++) r[i] = entry;
                return r;
            }

            double spacing;
            if (p.StillAuto)
            {
                // 让第一段的 SV 正好等于进入速度，接进主谱面时不会突跳
                var dt0 = cellTimes[notes[1]] - cellTimes[notes[0]];
                spacing = dt0 > 1e-9 ? entry * dt0 : p.StillSpacing;
            }
            else
            {
                spacing = p.StillSpacing;
            }

            if (Math.Abs(spacing) < 1e-9) spacing = p.StillSpacing;

            for (var k = 0; k + 1 < notes.Count; k++)
            {
                var a = notes[k];
                var b = notes[k + 1];
                var span = cellTimes[b] - cellTimes[a];
                var sv = span > 1e-9 ? spacing / span : entry;
                for (var i = a; i < b; i++) r[i] = sv;
            }

            // 最后一个音符之后保持最后一档速度，避免末格又抖一下
            var lastNote = notes[^1];
            var tail = lastNote > 0 ? r[lastNote - 1] : entry;
            for (var i = lastNote; i < n; i++) r[i] = tail;

            // 第一个音符之前用进入速度，保证边界连续
            for (var i = 0; i < notes[0]; i++) r[i] = entry;

            return r;
        }

        private static List<(int S, int E)> Sections(int n, Func<int, bool> hasNote)
        {
            var idx = new List<int>();
            for (var i = 0; i < n; i++)
                if (hasNote(i))
                    idx.Add(i);

            var res = new List<(int, int)>();
            if (idx.Count < 2)
            {
                res.Add((0, n));
                return res;
            }

            for (var k = 0; k + 1 < idx.Count; k++) res.Add((idx[k], idx[k + 1]));
            res.Add((idx[^1], n));
            return res;
        }

        private static double CubicBezier(double p2, double p3, double t)
        {
            var u = 1 - t;
            return 3 * t * u * u * p2 + 3 * t * t * u * p3 + t * t * t;
        }
    }
}
