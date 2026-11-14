using NandFlashTester.Core.Identification;

namespace NandFlashTester.Tests;

public class IdentificationTests
{
    [Fact]
    public void OnfiCrc_MatchesThePublishedPolynomialAndInit()
    {
        Assert.Equal((ushort)0xCFA1, Crc16.Onfi([0x00]));
        Assert.Equal((ushort)0x15B3, Crc16.Onfi("ONFI"u8));
    }

    [Fact]
    public void OnfiCrc_RejectsAFlippedBitAndAcceptsARecomputedPage()
    {
        var page = new byte[256];
        "ONFI"u8.CopyTo(page);
        page[80] = 0x00;
        page[81] = 0x10;
        var crc = Crc16.Onfi(page.AsSpan(0, 254));
        page[254] = (byte)crc;
        page[255] = (byte)(crc >> 8);
        Assert.True(ParameterPages.TryCrc(page, true, null, out var note));
        Assert.Contains("0x8005", note, StringComparison.Ordinal);
        page[80] ^= 0x01;
        Assert.False(ParameterPages.TryCrc(page, true, null, out _));
    }

    [Fact]
    public void CardCrc_UsesOnlyTheParametersOnTheCard()
    {
        var page = new byte[256];
        page[0] = 0x11;
        var crc = Crc16.Compute(page.AsSpan(0, 10), 0x1234, 0x8005, false, false, 0);
        page[10] = (byte)crc;
        page[11] = (byte)(crc >> 8);
        var card = new CardCrc
        {
            Mode = "card",
            Polynomial = 0x8005,
            Init = 0x1234,
            CoverageStart = 0,
            CoverageEndExclusive = 10,
            StoredOffset = 10,
            Citation = "合成参数卡"
        };
        Assert.True(ParameterPages.TryCrc(page, false, card, out var note));
        Assert.Equal("合成参数卡", note);
        card = new CardCrc { Mode = "card", Polynomial = 0x8005, Init = 0x1235, CoverageStart = 0, CoverageEndExclusive = 10, StoredOffset = 10 };
        Assert.False(ParameterPages.TryCrc(page, false, card, out _));
    }

    [Fact]
    public void ParameterPage_UsesTheSecondCopyWhenTheFirstCrcFails()
    {
        var page = SyntheticSamples.ParameterPage();
        Assert.True(ParameterPages.TrySelectOnfi(page, out var decoded, out _));
        Assert.NotNull(decoded);
        Assert.Equal(1, decoded!.CopyIndex);
        Assert.Equal(4096, decoded.PageDataBytes);
        Assert.Equal(256, decoded.SpareBytes);
        Assert.Equal(256, decoded.PagesPerBlock);
        Assert.Equal(1024, decoded.BlocksPerLun);
        Assert.Equal(1, decoded.Luns);
        Assert.Equal(3, decoded.BitsPerCell);
        Assert.Equal(8, decoded.EccBits);
        Assert.Equal("SYNTHETIC", decoded.Manufacturer);
        Assert.Equal("SYN-ONFI-DEMO", decoded.Model);
        Assert.Contains("4.0", decoded.RevisionNames);
        Assert.Contains("NV-DDR3", decoded.Interfaces);
        Assert.Equal([0, 1, 2, 3, 4, 5], decoded.SdrTimingModes);
    }

    [Fact]
    public void ParameterPage_DoesNotFillGeometryWhenEveryCopyFailsCrc()
    {
        var page = SyntheticSamples.ParameterPage();
        page[256 + 80] ^= 0x01;
        page[512 + 80] ^= 0x01;
        Assert.False(ParameterPages.TrySelectOnfi(page, out _, out var note));
        Assert.Contains("未采用", note, StringComparison.Ordinal);
    }

    [Fact]
    public void UniqueId_SkipsABadCopyAndRejectsAShortBuffer()
    {
        var data = SyntheticSamples.UniqueId();
        var result = UniqueIdCheck.Inspect(data, 16, 32, 16);
        Assert.True(result.Valid);
        Assert.Equal(0, result.CopyIndex);
        data[0] ^= 0x01;
        result = UniqueIdCheck.Inspect(data, 16, 32, 16);
        Assert.False(result.Valid);
        Assert.False(UniqueIdCheck.Inspect(new byte[8], 16, 32, 16).Valid);
    }

