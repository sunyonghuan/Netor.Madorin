using Avalonia.Controls.Documents;
using Avalonia.Media;

using AvaloniaRun = Avalonia.Controls.Documents.Run;

namespace Netor.Cortana.UI.Controls.Common;

/// <summary>
/// 手写基础代码语法高亮器。状态机遍历代码，识别字符串/注释/关键字/数字着色。
/// AOT 安全：不使用反射，无第三方依赖。
/// </summary>
internal static class SimpleCodeHighlighter
{
    // ── 颜色常量 ──
    private static readonly SolidColorBrush s_keyword = new(Color.Parse("#569cd6"));
    private static readonly SolidColorBrush s_string = new(Color.Parse("#ce9178"));
    private static readonly SolidColorBrush s_comment = new(Color.Parse("#6a9955"));
    private static readonly SolidColorBrush s_number = new(Color.Parse("#b5cea8"));
    private static readonly SolidColorBrush s_type = new(Color.Parse("#4ec9b0"));
    private static readonly SolidColorBrush s_plain = new(Color.Parse("#d4d4d4"));
    private static readonly SolidColorBrush s_punctuation = new(Color.Parse("#d4d4d4"));
    private static readonly SolidColorBrush s_jsonKey = new(Color.Parse("#9cdcfe"));
    private static readonly SolidColorBrush s_xmlTag = new(Color.Parse("#569cd6"));
    private static readonly SolidColorBrush s_xmlAttr = new(Color.Parse("#9cdcfe"));

    private static readonly HashSet<string> s_csharpKeywords =
    [
        "abstract", "as", "async", "await", "base", "bool", "break", "byte", "case", "catch",
        "char", "checked", "class", "const", "continue", "decimal", "default", "delegate", "do",
        "double", "else", "enum", "event", "explicit", "extern", "false", "finally", "fixed",
        "float", "for", "foreach", "goto", "if", "implicit", "in", "int", "interface", "internal",
        "is", "lock", "long", "namespace", "new", "null", "object", "operator", "out", "override",
        "params", "private", "protected", "public", "readonly", "record", "ref", "return", "sbyte",
        "sealed", "short", "sizeof", "stackalloc", "static", "string", "struct", "switch", "this",
        "throw", "true", "try", "typeof", "uint", "ulong", "unchecked", "unsafe", "ushort",
        "using", "var", "virtual", "void", "volatile", "where", "while", "yield", "get", "set",
        "init", "value", "required", "file", "scoped", "not", "and", "or", "when", "with",
    ];

    private static readonly HashSet<string> s_jsKeywords =
    [
        "async", "await", "break", "case", "catch", "class", "const", "continue", "debugger",
        "default", "delete", "do", "else", "export", "extends", "false", "finally", "for",
        "from", "function", "if", "import", "in", "instanceof", "let", "new", "null", "of",
        "return", "super", "switch", "this", "throw", "true", "try", "typeof", "undefined",
        "var", "void", "while", "yield",
    ];

    private static readonly HashSet<string> s_pythonKeywords =
    [
        "False", "None", "True", "and", "as", "assert", "async", "await", "break", "class",
        "continue", "def", "del", "elif", "else", "except", "finally", "for", "from", "global",
        "if", "import", "in", "is", "lambda", "nonlocal", "not", "or", "pass", "raise",
        "return", "try", "while", "with", "yield",
    ];

    private static readonly HashSet<string> s_sqlKeywords =
    [
        "select", "from", "where", "insert", "update", "delete", "create", "drop", "alter",
        "table", "index", "into", "values", "set", "join", "inner", "outer", "left", "right",
        "on", "and", "or", "not", "null", "is", "like", "in", "between", "exists", "having",
        "group", "by", "order", "asc", "desc", "limit", "offset", "as", "distinct", "count",
        "sum", "avg", "max", "min", "union", "all", "case", "when", "then", "else", "end",
    ];

    /// <summary>
    /// 将代码字符串解析为着色 Run 集合并添加到 InlineCollection。
    /// </summary>
    public static void Highlight(string code, string? language, InlineCollection target)
    {
        var lang = NormalizeLanguage(language);

        if (lang is "json")
        {
            HighlightJson(code, target);
            return;
        }
        if (lang is "xml" or "html" or "xaml" or "axaml" or "svg")
        {
            HighlightXml(code, target);
            return;
        }

        var keywords = lang switch
        {
            "csharp" => s_csharpKeywords,
            "js" or "javascript" or "typescript" or "ts" or "jsx" or "tsx" => s_jsKeywords,
            "python" or "py" => s_pythonKeywords,
            "sql" => s_sqlKeywords,
            _ => null,
        };

        if (keywords is null)
        {
            // 未知语言，纯文本
            target.Add(new AvaloniaRun(code) { Foreground = s_plain });
            return;
        }

        HighlightCLike(code, keywords, lang, target);
    }

