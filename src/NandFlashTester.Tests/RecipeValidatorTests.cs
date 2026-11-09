using System.Text;
using NandFlashTester.Core.Recipes;

namespace NandFlashTester.Tests;

public class RecipeValidatorTests
{
    [Fact]
    public void Presets_are_valid_and_key_order_does_not_change_the_hash()
    {
        var success = RecipeValidator.Validate(Encoding.UTF8.GetBytes(RecipePresets.SuccessJson));
        Assert.True(success.Ok);
        Assert.NotNull(success.Recipe);
        Assert.Equal(RecipePresets.Success.Sha256, success.Recipe.Sha256);
        Assert.Equal(42u, success.Recipe.Seed);

        const string reordered = """
            {"inject":{"mode":"none"},"target":{"pageCount":4,"pageStart":0,"block":0,"plane":0,"lun":0,"ce":0,"channel":0},"pattern":{"kind":"increment"},"topology":{"pageSizeBytes":64,"pagesPerBlock":4,"blocks":2,"planes":1,"luns":1,"ces":1,"channels":1},"paceMs":0,"seed":42,"version":"1.0.0","name":"基础擦写读成功","schema":"nand.sim.recipe/1"}
            """;
        var again = RecipeValidator.Validate(Encoding.UTF8.GetBytes(reordered));
        Assert.True(again.Ok);
        Assert.Equal(success.Recipe.Sha256, again.Recipe!.Sha256);
        Assert.Equal(success.Recipe.CanonicalJson, again.Recipe.CanonicalJson);
    }

    [Theory]
    [InlineData("""{"schema":"nand.sim.recipe/1","script":"Process.Start(\"calc\")"}""", "脚本")]
    [InlineData("""{"schema":"nand.sim.recipe/1","name":"基础擦写读成功","version":"1.0.0","seed":1,"topology":{"channels":1,"ces":1,"luns":1,"planes":1,"blocks":1,"pagesPerBlock":1,"pageSizeBytes":16},"pattern":{"kind":"increment"},"target":{"channel":0,"ce":0,"lun":0,"plane":0,"block":0,"pageStart":0,"pageCount":1},"inject":{"mode":"none"},"shell":"bash"}""", "脚本")]
    [InlineData("""{"schema":"nand.sim.recipe/1","schema":"other"}""", "重复")]
    [InlineData("""{"schema":"nand.sim.recipe/1","name":"基础擦写读成功","version":"9.9.9","seed":1,"topology":{"channels":1,"ces":1,"luns":1,"planes":1,"blocks":1,"pagesPerBlock":1,"pageSizeBytes":16},"pattern":{"kind":"increment"},"target":{"channel":0,"ce":0,"lun":0,"plane":0,"block":0,"pageStart":0,"pageCount":1},"inject":{"mode":"none"}}""", "已批准版本")]
    [InlineData("""{"schema":"nand.sim.recipe/1","name":"基础擦写读成功","version":"1.0.0","seed":1.5,"topology":{"channels":1,"ces":1,"luns":1,"planes":1,"blocks":1,"pagesPerBlock":1,"pageSizeBytes":16},"pattern":{"kind":"increment"},"target":{"channel":0,"ce":0,"lun":0,"plane":0,"block":0,"pageStart":0,"pageCount":1},"inject":{"mode":"none"}}""", "整数")]
    [InlineData("""{"schema":"nand.sim.recipe/1","name":"基础擦写读成功","version":"1.0.0","seed":1,"topology":[],"pattern":{"kind":"increment"},"target":{"channel":0,"ce":0,"lun":0,"plane":0,"block":0,"pageStart":0,"pageCount":1},"inject":{"mode":"none"}}""", "数组")]
    [InlineData("""{"schema":"nand.sim.recipe/1","name":"基础擦写读成功","version":"1.0.0","seed":1,"topology":{"channels":1,"ces":1,"luns":1,"planes":1,"blocks":1,"pagesPerBlock":1,"pageSizeBytes":16,"file":"/tmp/a.bin"},"pattern":{"kind":"increment"},"target":{"channel":0,"ce":0,"lun":0,"plane":0,"block":0,"pageStart":0,"pageCount":1},"inject":{"mode":"none"}}""", "白名单")]
    [InlineData("""{"schema":"nand.sim.recipe/1","name":"=cmd","version":"1.0.0","seed":1,"topology":{"channels":1,"ces":1,"luns":1,"planes":1,"blocks":1,"pagesPerBlock":1,"pageSizeBytes":16},"pattern":{"kind":"file","path":"/etc/passwd"},"target":{"channel":0,"ce":0,"lun":0,"plane":0,"block":0,"pageStart":0,"pageCount":1},"inject":{"mode":"none"}}""", "未执行")]
    public void Illegal_recipes_are_rejected_without_a_recipe_object(string json, string fragment)
    {
        var result = RecipeValidator.Validate(Encoding.UTF8.GetBytes(json));
        Assert.False(result.Ok);
        Assert.Null(result.Recipe);
        Assert.Contains(result.Errors, error => error.Contains(fragment, StringComparison.Ordinal) && error.Contains("未执行", StringComparison.Ordinal));
    }

    [Fact]
    public void Out_of_range_target_and_inject_are_rejected()
    {
        const string json = """
            {"schema":"nand.sim.recipe/1","name":"越界","version":"1.0.0","seed":1,"topology":{"channels":1,"ces":1,"luns":1,"planes":1,"blocks":1,"pagesPerBlock":2,"pageSizeBytes":16},"pattern":{"kind":"increment"},"target":{"channel":0,"ce":0,"lun":0,"plane":0,"block":0,"pageStart":0,"pageCount":0},"inject":{"mode":"bit_flip","page":9,"byteOffset":99,"bit":0}}
            """;
        var result = RecipeValidator.Validate(Encoding.UTF8.GetBytes(json));
        Assert.False(result.Ok);
        Assert.Contains(result.Errors, error => error.Contains("pageCount", StringComparison.Ordinal));
    }
}
