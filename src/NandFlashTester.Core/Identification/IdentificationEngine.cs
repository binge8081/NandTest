namespace NandFlashTester.Core.Identification;

public sealed record SourcedField(string Key, string Label, string? Value, string? Source, bool Missing, string? PendingReason);

public sealed record CaptureJudgement(
    string Label,
    string? Id00Hex,
    string? Id20Hex,
    string? Id40Hex,
    string? UniqueIdHex,
    int? ParameterPageBytes,
    string State,
    string Summary,
    string? PartNumber,
    string? DatasheetRevision,
    string? CardId,
    IReadOnlyList<SourcedField> Fields,
    IReadOnlyList<string> MissingFields,
    IReadOnlyList<string> Conflicts,
    IReadOnlyList<PendingItem> Pending);

public sealed record IdentificationReport(
    string State,
    string Summary,
    string? PartNumber,
    string? DatasheetRevision,
    string? CardId,
    IReadOnlyList<SourcedField> Fields,
    IReadOnlyList<string> MissingFields,
    IReadOnlyList<string> Conflicts,
    IReadOnlyList<PendingItem> Pending,
    IReadOnlyList<CaptureJudgement> Captures,
    string Consistency,
    bool HardwareExecuted);

public sealed record RawCapture(string Label, byte[]? Id00, byte[]? Id20, byte[]? Id40, byte[]? UniqueId, byte[]? ParameterPage);

public static class Recognition
{
    public const string Identified = "已识别";
    public const string Partial = "部分识别";
    public const string Unrecognized = "未识别";
    public const string Inconsistent = "不一致";
}

public static class IdentificationEngine
{
    private static readonly (string Key, string Label)[] Order =
    [
        ("vendor", "厂商"),
        ("model", "型号"),
        ("partNumber", "料号"),
        ("datasheetRevision", "资料版本"),
        ("interface", "接口"),
        ("timingModes", "时序模式"),
        ("pageDataBytes", "页大小"),
        ("spareBytes", "spare"),
        ("pagesPerBlock", "每块页数"),
        ("blocksPerLun", "每 LUN 块数"),
        ("lunsPerCe", "每 CE 的 LUN"),
        ("bitsPerCell", "每单元位数"),
        ("ecc", "ECC 需求"),
        ("voltage", "电压"),
        ("temperature", "温度规格"),
        ("uniqueId", "Unique ID")
    ];

    public static IdentificationReport Decode(IReadOnlyList<SkuCard> cards, IReadOnlyList<RawCapture> captures)
    {
        if (captures.Count == 0)
        {
            return new IdentificationReport(Recognition.Unrecognized, "没有读出字节。", null, null, null, [], ["没有读出字节"], [], [], [], "没有可比较的 LUN。", false);
        }

        var judged = captures.Select(capture => Judge(cards, capture)).ToList();
        var consistency = DescribeConsistency(judged);
        var state = judged[0].State;
        var summary = judged[0].Summary;
        if (judged.Count > 1)
        {
            if (consistency.StartsWith("不一致", StringComparison.Ordinal) || judged.Any(item => item.State == Recognition.Inconsistent))
            {
                state = Recognition.Inconsistent;
                summary = consistency;
            }
            else if (judged.All(item => item.State == Recognition.Identified) && judged.Select(item => item.PartNumber).Distinct().Count() == 1)
            {
                state = Recognition.Identified;
                summary = judged[0].Summary + " 各 LUN 一致。";
            }
            else if (judged.Any(item => item.State == Recognition.Partial))
            {
                state = Recognition.Partial;
                summary = "多笔读出没有同时给出唯一料号。 " + consistency;
            }
            else
            {
                state = Recognition.Unrecognized;
                summary = "没有一笔读出匹配到参数卡。";
            }
        }

        var primary = judged[0];
        return new IdentificationReport(
            state,
            summary,
            state == Recognition.Identified ? primary.PartNumber : primary.PartNumber,
            primary.DatasheetRevision,
            primary.CardId,
            primary.Fields,
            primary.MissingFields,
            judged.SelectMany(item => item.Conflicts).Distinct().ToList(),
            primary.Pending,
            judged,
            consistency,
            false);
    }

