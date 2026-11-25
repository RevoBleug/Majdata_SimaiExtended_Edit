using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace MajdataEdit.SvBrush
{
    /// <summary>
    /// 把一段谱面文本切成"格子"、读写格子里的 &lt;SV*&gt; 指令。
    ///
    /// 这里是整个工具最要紧的约束：
    ///   只会改格子内部的 &lt;SV*&gt;，逗号一个都不动 —— 所以时间绝对不变，
    ///   不可能因为插入 SV 而让后面整张谱面平移。
    /// </summary>
    public static class SvCellText
    {
        /// <summary>1 个 ScrollDist 单位大约等于多少屏幕（判定圈半径 4.8，屏高按 2 倍算，可调）。</summary>
        public const double UnitsPerScreen = 2.0;

        // ------------------------------------------------------------------ 扫描

        /// <summary>
        /// 找顶层逗号。会跳过 &lt;指令&gt;、(bpm)、{拍长} 和 || 注释里的逗号。
        /// 注意 '&lt;' 同时也是滑条键位，所以只有确实是 &lt;HS*/SV*/SC*/GR*/&lt;/GR&gt; 才当指令。
        /// </summary>
        public static List<int> FindTopCommas(string text)
        {
            var result = new List<int>();
            var angle = 0;
            var paren = 0;
            var brace = 0;

            for (var i = 0; i < text.Length; i++)
            {
                var c = text[i];

                if (c == '|' && i + 1 < text.Length && text[i + 1] == '|')
                {
                    while (i < text.Length && text[i] != '\n') i++;
                    continue;
                }

                if (angle == 0 && paren == 0 && brace == 0 && c == '<' && IsDirectiveStart(text, i))
                {
                    angle++;
                    continue;
                }

                switch (c)
                {
                    case '<':
                        if (angle > 0) angle++;
                        break;
                    case '>':
                        if (angle > 0) angle--;
                        break;
                    case '(':
                        paren++;
                        break;
                    case ')':
                        if (paren > 0) paren--;
                        break;
                    case '{':
                        brace++;
                        break;
                    case '}':
                        if (brace > 0) brace--;
                        break;
                    case ',':
                        if (angle == 0 && paren == 0 && brace == 0) result.Add(i);
                        break;
                }
            }

            return result;
        }

        private static bool IsDirectiveStart(string s, int i)
        {
            var j = i + 1;
            if (j >= s.Length) return false;

            // </GR>
            if (s[j] == '/')
                return j + 2 < s.Length && s[j + 1] == 'G' && s[j + 2] == 'R';

            if (j + 2 >= s.Length) return false;
            var a = s[j];
            var b = s[j + 1];
            var c = s[j + 2];

            if (a == 'H' && b == 'S' && c == '*') return true;
            if (a == 'S' && (b == 'V' || b == 'C') && c == '*') return true;
            if (a == 'G' && b == 'R' && (c == '*' || c == ':')) return true;
            return false;
        }

        /// <summary>每一格在文本里的起始下标（和 SplitCells 一一对应）。</summary>
        public static List<int> SplitCellStarts(string text)
        {
            var starts = new List<int> { 0 };
            foreach (var c in FindTopCommas(text)) starts.Add(c + 1);

            // 以逗号结尾时最后一个起点正好落在文本末尾，不是一格
            if (starts.Count > 1 && starts[^1] >= text.Length) starts.RemoveAt(starts.Count - 1);
            return starts;
        }

        /// <summary>按顶层逗号切格。以逗号结尾的文本不会多出一个空尾格。</summary>
        public static List<string> SplitCells(string text)
        {
            var cells = new List<string>();
            var start = 0;
            foreach (var c in FindTopCommas(text))
            {
                cells.Add(text.Substring(start, c - start));
                start = c + 1;
            }

            if (start < text.Length) cells.Add(text.Substring(start));
            return cells;
        }

        // ------------------------------------------------------------------ 单元格操作

        /// <summary>去掉格子里的所有 &lt;SV*...&gt; 指令。</summary>
        public static string StripSv(string cell)
        {
            var sb = new StringBuilder(cell.Length);
            for (var i = 0; i < cell.Length; i++)
            {
                if (cell[i] == '<' && i + 3 < cell.Length && cell[i + 1] == 'S' && cell[i + 2] == 'V' && cell[i + 3] == '*')
                {
                    var end = cell.IndexOf('>', i);
                    if (end < 0) break;
                    i = end;
                    continue;
                }

                sb.Append(cell[i]);
            }

            return sb.ToString();
        }

        /// <summary>格子里有没有音符（把各种指令、bpm、拍长、注释都剥掉之后还剩东西）。</summary>
        public static bool HasNote(string cell)
        {
            var s = cell;
            var sb = new StringBuilder(s.Length);
            for (var i = 0; i < s.Length; i++)
            {
                if (s[i] == '|' && i + 1 < s.Length && s[i + 1] == '|')
                {
                    while (i < s.Length && s[i] != '\n') i++;
                    continue;
                }

                if (s[i] == '<' && IsDirectiveStart(s, i))
                {
                    var end = s.IndexOf('>', i);
                    if (end < 0) break;
                    i = end;
                    continue;
                }

                if (s[i] == '(')
                {
                    var end = s.IndexOf(')', i);
                    if (end < 0) break;
                    i = end;
                    continue;
                }

                if (s[i] == '{')
                {
                    var end = s.IndexOf('}', i);
                    if (end < 0) break;
                    i = end;
                    continue;
                }

                sb.Append(s[i]);
            }

            return sb.ToString().Trim().Length > 0;
        }

        /// <summary>SV 指令的插入点：跳过开头的 (bpm) 和 {拍长}，保证渐变时长按新的 bpm 计算。</summary>
        private static int SvInsertIndex(string cell)
        {
            var i = 0;
            while (i < cell.Length)
            {
                var c = cell[i];
                if (char.IsWhiteSpace(c))
                {
                    i++;
                    continue;
                }

                if (c == '(')
                {
                    var end = cell.IndexOf(')', i);
                    if (end < 0) return i;
                    i = end + 1;
                    continue;
                }

                if (c == '{')
                {
                    var end = cell.IndexOf('}', i);
                    if (end < 0) return i;
                    i = end + 1;
                    continue;
                }

                break;
            }

            return i;
        }

        public static string InsertSv(string cell, string directive)
        {
            var at = SvInsertIndex(cell);
            return cell.Insert(at, directive);
        }

        /// <summary>读出格子里已有的 SV 目标值（渐变的取终值）；没有则返回 null。</summary>
        public static double? ReadSvValue(string cell)
        {
            for (var i = 0; i < cell.Length; i++)
            {
                if (cell[i] != '<' || i + 3 >= cell.Length) continue;
                if (cell[i + 1] != 'S' || cell[i + 2] != 'V' || cell[i + 3] != '*') continue;

                var end = cell.IndexOf('>', i);
                if (end < 0) return null;
                var expr = cell.Substring(i + 4, end - i - 4);
                var hash = expr.IndexOf('#');
                if (hash >= 0) expr = expr[..hash];

                // <SV*起:止#...>：目标是冒号后面那个值
                var colon = expr.IndexOf(':');
                if (colon >= 0) expr = expr[(colon + 1)..];

                if (double.TryParse(expr, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) return v;
                return null;
            }

            return null;
        }

        /// <summary>
        /// 反解：把选区里已有的 SV 读成逐格数值（没有 SV 的格子沿用上一格）。
        /// 渐变按终值算，不会展开成斜坡 —— 用来当初始值已经够了。
        /// </summary>
        public static double[] ReadExisting(IReadOnlyList<string> cells, double fallback = 1.0)
        {
            var result = new double[cells.Count];
            var current = fallback;
            for (var i = 0; i < cells.Count; i++)
            {
                var v = ReadSvValue(cells[i]);
                if (v.HasValue) current = v.Value;
                result[i] = current;
            }

            return result;
        }

        public static string FormatValue(double v, int decimals)
        {
            if (Math.Abs(v) < 5e-13) v = 0;
            if (decimals < 0) decimals = 0;
            var fmt = decimals == 0 ? "0" : "0." + new string('#', decimals);
            return v.ToString(fmt, CultureInfo.InvariantCulture);
        }
    }

    public sealed class SvBrushBuildResult
    {
        public string Text = string.Empty;
        public int Cells;
        public int NoteCells;
        public double MaxAbsSv;
        public double MaxCellMoveScreens;
        public double CellSeconds;
        public bool Folded;
        public int GradientCount;
    }

    /// <summary>把"每格一个 SV"拼回谱面文本，并给出写入前的数值摘要。</summary>
    public static class SvBrushBuilder
    {
        public static SvBrushBuildResult Build(string selection, double cellSeconds,
            SvShapeType type, SvShapeParams p, int decimals, bool fold, double[]? cellTimes = null)
        {
            var result = new SvBrushBuildResult();
            var cells = SvCellText.SplitCells(selection);
            if (cells.Count == 0) return result;

            var hasNote = new bool[cells.Count];
            var noteCount = 0;
            for (var i = 0; i < cells.Count; i++)
            {
                hasNote[i] = SvCellText.HasNote(cells[i]);
                if (hasNote[i]) noteCount++;
            }

            var values = SvShapeGenerator.Generate(type, p, cells.Count, i => hasNote[i], cellTimes);

            for (var i = 0; i < values.Length; i++)
            {
                var a = Math.Abs(values[i]);
                if (a > result.MaxAbsSv) result.MaxAbsSv = a;
            }

            result.Cells = cells.Count;
            result.NoteCells = noteCount;
            result.CellSeconds = cellSeconds;
            result.MaxCellMoveScreens = cellSeconds > 0
                ? result.MaxAbsSv * cellSeconds / SvCellText.UnitsPerScreen
                : 0;

            // 先剥掉旧的 SV，再按需写入新的
            var stripped = new string[cells.Count];
            for (var i = 0; i < cells.Count; i++) stripped[i] = SvCellText.StripSv(cells[i]);

            var directives = new string?[cells.Count];
            for (var i = 0; i < cells.Count; i++) directives[i] = "<SV*" + SvCellText.FormatValue(values[i], decimals) + ">";

            if (fold && cellSeconds > 0 && cells.Count >= 3)
                directives = Fold(values, directives, cellSeconds, out _, result);

            var sb = new StringBuilder();
            for (var i = 0; i < cells.Count; i++)
            {
                var cell = stripped[i];
                if (directives[i] != null) cell = SvCellText.InsertSv(cell, directives[i]!);
                sb.Append(cell);
                if (i < cells.Count - 1) sb.Append(',');
            }

            // 选区如果本来以逗号结尾，补回去
            if (selection.TrimEnd().EndsWith(",", StringComparison.Ordinal)) sb.Append(',');

            result.Text = sb.ToString();
            return result;
        }

        /// <summary>
        /// 渐变折叠：把一段近似直线的 SV 序列压成一条 &lt;SV*目标##时长&gt;。
        ///
        /// 关键点：渐变是从"上一格生效的值"开始插值到目标值的，所以目标值必须按
        /// (当前值 + 目标值)/2 × 格数 = 台阶位移总和 来反解，否则音符会整体跑偏。
        /// 第一格永远写死值（选区开头的 SV 是什么我们不知道，不能让它参与插值）。
        /// </summary>
        private static string?[] Fold(double[] values, string?[] directives, double cellSeconds,
            out int gradientCount, SvBrushBuildResult result)
        {
            gradientCount = 0;
            var n = values.Length;
            var output = new string?[n];
            if (n == 0) return output;

            var min = double.MaxValue;
            var max = double.MinValue;
            foreach (var v in values)
            {
                if (v < min) min = v;
                if (v > max) max = v;
            }

            var tol = Math.Max(0.008, (max - min) * 0.02);

            output[0] = directives[0];
            var cur = values[0];
            var i = 1;

            while (i < n)
            {
                var m = 1;
                while (i + m + 1 < n && Collinear(values, i, i + m + 1, tol)) m++;

                if (m >= 2)
                {
                    var sum = 0.0;
                    for (var k = i; k <= i + m - 1; k++) sum += values[k];

                    // 斜坡 (cur → target) 在 m 格内的位移 = 台阶位移之和
                    var target = 2 * sum / m - cur;
                    var duration = m * cellSeconds;

                    // 用 "起:止" 形式把起点也写死：这样折叠就不依赖上一格实际生效的值，
                    // 中间无论有没有别的指令插进来，斜坡都是我们要的那条。
                    output[i] = "<SV*" + SvCellText.FormatValue(cur, 4)
                                           + ":" + SvCellText.FormatValue(target, 4)
                                           + "##" + duration.ToString("0.######", CultureInfo.InvariantCulture) + ">";

                    for (var k = i + 1; k <= i + m - 1 && k < n; k++) output[k] = null;

                    gradientCount++;
                    cur = target;
                    i += m;
                }
                else
                {
                    output[i] = directives[i];
                    cur = values[i];
                    i++;
                }
            }

            result.Folded = gradientCount > 0;
            result.GradientCount = gradientCount;
            return output;
        }

        /// <summary>values[a..b] 是否近似落在一条直线上。</summary>
        private static bool Collinear(double[] values, int a, int b, double tol)
        {
            if (b - a < 2) return true;
            var span = b - a;
            var v0 = values[a];
            var dv = values[b] - v0;

            for (var k = a; k <= b; k++)
            {
                var expect = v0 + dv * (k - a) / span;
                if (Math.Abs(values[k] - expect) > tol) return false;
            }

            return true;
        }
    }
}
