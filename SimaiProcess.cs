using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media.Converters;

namespace MajdataEdit;

internal static class SimaiProcess
{
    public static string? title;
    public static string? artist;
    public static string? designer;
    public static string? other_commands;
    public static float first;
    public static string[] fumens = new string[7];
    public static string[] levels = new string[7];

    /// <summary>
    ///     the timing points that contains notedata
    /// </summary>
    public static List<SimaiTimingPoint> notelist = new();

    /// <summary>
    ///     the timing points made by "," in maidata
    /// </summary>
    public static List<SimaiTimingPoint> timinglist = new();

    /// <summary>
    ///     Reset all the data in the static class.
    /// </summary>
    public static void ClearData()
    {
        title = "";
        artist = "";
        designer = "";
        first = 0;
        fumens = new string[7];
        levels = new string[7];
        notelist = new List<SimaiTimingPoint>();
        timinglist = new List<SimaiTimingPoint>();
    }

    /// <summary>
    ///     Read the maidata.txt into the static class, including the variables. Show up a messageBox when enconter any
    ///     exception.
    /// </summary>
    /// <param name="filename">file path of maidata.txt</param>
    /// <returns>if the read process faced any error</returns>
    public static bool ReadData(string filename)
    {
        var i = 0;
        other_commands = "";
        try
        {
            var maidataTxt = File.ReadAllLines(filename, Encoding.UTF8);
            for (i = 0; i < maidataTxt.Length; i++)
                if (maidataTxt[i].StartsWith("&title="))
                    title = GetValue(maidataTxt[i]);
                else if (maidataTxt[i].StartsWith("&artist="))
                    artist = GetValue(maidataTxt[i]);
                else if (maidataTxt[i].StartsWith("&des="))
                    designer = GetValue(maidataTxt[i]);
                else if (maidataTxt[i].StartsWith("&first="))
                    first = float.Parse(GetValue(maidataTxt[i]));
                else if (maidataTxt[i].StartsWith("&lv_") || maidataTxt[i].StartsWith("&inote_"))
                    for (var j = 1; j < 8 && i < maidataTxt.Length; j++)
                    {
                        if (maidataTxt[i].StartsWith("&lv_" + j + "="))
                            levels[j - 1] = GetValue(maidataTxt[i]);
                        if (maidataTxt[i].StartsWith("&inote_" + j + "="))
                        {
                            var TheNote = "";
                            TheNote += GetValue(maidataTxt[i]) + "\n";
                            i++;
                            for (; i < maidataTxt.Length; i++)
                            {
                                if (i < maidataTxt.Length)
                                    if (maidataTxt[i].StartsWith("&"))
                                        break;
                                TheNote += maidataTxt[i] + "\n";
                            }

                            fumens[j - 1] = TheNote;
                        }
                    }
                else
                    other_commands += maidataTxt[i].Trim() + "\n";

            other_commands = other_commands.Trim();
            return true;
        }
        catch (Exception e)
        {
            MessageBox.Show("在maidata.txt第" + (i + 1) + "行:\n" + e.Message, "读取谱面时出现错误");
            return false;
        }
    }

    /// <summary>
    ///     Save the static data to maidata.txt
    /// </summary>
    /// <param name="filename">file path of maidata.txt</param>
    public static void SaveData(string filename)
    {
        var maidata = new List<string>
        {
            "&title=" + title,
            "&artist=" + artist,
            "&first=" + first,
            "&des=" + designer,
            other_commands!
        };
        for (var i = 0; i < levels.Length; i++)
            if (levels[i] != null && levels[i] != "")
                maidata.Add("&lv_" + (i + 1) + "=" + levels[i].Trim());
        for (var i = 0; i < fumens.Length; i++)
            if (fumens[i] != null && fumens[i] != "")
                maidata.Add("&inote_" + (i + 1) + "=" + fumens[i].Trim());
        File.WriteAllLines(filename, maidata.ToArray());
    }

    private static string GetValue(string varline)
    {
        return varline.Substring(varline.IndexOf("=") + 1);
    }

    /// <summary>
    ///     This method serialize the fumen data and load it into the static class.
    /// </summary>
    /// <param name="text">fumen text</param>
    /// <param name="position">the position of the cusor, to get the return time</param>
    /// <returns>the song time at the position</returns>
    public static double Serialize(string text, long position = 0)
    {
        // 文本没变时不要重新解析整张谱面。
        // 编辑器在光标每一次移动（SelectionChanged）都会调这个方法拿光标时间，
        // 而十几万个字符的谱面解析一次要 20~30ms，光按方向键就会卡。
        // 这里按"文本 + &first"做记忆：命中就直接用上次解析时记下的逗号表二分。
        if (cachedText != null && cachedFirst == first && string.Equals(cachedText, text, StringComparison.Ordinal))
        {
            if (position <= 0) return cachedRequested;
            return ResolveTimeAtPosition(position);
        }

        var original = text;
        var groups = new Dictionary<string, string>();
        groupBlocks.Clear();
        groupInsertTime.Clear();
        groupInsertBpm.Clear();
        groupInsertBeats.Clear();
        groupInsertIndex.Clear();
        text = GroupSyntax.ExtractDefinitions(text, groups, groupBlocks);
        cachedGroups = groups;

        commaCharIndex.Clear();
        commaStartTime.Clear();

        var _notelist = new List<SimaiTimingPoint>();
        var _timinglist = new List<SimaiTimingPoint>();
        var groupCounter = 0;
        var requested = SerializeCore(text, position, groups, first, 0f, 1f, 1f, 1f, 4, 0, 0, ref groupCounter,
            _notelist, _timinglist, out _);

        // 组是"叠加"进来的：组内音符的时间只按组自己算，可能和主谱面交错。
        // 统一按时间排序——View 侧是按顺序收集 SV/SC 的，顺序乱了整条时间轴都会错。
        _notelist.Sort((a, b) => a.time.CompareTo(b.time));
        _timinglist.Sort((a, b) => a.time.CompareTo(b.time));

        notelist = _notelist;
        timinglist = _timinglist;

        cachedText = original;
        cachedFirst = first;
        fastTableText = original;
        cachedRequested = requested;
        ParseVersion++;

        return ResolveTimeAtPosition(position, requested);
    }