    public static CaptureJudgement Judge(IReadOnlyList<SkuCard> cards, RawCapture capture)
    {
        var conflicts = new List<string>();
        var bag = new Dictionary<string, SourcedField>(StringComparer.Ordinal);
        DecodedParameterPage? onfi = null;
        DecodedParameterPage? jedec = null;
        if (capture.ParameterPage is { Length: > 0 })
        {
            if (HasCopySignature(capture.ParameterPage, OnfiPublic.CopyLength, OnfiPublic.Signature))
            {
                if (ParameterPages.TrySelectOnfi(capture.ParameterPage, out onfi, out var note) && onfi is not null)
                {
                    FillFromPage(bag, onfi);
                }
                else
                {
                    Missing(bag, "pageDataBytes", "页大小", note);
                }
            }
            else if (HasCopySignature(capture.ParameterPage, 512, OnfiPublic.JedecPageSignature))
            {
                Missing(bag, "pageDataBytes", "页大小", "JESD 签名要等匹配到参数卡后才决定能否采用。");
            }
            else
            {
                conflicts.Add("参数页签名不是 ONFI 或 JESD，未按公开布局填写几何。");
            }
        }

        if (capture.Id20 is { Length: >= 4 })
        {
            if (OnfiPublic.StartsWith(capture.Id20, OnfiPublic.Signature))
            {
                SetIfEmpty(bag, "interface", "接口", "ONFI（90h 地址 20h 签名）", "ID 20h 字节 0–3");
            }
            else
            {
                conflicts.Add("90h 地址 20h 的前 4 字节不是 ONFI 签名。");
            }
        }

        if (capture.Id40 is { Length: >= 5 })
        {
            if (!OnfiPublic.StartsWith(capture.Id40, OnfiPublic.JedecIdSignature))
            {
                conflicts.Add("90h 地址 40h 的前 5 字节不是 JEDEC 签名。");
            }
            else if (Value(bag, "interface") is { Missing: false, Value: { } existing, Source: { } source })
            {
                if (!existing.Contains("JEDEC", StringComparison.Ordinal))
                {
                    Set(bag, "interface", "接口", existing + " · JEDEC ID 签名", source + "；ID 40h 字节 0–4");
                }
            }
            else
            {
                Set(bag, "interface", "接口", "JEDEC ID 签名", "ID 40h 字节 0–4");
            }
        }

        if (capture.Id00 is { Length: > 0 } && onfi?.JedecManufacturer is byte pageMfr && capture.Id00[0] != pageMfr)
        {
            conflicts.Add($"ID 00h 字节 0 为 {capture.Id00[0]:X2}，参数页偏移 64 为 {pageMfr:X2}。两处厂商码不一致，不选取其中一处。");
            Clear(bag, "vendor");
            Missing(bag, "vendor", "厂商", "ID 与参数页的厂商码冲突。");
        }

        var idHits = new List<(SkuCard Card, SkuPart Part)>();
        var modelHits = new List<(SkuCard Card, SkuPart Part)>();
        foreach (var card in cards)
        {
            foreach (var skuPart in card.Parts)
            {
                if (skuPart.Id00 is not null && capture.Id00 is not null && skuPart.Id00.AsSpan().SequenceEqual(capture.Id00))
                {
                    idHits.Add((card, skuPart));
                }

                if (onfi?.Model is { Length: > 0 } model && MatchesModel(skuPart, model))
                {
                    modelHits.Add((card, skuPart));
                }
            }
        }

        var candidates = idHits;
        if (idHits.Count > 0 && modelHits.Count > 0)
        {
            candidates = idHits.Where(hit => modelHits.Any(model => SamePart(hit, model))).ToList();
            if (candidates.Count == 0)
            {
                conflicts.Add("00h ID 匹配到的料号与参数页型号匹配到的料号不是同一组。");
            }
        }
        else if (idHits.Count == 0)
        {
            candidates = modelHits;
        }
        SkuCard? associated = null;
        SkuPart? part = null;
        if (candidates.Select(item => item.Card.CardId + "/" + item.Part.PartNumber).Distinct(StringComparer.Ordinal).Count() == 1)
        {
            associated = candidates[0].Card;
            part = candidates[0].Part;
        }
        else if (candidates.Count > 1)
        {
            associated = candidates.Select(item => item.Card).DistinctBy(item => item.CardId).Count() == 1 ? candidates[0].Card : null;
        }
        else if (capture.Id00 is { Length: > 0 })
        {
            var makers = cards.Where(card => card.ManufacturerId is byte id && id == capture.Id00[0]).ToList();
            if (makers.Count == 1)
            {
                associated = makers[0];
            }
            else if (makers.Count > 1)
            {
                conflicts.Add("厂商字节对应多张参数卡，不能确定料号。");
            }
        }

        if (associated is not null && capture.ParameterPage is { Length: > 0 } && HasCopySignature(capture.ParameterPage, 512, OnfiPublic.JedecPageSignature))
        {
            var accept = associated.AcceptIdenticalCopiesWithoutCrc || associated.Crc.Mode == "card";
            if (ParameterPages.TrySelectJedec(capture.ParameterPage, associated.Crc.Mode == "card" ? associated.Crc : null, associated.AcceptIdenticalCopiesWithoutCrc, out jedec, out var jedecNote) && jedec is not null)
            {
                FillFromPage(bag, jedec);
            }
            else
            {
                Missing(bag, "pageDataBytes", "页大小", jedecNote);
                _ = accept;
            }
        }

        var blocked = associated?.Conflicts.Any(item => item.BlocksAutoMatch) == true;
        if (blocked)
        {
            conflicts.AddRange(associated!.Conflicts.Where(item => item.BlocksAutoMatch).Select(item => $"{item.Topic}：{item.Detail}"));
        }

        if (associated is not null)
        {
            foreach (var item in associated.InterfaceConflicts)
            {
                conflicts.Add(item);
            }

            if (associated.InterfaceConflicts.Count > 0 || associated.InterfaceStandard == "conflicting")
            {
                conflicts.Add("参数卡记录了接口冲突，不把其中一种接口写成已确认值。");
                Clear(bag, "interface");
                Missing(bag, "interface", "接口", "接口标准在参数卡中互相冲突。");
            }
        }

        ApplyCard(bag, associated, part, blocked, onfi ?? jedec, conflicts);
        ApplyBitFields(bag, associated, capture, conflicts);
        ApplyUid(bag, associated, capture.UniqueId);
        CompareGeometry(bag, associated, blocked, conflicts);

        var state = ResolveState(associated, part, blocked, conflicts, candidates.Count);
        string? partNumber = null;
        if (state != Recognition.Inconsistent && !blocked && part is { PartNumberKnown: true } && candidates.Count == 1)
        {
            partNumber = part.PartNumber;
        }

        if (state == Recognition.Inconsistent)
        {
            Clear(bag, "partNumber");
            Missing(bag, "partNumber", "料号", "存在冲突，不填写料号。");
        }
        else if (partNumber is not null && associated is not null)
        {
            Set(bag, "partNumber", "料号", partNumber, $"参数卡 {associated.CardId} + {associated.DatasheetLabel}");
        }

        if (state != Recognition.Unrecognized && !string.IsNullOrWhiteSpace(associated?.DatasheetRevision))
        {
            Set(bag, "datasheetRevision", "资料版本", associated!.DatasheetRevision!, $"参数卡 {associated.CardId}");
        }

        EnsureAll(bag, associated);
        var fields = Order.Select(item => bag[item.Key]).ToList();
        var missing = fields.Where(item => item.Missing).Select(item => item.PendingReason is null ? item.Label : $"{item.Label}：{item.PendingReason}").ToList();
        var pending = associated?.Pending.ToList() ?? [];
        foreach (var item in pending)
        {
            missing.Add($"待补参数 {item.Field}：{item.Reason}");
        }

        var revision = state == Recognition.Unrecognized ? null : associated?.DatasheetRevision;

        var summary = state switch
        {
            Recognition.Identified => $"已识别：{partNumber}，资料版本 {revision}。",
            Recognition.Partial => partNumber is null ? "部分识别：还不能唯一确定料号。缺失字段见列表。" : $"部分识别：{partNumber}。缺失字段见列表。",
            Recognition.Inconsistent => "不一致：读出字节之间或与参数卡冲突，见冲突列表。不自动改写料号。",
            _ => "未识别：没有参数卡与这些字节匹配。请手工填写，缺失项保持空白。"
        };

        return new CaptureJudgement(
            capture.Label,
            HexOrNull(capture.Id00),
            HexOrNull(capture.Id20),
            HexOrNull(capture.Id40),
            HexOrNull(capture.UniqueId),
            capture.ParameterPage?.Length,
            state,
            summary,
            state == Recognition.Inconsistent ? null : partNumber,
            state == Recognition.Unrecognized ? null : revision,
            state == Recognition.Unrecognized ? null : associated?.CardId,
            fields,
            missing,
            conflicts.Distinct().ToList(),
            pending);
    }

