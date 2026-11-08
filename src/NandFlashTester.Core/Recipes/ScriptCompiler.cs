using System.Security.Cryptography;
using System.Text;

namespace NandFlashTester.Core.Recipes;

public sealed record ScriptDiagnostic(int Line, int Column, string Message);

public sealed record ScriptAction(
    int Index,
    int Line,
    string Op,
    int Block,
    int? Page,
    bool HardwareExecuted,
    string State,
    uint? PatternSeed = null);

public sealed record ScriptPlan(
    string Name,
    uint Seed,
    string Sha256,
    int EraseCount,
    int ProgramCount,
    int ReadCount,
    int CompareCount,
    bool HardwareExecuted,
    string ExecutionState,
    IReadOnlyList<ScriptAction> Actions,
    int? StopWhenBitErrorsAbove = null,
    string? CountStopRule = null,
    int CountStopThreshold = 0);

public sealed record ScriptCompileResult(bool Ok, ScriptPlan? Plan, IReadOnlyList<ScriptDiagnostic> Diagnostics)
{
    public static ScriptCompileResult Reject(IReadOnlyList<ScriptDiagnostic> diagnostics) => new(false, null, diagnostics);
}

public static class ScriptExamples
{
    public const string PageLoop =
        """
        name 页编程比较
        seed 1
        var block = 0
        var pages = 2
        erase block
        loop pages
          program block i
          read block i
          compare block i
        end
        """;

    public const string Notes =
        "自定义脚本由主机展开。var 保存整数，loop 次数必须在 1 到 32，嵌套最多 2 层。i 是本层循环序号。if 只在计划时选择分支。erase、program、read、compare 只进入主机计划，没有硬件时不发送。";
}

public static class ScriptCompiler
{
    public const int MaxBytes = 16 * 1024;
    public const int MaxRepeat = 32;
    public const int MaxDepth = 2;
    public const int MaxExpanded = 256;

    private static readonly HashSet<string> Forbidden = new(StringComparer.OrdinalIgnoreCase)
    {
        "script", "exec", "execute", "command", "shell", "python", "eval", "import", "function", "cmd", "powershell", "bash"
    };

