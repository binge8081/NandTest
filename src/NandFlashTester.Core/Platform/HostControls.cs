namespace NandFlashTester.Core.Platform;

public sealed record StartDecision(bool CanStart, string Message);

public static class HostInstanceGate
{
    public const string MissingExecutable = "未找到 NandFlashTester.Api.exe。请先使用已发布的 win-x64 文件夹。";
    public const string AlreadyRunning = "端口 5080 已被占用，或上位机已在运行。请先运行 stop.ps1，不要再开一个实例。";
    public const string Ready = "可以启动。本检查不访问用户磁盘上的安装目录。";

    public static StartDecision Decide(bool executablePresent, bool portBusy, bool processAlreadyRunning)
    {
        if (!executablePresent)
        {
            return new StartDecision(false, MissingExecutable);
        }

        if (portBusy || processAlreadyRunning)
        {
            return new StartDecision(false, AlreadyRunning);
        }

        return new StartDecision(true, Ready);
    }
}

public sealed record PolicyDecision(bool Approved, bool Applied, string Reason);

public static class UnapprovedPolicy
{
    public static PolicyDecision Evaluate(string? domain, string? name)
    {
        var area = string.IsNullOrWhiteSpace(domain) ? "未命名" : domain.Trim();
        return new PolicyDecision(false, false, $"{area} 的角色、MES 或分档阈值尚未批准。已拒绝，不产生分档或放行。");
    }
}