    private static void FillFromPage(Dictionary<string, SourcedField> bag, DecodedParameterPage page)
    {
        var layout = page.Layout == "onfi-public" ? "参数页" : "JEDEC 参数页";
        if (!string.IsNullOrWhiteSpace(page.Manufacturer))
        {
            Set(bag, "vendor", "厂商", page.Manufacturer, $"{layout}偏移 32");
        }

        if (!string.IsNullOrWhiteSpace(page.Model))
        {
            Set(bag, "model", "型号", page.Model, $"{layout}偏移 44");
        }

        if (page.RevisionNames.Count > 0)
        {
            var unnamed = page.UnnamedRevisionBits.Count == 0 ? "" : "（另有未命名位）";
            Set(bag, "interface", "接口", "ONFI " + string.Join(" / ", page.RevisionNames) + unnamed + FormatNv(page.Interfaces), "参数页偏移 4 与偏移 6");
        }
        else if (page.Interfaces.Count > 0)
        {
            Set(bag, "interface", "接口", string.Join(" · ", page.Interfaces), "参数页偏移 6");
        }

        if (page.SdrTimingModes.Count > 0)
        {
            Set(bag, "timingModes", "时序模式", "SDR " + string.Join(", ", page.SdrTimingModes), "参数页偏移 129");
        }

        SetNumber(bag, "pageDataBytes", "页大小", page.PageDataBytes, $"{layout}偏移 80");
        SetNumber(bag, "spareBytes", "spare", page.SpareBytes, $"{layout}偏移 84");
        SetNumber(bag, "pagesPerBlock", "每块页数", page.PagesPerBlock, $"{layout}偏移 92");
        SetNumber(bag, "blocksPerLun", "每 LUN 块数", page.BlocksPerLun, $"{layout}偏移 96");
        SetNumber(bag, "lunsPerCe", "每 CE 的 LUN", page.Luns, $"{layout}偏移 100");
        if (page.BitsPerCell is int bits)
        {
            Set(bag, "bitsPerCell", "每单元位数", BitsLabel(bits), $"{layout}偏移 102");
        }

        if (page.EccFieldDefined && page.EccBits is int ecc)
        {
            Set(bag, "ecc", "ECC 需求", $"{ecc} bit", "参数页偏移 112");
        }
        else if (page.Layout == "onfi-public")
        {
            Missing(bag, "ecc", "ECC 需求", "偏移 112 未给出可用的纠错位数。");
        }

        foreach (var note in page.Notes)
        {
            if (note.Contains("FFh", StringComparison.Ordinal) && !bag.ContainsKey("ecc"))
            {
                Missing(bag, "ecc", "ECC 需求", note);
            }
        }
    }