    public static ScriptCompileResult Compile(string? text)
    {
        if (SourceScript.LooksLike(text))
        {
            return SourceScript.Compile(text!);
        }

        var diagnostics = new List<ScriptDiagnostic>();
        if (string.IsNullOrEmpty(text))
        {
            return ScriptCompileResult.Reject([At(1, 1, "脚本为空，未执行。")]);
        }

        if (text.Contains('\0'))
        {
            return ScriptCompileResult.Reject([At(1, 1, "脚本含有空字节，未执行。")]);
        }

        if (text[0] == '\uFEFF')
        {
            return ScriptCompileResult.Reject([At(1, 1, "脚本含有 BOM，未执行。")]);
        }

        if (Encoding.UTF8.GetByteCount(text) > MaxBytes)
        {
            return ScriptCompileResult.Reject([At(1, 1, "脚本超过 16 KiB，未执行。")]);
        }

        var lines = Split(text);
        var parser = new Parser(lines, diagnostics);
        var statements = parser.ParseBlock(1, 1, stopAtElse: false, top: true);
        if (diagnostics.Count > 0)
        {
            return ScriptCompileResult.Reject(diagnostics);
        }

        string? name = null;
        uint? seed = null;
        var body = new List<Stmt>();
        foreach (var statement in statements)
        {
            if (statement is NameStmt named)
            {
                if (name is not null)
                {
                    diagnostics.Add(At(named.Line, named.Column, "name 重复，未执行。"));
                }
                else if (!ValidName(named.Value))
                {
                    diagnostics.Add(At(named.Line, named.Column, "名称含有不允许的字符或长度，未执行。"));
                }
                else
                {
                    name = named.Value;
                }
            }
            else if (statement is SeedStmt seeded)
            {
                if (seed is not null)
                {
                    diagnostics.Add(At(seeded.Line, seeded.Column, "seed 重复，未执行。"));
                }
                else
                {
                    seed = seeded.Value;
                }
            }
            else
            {
                body.Add(statement);
            }
        }

        if (name is null)
        {
            diagnostics.Add(At(1, 1, "缺少 name，未执行。"));
        }

        if (seed is null)
        {
            diagnostics.Add(At(1, 1, "缺少 seed，未执行。"));
        }

        var actions = new List<ScriptAction>();
        if (diagnostics.Count == 0)
        {
            Expand(body, new Dictionary<string, int>(StringComparer.Ordinal), new HashSet<string>(StringComparer.Ordinal), 0, new HashSet<(int Block, int Page)>(), actions, diagnostics);
        }

        if (diagnostics.Count == 0 && actions.Count == 0)
        {
            diagnostics.Add(At(1, 1, "没有可计划的擦写读比较，未执行。"));
        }

        if (diagnostics.Count > 0 || name is null || seed is null)
        {
            return ScriptCompileResult.Reject(diagnostics);
        }

        return new ScriptCompileResult(true, new ScriptPlan(
            name,
            seed.Value,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))),
            actions.Count(action => action.Op == "erase"),
            actions.Count(action => action.Op == "program"),
            actions.Count(action => action.Op == "read"),
            actions.Count(action => action.Op == "compare"),
            false,
            "planned",
            actions), []);
    }

    private static void Expand(
        IReadOnlyList<Stmt> statements,
        Dictionary<string, int> env,
        HashSet<string> declared,
        int depth,
        HashSet<(int Block, int Page)> read,
        List<ScriptAction> actions,
        List<ScriptDiagnostic> diagnostics)
    {
        foreach (var statement in statements)
        {
            if (diagnostics.Count > 0)
            {
                return;
            }

            switch (statement)
            {
                case VarStmt variable:
                    if (variable.Name == "i")
                    {
                        diagnostics.Add(At(variable.Line, variable.Column, "i 是循环序号，不能再声明。"));
                        return;
                    }

                    if (!declared.Add(variable.Name))
                    {
                        diagnostics.Add(At(variable.Line, variable.Column, $"变量 {variable.Name} 已经声明。"));
                        return;
                    }

                    env[variable.Name] = Eval(variable.Value, env, diagnostics);
                    break;
                case LoopStmt loop:
                    var count = Eval(loop.Count, env, diagnostics);
                    if (diagnostics.Count > 0)
                    {
                        return;
                    }

                    if (count is < 1 or > MaxRepeat)
                    {
                        diagnostics.Add(At(loop.Line, loop.Column, $"循环次数必须在 1 到 {MaxRepeat} 之间，未执行。"));
                        return;
                    }

                    if (depth + 1 > MaxDepth)
                    {
                        diagnostics.Add(At(loop.Line, loop.Column, $"循环嵌套超过 {MaxDepth} 层，未执行。"));
                        return;
                    }

                    if (loop.Body.Count == 0)
                    {
                        diagnostics.Add(At(loop.Line, loop.Column, "循环体为空，未执行。"));
                        return;
                    }

                    for (var index = 0; index < count; index++)
                    {
                        var childEnv = new Dictionary<string, int>(env, StringComparer.Ordinal) { ["i"] = index };
                        var childDeclared = new HashSet<string>(declared, StringComparer.Ordinal) { "i" };
                        var childRead = new HashSet<(int Block, int Page)>(read);
                        Expand(loop.Body, childEnv, childDeclared, depth + 1, childRead, actions, diagnostics);
                        if (diagnostics.Count > 0)
                        {
                            return;
                        }
                    }

                    break;
                case IfStmt branch:
                    var left = Eval(branch.Left, env, diagnostics);
                    var right = Eval(branch.Right, env, diagnostics);
                    if (diagnostics.Count > 0)
                    {
                        return;
                    }

                    Expand(left == right ? branch.Then : branch.Else, env, declared, depth, read, actions, diagnostics);
                    break;
                case OpStmt op:
                    var block = Eval(op.Block, env, diagnostics);
                    int? page = null;
                    if (op.Page is not null)
                    {
                        page = Eval(op.Page, env, diagnostics);
                    }

                    if (diagnostics.Count > 0)
                    {
                        return;
                    }

                    if (block is < 0 or > 65535)
                    {
                        diagnostics.Add(At(op.Line, op.Column, "块号超出 0 到 65535，未执行。"));
                        return;
                    }

                    if (page is < 0 or > 65535)
                    {
                        diagnostics.Add(At(op.Line, op.Column, "页号超出 0 到 65535，未执行。"));
                        return;
                    }

                    if (op.Op == "erase")
                    {
                        read.RemoveWhere(item => item.Block == block);
                    }
                    else if (op.Op == "read")
                    {
                        read.Add((block, page!.Value));
                    }
                    else if (op.Op == "compare" && !read.Contains((block, page!.Value)))
                    {
                        diagnostics.Add(At(op.Line, op.Column, $"块 {block} 页 {page} 的比较前面没有同页读回，未执行。"));
                        return;
                    }

                    actions.Add(new ScriptAction(actions.Count, op.Line, op.Op, block, page, false, "planned"));
                    if (actions.Count > MaxExpanded)
                    {
                        diagnostics.Add(At(op.Line, op.Column, $"展开后的步骤超过 {MaxExpanded}，未执行。"));
                        return;
                    }

                    break;
            }
        }
    }

    private static int Eval(Expr expr, Dictionary<string, int> env, List<ScriptDiagnostic> diagnostics)
    {
        if (expr.Literal is int literal)
        {
            return literal;
        }

        if (env.TryGetValue(expr.Name!, out var value))
        {
            return value;
        }

        diagnostics.Add(At(expr.Line, expr.Column, $"变量 {expr.Name} 还没有赋值。"));
        return 0;
    }

    private static List<SourceLine> Split(string text)
    {
        var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var rows = normalized.Split('\n');
        var lines = new List<SourceLine>();
        for (var index = 0; index < rows.Length; index++)
        {
            var raw = rows[index];
            var comment = raw.IndexOf('#');
            if (comment >= 0)
            {
                raw = raw[..comment];
            }

            var column = 1;
            var cursor = 0;
            while (cursor < raw.Length && raw[cursor] is ' ' or '\t')
            {
                column++;
                cursor++;
            }

            if (cursor >= raw.Length)
            {
                continue;
            }

            var tokens = new List<Token>();
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

                tokens.Add(new Token(raw[begin..cursor], start));
            }

            if (tokens.Count > 0)
            {
                lines.Add(new SourceLine(index + 1, tokens));
            }
        }

        return lines;
    }

    private static ScriptDiagnostic At(int line, int column, string message) =>
        new(line, column, $"第 {line} 行第 {column} 列：{message}");

    private static bool ValidName(string name)
    {
        if (name.Length is < 1 or > 64)
        {
            return false;
        }

        foreach (var ch in name)
        {
            if (!char.IsLetterOrDigit(ch) && ch is not ('_' or ' ' or '-'))
            {
                return false;
            }
        }

        return true;
    }

    private sealed class Parser(List<SourceLine> lines, List<ScriptDiagnostic> diagnostics)
    {
        private int _index;

        public List<Stmt> ParseBlock(int openerLine, int openerColumn, bool stopAtElse, bool top = false)
        {
            var statements = new List<Stmt>();
            while (_index < lines.Count)
            {
                var line = lines[_index];
                var key = line.Tokens[0];
                if (key.Text == "end")
                {
                    _index++;
                    if (top)
                    {
                        diagnostics.Add(At(line.Number, key.Column, "end 没有对应的块，未执行。"));
                        return statements;
                    }

                    return statements;
                }

                if (key.Text == "else")
                {
                    if (!stopAtElse)
                    {
                        diagnostics.Add(At(line.Number, key.Column, "else 没有对应的 if，未执行。"));
                        _index++;
                    }

                    return statements;
                }

                _index++;
                statements.Add(ParseStatement(line));
                if (diagnostics.Count > 0)
                {
                    return statements;
                }
            }

            if (!top)
            {
                diagnostics.Add(At(openerLine, openerColumn, "块没有 end，未执行。"));
            }

            return statements;
        }

        private Stmt ParseStatement(SourceLine line)
        {
            var key = line.Tokens[0];
            if (Forbidden.Contains(key.Text))
            {
                diagnostics.Add(At(line.Number, key.Column, "这条语句不能执行，未执行。"));
                return new NopStmt(line.Number, key.Column);
            }

            switch (key.Text)
            {
                case "name":
                    if (line.Tokens.Count < 2)
                    {
                        diagnostics.Add(At(line.Number, key.Column, "name 缺少名称，未执行。"));
                        return new NopStmt(line.Number, key.Column);
                    }

                    return new NameStmt(line.Number, key.Column, string.Join(' ', line.Tokens.Skip(1).Select(token => token.Text)));
                case "seed":
                    if (line.Tokens.Count != 2 || !uint.TryParse(line.Tokens[1].Text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var seed))
                    {
                        diagnostics.Add(At(line.Number, key.Column, "seed 必须是 0 到 4294967295 的整数，未执行。"));
                        return new NopStmt(line.Number, key.Column);
                    }

                    return new SeedStmt(line.Number, key.Column, seed);
                case "var":
                    if (line.Tokens.Count != 4 || line.Tokens[2].Text != "=" || !ValidIdent(line.Tokens[1].Text))
                    {
                        diagnostics.Add(At(line.Number, key.Column, "var 的写法是 var 名称 = 整数或变量，未执行。"));
                        return new NopStmt(line.Number, key.Column);
                    }

                    return new VarStmt(line.Number, key.Column, line.Tokens[1].Text, ParseExpr(line, line.Tokens[3]));
                case "loop":
                    if (line.Tokens.Count != 2)
                    {
                        diagnostics.Add(At(line.Number, key.Column, "loop 只能带一个次数，未执行。"));
                        return new NopStmt(line.Number, key.Column);
                    }

                    var count = ParseExpr(line, line.Tokens[1]);
                    var loopBody = ParseBlock(line.Number, key.Column, false);
                    return new LoopStmt(line.Number, key.Column, count, loopBody);
                case "if":
                    if (line.Tokens.Count != 4 || line.Tokens[2].Text != "==")
                    {
                        diagnostics.Add(At(line.Number, key.Column, "if 的写法是 if 左值 == 右值，未执行。"));
                        return new NopStmt(line.Number, key.Column);
                    }

                    var left = ParseExpr(line, line.Tokens[1]);
                    var right = ParseExpr(line, line.Tokens[3]);
                    var then = ParseBlock(line.Number, key.Column, true);
                    var elseBody = new List<Stmt>();
                    if (_index < lines.Count && lines[_index].Tokens[0].Text == "else")
                    {
                        var elseLine = lines[_index];
                        _index++;
                        elseBody = ParseBlock(elseLine.Number, elseLine.Tokens[0].Column, false);
                    }

                    return new IfStmt(line.Number, key.Column, left, right, then, elseBody);
                case "erase":
                    return ParseOp(line, "erase", withPage: false);
                case "program":
                case "read":
                case "compare":
                    return ParseOp(line, key.Text, withPage: true);
                default:
                    diagnostics.Add(At(line.Number, key.Column, $"语句 {key.Text} 不在自定义脚本里，未执行。"));
                    return new NopStmt(line.Number, key.Column);
            }
        }

        private OpStmt ParseOp(SourceLine line, string op, bool withPage)
        {
            var expected = withPage ? 3 : 2;
            if (line.Tokens.Count != expected)
            {
                diagnostics.Add(At(line.Number, line.Tokens[0].Column, withPage ? $"{op} 需要块和页，未执行。" : "erase 只写块，未执行。"));
                return new OpStmt(line.Number, line.Tokens[0].Column, op, new Expr(line.Number, line.Tokens[0].Column, 0, null), null);
            }

            var block = ParseExpr(line, line.Tokens[1]);
            Expr? page = withPage ? ParseExpr(line, line.Tokens[2]) : null;
            return new OpStmt(line.Number, line.Tokens[0].Column, op, block, page);
        }

        private Expr ParseExpr(SourceLine line, Token token)
        {
            if (int.TryParse(token.Text, System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out var number) && !token.Text.Contains('+'))
            {
                return new Expr(line.Number, token.Column, number, null);
            }

            if (!ValidIdent(token.Text))
            {
                diagnostics.Add(At(line.Number, token.Column, $"表达式 {token.Text} 不是整数或变量，未执行。"));
                return new Expr(line.Number, token.Column, 0, null);
            }

            return new Expr(line.Number, token.Column, null, token.Text);
        }

        private static bool ValidIdent(string text)
        {
            if (text.Length is < 1 or > 32 || !(char.IsLetter(text[0]) || text[0] == '_'))
            {
                return false;
            }

            return text.All(ch => char.IsLetterOrDigit(ch) || ch == '_');
        }
    }

    private sealed record SourceLine(int Number, List<Token> Tokens);

    private sealed record Token(string Text, int Column);

    private sealed record Expr(int Line, int Column, int? Literal, string? Name);

    private abstract record Stmt(int Line, int Column);

    private sealed record NopStmt(int Line, int Column) : Stmt(Line, Column);

    private sealed record NameStmt(int Line, int Column, string Value) : Stmt(Line, Column);

    private sealed record SeedStmt(int Line, int Column, uint Value) : Stmt(Line, Column);

    private sealed record VarStmt(int Line, int Column, string Name, Expr Value) : Stmt(Line, Column);

    private sealed record LoopStmt(int Line, int Column, Expr Count, List<Stmt> Body) : Stmt(Line, Column);

    private sealed record IfStmt(int Line, int Column, Expr Left, Expr Right, List<Stmt> Then, List<Stmt> Else) : Stmt(Line, Column);

    private sealed record OpStmt(int Line, int Column, string Op, Expr Block, Expr? Page) : Stmt(Line, Column);
}
