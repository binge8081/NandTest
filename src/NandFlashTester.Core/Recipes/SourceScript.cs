using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace NandFlashTester.Core.Recipes;

/// <summary>
/// 命令文本改编自用户于 2026-11-08 授权复用的脚本编辑源码 NFA/src/MainClass.java。
/// 压缩包内没有单独的许可证文件。bin 下的 class 未执行、未入库。
/// 这里只解析文本并生成主机计划。原始命令字节、特征、状态、忙时间和 ECC 不形成发送载荷。
/// </summary>
public static class SourceScript
{
    private static readonly HashSet<string> Mnemonics = new(StringComparer.Ordinal)
    {
        "REPEAT", "WAITMS", "WUNTIL", "SWRSET", "SWWAIT", "TSTAMP", "SETBUF",
        "VSET01", "VADD01", "VMUL01", "VSET02", "VADD02", "VMUL02",
        "VSET04", "VADD04", "VMUL04", "VSET08", "VADD08", "VMUL08",
        "CNDSTP", "FNSCRT", "FNRSLT", "FNDUMP", "FNCOMP", "FNECNT",
        "BKERAS", "PGPROG", "BKPROG", "PGREAD", "BKREAD", "PGRDCP", "BKRDCP",
        "PGCOMP", "PGRDEC", "BKRDEC", "PGRDDM", "PGDUMP", "BKRDDM",
        "CMND00", "CMND01", "CMND02", "CMND03", "CMND04",
        "CMND11", "CMND12", "CMND13", "CMND14",
        "CMND21", "CMND22", "CMND23", "CMND24",
        "CMND31", "CMND32", "CMND33", "CMND34",
        "CMND41", "CMND42", "CMND43", "CMND44",
        "SNDAD0", "SNDAD1", "SNDAD2", "SNDAD3", "SNDAD4", "SNDAD5",
        "SETFTR", "GETDAT", "SETDT1", "SETDT2", "SETDT3", "SETDT4", "SETDAT",
        "STATUS", "BUSYTM", "ECCANA",
        "BKERMC", "PGPGMC", "BKPGMC", "BEMCP2", "BEMCP4", "PPMCP2", "PPMCP4", "BPMCP2", "BPMCP4"
    };

    private static readonly HashSet<string> Withheld = new(StringComparer.Ordinal)
    {
        "SETBUF", "FNSCRT", "FNRSLT", "FNDUMP", "FNCOMP", "FNECNT",
        "CMND00", "CMND01", "CMND02", "CMND03", "CMND04",
        "CMND11", "CMND12", "CMND13", "CMND14",
        "CMND21", "CMND22", "CMND23", "CMND24",
        "CMND31", "CMND32", "CMND33", "CMND34",
        "CMND41", "CMND42", "CMND43", "CMND44",
        "SNDAD0", "SNDAD1", "SNDAD2", "SNDAD3", "SNDAD4", "SNDAD5",
        "SETFTR", "GETDAT", "SETDT1", "SETDT2", "SETDT3", "SETDT4", "SETDAT",
        "STATUS", "BUSYTM", "ECCANA",
        "BKERMC", "PGPGMC", "BKPGMC", "BEMCP2", "BEMCP4", "PPMCP2", "PPMCP4", "BPMCP2", "BPMCP4",
        "TSTAMP", "SWRSET", "SWWAIT", "WUNTIL"
    };