    private static void ApplyCard(Dictionary<string, SourcedField> bag, SkuCard? card, SkuPart? part, bool blocked, DecodedParameterPage? page, List<string> conflicts)
    {
        if (card is null || blocked)
        {
            return;
        }

        var source = $"参数卡 {card.CardId} + {card.DatasheetLabel}" + (card.DatasheetPageNote is null ? "" : $"（{card.DatasheetPageNote}）");
        var vendorConflict = conflicts.Any(item => item.Contains("厂商码", StringComparison.Ordinal));
        if (!vendorConflict && ValueMissing(bag, "vendor") && card.VendorLegalNameKnown && card.Vendor is not null)
        {
            Set(bag, "vendor", "厂商", card.Vendor, source);
        }

        if (page?.Manufacturer is { Length: > 0 } manufacturer && card.Vendor is not null && !string.Equals(manufacturer, card.Vendor, StringComparison.OrdinalIgnoreCase))
        {
            conflicts.Add($"参数页厂商“{manufacturer}”与参数卡厂商“{card.Vendor}”不一致。");
            Clear(bag, "vendor");
            Missing(bag, "vendor", "厂商", "参数页与参数卡冲突。");
        }

        if (ValueMissing(bag, "model") && part?.ModelNames.Count == 1)
        {
            Set(bag, "model", "型号", part.ModelNames[0], source);
        }

        if (page?.Model is { Length: > 0 } model && part is not null && part.ModelNames.Count > 0 && !MatchesModel(part, model) && !string.Equals(model, part.PartNumber, StringComparison.Ordinal))
        {
            conflicts.Add($"参数页型号“{model}”不在参数卡为该料号列出的型号中。");
        }

        if (card.InterfaceConflicts.Count == 0 && card.InterfaceStandard is not null && ValueMissing(bag, "interface"))
        {
            var text = card.InterfaceStandard;
            if (!string.IsNullOrWhiteSpace(card.InterfaceVersion))
            {
                text += " " + card.InterfaceVersion;
            }

            if (card.DataInterfaces.Count > 0)
            {
                text += " · " + string.Join(" · ", card.DataInterfaces);
            }

            Set(bag, "interface", "接口", text, source);
        }

        if (card.Geometry.Source == "datasheet-features")
        {
            FillGeometryGap(bag, "pageDataBytes", "页大小", card.Geometry.PageDataBytes, source);
            if (card.Geometry.SpareConflicts.Count > 1)
            {
                Missing(bag, "spareBytes", "spare", "参数卡记录了互相冲突的 spare，不选取。");
                conflicts.Add("spare 存在多个资料值：" + string.Join(" / ", card.Geometry.SpareConflicts) + "。不选取。");
            }
            else
            {
                FillGeometryGap(bag, "spareBytes", "spare", card.Geometry.SpareBytes, source);
            }

            FillGeometryGap(bag, "pagesPerBlock", "每块页数", card.Geometry.PagesPerBlock, source);
            FillGeometryGap(bag, "blocksPerLun", "每 LUN 块数", card.Geometry.BlocksPerLun, source);
            FillGeometryGap(bag, "lunsPerCe", "每 CE 的 LUN", card.Geometry.LunsPerCe, source);
            FillGeometryGap(bag, "bitsPerCell", "每单元位数", card.Geometry.BitsPerCell, source);
            if (ValueMissing(bag, "ecc") && card.Geometry.EccBits is int ecc)
            {
                Set(bag, "ecc", "ECC 需求", $"{ecc} bit", source);
            }
        }

        if (card.Vcc is not null || card.Vccq is not null)
        {
            Set(bag, "voltage", "电压", string.Join(" / ", new[] { card.Vcc, card.Vccq }.Where(item => item is not null)), source);
        }

        if (card.TemperatureText is not null)
        {
            Set(bag, "temperature", "温度规格", card.TemperatureText, source);
        }

        foreach (var field in card.TimingModeFields)
        {
            if (page is null || field.Offset < 0 || field.Length <= 0 || field.Offset + field.Length > page.Bytes.Length)
            {
                continue;
            }

            var bits = OnfiPublic.SetBits(page.Bytes.AsSpan(field.Offset, field.Length));
            var text = bits.Count == 0 ? $"{field.Name}：支持位均为 0" : $"{field.Name} " + string.Join(", ", bits);
            var current = Value(bag, "timingModes");
            Set(bag, "timingModes", "时序模式", current is { Missing: false, Value: { } existing } ? existing + " · " + text : text, $"参数页偏移 {field.Offset}（参数卡字段 {field.Name}）");
        }

        if (page?.Interfaces.Contains("NV-DDR3") == true && card.TimingModeFields.All(field => !field.Name.Contains("ddr3", StringComparison.OrdinalIgnoreCase)))
        {
            var current = Value(bag, "timingModes");
            if (current is { Missing: false, Value: { } text, Source: { } modeSource } && !text.Contains("NV-DDR3", StringComparison.Ordinal))
            {
                Set(bag, "timingModes", "时序模式", text + "；NV-DDR3 时序模式偏移待补", modeSource + "；NV-DDR3 偏移未在参数卡给出");
            }
            else if (current is not { Missing: false })
            {
                Missing(bag, "timingModes", "时序模式", "特征位标明 NV-DDR3，但参数卡没有给出时序模式偏移。");
            }
        }
    }

