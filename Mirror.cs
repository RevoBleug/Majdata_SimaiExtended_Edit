using System.Text;
using System.Text.RegularExpressions;

namespace MajdataEdit;

//小额负责的部分哟
internal static class Mirror
{
    public enum HandleType
    {
        LRMirror,
        UDMirror,
        HalfRotation,
        Rotation45,
        CcwRotation45
    }

    private static readonly Dictionary<char, char> MIRROR_LEFT_RIGHT_MAP = new()
    {
        { '1', '8' },
        { '2', '7' },
        { '3', '6' },
        { '4', '5' },
        { '5', '4' },
        { '6', '3' },
        { '7', '2' },
        { '8', '1' },
        { 'q', 'p' },
        { 'p', 'q' },
        { '<', '>' },
        { '>', '<' },
        { 'z', 's' },
        { 's', 'z' }
    };

    // 当遇到这些字符时 使用特殊的映射表
    private static readonly HashSet<char> MIRROR_SPECIAL_PREFIX = new() { 'D', 'E' };

    private static readonly Dictionary<char, char> MIRROR_LEFT_RIGHT_SPECIAL_MAP = new()
    {
        { '8', '2' },
        { '2', '8' },
        { '3', '7' },
        { '7', '3' },
        { '4', '6' },
        { '6', '4' },
        { '1', '1' },
        { '5', '5' }
    };

    private static readonly Dictionary<char, char> MIRROR_UPSIDE_DOWN_MAP = new()
    {
        { '4', '1' },
        { '5', '8' },
        { '6', '7' },
        { '3', '2' },
        { '7', '6' },
        { '2', '3' },
        { '8', '5' },
        { '1', '4' },
        { 'q', 'p' },
        { 'p', 'q' },
        { 'z', 's' },
        { 's', 'z' }
    };

    private static readonly Dictionary<char, char> MIRROR_UPSIDE_DOWN_SPECIAL_MAP = new()
    {
        { '4', '2' },
        { '2', '4' },
        { '1', '5' },
        { '5', '1' },
        { '8', '6' },
        { '6', '8' },
        { '3', '3' },
        { '7', '7' }
    };

    private static readonly Dictionary<char, char> ROTATE_CW_45_MAP = new()
    {
        { '8', '1' },
        { '7', '8' },
        { '6', '7' },
        { '5', '6' },
        { '4', '5' },
        { '3', '4' },
        { '2', '3' },
        { '1', '2' }
    };

    private static readonly Dictionary<char, char> ROTATE_CCW_45_MAP = new()
    {
        { '1', '8' },
        { '2', '1' },
        { '3', '2' },
        { '4', '3' },
        { '5', '4' },
        { '6', '5' },
        { '7', '6' },
        { '8', '7' }
    };

    private static readonly HashSet<char> ROTATE_CW_45_SPECIAL_PREFIX = new() { '2', '6' };

    private static readonly HashSet<char> ROTATE_CCW_45_SPECIAL_PREFIX = new() { '3', '7' };

    private static readonly Dictionary<char, char> ROTATE_45_SPECIAL_MAP = new()
    {
        { '<', '>' },
        { '>', '<' }
    };

    public static string NoteMirrorHandle(string str, HandleType type)
    {
        // NOTE: 类似 1-5[8:1]{16}, 这样的字符串 可以被SimaiProcess处理 但无法被正确镜像
        // 我认为这是对的 因为这种语法本身就是错误的 只不过SimaiProcess没有做处理而已 不能因此而妥协 以上

        var resultString = new StringBuilder(); // 最终的结果
        var curPart = new StringBuilder(); // 当前的一部分
        var isPartIgnored = false; // 当前部分是否需要被忽略（{} () <> 之类的指令块）
        var inTag = false; // 是否正在读尖括号指令

        void FlushPart()
        {
            if (curPart.Length == 0)
            {
                isPartIgnored = false;
                return;
            }

            var part = curPart.ToString();
            resultString.Append(isPartIgnored ? part : NoteMirrorPart(part, type));
            curPart.Clear();
            isPartIgnored = false;
        }

        // 空白字符会被正常加入每一个part 因此应当在子方法中进行忽略处理 这是为了保持空白字符位置不变
        for (var index = 0; index < str.Length; index++)
        {
            var c = str[index];

            if (inTag)
            {
                curPart.Append(c);
                if (c == '>')
                {
                    inTag = false;
                    FlushPart();
                }

                continue;
            }

            // 尖括号指令（<HS*> <SV*> <SC*> <GR*> </GR>）必须原样保留、绝不参与镜像：
            // 否则里面的数字会被当成键位换掉（<SV*0.8> 镜像成 <SV*0.7>，<SC*2> 变成 <SC*7>）。
            // 注意 '<' 本身也是滑条键位（1<3 这种），只有后面紧跟已知指令头时才当指令。
            if (c == '<' && IsDirectiveAt(str, index))
            {
                FlushPart();
                isPartIgnored = true;
                inTag = true;
                curPart.Append(c);
                continue;
            }

            curPart.Append(c);

            // 如果存在以下字符 则本部分一定是被忽略的
            if (c == '{' || c == '}' || c == '(' || c == ')')
                isPartIgnored = true;

            // 以下字符表示本part结束
            if (c == '}' || c == ')' || c == ',' || c == '/' || c == '`')
                FlushPart();
        }

        // 处理完以后可能还会有剩余的没做转换（可能结尾的逗号没选进去） 也要处理一下
        FlushPart();

        return resultString.ToString();
    }