    [Fact]
    public void SyntheticOnfiDump_IsIdentifiedWithSources()
    {
        var card = Load("synthetic-onfi.json");
        var report = IdentificationEngine.Decode([card], [SampleCapture("导入")]);
        Assert.Equal(Recognition.Identified, report.State);
        Assert.Equal("SYN-ONFI-DEMO", report.PartNumber);
        Assert.Equal("SYN-1.0", report.DatasheetRevision);
        Assert.Equal("参数页偏移 80", Field(report, "pageDataBytes").Source);
        Assert.Equal("4096", Field(report, "pageDataBytes").Value);
        Assert.Equal("参数页偏移 84", Field(report, "spareBytes").Source);
        Assert.Equal("参数页偏移 102", Field(report, "bitsPerCell").Source);
        Assert.Contains("3", Field(report, "bitsPerCell").Value, StringComparison.Ordinal);
        Assert.Contains("ONFI 4.0", Field(report, "interface").Value, StringComparison.Ordinal);
        Assert.Contains("SDR", Field(report, "timingModes").Value, StringComparison.Ordinal);
        Assert.Contains("NV-DDR3", Field(report, "timingModes").Value, StringComparison.Ordinal);
        Assert.DoesNotContain("待补", Field(report, "timingModes").Value, StringComparison.Ordinal);
        Assert.StartsWith("01 02 03", Field(report, "uniqueId").Value, StringComparison.Ordinal);
        Assert.Contains(OnfiPublic.UniqueIdCitation, Field(report, "uniqueId").Source, StringComparison.Ordinal);
        Assert.Empty(report.Pending);
        Assert.False(report.HardwareExecuted);
    }

    [Fact]
    public void ShortManufacturerByte_IsPartialAndListsMissingFields()
    {
        var card = Load("synthetic-gap.json");
        var report = IdentificationEngine.Decode([card], [new RawCapture("导入", [0x54], null, null, null, null)]);
        Assert.Equal(Recognition.Partial, report.State);
        Assert.Null(report.PartNumber);
        Assert.Contains(report.MissingFields, item => item.Contains("料号", StringComparison.Ordinal));
        Assert.Contains(report.Pending, item => item.Field == "tWHR");
        Assert.Contains(report.MissingFields, item => item.Contains("待补参数", StringComparison.Ordinal));
    }

    [Fact]
    public void UnknownManufacturer_StaysUnrecognized()
    {
        var cards = new[] { Load("synthetic-onfi.json"), Load("synthetic-gap.json") };
        var report = IdentificationEngine.Decode(cards, [new RawCapture("导入", [0x11, 0x00], null, null, null, null)]);
        Assert.Equal(Recognition.Unrecognized, report.State);
        Assert.Null(report.PartNumber);
        Assert.All(report.Fields, field => Assert.True(field.Missing || field.Key == "interface"));
    }

    [Fact]
    public void ManufacturerMismatch_IsInconsistentAndDoesNotPickAValue()
    {
        var card = Load("synthetic-onfi.json");
        var capture = SampleCapture("冲突");
        capture = new RawCapture(capture.Label, [0x11, 0xA1, 0x01, 0x10, 0x00, 0x00, 0x00, 0x00], capture.Id20, capture.Id40, capture.UniqueId, capture.ParameterPage);
        var report = IdentificationEngine.Decode([card], [capture]);
        Assert.Equal(Recognition.Inconsistent, report.State);
        Assert.Null(report.PartNumber);
        Assert.Contains(report.Conflicts, item => item.Contains("字节 0", StringComparison.Ordinal));
        Assert.True(Field(report, "vendor").Missing);
    }