    private static void CompareGeometry(Dictionary<string, SourcedField> bag, SkuCard? card, bool blocked, List<string> conflicts)
    {
        if (card is null || blocked || card.Geometry.Source == "unknown")
        {
            return;
        }

        Compare(bag, conflicts, "pageDataBytes", "页大小", card.Geometry.PageDataBytes);
        if (card.Geometry.SpareConflicts.Count <= 1)
        {
            Compare(bag, conflicts, "spareBytes", "spare", card.Geometry.SpareBytes);
        }

        Compare(bag, conflicts, "pagesPerBlock", "每块页数", card.Geometry.PagesPerBlock);
        Compare(bag, conflicts, "blocksPerLun", "每 LUN 块数", card.Geometry.BlocksPerLun);
        Compare(bag, conflicts, "lunsPerCe", "每 CE 的 LUN", card.Geometry.LunsPerCe);
        Compare(bag, conflicts, "bitsPerCell", "每单元位数", card.Geometry.BitsPerCell);
    }

    private static void Compare(Dictionary<string, SourcedField> bag, List<string> conflicts, string key, string label, long? cardValue)
    {
        if (cardValue is null || Value(bag, key) is not { Missing: false, Value: { } text, Source: { } source })
        {
            return;
        }

        if (!source.Contains("参数页", StringComparison.Ordinal))
        {
            return;
        }

        if (long.TryParse(Digits(text), out var number) && number != cardValue)
        {
            conflicts.Add($"{label}：参数页为 {number}，参数卡为 {cardValue}。不选取。");
            Clear(bag, key);
            Missing(bag, key, label, "参数页与参数卡冲突。");
        }
    }

