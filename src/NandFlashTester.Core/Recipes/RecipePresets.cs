using System.Text;

namespace NandFlashTester.Core.Recipes;

public static class RecipePresets
{
    public const string SuccessJson = """
        {"schema":"nand.sim.recipe/1","name":"基础擦写读成功","version":"1.0.0","seed":42,"paceMs":0,"topology":{"channels":1,"ces":1,"luns":1,"planes":1,"blocks":2,"pagesPerBlock":4,"pageSizeBytes":64},"pattern":{"kind":"increment"},"target":{"channel":0,"ce":0,"lun":0,"plane":0,"block":0,"pageStart":0,"pageCount":4},"inject":{"mode":"none"}}
        """;

    public const string BitFlipJson = """
        {"schema":"nand.sim.recipe/1","name":"注入一位失败","version":"1.0.0","seed":7,"paceMs":0,"topology":{"channels":1,"ces":1,"luns":1,"planes":1,"blocks":2,"pagesPerBlock":4,"pageSizeBytes":64},"pattern":{"kind":"increment"},"target":{"channel":0,"ce":0,"lun":0,"plane":0,"block":0,"pageStart":0,"pageCount":4},"inject":{"mode":"bit_flip","page":1,"byteOffset":3,"bit":0}}
        """;

    public static Recipe Success { get; } = Must(SuccessJson);

    public static Recipe BitFlip { get; } = Must(BitFlipJson);

    public static IReadOnlyList<(string Id, string Title, string Json)> All { get; } =
    [
        ("success", "正常成功", SuccessJson),
        ("bit-flip", "注入一位失败", BitFlipJson)
    ];

    private static Recipe Must(string json)
    {
        var result = RecipeValidator.Validate(Encoding.UTF8.GetBytes(json));
        if (!result.Ok || result.Recipe is null)
        {
            throw new InvalidOperationException(string.Join(" ", result.Errors));
        }

        return result.Recipe;
    }
}