    [Fact]
    public void GeometryMismatch_ClearsTheField()
    {
        var json = File.ReadAllText(Example("synthetic-onfi.json")).Replace("\"pageDataBytes\": 4096", "\"pageDataBytes\": 8192", StringComparison.Ordinal);
        var card = SkuCardLoader.Parse(json);
        var report = IdentificationEngine.Decode([card], [SampleCapture("几何")]);
        Assert.Equal(Recognition.Inconsistent, report.State);
        Assert.True(Field(report, "pageDataBytes").Missing);
        Assert.Contains(report.Conflicts, item => item.Contains("页大小", StringComparison.Ordinal));
    }

    [Fact]
    public void SharedId_StaysPartialUntilTheParameterPageModelSelectsOnePart()
    {
        var json = File.ReadAllText(Example("synthetic-onfi.json"));
        var twin = json.Replace("SYN-ONFI-DEMO", "SYN-ONFI-TWIN", StringComparison.Ordinal).Replace("syn-onfi-demo", "syn-onfi-twin", StringComparison.Ordinal);
        var first = SkuCardLoader.Parse(json);
        var second = SkuCardLoader.Parse(twin);
        var merged = new SkuCard
        {
            CardId = first.CardId,
            Vendor = first.Vendor,
            VendorLegalNameKnown = true,
            DatasheetRevision = first.DatasheetRevision,
            ManufacturerId = first.ManufacturerId,
            Parts = [first.Parts[0], new SkuPart { PartNumber = "SYN-ONFI-OTHER", PartNumberKnown = true, Id00 = first.Parts[0].Id00, ModelNames = ["SYN-ONFI-OTHER"] }],
            Geometry = first.Geometry,
            ReadIdAddresses = first.ReadIdAddresses,
            UniqueIdSupported = first.UniqueIdSupported,
            UniqueIdLayout = first.UniqueIdLayout,
            UniqueIdOpcode = first.UniqueIdOpcode,
            ParameterPageSupported = first.ParameterPageSupported,
            ParameterPageLayout = first.ParameterPageLayout,
            Crc = first.Crc,
            TimingModeFields = first.TimingModeFields,
            Vcc = first.Vcc,
            Vccq = first.Vccq,
            TemperatureText = first.TemperatureText
        };
        var withoutPage = new RawCapture("只有ID", SyntheticSamples.Id00, SyntheticSamples.Id20, SyntheticSamples.Id40, SyntheticSamples.UniqueId(), null);
        var ambiguous = IdentificationEngine.Decode([merged], [withoutPage]);
        Assert.Equal(Recognition.Partial, ambiguous.State);
        Assert.Null(ambiguous.PartNumber);
        var identified = IdentificationEngine.Decode([merged], [SampleCapture("含参数页")]);
        Assert.Equal(Recognition.Identified, identified.State);
        Assert.Equal("SYN-ONFI-DEMO", identified.PartNumber);
        _ = second;
    }

    [Fact]
    public void IncompleteBitField_DoesNotInventANumber()
    {
        var card = Load("synthetic-gap.json");
        var report = IdentificationEngine.Decode([card], [new RawCapture("位", [0x54, 0x00, 0x01], null, null, null, null)]);
        Assert.True(Field(report, "lunsPerCe").Missing);
        Assert.Contains("不完整", Field(report, "lunsPerCe").PendingReason, StringComparison.Ordinal);
    }

    [Fact]
    public void BitField_FillsOnlyAMappedValue()
    {
        var json = """
        {"schemaVersion":"1","cardId":"syn-bits","vendor":"SYN","vendorLegalNameKnown":true,"datasheet":{"revision":"SYN-BITS"},"parts":[{"partNumber":"SYN-BIT","partNumberKnown":true,"id00Hex":"53 01","modelNames":["SYN-BIT"]}],"manufacturerIdHex":"53","commands":{"readId":{"addresses":[{"address":"00","supported":true,"lengthBytes":2}]}},"timings":{"phases":[]},"geometry":{"source":"unknown"},"pending":[],"idBitFields":[{"address":"00","byteIndex":1,"mask":3,"shift":0,"field":"lunsPerCe","maps":[{"value":1,"number":2}]}]}
        """;
        var card = SkuCardLoader.Parse(json);
        var hit = IdentificationEngine.Decode([card], [new RawCapture("位", [0x53, 0x01], null, null, null, null)]);
        Assert.Equal("2", Field(hit, "lunsPerCe").Value);
        Assert.Equal("ID 00h 字节 1", Field(hit, "lunsPerCe").Source);
        var miss = IdentificationEngine.Decode([card], [new RawCapture("位", [0x53, 0x02], null, null, null, null)]);
        Assert.True(Field(miss, "lunsPerCe").Missing);
        Assert.Null(Field(miss, "lunsPerCe").Value);
    }