    private static void ApplyBitFields(Dictionary<string, SourcedField> bag, SkuCard? card, RawCapture capture, List<string> conflicts)
    {
        if (card is null)
        {
            return;
        }

        foreach (var rule in card.IdBitFields)
        {
            var bytes = rule.Address switch
            {
                "00" => capture.Id00,
                "20" => capture.Id20,
                "40" => capture.Id40,
                _ => null
            };
            var label = LabelOf(rule.Field);
            if (bytes is null || rule.ByteIndex < 0 || rule.ByteIndex >= bytes.Length)
            {
                Missing(bag, rule.Field, label, rule.Note ?? "ID 字节不足，位域没有读到。");
                continue;
            }

            if (rule.Incomplete || rule.Maps.Count == 0)
            {
                Missing(bag, rule.Field, label, rule.Note ?? "参数卡标明该字节的分位表不完整，不解码。");
                continue;
            }

            var extracted = (bytes[rule.ByteIndex] & rule.Mask) >> rule.Shift;
            var map = rule.Maps.FirstOrDefault(item => item.Value == extracted);
            if (map is null)
            {
                Missing(bag, rule.Field, label, $"ID {rule.Address}h 字节 {rule.ByteIndex} 的位值 {extracted} 不在参数卡对照表中。");
                continue;
            }

            var text = map.Number?.ToString() ?? map.Text;
            if (text is null)
            {
                Missing(bag, rule.Field, label, "对照表没有给出数值。");
                continue;
            }

            var source = $"ID {rule.Address}h 字节 {rule.ByteIndex}";
            var existing = Value(bag, rule.Field);
            if (existing is { Missing: false, Value: { } current } && !string.Equals(Digits(current), Digits(text), StringComparison.Ordinal) && !string.Equals(current, text, StringComparison.Ordinal))
            {
                conflicts.Add($"{label}：{existing.Source} 为 {current}，{source} 为 {text}。不选取。");
                Clear(bag, rule.Field);
                Missing(bag, rule.Field, label, "ID 与另一来源冲突。");
                continue;
            }

            if (existing is not { Missing: false })
            {
                Set(bag, rule.Field, label, text, source);
            }
        }
    }