    public static bool LooksLike(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        foreach (var raw in text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '\'')
            {
                continue;
            }

            var token = line.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0].ToUpperInvariant();
            if (Mnemonics.Contains(token))
            {
                return true;
            }
        }

        return false;
    }

    public static ScriptCompileResult Compile(string text)
    {
        var diagnostics = new List<ScriptDiagnostic>();
        if (Encoding.UTF8.GetByteCount(text) > ScriptCompiler.MaxBytes)
        {
            return ScriptCompileResult.Reject([At(1, 1, "脚本超过 16 KiB，未执行。")]);
        }

        var lines = new List<CodeLine>();
        var rows = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        for (var i = 0; i < rows.Length; i++)
        {
            var raw = rows[i];
            var column = 1;
            var cursor = 0;
            while (cursor < raw.Length && raw[cursor] is ' ' or '\t')
            {
                column++;
                cursor++;
            }

            if (cursor >= raw.Length || raw[cursor] == '\'')
            {
                continue;
            }

            var tokens = new List<Tok>();
            while (cursor < raw.Length)
            {
                while (cursor < raw.Length && raw[cursor] is ' ' or '\t')
                {
                    column++;
                    cursor++;
                }

                if (cursor >= raw.Length)
                {
                    break;
                }

                var start = column;
                var begin = cursor;
                while (cursor < raw.Length && raw[cursor] is not (' ' or '\t'))
                {
                    column++;
                    cursor++;
                }

                tokens.Add(new Tok(raw[begin..cursor], start));
            }

            if (tokens.Count > 0)
            {
                lines.Add(new CodeLine(i + 1, tokens));
            }
        }

        if (lines.Count == 0)
        {
            return ScriptCompileResult.Reject([At(1, 1, "脚本没有命令，未执行。")]);
        }

        var vars = new uint[16];
        var actions = new List<ScriptAction>();
        var address = new Cursor();
        uint seed = 1;
        int? stopAbove = null;
        Expand(lines, 0, lines.Count, 0, vars, address, actions, diagnostics, ref seed, ref stopAbove);
        if (diagnostics.Count > 0)
        {
            return ScriptCompileResult.Reject(diagnostics);
        }

        if (actions.Count == 0)
        {
            return ScriptCompileResult.Reject([At(lines[0].Number, lines[0].Tokens[0].Column, "没有可计划的步骤，未执行。")]);
        }

        var numbered = actions.Select((action, index) => action with { Index = index }).ToArray();
        return new ScriptCompileResult(true, new ScriptPlan(
            "源脚本",
            seed,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))),
            numbered.Count(action => action.Op == "erase"),
            numbered.Count(action => action.Op == "program"),
            numbered.Count(action => action.Op == "read"),
            numbered.Count(action => action.Op == "compare"),
            false,
            "planned",
            numbered,
            stopAbove), []);
    }

    private static int Expand(List<CodeLine> lines, int start, int count, int depth, uint[] vars, Cursor cursor, List<ScriptAction> actions, List<ScriptDiagnostic> diagnostics, ref uint seed, ref int? stopAbove)
    {
        var index = start;
        var consumed = 0;
        while (consumed < count && index < lines.Count && diagnostics.Count == 0)
        {
            var line = lines[index];
            var op = line.Tokens[0].Text.ToUpperInvariant();
            if (!Mnemonics.Contains(op) && !op.StartsWith("CMND", StringComparison.Ordinal) && !op.StartsWith("SNDAD", StringComparison.Ordinal))
            {
                diagnostics.Add(At(line.Number, line.Tokens[0].Column, $"语句 {op} 不在已授权的命令文本里，未执行。"));
                return consumed;
            }

            if (op == "REPEAT")
            {
                if (line.Tokens.Count != 3 || !Hex(line.Tokens[1].Text, out var bodyCount) || !Hex(line.Tokens[2].Text, out var times))
                {
                    diagnostics.Add(At(line.Number, line.Tokens[0].Column, "REPEAT 需要两个十六进制数：后续行数和次数，未执行。"));
                    return consumed;
                }

                if (bodyCount is < 1 or > ScriptCompiler.MaxRepeat || times is < 1 or > ScriptCompiler.MaxRepeat)
                {
                    diagnostics.Add(At(line.Number, line.Tokens[0].Column, $"REPEAT 的行数和次数都必须在 1 到 {ScriptCompiler.MaxRepeat} 之间，未执行。"));
                    return consumed;
                }

                if (depth + 1 > ScriptCompiler.MaxDepth)
                {
                    diagnostics.Add(At(line.Number, line.Tokens[0].Column, $"循环嵌套超过 {ScriptCompiler.MaxDepth} 层，未执行。"));
                    return consumed;
                }

                if (index + 1 + (int)bodyCount > lines.Count)
                {
                    diagnostics.Add(At(line.Number, line.Tokens[0].Column, "REPEAT 后面没有足够的命令，未执行。"));
                    return consumed;
                }

                for (var time = 0; time < times; time++)
                {
                    Expand(lines, index + 1, (int)bodyCount, depth + 1, vars, cursor, actions, diagnostics, ref seed, ref stopAbove);
                    if (diagnostics.Count > 0 || actions.Count > ScriptCompiler.MaxExpanded)
                    {
                        if (actions.Count > ScriptCompiler.MaxExpanded && diagnostics.Count == 0)
                        {
                            diagnostics.Add(At(line.Number, line.Tokens[0].Column, $"展开后的步骤超过 {ScriptCompiler.MaxExpanded}，未执行。"));
                        }

                        return consumed;
                    }
                }

                index += 1 + (int)bodyCount;
                consumed += 1 + (int)bodyCount;
                continue;
            }

            Emit(line, op, vars, cursor, actions, diagnostics, ref seed, ref stopAbove);
            if (actions.Count > ScriptCompiler.MaxExpanded)
            {
                diagnostics.Add(At(line.Number, line.Tokens[0].Column, $"展开后的步骤超过 {ScriptCompiler.MaxExpanded}，未执行。"));
                return consumed;
            }

            index++;
            consumed++;
        }

        return consumed;
    }

    private static void Emit(CodeLine line, string op, uint[] vars, Cursor cursor, List<ScriptAction> actions, List<ScriptDiagnostic> diagnostics, ref uint seed, ref int? stopAbove)
    {
        if (op is "VSET01" or "VADD01" or "VMUL01" or "VSET02" or "VADD02" or "VMUL02" or "VSET04" or "VADD04" or "VMUL04" or "VSET08" or "VADD08" or "VMUL08")
        {
            if (line.Tokens.Count != 3 || !Hex(line.Tokens[1].Text, out var slot) || !Hex(line.Tokens[2].Text, out var value) || slot > 15)
            {
                diagnostics.Add(At(line.Number, line.Tokens[0].Column, "变量命令需要 0 到 15 的编号和十六进制值，未执行。"));
                return;
            }

            var mask = op[^2..] switch
            {
                "01" => 0xFu,
                "02" => 0xFFu,
                "04" => 0xFFFFu,
                _ => 0xFFFFFFFFu
            };
            var current = vars[slot];
            vars[slot] = op.StartsWith("VSET", StringComparison.Ordinal) ? value & mask
                : op.StartsWith("VADD", StringComparison.Ordinal) ? (current + value) & mask
                : (current * value) & mask;
            return;
        }

        if (op == "CNDSTP")
        {
            if (line.Tokens.Count < 2 || !Hex(line.Tokens[1].Text, out var code))
            {
                diagnostics.Add(At(line.Number, line.Tokens[0].Column, "CNDSTP 缺少条件码，未执行。"));
                return;
            }

            if (code == 0x22)
            {
                if (line.Tokens.Count != 3 || !Hex(line.Tokens[2].Text, out var threshold) || threshold > int.MaxValue)
                {
                    diagnostics.Add(At(line.Number, line.Tokens[0].Column, "CNDSTP 0022 需要位错误上限，未执行。"));
                    return;
                }

                stopAbove = (int)threshold;
            }

            actions.Add(new ScriptAction(actions.Count, line.Number, "withheld", 0, null, false, "planned"));
            return;
        }

        if (op == "WAITMS")
        {
            if (line.Tokens.Count != 2 || !Hex(line.Tokens[1].Text, out var ms) || ms > 2000)
            {
                diagnostics.Add(At(line.Number, line.Tokens[0].Column, "WAITMS 只接受不超过 2000 毫秒的十六进制时间，未执行。"));
                return;
            }

            actions.Add(new ScriptAction(actions.Count, line.Number, "wait", 0, null, false, "planned"));
            return;
        }

        if (Withheld.Contains(op) || op.StartsWith("CMND", StringComparison.Ordinal) || op.StartsWith("SNDAD", StringComparison.Ordinal))
        {
            actions.Add(new ScriptAction(actions.Count, line.Number, "withheld", 0, null, false, "planned"));
            return;
        }

        uint? pattern = PatternSeed(line, vars, diagnostics, ref seed);
        if (diagnostics.Count > 0)
        {
            return;
        }

        switch (op)
        {
            case "BKERAS":
                actions.Add(Media(line, "erase", vars, cursor, diagnostics, pattern, page: false));
                break;
            case "BKPROG":
            case "BKREAD":
            case "BKRDDM":
                actions.Add(Media(line, op == "BKPROG" ? "program" : "read", vars, cursor, diagnostics, pattern, page: false));
                break;
            case "BKRDEC":
                actions.Add(Media(line, "read", vars, cursor, diagnostics, pattern, page: false));
                actions.Add(Media(line, "compare", vars, cursor, diagnostics, pattern, page: false));
                break;
            case "PGPROG":
                actions.Add(Media(line, "program", vars, cursor, diagnostics, pattern, page: true));
                break;
            case "PGREAD":
            case "PGRDDM":
                actions.Add(Media(line, "read", vars, cursor, diagnostics, pattern, page: true));
                break;
            case "PGRDCP":
            case "PGRDEC":
                actions.Add(Media(line, "read", vars, cursor, diagnostics, pattern, page: true));
                actions.Add(Media(line, "compare", vars, cursor, diagnostics, pattern, page: true));
                break;
            case "PGCOMP":
                actions.Add(new ScriptAction(actions.Count, line.Number, "compare", cursor.Block, cursor.Page < 0 ? 0 : cursor.Page, false, "planned", pattern));
                break;
            case "PGDUMP":
                actions.Add(new ScriptAction(actions.Count, line.Number, "read", cursor.Block, cursor.Page < 0 ? 0 : cursor.Page, false, "planned", pattern));
                break;
            case "BKRDCP":
                actions.Add(Media(line, "read", vars, cursor, diagnostics, pattern, page: false));
                actions.Add(Media(line, "compare", vars, cursor, diagnostics, pattern, page: false));
                break;
            default:
                diagnostics.Add(At(line.Number, line.Tokens[0].Column, $"语句 {op} 不能变成主机计划，未执行。"));
                break;
        }
    }

    private static ScriptAction Media(CodeLine line, string op, uint[] vars, Cursor cursor, List<ScriptDiagnostic> diagnostics, uint? pattern, bool page)
    {
        if (diagnostics.Count > 0 || line.Tokens.Count < (page ? 4 : 3))
        {
            if (diagnostics.Count == 0)
            {
                diagnostics.Add(At(line.Number, line.Tokens[0].Column, "命令缺少块或页，未执行。"));
            }

            return new ScriptAction(0, line.Number, op, 0, page ? 0 : null, false, "planned", pattern);
        }

        if (!Value(line.Tokens[2], vars, out var block) || block > 65535 || (page && (!Value(line.Tokens[3], vars, out var pageNo) || pageNo > 65535)))
        {
            diagnostics.Add(At(line.Number, line.Tokens[0].Column, "块号或页号不是 0 到 65535 的十六进制数或变量，未执行。"));
            return new ScriptAction(0, line.Number, op, 0, page ? 0 : null, false, "planned", pattern);
        }

        int? pageValue = page && Value(line.Tokens[3], vars, out var resolved) ? (int)resolved : page ? 0 : null;
        cursor.Block = (int)block;
        if (pageValue is int numbered)
        {
            cursor.Page = numbered;
        }

        return new ScriptAction(0, line.Number, op, (int)block, pageValue, false, "planned", pattern);
    }

    private static uint? PatternSeed(CodeLine line, uint[] vars, List<ScriptDiagnostic> diagnostics, ref uint seed)
    {
        var marker = line.Tokens.FindIndex(token => token.Text.Equals("RANDOM", StringComparison.OrdinalIgnoreCase) || token.Text.Equals("INCRMT", StringComparison.OrdinalIgnoreCase) || token.Text.Equals("BUFFER", StringComparison.OrdinalIgnoreCase));
        if (marker < 0)
        {
            return null;
        }

        var kind = line.Tokens[marker].Text.ToUpperInvariant();
        if (kind == "BUFFER")
        {
            diagnostics.Add(At(line.Number, line.Tokens[marker].Column, "BUFFER 图案不读取外部文件，未执行。"));
            return null;
        }

        if (marker + 1 >= line.Tokens.Count || !Value(line.Tokens[marker + 1], vars, out var value))
        {
            diagnostics.Add(At(line.Number, line.Tokens[marker].Column, "图案缺少十六进制参数，未执行。"));
            return null;
        }

        seed = value == 0 ? 1u : value;
        return seed;
    }

    private static bool Value(Tok token, uint[] vars, out uint value)
    {
        var text = token.Text;
        if (text.Length == 3 && (text[0] == 'V' || text[0] == 'v') && Hex(text[1..], out var slot) && slot < 16)
        {
            value = vars[slot];
            return true;
        }

        return Hex(text, out value);
    }

    private static bool Hex(string text, out uint value)
    {
        return uint.TryParse(text, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out value);
    }

    private static ScriptDiagnostic At(int line, int column, string message) =>
        new(line, column, $"第 {line} 行第 {column} 列：{message}");

    private sealed class Cursor
    {
        public int Block { get; set; }
        public int Page { get; set; } = -1;
    }

    private sealed record CodeLine(int Number, List<Tok> Tokens);

    private sealed record Tok(string Text, int Column);
}