    [Fact]
    public void TwoLuns_ReportConsistency()
    {
        var card = Load("synthetic-onfi.json");
        var same = IdentificationEngine.Decode([card], [SampleCapture("L0"), SampleCapture("L1")]);
        Assert.Equal(Recognition.Identified, same.State);
        Assert.Contains("一致", same.Consistency, StringComparison.Ordinal);
        var mixed = IdentificationEngine.Decode(
            [card, Load("synthetic-gap.json")],
            [SampleCapture("L0"), new RawCapture("L1", [0x54], null, null, null, null)]);
        Assert.Equal(Recognition.Inconsistent, mixed.State);
        Assert.Contains("不一致", mixed.Consistency, StringComparison.Ordinal);
    }

    [Fact]
    public void Plan_IsPerLunAndRefusesMissingTiming()
    {
        var card = Load("synthetic-onfi.json");
        var targets = new DutTarget[] { new(1, 2, 3, 4), new(0, 1, 0, 1) };
        var plan = IdentificationPlanBuilder.Build(card, targets);
        Assert.Contains(plan.Steps, step => step.TargetId == "1.2.3.4" && step.Operation == "read-id-00" && step.Included);
        Assert.Contains(plan.Steps, step => step.TargetId == "0.1.0.1" && step.Operation == "reset" && step.Cycles.Any(cycle => cycle.OpcodeHex == "FF"));
        Assert.Contains(plan.Steps, step => step.Operation == "read-id-20" && step.Cycles.Any(cycle => cycle.WaitNs == 1234));
        Assert.DoesNotContain(plan.Steps, step => step.Cycles.Any(cycle => cycle.WaitNs == 80));
        var blocked = SkuCardLoader.Parse(File.ReadAllText(Example("synthetic-onfi.json")).Replace("\"tWhrMinNs\": 1234", "\"tWhrMinNs\": null", StringComparison.Ordinal));
        var refused = IdentificationPlanBuilder.Build(blocked, targets);
        Assert.Contains(refused.Steps, step => step.Operation == "read-id-00" && step.Blocked && step.BlockReason!.Contains("tWHR", StringComparison.Ordinal));
        Assert.DoesNotContain(refused.Steps.Where(step => step.Operation.StartsWith("read-id", StringComparison.Ordinal)), step => step.Included);
    }

    [Fact]
    public void Plan_OmitsCommandsTheCardDoesNotSupport()
    {
        var json = File.ReadAllText(Example("synthetic-onfi.json"))
            .Replace("\"address\": \"20\", \"supported\": true", "\"address\": \"20\", \"supported\": false", StringComparison.Ordinal)
            .Replace("\"supported\": true, \"opcode\": \"ED\"", "\"supported\": false, \"opcode\": \"ED\"", StringComparison.Ordinal);
        var plan = IdentificationPlanBuilder.Build(SkuCardLoader.Parse(json), [new DutTarget(0, 0, 0, 0)]);
        Assert.Contains(plan.Steps, step => step.Operation == "read-id-20" && !step.Included && !step.Blocked);
        Assert.Contains(plan.Steps, step => step.Operation == "unique-id" && !step.Included && !step.Blocked);
    }