    private static void ApplyUid(Dictionary<string, SourcedField> bag, SkuCard? card, byte[]? data)
    {
        if (card?.UniqueIdSupported == false)
        {
            Missing(bag, "uniqueId", "Unique ID", "参数卡标明该料号没有 Read Unique ID。");
            return;
        }

        if (data is null || data.Length == 0)
        {
            Missing(bag, "uniqueId", "Unique ID", card?.UniqueIdSupported is null ? "没有 Unique ID 字节，参数卡也未说明是否支持。" : "没有 Unique ID 字节。");
            return;
        }

        var copies = card?.UniqueIdCopies;
        var copyBytes = card?.UniqueIdCopyBytes;
        var uidBytes = card?.UniqueIdBytes;
        var layout = card?.UniqueIdLayout;
        if (layout == "onfi-public" || card is null)
        {
            copies ??= 16;
            copyBytes ??= 32;
            uidBytes ??= 16;
        }

        if (copies is null || copyBytes is null || uidBytes is null)
        {
            Missing(bag, "uniqueId", "Unique ID", "参数卡没有给出份数和长度，不套用默认值。");
            return;
        }

        var result = UniqueIdCheck.Inspect(data, copies.Value, copyBytes.Value, uidBytes.Value);
        if (!result.Valid || result.Uid is null)
        {
            Missing(bag, "uniqueId", "Unique ID", result.Note);
            return;
        }

        var citation = layout == "onfi-public" || card is null ? OnfiPublic.UniqueIdCitation : "参数卡给出的 Unique ID 份数与补码规则";
        Set(bag, "uniqueId", "Unique ID", HexText.Format(result.Uid), $"{result.Note} {citation}");
    }

    private static string ResolveState(SkuCard? card, SkuPart? part, bool blocked, List<string> conflicts, int candidateCount)
    {
        if (conflicts.Count > 0 || blocked)
        {
            return Recognition.Inconsistent;
        }

        if (part is { PartNumberKnown: true } && !string.IsNullOrWhiteSpace(card?.DatasheetRevision) && candidateCount == 1)
        {
            return Recognition.Identified;
        }

        if (card is not null)
        {
            return Recognition.Partial;
        }

        return Recognition.Unrecognized;
    }

    private static void EnsureAll(Dictionary<string, SourcedField> bag, SkuCard? card)
    {
        foreach (var (key, label) in Order)
        {
            if (!bag.ContainsKey(key))
            {
                var reason = card?.Pending.FirstOrDefault(item => item.Field == key)?.Reason;
                Missing(bag, key, label, reason);
            }
        }
    }