    /// <summary>
    ///     光标位置对应的时间。光标落在 &lt;GR:name&gt; 定义块里时，用组内自己的时间轴
    ///     （从插入点起算）重解析一遍组体——否则拿到的是"定义块那几行空白在主谱面上对应的时间"。
    /// </summary>
    private static double ResolveTimeAtPosition(long position, double fallback = double.NaN)
    {
        if (double.IsNaN(fallback)) fallback = TimeAtPosition(position);
        if (cachedGroups == null || groupBlocks.Count == 0) return fallback;

        for (var b = 0; b < groupBlocks.Count; b++)
        {
            var block = groupBlocks[b];
            // 光标在组体内部才算；落在 <GR:xxx> / </GR> 标记上不影响
            if (position < block.BodyStart || position > block.BodyEnd) continue;
            if (string.IsNullOrEmpty(block.Name)) continue;
            if (!cachedGroups.TryGetValue(block.Name, out var body)) return fallback;
            if (!groupInsertTime.TryGetValue(block.Name, out var insertTime)) return fallback;

            var local = position - block.BodyStart;
            var noteBuffer = new List<SimaiTimingPoint>();
            var timingBuffer = new List<SimaiTimingPoint>();
            var groupIndex = groupInsertIndex.TryGetValue(block.Name, out var gi) ? gi : 1;
            var groupCounter = groupIndex;
            try
            {
                // 组内解析不推进主时间轴：起点是插入点，SV/SC/HS 一律从 1 起，BPM/拍长继承插入点
                return SerializeCore(body, local, cachedGroups, insertTime,
                    groupInsertBpm.TryGetValue(block.Name, out var bpm) ? bpm : 150f,
                    1f, 1f, 1f,
                    groupInsertBeats.TryGetValue(block.Name, out var beats) ? beats : 4,
                    1, groupIndex, ref groupCounter, noteBuffer, timingBuffer, out _);
            }
            catch
            {
                return fallback;
            }
        }

        return fallback;
    }

    /// <summary>
    ///     每次都重解析太贵，这里把上一次的结果留着；
    ///     调用方拿它判断"文本是不是和上次解析的一样"。
    /// </summary>
    public static bool HasFreshParse(string text)
    {
        return cachedText != null && cachedFirst == first && string.Equals(cachedText, text, StringComparison.Ordinal);
    }

    private static string? cachedText;
    /// <summary>当前 commaCharIndex / commaStartTime 对应的是哪份文本（快速重建和完整解析共用这两个表）</summary>
    private static string? fastTableText;
    private static float cachedFirst;
    private static double cachedRequested;
    private static Dictionary<string, string>? cachedGroups;

    // 组定义块在谱面原文里的区间，以及每个组的插入点信息。
    // 光标落在组定义块里时，要用"插入点时间 + 组内时间轴"来算它对应的时间，
    // 不能走主谱面的逗号表（组定义那几行在编辑视图里被占位成空白，查出来是错的）。
    private static readonly List<GroupBlock> groupBlocks = new();
    private static readonly Dictionary<string, double> groupInsertTime = new();
    private static readonly Dictionary<string, float> groupInsertBpm = new();
    private static readonly Dictionary<string, int> groupInsertBeats = new();
    private static readonly Dictionary<string, int> groupInsertIndex = new();

    /// <summary>解析结果版本号，供缓存失效用（DrawWave 之类）</summary>
    public static int ParseVersion { get; private set; }

    // 每个逗号在文本里的字符下标 + 走完这个逗号后的时间。
    // 光标位置查时间就看它，O(log n)，不用重解析。
    private static readonly List<int> commaCharIndex = new();
    private static readonly List<double> commaStartTime = new();