    [Fact]
    public void PreAndPostInit_AreNotChosenWhenThePhaseIsUnknown()
    {
        var json = File.ReadAllText(Example("synthetic-onfi.json"))
            .Replace("\"requiredFirst\": true", "\"requiredFirst\": null", StringComparison.Ordinal)
            .Replace("\"name\": \"default\"", "\"name\": \"pre-init\"", StringComparison.Ordinal);
        json = json.Replace("\"phases\": [", "\"phases\": [{\"name\":\"post-init\",\"tWhrMinNs\":100,\"tRrMinNs\":5,\"tAdlMinNs\":300},", StringComparison.Ordinal);
        var plan = IdentificationPlanBuilder.Build(SkuCardLoader.Parse(json), [new DutTarget(0, 0, 0, 0)]);
        Assert.Contains(plan.Steps, step => step.Blocked && step.BlockReason!.Contains("pre-init", StringComparison.Ordinal));
    }

    [Fact]
    public void Transport_EmitsNoBytesWhenDisconnectedOrConnected()
    {
        var plan = IdentificationPlanBuilder.Build(Load("synthetic-onfi.json"), [new DutTarget(0, 0, 0, 0)]);
        var offline = IdentificationTransport.Execute(plan, connected: false);
        var online = IdentificationTransport.Execute(plan, connected: true);
        Assert.Equal(0, offline.WireBytesEmitted);
        Assert.Equal(0, online.WireBytesEmitted);
        Assert.False(offline.Sent);
        Assert.False(online.Sent);
        Assert.Equal("NOT_CONNECTED", offline.Code);
        Assert.Equal("UNSUPPORTED_OPERATION", online.Code);
        Assert.Contains(online.Gaps, gap => gap.Id == "control-1-no-raw-nand");
        Assert.Contains(online.Gaps, gap => gap.Id == "fpga-frame-unfrozen");
    }

    [Fact]
    public void HexImport_AcceptsSeparatorsAndRejectsAPartialByte()
    {
        Assert.True(HexText.TryParse("4F 4e,0x46\n49", out var bytes, out _));
        Assert.Equal(new byte[] { 0x4F, 0x4E, 0x46, 0x49 }, bytes);
        Assert.True(HexText.TryParse("4F4E4649", out bytes, out _));
        Assert.Equal(4, bytes.Length);
        Assert.False(HexText.TryParse("4", out _, out var error));
        Assert.Contains("十六进制", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Loader_RejectsAnUnknownProperty()
    {
        var json = File.ReadAllText(Example("synthetic-onfi.json")).Replace("\"family\"", "\"secretTable\"", StringComparison.Ordinal);
        var ex = Assert.Throws<InvalidDataException>(() => SkuCardLoader.Parse(json));
        Assert.Contains("不符合", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EccFf_StaysEmpty()
    {
        var page = SyntheticSamples.ParameterPage();
        page[256 + 112] = 0xFF;
        page[512 + 112] = 0xFF;
        var crc1 = Crc16.Onfi(page.AsSpan(256, 254));
        page[256 + 254] = (byte)crc1;
        page[256 + 255] = (byte)(crc1 >> 8);
        var crc2 = Crc16.Onfi(page.AsSpan(512, 254));
        page[512 + 254] = (byte)crc2;
        page[512 + 255] = (byte)(crc2 >> 8);
        var card = SkuCardLoader.Parse(File.ReadAllText(Example("synthetic-onfi.json")).Replace("\"eccBits\": 8", "\"eccBits\": null", StringComparison.Ordinal));
        var report = IdentificationEngine.Decode([card], [new RawCapture("ECC", SyntheticSamples.Id00, SyntheticSamples.Id20, SyntheticSamples.Id40, null, page)]);
        Assert.True(Field(report, "ecc").Missing);
        Assert.Contains("112", Field(report, "ecc").PendingReason, StringComparison.Ordinal);
    }

    private static RawCapture SampleCapture(string label) => new(label, SyntheticSamples.Id00, SyntheticSamples.Id20, SyntheticSamples.Id40, SyntheticSamples.UniqueId(), SyntheticSamples.ParameterPage());

    private static SkuCard Load(string name) => SkuCardLoader.Parse(File.ReadAllText(Example(name)), name);

    private static string Example(string name)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "NandFlashTester.slnx")))
        {
            dir = dir.Parent;
        }

        return Path.Combine(dir!.FullName, "schemas", "examples", name);
    }

    private static SourcedField Field(IdentificationReport report, string key) => report.Fields.Single(field => field.Key == key);
}