    private static string NormalizeLanguage(string? lang)
    {
        if (string.IsNullOrWhiteSpace(lang)) return "";
        var l = lang.Trim().ToLowerInvariant();
        return l switch
        {
            "c#" => "csharp",
            "cs" => "csharp",
            "js" => "js",
            "ts" => "typescript",
            "py" => "python",
            _ => l,
        };
    }

    // ── C-Like 语言高亮（C#, JS, Python, SQL） ──

    private static void HighlightCLike(string code, HashSet<string> keywords, string lang, InlineCollection target)
    {
        int i = 0;
        int len = code.Length;
        var buf = new System.Text.StringBuilder();
        bool isPython = lang is "python" or "py";
        string singleComment = isPython ? "#" : "//";

        while (i < len)
        {
            char c = code[i];

            // 单行注释
            if (MatchAt(code, i, singleComment))
            {
                FlushBuffer(buf, s_plain, target);
                int end = code.IndexOf('\n', i);
                if (end < 0) end = len;
                target.Add(new AvaloniaRun(code[i..end]) { Foreground = s_comment });
                i = end;
                continue;
            }

            // 多行注释 /* ... */（非 Python）
            if (!isPython && c == '/' && i + 1 < len && code[i + 1] == '*')
            {
                FlushBuffer(buf, s_plain, target);
                int end = code.IndexOf("*/", i + 2, StringComparison.Ordinal);
                if (end < 0) end = len - 2;
                target.Add(new AvaloniaRun(code[i..(end + 2)]) { Foreground = s_comment });
                i = end + 2;
                continue;
            }

            // 字符串（双引号）
            if (c == '"')
            {
                FlushBuffer(buf, s_plain, target);
                i = ReadString(code, i, '"', target);
                continue;
            }

            // 字符串（单引号）
            if (c == '\'')
            {
                FlushBuffer(buf, s_plain, target);
                i = ReadString(code, i, '\'', target);
                continue;
            }

            // 数字
            if (char.IsDigit(c) && (i == 0 || !char.IsLetterOrDigit(code[i - 1])))
            {
                FlushBuffer(buf, s_plain, target);
                int start = i;
                while (i < len && (char.IsDigit(code[i]) || code[i] == '.' || code[i] == 'x'
                    || code[i] == 'X' || code[i] == 'f' || code[i] == 'L'
                    || code[i] == 'd' || code[i] == 'm'
                    || (code[i] >= 'a' && code[i] <= 'f')
                    || (code[i] >= 'A' && code[i] <= 'F')))
                    i++;
                target.Add(new AvaloniaRun(code[start..i]) { Foreground = s_number });
                continue;
            }

            // 标识符 / 关键字
            if (char.IsLetter(c) || c == '_' || c == '@')
            {
                FlushBuffer(buf, s_plain, target);
                int start = i;
                while (i < len && (char.IsLetterOrDigit(code[i]) || code[i] == '_'))
                    i++;
                var word = code[start..i];
                var lookup = lang == "sql" ? word.ToLowerInvariant() : word;

                if (keywords.Contains(lookup))
                    target.Add(new AvaloniaRun(word) { Foreground = s_keyword });
                else if (word.Length > 0 && char.IsUpper(word[0]) && lang is "csharp")
                    target.Add(new AvaloniaRun(word) { Foreground = s_type });
                else
                    target.Add(new AvaloniaRun(word) { Foreground = s_plain });
                continue;
            }

            buf.Append(c);
            i++;
        }

        FlushBuffer(buf, s_plain, target);
    }

    private static int ReadString(string code, int start, char quote, InlineCollection target)
    {
        int i = start + 1;
        while (i < code.Length)
        {
            if (code[i] == '\\' && i + 1 < code.Length) { i += 2; continue; }
            if (code[i] == quote) { i++; break; }
            i++;
        }
        target.Add(new AvaloniaRun(code[start..i]) { Foreground = s_string });
        return i;
    }

    // ── JSON 高亮 ──