    /// <summary>文本光标位置 → 该位置所在逗号的起始时间</summary>
    public static double TimeAtPosition(long position)
    {
        if (commaCharIndex.Count == 0) return first;
        var lo = 0;
        var hi = commaCharIndex.Count - 1;
        var ans = -1;
        while (lo <= hi)
        {
            var mid = (lo + hi) >> 1;
            // 必须是严格小于：commaCharIndex 存的是逗号字符本身的位置，
            // 光标停在逗号上（position == 逗号下标，即光标紧贴逗号左边）时还属于本格，
            // 越过逗号（position 更大）才算进下一格。
            // 原来写的是 <=，光标在 "1w5[8:1]|,,," 这种"格尾、逗号前"的地方会被算成下一格，整体偏一格。
            if (commaCharIndex[mid] < position)
            {
                ans = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        return ans < 0 ? first : commaStartTime[ans];
    }

    /// <summary>
    ///     只重建"逗号字符位置 → 该格起始时间"表，不生成任何音符对象。
    ///     输入谱面时每个字符都要更新光标时间（波形三角要实时跟手），
    ///     完整解析一次要 20~30ms，按键时会明显发黏；这里只按 SerializeCore 的规则推进 time，
    ///     十几万字符也只有零点几毫秒。
    ///     规则必须和 SerializeCore 保持一致：|| 注释、{beats}、(bpm)、&lt;GR:…&gt; 定义块占位、逗号推进。
    /// </summary>
    public static void BuildCommaTableFast(string text)
    {
        // 文本没变就不用重算（方向键移动光标时每次都会走到这里）
        if (fastTableText != null && string.Equals(fastTableText, text, StringComparison.Ordinal)) return;
        fastTableText = text;
        // 表已经换成"当前文本"的了，缓存里那份（对应旧文本）不能再命中，
        // 否则删掉刚输入的字符后文本回到旧值，会拿新表当旧表用，整体错位。
        cachedText = null;

        commaCharIndex.Clear();
        commaStartTime.Clear();
        if (text == null || text.Length == 0) return;

        double time = first;
        var bpm = 0f;
        var beats = 4;
        var i = 0;
        var n = text.Length;

        while (i < n)
        {
            var c = text[i];

            if (c == '|' && i + 1 < n && text[i + 1] == '|')
            {
                while (i < n && text[i] != '\n') i++;
                continue;
            }

            if (c == '(')
            {
                var start = ++i;
                while (i < n && text[i] != ')') i++;
                var expr = text.Substring(start, i - start);
                i++;
                // (90#7/8&80)：拍号和星星速度都不影响格子时长，只取 bpm
                var amp = expr.IndexOf('&');
                if (amp >= 0) expr = expr[..amp];
                var hash = expr.IndexOf('#');
                if (hash >= 0) expr = expr[..hash];
                if (float.TryParse(expr, out var parsedBpm) && parsedBpm > 0f) bpm = parsedBpm;
                continue;
            }

            if (c == '{')
            {
                var start = ++i;
                while (i < n && text[i] != '}') i++;
                var beatsText = text.Substring(start, i - start);
                i++;
                if (int.TryParse(beatsText, out var parsedBeats) && parsedBeats > 0) beats = parsedBeats;
                continue;
            }

            if (c == '<')
            {
                // <GR:name>…</GR> 是组定义，整块不占主时间轴。
                // 两边都是逗号时后面的逗号也要一起吃掉——和 ExtractDefinitions 的占位规则一致，
                // 否则会凭空多一个空拍，后面所有格子的时间整体后移。
                if (i + 4 <= n && string.CompareOrdinal(text, i, "<GR:", 0, 4) == 0)
                {
                    var nameEnd = text.IndexOf('>', i + 4);
                    var close = nameEnd < 0 ? -1 : text.IndexOf("</GR>", nameEnd, StringComparison.Ordinal);
                    if (close < 0)
                    {
                        i = n;
                        break;
                    }

                    var blockEnd = close + 5;
                    if (i > 0 && text[i - 1] == ',' && blockEnd < n && text[blockEnd] == ',') blockEnd++;
                    i = blockEnd;
                    continue;
                }

                // 注意：'<' 同时也是宽音符的方向符（5<2 是"5 往逆时针到 2"），
                // 不能见到 '<' 就吞到下一个 '>'，否则整段谱面会被跳过去。
                // 只有 </GR> 和 <HS*/<SV*/<SC*/<GR* 是真正的指令。
                if (i + 5 <= n && string.CompareOrdinal(text, i, "</GR>", 0, 5) == 0)
                {
                    i += 5;
                    continue;
                }

                if (i + 4 <= n)
                {
                    var head = text.Substring(i + 1, 3);
                    if (head == "HS*" || head == "SV*" || head == "SC*" || head == "GR*")
                    {
                        var close = text.IndexOf('>', i + 4);
                        i = close < 0 ? n : close + 1;
                        continue;
                    }
                }

                i++;
                continue;
            }

            if (c == ',')
            {
                if (bpm > 0f) time += 1d / (bpm / 60d) * 4d / beats;
                commaCharIndex.Add(i);
                commaStartTime.Add(time);
                i++;
                continue;
            }

            i++;
        }
    }

    /// <summary>
    ///     实际解析。带初始状态参数，是为了让 &lt;GR*x&gt; 插入的组能继承插入点的 bpm/拍长/SV/SC/HS。
    ///     组内解析时 time 从 startTime 开始，解析完不影响外层的 time —— 组内时间不参与整张谱面的时间推进。
    /// </summary>
    private static double SerializeCore(string text, long position, Dictionary<string, string> groups,
        double startTime, float initBpm, float initSV, float initSC, float initHS, int initBeats, int depth,
        int groupIndex, ref int groupCounter,
        List<SimaiTimingPoint> _notelist, List<SimaiTimingPoint> _timinglist,
        out double endTime)
    {
        endTime = startTime;
        try
        {
            float bpm = initBpm;
            var curHSpeed = initHS;
            var curSVeloc = initSV;
            var curSVTrans = 0f; // 本次 SV 的渐变时长（秒），0 表示瞬间生效
            var curSVStart = 0f; // 本次 SV 的显式起始值（<SV*-1:0#4:1> 里的 -1）
            var haveSVStart = false;
            var curScale = initSC; // SC：谱面整体缩放
            var curScaleTrans = 0f;
            var curScaleStart = 0f;
            var haveScaleStart = false;
            double time = startTime; //in seconds
            double requestedTime = 0;
            var beats = initBeats;
            var sigNum = 4; // 拍号分子，只影响编辑器波形上的小节线
            var sigDen = 4; // 拍号分母
            var starBpm = initBpm; // 星星启动速度用的 bpm，默认跟随当前 bpm
            var haveNote = false;
            var haveSV = false;
            var haveSC = false;
            var noteTemp = "";
            int Ycount = 0, Xcount = 0;

            for (var i = 0; i < text.Length; i++)
            {
                if (text[i] == '|' && i + 1 < text.Length && text[i + 1] == '|')
                {
                    // 跳过注释
                    Xcount++;
                    while (i < text.Length && text[i] != '\n')
                    {
                        i++;
                        Xcount++;
                    }

                    Ycount++;
                    Xcount = 0;
                    continue;
                }

                if (text[i] == '\n')
                {
                    Ycount++;
                    Xcount = 0;
                }
                else
                {
                    Xcount++;
                }

                if (i - 1 < position) requestedTime = time;
                if (text[i] == '(')
                    //Get bpm，支持三种扩展写法：
                    //  (90)            只改 bpm（星星速度跟着变成 90，拍号不变）
                    //  (90#7/8)        同时指定拍号 7/8（只影响编辑器波形上的小节线）
                    //  (90#7/8&80)     再单独指定星星启动速度：80bpm 下的一个四分音符
                    //括号一律是英文半角。& 可以单独用：(90&80)
                {
                    haveNote = false;
                    noteTemp = "";
                    var bpmExpr = "";
                    i++;
                    Xcount++;
                    while (i < text.Length && text[i] != ')')
                    {
                        bpmExpr += text[i];
                        i++;
                        Xcount++;
                    }

                    // 星星启动速度（& 之后）
                    var starPart = float.NaN;
                    var ampIndex = bpmExpr.IndexOf('&');
                    if (ampIndex >= 0)
                    {
                        if (float.TryParse(bpmExpr[(ampIndex + 1)..], out var parsedStar)) starPart = parsedStar;
                        bpmExpr = bpmExpr[..ampIndex];
                    }

                    // 拍号（# 之后），只写分子时默认 /4
                    var hashIndex = bpmExpr.IndexOf('#');
                    if (hashIndex >= 0)
                    {
                        var sig = bpmExpr[(hashIndex + 1)..];
                        bpmExpr = bpmExpr[..hashIndex];
                        var slash = sig.IndexOf('/');
                        if (slash < 0)
                        {
                            if (int.TryParse(sig, out var onlyNum) && onlyNum > 0)
                            {
                                sigNum = onlyNum;
                                sigDen = 4;
                            }
                        }
                        else
                        {
                            if (int.TryParse(sig[..slash], out var num) && num > 0) sigNum = num;
                            if (int.TryParse(sig[(slash + 1)..], out var den) && den > 0) sigDen = den;
                        }
                    }

                    bpm = float.Parse(bpmExpr);
                    // 没写 & 的时候星星速度跟着 bpm 走
                    starBpm = float.IsNaN(starPart) ? bpm : starPart;
                    //Console.WriteLine("BPM" + bpm);
                    continue;
                }

                if (text[i] == '{')
                    //Get beats
                {
                    haveNote = false;
                    noteTemp = "";
                    var beats_s = "";
                    i++;
                    Xcount++;
                    while (text[i] != '}')
                    {
                        beats_s += text[i];
                        i++;
                        Xcount++;
                    }

                    beats = int.Parse(beats_s);
                    //Console.WriteLine("BEAT" + beats);
                    continue;
                }

                if (text[i] == 'H')
                    //Get HS
                {
                    haveNote = false;
                    noteTemp = "";
                    var hs_s = "";
                    if (text[i + 1] == 'S' && text[i + 2] == '*')
                    {
                        i += 3;
                        Xcount += 3;
                    }

                    while (text[i] != '>')
                    {
                        hs_s += text[i];
                        i++;
                        Xcount++;
                    }

                    curHSpeed = float.Parse(hs_s);
                    //Console.WriteLine("HS" + curHSpeed);
                    continue;
                }

                if (text[i] == 'S' && i + 2 < text.Length && text[i + 2] == '*' &&
                    (text[i + 1] == 'V' || text[i + 1] == 'C'))
                //Get SV / SC
                {
                    //haveNote = false;
                    noteTemp = "";
                    var isScale = text[i + 1] == 'C';
                    i += 3;
                    Xcount += 3;

                    var expr = "";
                    while (i < text.Length && text[i] != '>')
                    {
                        expr += text[i];
                        i++;
                        Xcount++;
                    }

                    // expr 四种写法：
                    //   1.5          立即变成 1.5
                    //   1.5#4:1      用"当前bpm下 4分音符 × 1"的时长，从当前值渐变到 1.5
                    //   1.5##1.145   用 1.145 秒从当前值渐变到 1.5
                    //   -1:0#4:1     同样的时长，但起始值显式指定为 -1，从 -1 渐变到 0
                    //                （不写冒号就从"上一条指令留下的当前值"接，写了就强制从起值开始）
                    var target = 1f;
                    var trans = 0f;
                    var startVal = 0f;
                    var hasStart = false;

                    var hash = expr.IndexOf('#');
                    var valuePart = hash < 0 ? expr : expr[..hash];

                    // 冒号前是显式起始值。只能取第一个冒号：时长部分（4:1）在 # 后面，不会被误切。
                    var colon = valuePart.IndexOf(':');
                    if (colon >= 0)
                    {
                        if (float.TryParse(valuePart[..colon], out var parsedStart))
                        {
                            startVal = parsedStart;
                            hasStart = true;
                        }

                        valuePart = valuePart[(colon + 1)..];
                    }

                    target = float.Parse(valuePart);

                    if (hash >= 0)
                    {
                        var tail = expr[(hash + 1)..];
                        if (tail.StartsWith("#"))
                        {
                            trans = float.Parse(tail[1..]);
                        }
                        else
                        {
                            var ratio = tail.Split(':');
                            var divide = int.Parse(ratio[0]);
                            var count = int.Parse(ratio[1]);
                            trans = bpm > 0 ? 60f / bpm * 4f / divide * count : 0f;
                        }
                    }

                    // 没有渐变时长时"起始值"没有意义（瞬间跳变），忽略掉
                    if (trans <= 0f) hasStart = false;

                    if (isScale)
                    {
                        curScale = target;
                        curScaleTrans = trans;
                        curScaleStart = startVal;
                        haveScaleStart = hasStart;
                        haveSC = true;
                    }
                    else
                    {
                        curSVeloc = target;
                        curSVTrans = trans;
                        curSVStart = startVal;
                        haveSVStart = hasStart;
                        haveSV = true;
                    }

                    continue;
                }

                if (text[i] == 'G' && i + 4 < text.Length && text[i + 1] == 'R' && text[i + 2] == '*')
                //Insert Group：组内时间独立计算，不推进主谱面的时间
                {
                    i += 3;
                    Xcount += 3;
                    var groupName = "";
                    while (i < text.Length && text[i] != '>')
                    {
                        groupName += text[i];
                        i++;
                        Xcount++;
                    }

                    groupName = groupName.Trim();
                    // 组内不允许再插组：depth > 0 时不再展开
                    if (depth == 0 && groups != null && groups.TryGetValue(groupName, out var groupBody))
                    {
                        // 每次插入分配一个独立编号：组内的 SV/SC 走它自己的一条曲线，
                        // 组外的主谱面继续走主曲线，两边互不覆盖。
                        // SV / SC / HS 一律从 1 起，不继承插入点的值（组内写什么就是什么）。
                        // BPM 和拍长仍然沿用插入点的值：这两个是算时间必需的，组内没写就只能继承。
                        var newGroup = ++groupCounter;
                        var noteFrom = _notelist.Count;
                        var timingFrom = _timinglist.Count;
                        var insertTime = time;

                        // 记下插入点：编辑器里光标落在组定义块内时，要用"插入点 + 组内时间轴"算时间。
                        // 同一个组插多处时以第一处为准（组内的位置感按第一次使用来）。
                        if (!groupInsertTime.ContainsKey(groupName))
                        {
                            groupInsertTime[groupName] = insertTime;
                            groupInsertBpm[groupName] = bpm;
                            groupInsertBeats[groupName] = beats;
                            groupInsertIndex[groupName] = newGroup;
                        }

                        SerializeCore(groupBody, long.MaxValue, groups, time, bpm, 1f, 1f, 1f,
                            beats, depth + 1, newGroup, ref groupCounter, _notelist, _timinglist, out _);

                        // 组内时间点都记下插入点：View 要用它把组曲线和主谱面在插入点的位置对齐。
                        // 组开头可能是空拍，组的第一个时间点会晚于插入点，所以不能只看第一个时间点。
                        for (var k = noteFrom; k < _notelist.Count; k++)
                        {
                            _notelist[k].groupStartTime = insertTime;
                            _notelist[k].groupInitSV = 1f;
                            _notelist[k].groupInitScale = 1f;
                        }

                        for (var k = timingFrom; k < _timinglist.Count; k++)
                        {
                            _timinglist[k].groupStartTime = insertTime;
                            _timinglist[k].groupInitSV = 1f;
                            _timinglist[k].groupInitScale = 1f;
                        }
                    }

                    continue;
                }

                if (isNote(text[i])) haveNote = true;
                if ((haveNote || haveSV) && text[i] != ',') noteTemp += text[i];
                if (text[i] == ',')
                {
                    // haveSC 也算：只有 SC 没有音符的时间点也要进 notelist，
                    // 否则 sendRequestRun 序列化 notelist 时这条 SC 会丢，View 收不到。
                    if (haveNote || haveSV || haveSC)
                    {
                        if (noteTemp.Contains('`'))
                        {
                            // 伪双
                            var fakeEachList = noteTemp.Split('`');
                            var fakeTime = time;
                            var timeInterval = 0.625 / bpm; // 384分音
                            foreach (var fakeEachGroup in fakeEachList)
                            {
                                Console.WriteLine(fakeEachGroup);
                                _notelist.Add(new SimaiTimingPoint(fakeTime, Xcount, Ycount, fakeEachGroup, bpm,
                                    curHSpeed, curSVeloc, curSVTrans, curScale, curScaleTrans, groupIndex, sigNum,
                                    sigDen, starBpm, haveSVStart, curSVStart, haveScaleStart, curScaleStart));
                                fakeTime += timeInterval;
                            }
                        }
                        else
                        {
                            _notelist.Add(new SimaiTimingPoint(time, Xcount, Ycount, noteTemp, bpm, curHSpeed, curSVeloc, curSVTrans, curScale, curScaleTrans, groupIndex, sigNum, sigDen, starBpm, haveSVStart, curSVStart, haveScaleStart, curScaleStart));
                        }
                        //Console.WriteLine("Note:" + noteTemp);

                        noteTemp = "";
                    }

                    _timinglist.Add(new SimaiTimingPoint(time, Xcount, Ycount, "", bpm, curHSpeed, curSVeloc, curSVTrans, curScale, curScaleTrans, groupIndex, sigNum, sigDen, starBpm, haveSVStart, curSVStart, haveScaleStart, curScaleStart));


                    time += 1d / (bpm / 60d) * 4d / beats;
                    // 记下"这个逗号结束的位置"和"走完后的时间"，供光标查时间用。
                    // 组内递归（depth > 0）的 i 是组文本里的下标，对不上主文本，不能记。
                    if (depth == 0)
                    {
                        commaCharIndex.Add(i);
                        commaStartTime.Add(time);
                    }
                    //Console.WriteLine(time);
                    haveNote = false;
                    haveSV = false;
                    haveSC = false;
                    // 渐变时长只对紧跟的那一个时间点有效。不复位的话，
                    // 后面每个 timepoint 都会带着同一个 trans 发给 View，
                    // View 每帧都当成"正在渐变"，分段函数就整段错了。
                    curSVTrans = 0f;
                    curScaleTrans = 0f;
                    haveSVStart = false;
                    haveScaleStart = false;
                }
            }

            //Console.WriteLine(notelist.ToArray());
            endTime = time;
            return requestedTime;
        }
        catch (Exception e)
        {
            Console.WriteLine(e.Message);
            return 0;
        }
    }

    public static void ClearNoteListPlayedState()
    {
        notelist.Sort((x, y) => x.time.CompareTo(y.time));
        for (var i = 0; i < notelist.Count; i++) notelist[i].havePlayed = false;
    }

    private static bool isNote(char noteText)
    {
        var SlideMarks = "1234567890ABCDE"; ///ABCDE for touch
        foreach (var mark in SlideMarks)
            if (noteText == mark)
                return true;
        return false;
    }

    public static string GetDifficultyText(int index)
    {
        if (index == 0) return "EASY";
        if (index == 1) return "BASIC";
        if (index == 2) return "ADVANCED";
        if (index == 3) return "EXPERT";
        if (index == 4) return "MASTER";
        if (index == 5) return "Re:MASTER";
        if (index == 6) return "ORIGINAL";
        return "DEFAULT";
    }

    /// <summary>
    ///     宽音符：5&gt;3 / 5&lt;3，可带 b / x / c / m 后缀（5&gt;3b、5b&gt;3、5&gt;3x、5&gt;3c…），只有 Tap / Break 有。
    ///     规则是"剥掉后缀之后正好是 键位 + 尖括号 + 键位"，整体不含方括号——
    ///     带 [时长] 的仍然是 slide（合法 slide 必须有时长，不带 [] 的 5&gt;3 本来就是坏写法）。
    ///     放在 SimaiProcess 而不是 SimaiTimingPoint，是因为语法检查也要用。
    /// </summary>
    public static bool TryParseWideNote(string noteText, out int startPos, out int endPos, out bool clockwise)
    {
        startPos = 0;
        endPos = 0;
        clockwise = false;

        if (noteText.IndexOf('[') >= 0) return false;

        // b=break  x=EX  c=不跟 SV  m=mine，这几个后缀位置自由（5b>3 和 5>3b 都合法）
        var buf = new StringBuilder(4);
        for (var i = 0; i < noteText.Length; i++)
        {
            var ch = noteText[i];
            if (ch == 'b' || ch == 'x' || ch == 'c' || ch == 'm') continue;
            buf.Append(ch);
        }
        if (buf.Length != 3) return false;

        var s = buf[0];
        var mark = buf[1];
        var e = buf[2];
        if (s < '1' || s > '8' || e < '1' || e > '8') return false;
        if (mark != '>' && mark != '<') return false;

        startPos = s - '0';
        endPos = e - '0';

        // 7812 半圈：> 顺时针、< 逆时针；3456 半圈反过来。
        // 这样符号看起来就是箭头指向的方向，和键位在圆上的切线方向一致。
        var upperHalf = startPos is 7 or 8 or 1 or 2;
        clockwise = upperHalf ? mark == '>' : mark == '<';
        return true;
    }
}

internal class SimaiTimingPoint
{
    public float currentBpm = -1;
    public bool havePlayed;
    public float HSpeed = 1f;
    public float Scale = 1f; // SC：谱面整体缩放
    public float ScaleTransition; // SC 渐变时长（秒），0 = 瞬间生效
    public float SVeloc = 1f;
    public float SVTransition; // SV 渐变时长（秒），0 = 瞬间生效
    public bool hasSVStart; // <SV*-1:0#4:1> 里的 -1 是否显式给出（否则从"当前值"接着变）
    public float SVStart;
    public bool hasScaleStart;
    public float ScaleStart;
    public int groupIndex; // <GR*x> 插入的组编号，0 = 主谱面。组内 SV/SC/HS 与主谱面完全隔离
    public int signatureNumerator = 4; // 拍号分子（7/8 的 7），只影响编辑器波形上的小节线
    public int signatureDenominator = 4; // 拍号分母（7/8 的 8）
    public float starBpm = -1f; // 星星启动速度用的 bpm（-1 表示跟 currentBpm 一致）
    public double groupStartTime; // 组插入点的时间（组内时间点都带，供 View 对齐位置基准）
    public float groupInitSV = 1f; // 组曲线的初始 SV（组内一律从 1 起，不继承主谱面）
    public float groupInitScale = 1f; // 组曲线的初始 SC（组内一律从 1 起，不继承主谱面）
    public List<SimaiNote> noteList = new(); //only used for json serialize
    public string notesContent;
    public int rawTextPositionX;
    public int rawTextPositionY;
    public double time;

    public SimaiTimingPoint(double _time, int textposX = 0, int textposY = 0, string _content = "", float bpm = 0f,
        float _hspeed = 1f, float _sveloc = 1f, float _svtrans = 0f, float _scale = 1f, float _scaletrans = 0f,
        int _groupIndex = 0, int _sigNum = 4, int _sigDen = 4, float _starBpm = -1f,
        bool _hasSVStart = false, float _svStart = 0f, bool _hasScaleStart = false, float _scaleStart = 0f)
    {
        hasSVStart = _hasSVStart;
        SVStart = _svStart;
        hasScaleStart = _hasScaleStart;
        ScaleStart = _scaleStart;
        groupIndex = _groupIndex;
        signatureNumerator = _sigNum;
        signatureDenominator = _sigDen;
        starBpm = _starBpm;
        time = _time;
        rawTextPositionX = textposX;
        rawTextPositionY = textposY;
        notesContent = _content.Replace("\n", "").Replace(" ", "");
        currentBpm = bpm;
        HSpeed = _hspeed;
        SVeloc = _sveloc;
        SVTransition = _svtrans;
        Scale = _scale;
        ScaleTransition = _scaletrans;
    }

    public List<SimaiNote> getNotes()
    {
        if (noteList.Count != 0) return noteList;

        var simaiNotes = new List<SimaiNote>();
        if (notesContent == "") return simaiNotes;
        try
        {
            var dummy = 0;
            if (notesContent.Length == 2 && int.TryParse(notesContent, out dummy)) //连写数字
            {
                simaiNotes.Add(getSingleNote(notesContent[0].ToString()));
                simaiNotes.Add(getSingleNote(notesContent[1].ToString()));
                return simaiNotes;
            }

            if (notesContent.Contains('/'))
            {
                var notes = notesContent.Split('/');
                foreach (var note in notes)
                    if (note.Contains('*'))
                        simaiNotes.AddRange(getSameHeadSlide(note));
                    else
                        simaiNotes.Add(getSingleNote(note));
                return simaiNotes;
            }

            if (notesContent.Contains('*'))
            {
                simaiNotes.AddRange(getSameHeadSlide(notesContent));
                return simaiNotes;
            }

            simaiNotes.Add(getSingleNote(notesContent));
            noteList = simaiNotes;
            return simaiNotes;
        }
        catch
        {
            noteList = new List<SimaiNote>();
            return noteList;
        }
    }

    private List<SimaiNote> getSameHeadSlide(string content)
    {
        var simaiNotes = new List<SimaiNote>();
        var noteContents = content.Split('*');
        var note1 = getSingleNote(noteContents[0]);
        simaiNotes.Add(note1);
        var newNoteContent = noteContents.ToList();
        newNoteContent.RemoveAt(0);
        //删除第一个NOTE
        foreach (var item in newNoteContent)
        {
            var note2text = note1.startPosition + item;
            var note2 = getSingleNote(note2text);
            note2.isSlideNoHead = true;
            simaiNotes.Add(note2);
        }

        return simaiNotes;
    }

    private SimaiNote getSingleNote(string noteText)
    {
        var simaiNote = new SimaiNote();

        if (isTouchNote(noteText))
        {
            simaiNote.touchArea = noteText[0];
            if (simaiNote.touchArea != 'C') simaiNote.startPosition = int.Parse(noteText[1].ToString());
            else simaiNote.startPosition = 8;
            simaiNote.noteType = SimaiNoteType.Touch;
        }
        else
        {
            simaiNote.startPosition = int.Parse(noteText[0].ToString());
            simaiNote.noteType = SimaiNoteType.Tap; //if nothing happen in following if
        }

        // 宽音符：只有 Tap / Break。必须在 slide 判断之前定下来，
        // 否则 5>3 会被 isSlideNote 当成 slide（含 '>'），再去要 [时长] 而崩掉。
        if (SimaiProcess.TryParseWideNote(noteText, out var wideStart, out var wideEnd, out var wideClockwise))
        {
            simaiNote.startPosition = wideStart;
            simaiNote.wideEndPosition = wideEnd;
            simaiNote.isWideClockwise = wideClockwise;
            simaiNote.isWide = true;
            simaiNote.noteType = SimaiNoteType.Tap;
        }

        if (noteText.Contains('f')) simaiNote.isHanabi = true;

        //hold
        if (noteText.Contains('h'))
        {
            if (isTouchNote(noteText))
            {
                simaiNote.noteType = SimaiNoteType.TouchHold;
                simaiNote.holdTime = getTimeFromBeats(noteText);
                //Console.WriteLine("Hold:" +simaiNote.touchArea+ simaiNote.startPosition + " TimeLastFor:" + simaiNote.holdTime);
            }
            else
            {
                simaiNote.noteType = SimaiNoteType.Hold;
                if (noteText.Last() == 'h')
                    simaiNote.holdTime = 0;
                else
                    simaiNote.holdTime = getTimeFromBeats(noteText);
                //Console.WriteLine("Hold:" + simaiNote.startPosition + " TimeLastFor:" + simaiNote.holdTime);
            }
        }

        //slide
        if (!simaiNote.isWide && isSlideNote(noteText))
        {
            simaiNote.noteType = SimaiNoteType.Slide;
            simaiNote.slideTime = getTimeFromBeats(noteText);
            var timeStarWait = getStarWaitTime(noteText);
            simaiNote.slideStartTime = time + timeStarWait;
            if (noteText.Contains('!'))
            {
                simaiNote.isSlideNoHead = true;
                noteText = noteText.Replace("!", "");
            }
            else if (noteText.Contains('?'))
            {
                simaiNote.isSlideNoHead = true;
                noteText = noteText.Replace("?", "");
            }
            //Console.WriteLine("Slide:" + simaiNote.startPosition + " TimeLastFor:" + simaiNote.slideTime);
        }

        //break
        if (noteText.Contains('b'))
        {
            if (simaiNote.noteType == SimaiNoteType.Slide)
            {
                // 如果是Slide 则要检查这个b到底是星星头的还是Slide本体的

                // !!! **SHIT CODE HERE** !!!
                var startIndex = 0;
                while ((startIndex = noteText.IndexOf('b', startIndex)) != -1)
                {
                    if (startIndex < noteText.Length - 1)
                    {
                        // 如果b不是最后一个字符 我们就检查b之后一个字符是不是`[`符号：如果是 那么就是break slide
                        if (noteText[startIndex + 1] == '[')
                            simaiNote.isSlideBreak = true;
                        else
                            // 否则 那么不管这个break出现在slide的哪一个地方 我们都认为他是星星头的break
                            // SHIT CODE!
                            simaiNote.isBreak = true;
                    }
                    else
                    {
                        // 如果b符号是整个文本的最后一个字符 那么也是break slide（Simai语法）
                        simaiNote.isSlideBreak = true;
                    }

                    startIndex++;
                }
            }
            else
            {
                // 除此之外的Break就无所谓了
                simaiNote.isBreak = true;
            }

            noteText = noteText.Replace("b", "");
        }

        if (noteText.Contains('m'))
        {
            if (simaiNote.noteType == SimaiNoteType.Slide)
            {
                // 如果是Slide 则要检查这个b到底是星星头的还是Slide本体的

                // !!! **SHIT CODE HERE** !!!
                var startIndex = 0;
                while ((startIndex = noteText.IndexOf('m', startIndex)) != -1)
                {
                    if (startIndex < noteText.Length - 1)
                    {
                        // 如果b不是最后一个字符 我们就检查b之后一个字符是不是`[`符号：如果是 那么就是break slide
                        if (noteText[startIndex + 1] == '[')
                            simaiNote.isSlideMine = true;
                        else
                            // 否则 那么不管这个break出现在slide的哪一个地方 我们都认为他是星星头的break
                            // SHIT CODE!
                            simaiNote.isMine = true;
                    }
                    else
                    {
                        // 如果b符号是整个文本的最后一个字符 那么也是break slide（Simai语法）
                        simaiNote.isSlideMine = true;
                    }

                    startIndex++;
                }
            }
            else
            {
                // 除此之外的Break就无所谓了
                simaiNote.isMine = true;
            }
            noteText = noteText.Replace("m", "");
        }
        //EX
        if (noteText.Contains('x'))
        {
            simaiNote.isEx = true;
            noteText = noteText.Replace("x", "");
        }

        // c：让音符不跟 SV 走。对星星来说，c 写的位置决定它作用在谁身上：
        //   5c-1[8:1]            c 在轨迹符号之前 → 只作用于星星头，滑条照常跟 SV
        //   5-1c[8:1] / 5-1[8:1]c  c 在轨迹上或时长之后 → 只作用于星星本体，星星头照常跟
        //   两处都写 → 两个都不跟
        if (noteText.Contains('c'))
        {
            simaiNote.canSVAffect = false;
            simaiNote.canSVAffectHead = false;

            if (simaiNote.noteType == SimaiNoteType.Slide)
            {
                // 第一个轨迹符号的位置：它之前算"星星头"，之后算"星星本体"
                var markPos = -1;
                for (var i = 0; i < noteText.Length; i++)
                {
                    if ("-^v<>Vpqszw".IndexOf(noteText[i]) >= 0)
                    {
                        markPos = i;
                        break;
                    }
                }

                var headC = false;
                var bodyC = false;
                for (var i = 0; i < noteText.Length; i++)
                {
                    if (noteText[i] != 'c') continue;
                    if (markPos >= 0 && i < markPos) headC = true;
                    else bodyC = true;
                }

                // 只写了其中一处时，另一处保持"受 SV 影响"
                if (headC && !bodyC) simaiNote.canSVAffect = true;
                if (bodyC && !headC) simaiNote.canSVAffectHead = true;
            }
            else
            {
                // 非星星：c 就是音符自己，头尾不区分
                simaiNote.canSVAffectHead = false;
            }

            noteText = noteText.Replace("c", "");
        }

        //starHead
        if (noteText.Contains('$'))
        {
            simaiNote.isForceStar = true;
            if (noteText.Count(o => o == '$') == 2)
                simaiNote.isFakeRotate = true;
            noteText = noteText.Replace("$", "");
        }

        simaiNote.noteContent = noteText;
        return simaiNote;
    }

    private bool isSlideNote(string noteText)
    {
        var SlideMarks = "-^v<>Vpqszw";
        foreach (var mark in SlideMarks)
            if (noteText.Contains(mark))
                return true;
        return false;
    }

    private bool isTouchNote(string noteText)
    {
        var SlideMarks = "ABCDE";
        foreach (var mark in SlideMarks)
            if (noteText.StartsWith(mark.ToString()))
                return true;
        return false;
    }

    private double getTimeFromBeats(string noteText)
    {
        if (noteText.Count(c => { return c == '['; }) > 1)
        {
            // 组合slide 有多个时长
            double wholeTime = 0;

            var partStartIndex = 0;
            while (noteText.IndexOf('[', partStartIndex) >= 0)
            {
                var startIndex = noteText.IndexOf('[', partStartIndex);
                var overIndex = noteText.IndexOf(']', partStartIndex);
                partStartIndex = overIndex + 1;
                var innerString = noteText.Substring(startIndex + 1, overIndex - startIndex - 1);
                var timeOneBeat = 1d / (currentBpm / 60d);
                if (innerString.Count(o => o == '#') == 1)
                {
                    var times = innerString.Split('#');
                    if (times[1].Contains(':'))
                    {
                        innerString = times[1];
                        timeOneBeat = 1d / (double.Parse(times[0]) / 60d);
                    }
                    else
                    {
                        wholeTime += double.Parse(times[1]);
                        continue;
                    }
                }

                if (innerString.Count(o => o == '#') == 2)
                {
                    var times = innerString.Split('#');
                    wholeTime += double.Parse(times[2]);
                    continue;
                }

                var numbers = innerString.Split(':');
                var divide = int.Parse(numbers[0]);
                var count = int.Parse(numbers[1]);


                wholeTime += timeOneBeat * 4d / divide * count;
            }

            return wholeTime;
        }

        {
            var startIndex = noteText.IndexOf('[');
            var overIndex = noteText.IndexOf(']');
            var innerString = noteText.Substring(startIndex + 1, overIndex - startIndex - 1);
            var timeOneBeat = 1d / (currentBpm / 60d);
            if (innerString.Count(o => o == '#') == 1)
            {
                var times = innerString.Split('#');
                if (times[1].Contains(':'))
                {
                    innerString = times[1];
                    timeOneBeat = 1d / (double.Parse(times[0]) / 60d);
                }
                else
                {
                    return double.Parse(times[1]);
                }
            }

            if (innerString.Count(o => o == '#') == 2)
            {
                var times = innerString.Split('#');
                return double.Parse(times[2]);
            }

            var numbers = innerString.Split(':'); //TODO:customBPM
            var divide = int.Parse(numbers[0]);
            var count = int.Parse(numbers[1]);


            return timeOneBeat * 4d / divide * count;
        }
    }

    private double getStarWaitTime(string noteText)
    {
        var startIndex = noteText.IndexOf('[');
        var overIndex = noteText.IndexOf(']');
        var innerString = noteText.Substring(startIndex + 1, overIndex - startIndex - 1);
        // 星星启动速度：默认用 starBpm（(bpm#拍号&starBpm) 或 (bpm) 设的），
        // 音符自己写了 [4:1#bpm] 时以音符为准
        double bpm = starBpm > 0f ? starBpm : currentBpm;
        if (innerString.Count(o => o == '#') == 1)
        {
            var times = innerString.Split('#');
            bpm = double.Parse(times[0]);
        }

        if (innerString.Count(o => o == '#') == 2)
        {
            var times = innerString.Split('#');
            return double.Parse(times[0]);
        }

        return 1d / (bpm / 60d);
    }
}

internal enum SimaiNoteType
{
    Tap,
    Slide,
    Hold,
    Touch,
    TouchHold
}

internal class SimaiNote
{
    public double holdTime;
    public bool isBreak;
    public bool isEx;
    public bool isMine;
    public bool isFakeRotate;
    public bool isForceStar;
    public bool isHanabi;
    public bool isSlideBreak;
    public bool isSlideMine;
    public bool isSlideNoHead;
    public bool canSVAffect = true;
    // 星星头单独一份：c 写在轨迹符号之前只影响头（5c-1），写在后面只影响滑条（5-1c）。
    // 非星星音符没有"头"的概念，两个字段总是一致。
    public bool canSVAffectHead = true;

    // 宽音符（只有 Tap / Break 有）：5>3 表示从 5 沿某个方向铺到 3。
    // 方向由起点所在半圈和尖括号决定：7812 的 > 是顺时针、< 是逆时针；3456 反过来。
    public bool isWide;
    public int wideEndPosition; // 另一端键位 1-8
    public bool isWideClockwise; // 覆盖方向：true = 顺时针（键位 +1），false = 逆时针（键位 -1）

    public string? noteContent; //used for star explain
    public SimaiNoteType noteType;

    public double slideStartTime;
    public double slideTime;

    public int startPosition = 1; //键位（1-8）
    public char touchArea = ' ';
}
/// <summary>一个 &lt;GR:name&gt;…&lt;/GR&gt; 定义块在谱面原文里的字符区间</summary>
internal struct GroupBlock
{
    public string? Name;
    public int BlockStart; // "<GR:" 的 '<'
    public int BlockEnd; // "</GR>" 之后
    public int BodyStart; // 组体第一个字符（'&gt;' 之后）
    public int BodyEnd; // "</GR>" 的 '<'，不含
}

/// <summary>
///     &lt;GR:name&gt;内容&lt;/GR&gt; 定义组，&lt;GR*name&gt; 在谱面任意位置把组"叠加"上去。
///     组内的时间只按组自己算，不推进主谱面的时间，所以插入点之后的内容位置不变。
/// </summary>
internal static class GroupSyntax
{
    /// <summary>
    ///     把定义块从谱面文本里摘出来收进 groups；原位置用等长空白（保留换行）替换，
    ///     这样按字符位置取光标时间不会错位。
    /// </summary>
    /// <param name="blocks">
    ///     传进来就把每个定义块在原文里的区间记下来，供"光标在组里时算时间"用。
    ///     区间是相对传入的 text（原文）的字符下标，和占位后的文本对齐。
    /// </param>
    public static string ExtractDefinitions(string text, Dictionary<string, string> groups,
        List<GroupBlock>? blocks = null)
    {
        var sb = new StringBuilder(text.Length);
        var i = 0;
        while (i < text.Length)
        {
            var open = text.IndexOf("<GR:", i, StringComparison.Ordinal);
            if (open < 0)
            {
                sb.Append(text, i, text.Length - i);
                break;
            }

            var nameEnd = text.IndexOf('>', open + 4);
            if (nameEnd < 0)
            {
                sb.Append(text, i, text.Length - i);
                break;
            }

            var close = text.IndexOf("</GR>", nameEnd, StringComparison.Ordinal);
            if (close < 0)
            {
                sb.Append(text, i, text.Length - i);
                break;
            }

            var name = text.Substring(open + 4, nameEnd - open - 4).Trim();
            var body = text.Substring(nameEnd + 1, close - nameEnd - 1);
            if (name.Length > 0) groups[name] = body;

            blocks?.Add(new GroupBlock
            {
                Name = name,
                BlockStart = open,
                BlockEnd = close + 5,
                BodyStart = nameEnd + 1,
                BodyEnd = close
            });

            sb.Append(text, i, open - i);

            // 定义块整体占位成等长空白（保留换行），这样按字符位置取时间不会错位。
            // 但如果定义块是写在两个逗号之间的（1,<GR:1>…</GR>,2），
            // 直接占位会留下 "1,,2" —— 凭空多一个空拍，所以把后面那个逗号也一起吃掉。
            var swallowNextComma = open > 0 && text[open - 1] == ',' &&
                                   close + 5 < text.Length && text[close + 5] == ',';
            for (var k = open; k < close + 5; k++) sb.Append(text[k] == '\n' ? '\n' : ' ');
            if (swallowNextComma)
            {
                sb.Append(' ');
                i = close + 6;
            }
            else
            {
                i = close + 5;
            }
        }

        return sb.ToString();
    }

    /// <summary>
    ///     检查组引用：引用了没定义的组、以及组里又插组（不允许），都返回成可读的错误文本。
    /// </summary>
    public static List<string> FindGroupErrors(string text)
    {
        var defined = new Dictionary<string, string>();
        var i = 0;
        while (true)
        {
            var open = text.IndexOf("<GR:", i, StringComparison.Ordinal);
            if (open < 0) break;
            var nameEnd = text.IndexOf('>', open + 4);
            if (nameEnd < 0) break;
            var name = text.Substring(open + 4, nameEnd - open - 4).Trim();
            var close = text.IndexOf("</GR>", nameEnd, StringComparison.Ordinal);
            var body = close < 0 ? "" : text.Substring(nameEnd + 1, close - nameEnd - 1);
            if (name.Length > 0 && !defined.ContainsKey(name)) defined[name] = body;
            i = nameEnd + 1;
        }

        var errors = new List<string>();
        foreach (var pair in defined)
            if (pair.Value.IndexOf("<GR", StringComparison.Ordinal) >= 0)
                errors.Add("<GR:" + pair.Key + "> 组内不允许再插入组");

        i = 0;
        while (true)
        {
            var open = text.IndexOf("<GR*", i, StringComparison.Ordinal);
            if (open < 0) break;
            var nameEnd = text.IndexOf('>', open + 4);
            if (nameEnd < 0) break;
            var name = text.Substring(open + 4, nameEnd - open - 4).Trim();
            if (name.Length > 0 && !defined.ContainsKey(name))
                errors.Add("<GR*" + name + "> 引用了没有定义的组");
            i = nameEnd + 1;
        }

        return errors;
    }

    /// <summary>语法检查用：去掉 &lt;GR:name&gt; / &lt;/GR&gt; 标记（组内容留在原位一起检查），并删掉插入标记</summary>
    public static string StripForSyntax(string text)
    {
        var sb = new StringBuilder(text.Length);
        var i = 0;
        while (i < text.Length)
        {
            if (text[i] == '<')
            {
                if (Match(text, i, "<GR:"))
                {
                    var end = text.IndexOf('>', i);
                    if (end > 0)
                    {
                        i = end + 1;
                        continue;
                    }
                }
                else if (Match(text, i, "</GR>"))
                {
                    i += 5;
                    continue;
                }
                else if (Match(text, i, "<GR*"))
                {
                    var end = text.IndexOf('>', i);
                    if (end > 0)
                    {
                        i = end + 1;
                        continue;
                    }
                }
            }

            sb.Append(text[i]);
            i++;
        }

        return sb.ToString();
    }

    private static bool Match(string text, int index, string token)
    {
        if (index + token.Length > text.Length) return false;
        return string.CompareOrdinal(text, index, token, 0, token.Length) == 0;
    }
}