    /// <summary>
    ///     判断 index 处的 '<' 是否是一条指令的开头（HS* / SV* / SC* / GR* / GR: / /GR）。
    ///     '<' 同时是滑条键位符号，所以不能只看字符本身。
    /// </summary>
    private static bool IsDirectiveAt(string str, int index)
    {
        if (index < 0 || index + 1 >= str.Length || str[index] != '<') return false;

        if (str[index + 1] == '/')
            return index + 4 < str.Length && string.CompareOrdinal(str, index + 2, "GR>", 0, 3) == 0;

        if (index + 3 >= str.Length) return false;
        var head = str.Substring(index + 1, 3);
        return head == "HS*" || head == "SV*" || head == "SC*" || head == "GR*" || head == "GR:";
    }

    private static string NoteMirrorPart(string str, HandleType type)
    {
        switch (type)
        {
            case HandleType.LRMirror:    
                str = NormalMirrorPart(str, MIRROR_LEFT_RIGHT_MAP, MIRROR_LEFT_RIGHT_SPECIAL_MAP, MIRROR_SPECIAL_PREFIX);
                break;    
            case HandleType.UDMirror:
                str = NormalMirrorPart(str, MIRROR_UPSIDE_DOWN_MAP, MIRROR_UPSIDE_DOWN_SPECIAL_MAP, MIRROR_SPECIAL_PREFIX);
                break;
            case HandleType.HalfRotation:
                // 180 = 左右+上下
                str = NormalMirrorPart(str, MIRROR_LEFT_RIGHT_MAP, MIRROR_LEFT_RIGHT_SPECIAL_MAP, MIRROR_SPECIAL_PREFIX);
                str = NormalMirrorPart(str, MIRROR_UPSIDE_DOWN_MAP, MIRROR_UPSIDE_DOWN_SPECIAL_MAP, MIRROR_SPECIAL_PREFIX);
                break;
            case HandleType.Rotation45:
                str = NormalMirrorPart(str, ROTATE_CW_45_MAP, ROTATE_45_SPECIAL_MAP, ROTATE_CW_45_SPECIAL_PREFIX);
                break;
            case HandleType.CcwRotation45:
                str = NormalMirrorPart(str, ROTATE_CCW_45_MAP, ROTATE_45_SPECIAL_MAP, ROTATE_CCW_45_SPECIAL_PREFIX);
                break;
        }

        return str;
    }

    private static string NormalMirrorPart(string str, Dictionary<char, char> normalMap, Dictionary<char, char> specialMap, HashSet<char> specialPrefix)
    {
        StringBuilder result = new StringBuilder();
        bool isSpecialPrefix = false;
        bool isInBracket = false;

        foreach (char c in str)
        {
            // 空白字符忽略
            if (Char.IsWhiteSpace(c))
            {
                result.Append(c);
                continue;
            }

            // 以下是处理字符的部分
            if (isInBracket || c == '[')
            {
                // 进入方括号时忽略 因为这里是时长之类的设置
                // 注意当前字符为'['的时候也进入此分支 因为此时isInBrancket还是false 需要等到下面那个if块才会变
                result.Append(c);
            }
            else if (isSpecialPrefix)
            {
                // 如果前面读到一个D或者E 则使用特殊的映射
                isSpecialPrefix = false;
                if (specialMap.ContainsKey(c))
                {
                    result.Append(specialMap[c]);
                }
                else if (int.TryParse(c.ToString(), out int i) && normalMap.ContainsKey(c))
                {
                    result.Append(normalMap[c]);
                }
                else
                {
                    result.Append(c);
                }
            }
            else
            {
                // 其他情况 则使用默认映射
                if (normalMap.ContainsKey(c))
                {
                    result.Append(normalMap[c]);
                }
                else
                {
                    result.Append(c);
                }
            }

            // 以下是记录状态的部分
            // 记录是否进入了方括号内（即时间）
            if (c == '[')
            {
                isInBracket = true;
            }
            else if (c == ']')
            {
                isInBracket = false;
            }
            // 记录是否是一个特殊前缀 若是 则下面的字符需要使用特别的映射
            // 在左右或上下镜像中，用于D、E区Touch的特殊处理
            // 在45旋转中，本意是对Simai的弱智">"，"<"进行特殊处理，但没有判断本Note是否为Tap
            if (specialPrefix.Contains(c))
            {
                isSpecialPrefix = true;
            }
        }

        return result.ToString();
    }
}