    private static void HighlightJson(string code, InlineCollection target)
    {
        int i = 0;
        int len = code.Length;
        var buf = new System.Text.StringBuilder();

        while (i < len)
        {
            char c = code[i];

            if (c == '"')
            {
                FlushBuffer(buf, s_punctuation, target);
                int start = i;
                i++;
                while (i < len && code[i] != '"')
                {
                    if (code[i] == '\\' && i + 1 < len) i++;
                    i++;
                }
                if (i < len) i++; // closing "

                var str = code[start..i];
                // 判断是 key 还是 value：key 后面跟 :
                int j = i;
                while (j < len && char.IsWhiteSpace(code[j])) j++;
                var brush = (j < len && code[j] == ':') ? s_jsonKey : s_string;
                target.Add(new AvaloniaRun(str) { Foreground = brush });
                continue;
            }

            if (char.IsDigit(c) || (c == '-' && i + 1 < len && char.IsDigit(code[i + 1])))
            {
                FlushBuffer(buf, s_punctuation, target);
                int start = i;
                if (c == '-') i++;
                while (i < len && (char.IsDigit(code[i]) || code[i] == '.' || code[i] == 'e' || code[i] == 'E'))
                    i++;
                target.Add(new AvaloniaRun(code[start..i]) { Foreground = s_number });
                continue;
            }

            if (MatchWord(code, i, "true") || MatchWord(code, i, "false") || MatchWord(code, i, "null"))
            {
                FlushBuffer(buf, s_punctuation, target);
                int wlen = code[i] == 'n' ? 4 : (code[i] == 't' ? 4 : 5);
                target.Add(new AvaloniaRun(code[i..(i + wlen)]) { Foreground = s_keyword });
                i += wlen;
                continue;
            }

            buf.Append(c);
            i++;
        }

        FlushBuffer(buf, s_punctuation, target);
    }

    // ── XML/HTML 高亮 ──

    private static void HighlightXml(string code, InlineCollection target)
    {
        int i = 0;
        int len = code.Length;
        var buf = new System.Text.StringBuilder();

        while (i < len)
        {
            // 注释 <!-- ... -->
            if (MatchAt(code, i, "<!--"))
            {
                FlushBuffer(buf, s_plain, target);
                int end = code.IndexOf("-->", i + 4, StringComparison.Ordinal);
                if (end < 0) end = len - 3;
                target.Add(new AvaloniaRun(code[i..(end + 3)]) { Foreground = s_comment });
                i = end + 3;
                continue;
            }

            // 标签
            if (code[i] == '<')
            {
                FlushBuffer(buf, s_plain, target);
                int start = i;
                i++;
                if (i < len && code[i] == '/') i++;

                // 标签名
                int nameStart = i;
                while (i < len && !char.IsWhiteSpace(code[i]) && code[i] != '>' && code[i] != '/')
                    i++;
                if (nameStart < i)
                {
                    target.Add(new AvaloniaRun(code[start..nameStart]) { Foreground = s_punctuation });
                    target.Add(new AvaloniaRun(code[nameStart..i]) { Foreground = s_xmlTag });
                }
                else
                {
                    target.Add(new AvaloniaRun(code[start..i]) { Foreground = s_punctuation });
                }

                // 属性
                while (i < len && code[i] != '>')
                {
                    if (char.IsLetter(code[i]) || code[i] == ':' || code[i] == '_')
                    {
                        int aStart = i;
                        while (i < len && code[i] != '=' && code[i] != '>' && !char.IsWhiteSpace(code[i]))
                            i++;
                        target.Add(new AvaloniaRun(code[aStart..i]) { Foreground = s_xmlAttr });
                    }
                    else if (code[i] == '"')
                    {
                        i = ReadString(code, i, '"', target);
                        continue;
                    }
                    else
                    {
                        target.Add(new AvaloniaRun(code[i].ToString()) { Foreground = s_punctuation });
                        i++;
                    }
                }
                if (i < len && code[i] == '>')
                {
                    target.Add(new AvaloniaRun(">") { Foreground = s_punctuation });
                    i++;
                }
                continue;
            }

            buf.Append(code[i]);
            i++;
        }

        FlushBuffer(buf, s_plain, target);
    }

    // ── 辅助方法 ──

    private static void FlushBuffer(System.Text.StringBuilder buf, SolidColorBrush brush, InlineCollection target)
    {
        if (buf.Length == 0) return;
        target.Add(new AvaloniaRun(buf.ToString()) { Foreground = brush });
        buf.Clear();
    }

    private static bool MatchAt(string s, int i, string sub)
    {
        if (i + sub.Length > s.Length) return false;
        for (int k = 0; k < sub.Length; k++)
            if (s[i + k] != sub[k]) return false;
        return true;
    }

    private static bool MatchWord(string s, int i, string word)
    {
        if (!MatchAt(s, i, word)) return false;
        int end = i + word.Length;
        return end >= s.Length || !char.IsLetterOrDigit(s[end]);
    }
}