    private static string DescribeConsistency(IReadOnlyList<CaptureJudgement> captures)
    {
        if (captures.Count < 2)
        {
            return "只有一笔读出，无从比较 LUN。";
        }

        var diffs = new List<string>();
        foreach (var (key, label) in Order)
        {
            var values = captures.Select(item => item.Fields.First(field => field.Key == key).Value ?? "").Distinct(StringComparer.Ordinal).ToList();
            if (values.Count > 1)
            {
                diffs.Add(label);
            }
        }

        return diffs.Count == 0 ? "各 LUN 已填写字段一致。" : "不一致：" + string.Join("、", diffs) + "。";
    }

    private static bool HasCopySignature(byte[] data, int copyLength, ReadOnlySpan<byte> signature)
    {
        if (data.Length >= signature.Length && data.Length < copyLength)
        {
            return OnfiPublic.StartsWith(data, signature);
        }

        for (var offset = 0; offset + copyLength <= data.Length; offset += copyLength)
        {
            if (OnfiPublic.StartsWith(data.AsSpan(offset), signature))
            {
                return true;
            }
        }

        return false;
    }

    private static bool SamePart((SkuCard Card, SkuPart Part) left, (SkuCard Card, SkuPart Part) right) =>
        left.Card.CardId == right.Card.CardId && left.Part.PartNumber == right.Part.PartNumber;

    private static bool MatchesModel(SkuPart part, string model) =>
        string.Equals(part.PartNumber, model, StringComparison.Ordinal) || part.ModelNames.Any(item => string.Equals(item, model, StringComparison.Ordinal));

    private static string FormatNv(IReadOnlyList<string> interfaces) => interfaces.Count == 0 ? "" : " · " + string.Join(" · ", interfaces);

    private static string BitsLabel(int bits) => bits switch
    {
        1 => "1（SLC）",
        2 => "2（MLC）",
        3 => "3（TLC）",
        4 => "4（QLC）",
        _ => bits.ToString()
    };

    private static string LabelOf(string key) => Order.FirstOrDefault(item => item.Key == key).Label ?? key;

    private static string Digits(string text)
    {
        var end = 0;
        while (end < text.Length && char.IsDigit(text[end]))
        {
            end++;
        }

        return end == 0 ? text : text[..end];
    }

    private static void FillGeometryGap(Dictionary<string, SourcedField> bag, string key, string label, long? number, string source)
    {
        if (number is null || !ValueMissing(bag, key))
        {
            return;
        }

        Set(bag, key, label, key == "bitsPerCell" ? BitsLabel((int)number.Value) : number.Value.ToString(), source);
    }

    private static void SetNumber(Dictionary<string, SourcedField> bag, string key, string label, long? number, string source)
    {
        if (number is null)
        {
            return;
        }

        Set(bag, key, label, number.Value.ToString(), source);
    }

    private static void SetIfEmpty(Dictionary<string, SourcedField> bag, string key, string label, string value, string source)
    {
        if (ValueMissing(bag, key))
        {
            Set(bag, key, label, value, source);
        }
    }

    private static bool ValueMissing(Dictionary<string, SourcedField> bag, string key) => Value(bag, key) is not { Missing: false };

    private static SourcedField? Value(Dictionary<string, SourcedField> bag, string key) => bag.TryGetValue(key, out var field) ? field : null;

    private static void Set(Dictionary<string, SourcedField> bag, string key, string label, string value, string source) =>
        bag[key] = new SourcedField(key, label, value, source, false, null);

    private static void Missing(Dictionary<string, SourcedField> bag, string key, string label, string? reason)
    {
        if (bag.TryGetValue(key, out var existing) && !existing.Missing)
        {
            return;
        }

        bag[key] = new SourcedField(key, label, null, null, true, reason);
    }

    private static void Clear(Dictionary<string, SourcedField> bag, string key) => bag.Remove(key);

    private static string? HexOrNull(byte[]? data) => data is null || data.Length == 0 ? null : HexText.Format(data);
}
