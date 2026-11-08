namespace NandFlashTester.Core.Recipes;

public sealed record RecipeCapability(string Name, string State, string Detail);

public static class RecipeCapabilities
{
    public static IReadOnlyList<RecipeCapability> Language { get; } =
    [
        new("schema-check", "HostReady", "可以校验配方结构和版本。"),
        new("pattern", "HostReady", "可以在主机上生成 increment、constant、prng。这不是实测读回。"),
        new("bounds", "HostReady", "可以拒绝越界地址和非法图案长度。"),
        new("identify", "BlockedNoDevice", "读取身份要等设备连接和资料。当前不发送。"),
        new("erase", "BlockedNoDevice", "擦除要等设备连接和资料。当前不发送。"),
        new("program", "BlockedNoDevice", "写入要等设备连接和资料。当前不发送。"),
        new("read", "BlockedNoDevice", "读回要等设备连接和资料。当前不发送。"),
        new("compare", "BlockedNoDevice", "与实机读回比较要等设备连接。当前不产生实测错误数。"),
        new("step-debug", "NotInLanguage", "单步调试不在当前配方语言里。"),
        new("arbitrary-command", "NotInLanguage", "任意命令不在当前配方语言里。"),
        new("file-pattern", "NotInLanguage", "文件图案不在当前配方语言里。")
    ];

    public static IReadOnlyList<RecipeCapability> ForRecipe(Recipe? recipe)
    {
        if (recipe is null)
        {
            return Language;
        }

        return
        [
            new("schema-check", "HostReady", $"版本 {recipe.Version} 已通过结构校验。"),
            new("pattern", "HostReady", $"图案 {recipe.Pattern.KindText} 可以在主机上按种子 {recipe.Seed} 生成。这不是实测读回。"),
            new("bounds", "HostReady", "目标地址落在配方声明的拓扑内。"),
            new("erase", "BlockedNoDevice", "擦除步骤已识别，但设备未连接，不会执行。"),
            new("program", "BlockedNoDevice", "写入步骤已识别，但设备未连接，不会执行。"),
            new("read", "BlockedNoDevice", "读回步骤已识别，但设备未连接，不会执行。"),
            new("compare", "BlockedNoDevice", "比较步骤已识别，但没有实机读回，不产生实测错误数。")
        ];
    }
}
