using NandFlashTester.Core.Devices;
using NandFlashTester.Core.Patterns;

namespace NandFlashTester.Core.Recipes;

public static class RecipeContract
{
    public const string SchemaId = "nand.sim.recipe/1";
    public const string ApprovedVersion = "1.0.0";
}

public enum InjectMode
{
    None,
    BitFlip
}

public sealed record PatternSpec(PatternKind Kind, byte ConstantValue)
{
    public string KindText => Kind switch
    {
        PatternKind.Increment => "increment",
        PatternKind.Constant => "constant",
        PatternKind.Prng => "prng",
        _ => "unknown"
    };
}

public sealed record TargetSpec(int Channel, int Ce, int Lun, int Plane, int Block, int PageStart, int PageCount);

public sealed record InjectSpec(InjectMode Mode, int Page, int ByteOffset, int Bit)
{
    public static InjectSpec None { get; } = new(InjectMode.None, 0, 0, 0);

    public string ModeText => Mode == InjectMode.BitFlip ? "bit_flip" : "none";
}

public sealed record Recipe(
    string Name,
    string Version,
    uint Seed,
    int PaceMs,
    SimTopology Topology,
    PatternSpec Pattern,
    TargetSpec Target,
    InjectSpec Inject,
    string CanonicalJson,
    string Sha256);
