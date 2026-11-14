import { useEffect, useState } from "react";
import { api, type CaptureInput, type IdentificationOverview, type IdentificationReport } from "./api";

const emptyCapture = (): CaptureInput => ({ label: "导入", id00: "", id20: "", id40: "", uniqueId: "", parameterPage: "" });

export function ReadIdPage(props: { onOpenChip: () => void }) {
  const [overview, setOverview] = useState<IdentificationOverview | null>(null);
  const [capture, setCapture] = useState<CaptureInput>(emptyCapture());
  const [captures, setCaptures] = useState<CaptureInput[]>([]);
  const [report, setReport] = useState<IdentificationReport | null>(null);
  const [runMessage, setRunMessage] = useState<string | null>(null);
  const [selected, setSelected] = useState<string[]>([]);
  const [cardId, setCardId] = useState("");
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  function refresh() {
    return api.identification().then((body) => {
      setOverview(body);
      if (!cardId && body.cards.length === 1) {
        setCardId(body.cards[0].cardId);
      }
    });
  }

  useEffect(() => {
    void refresh().catch(() => setError("读 ID 页面没有拿到接口。"));
  }, []);

  async function decode(next: CaptureInput[]) {
    setBusy(true);
    setError(null);
    try {
      const body = await api.decodeIdentification(next);
      setReport(body.report);
      setCaptures(next);
    } catch (reason) {
      setError(reason instanceof Error ? reason.message : "解码没有完成。");
    } finally {
      setBusy(false);
    }
  }

  async function useSample(kind: "identified" | "partial" | "unrecognized") {
    const samples = await api.identificationSamples();
    const sample = samples[kind];
    const next = { ...emptyCapture(), ...sample };
    setCapture(next);
    await decode([next]);
  }

  async function run() {
    setBusy(true);
    setError(null);
    try {
      const body = await api.runIdentification({
        cardId: cardId || null,
        targets: selected.length === 0 ? [] : selected
      });
      setRunMessage(body.message);
    } catch (reason) {
      setError(reason instanceof Error ? reason.message : "读取没有完成。");
    } finally {
      setBusy(false);
    }
  }

  const targets = overview?.dutMap.targets ?? [];
  const stateClass = report?.state === "已识别" ? "good" : report?.state === "不一致" ? "bad" : "warn";

  return (
    <div className="readid">
      <div className="page-head">
        <div>
          <p className="eyebrow">当前芯片</p>
          <h1>读 ID 识别</h1>
          <p className="fine">左侧填写和读取，右侧预览解码结果。没有设备时不发送。导入的十六进制只在本机解码。</p>
        </div>
      </div>

      <div className="readid-split">
      <div className="readid-work">
      <section className="panel">
        <h2>已载入参数卡</h2>
        <p className="fine">目录 {overview?.skuDirectory ?? "…"}。参数卡由用户放在本机，不随程序发布厂商字节。</p>
        {(overview?.skuErrors.length ?? 0) > 0 && <ul className="errors">{overview?.skuErrors.map((item) => <li key={item}>{item}</li>)}</ul>}
        {(overview?.cards.length ?? 0) === 0 && <p>还没有参数卡。缺失项不会被猜成数值。</p>}
        {overview?.cards.map((card) => (
          <div key={card.cardId}>
            <p><strong>{card.cardId}</strong> · {card.vendor ?? "厂商未给出"} · 资料版本 {card.revision ?? "未给出"}</p>
            {card.pending.length === 0 ? <p className="fine">这张卡没有待补参数。</p> : (
              <ul>
                {card.pending.map((item) => <li key={item.field}><span className="pill warn">待补参数</span> {item.field}：{item.reason}</li>)}
              </ul>
            )}
            {card.conflicts.map((item) => <p key={item.topic} className="error">{item.topic}：{item.detail}</p>)}
          </div>
        ))}
      </section>

      <section className="panel" id="read-run">
        <h2>读取选中的 LUN</h2>
        <p className="fine">{overview?.connectionReason}</p>
        {targets.length === 0 && <p>DUT_MAP 没有目标。{overview?.dutMap.errors.join(" ")}</p>}
        <div className="lun-list">
          {targets.map((target) => (
            <label key={target.id} className="check">
              <input type="checkbox" checked={selected.includes(target.id)} onChange={(event) => {
                setSelected(event.target.checked ? [...selected, target.id] : selected.filter((item) => item !== target.id));
              }} />
              {target.label}
            </label>
          ))}
        </div>
        <div className="row">
          <label>命令时序所用参数卡
            <select aria-label="参数卡" value={cardId} onChange={(event) => setCardId(event.target.value)}>
              <option value="">不指定</option>
              {overview?.cards.map((card) => <option key={card.cardId} value={card.cardId}>{card.cardId}</option>)}
            </select>
          </label>
          <button type="button" className="primary" disabled={busy} onClick={() => void run()}>读取</button>
          <button type="button" disabled={targets.length === 0} onClick={() => setSelected(targets.map((target) => target.id))}>全选已载入 LUN</button>
        </div>
        {runMessage && <div className="banner" role="status">{runMessage}</div>}
        {overview && (
          <details>
            <summary>线上格式尚未冻结的项目</summary>
            <ul>{overview.wireGaps.map((gap) => <li key={gap.id}><strong>{gap.id}</strong> · {gap.detail}</li>)}</ul>
          </details>
        )}
      </section>

      <section className="panel" id="read-import">
        <h2>导入原始字节</h2>
        <p className="fine">粘贴在别处抓到的十六进制。空着的项保持空白，不会补字节。</p>
        <div className="row">
          <button type="button" onClick={() => void useSample("identified")}>填入合成 ONFI 并解码</button>
          <button type="button" onClick={() => void useSample("partial")}>填入部分识别示例</button>
          <button type="button" onClick={() => void useSample("unrecognized")}>填入未识别示例</button>
        </div>
        <div className="form-grid">
          <label className="wide">标签
            <input aria-label="导入标签" value={capture.label} onChange={(event) => setCapture({ ...capture, label: event.target.value })} />
          </label>
          <HexField label="90h 地址 00h" value={capture.id00 ?? ""} onChange={(id00) => setCapture({ ...capture, id00 })} />
          <HexField label="90h 地址 20h" value={capture.id20 ?? ""} onChange={(id20) => setCapture({ ...capture, id20 })} />
          <HexField label="90h 地址 40h" value={capture.id40 ?? ""} onChange={(id40) => setCapture({ ...capture, id40 })} />
          <HexField label="Unique ID" value={capture.uniqueId ?? ""} onChange={(uniqueId) => setCapture({ ...capture, uniqueId })} />
          <HexField label="参数页" value={capture.parameterPage ?? ""} onChange={(parameterPage) => setCapture({ ...capture, parameterPage })} />
        </div>
        <div className="row">
          <button type="button" className="primary" disabled={busy} onClick={() => void decode([...captures, capture])}>解码这一笔</button>
          <button type="button" onClick={() => { setCaptures([]); setReport(null); setCapture(emptyCapture()); }}>清空</button>
        </div>
        {error && <p className="error">{error}</p>}
      </section>
      </div>

      <aside className="panel readid-preview" id="read-result" aria-label="预览">
        <div className="section-head">
          <h2>预览</h2>
          <span className={`pill ${report ? stateClass : "warn"}`}>{report?.state ?? "尚未解码"}</span>
        </div>
        {!report && (
          <div>
            <p>解码或读取之后，识别结果出现在这里。没有读到的字段保持空白。</p>
            <p className="fine">{overview?.connectionReason}</p>
            {(overview?.cards ?? []).flatMap((card) => card.pending.map((item) => (
              <p key={`${card.cardId}-${item.field}`}><span className="pill warn">待补参数</span> {card.cardId} · {item.field}</p>
            )))}
          </div>
        )}
        {report && (
          <div>
            <p>{report.summary}</p>
            <p className="fine">{report.consistency} 硬件未执行。</p>
            {report.conflicts.length > 0 && <ul className="errors">{report.conflicts.map((item) => <li key={item}>{item}</li>)}</ul>}
            {report.missingFields.length > 0 && (
              <div>
                <h3>缺失字段</h3>
                <ul>{report.missingFields.map((item) => <li key={item}>{item}</li>)}</ul>
              </div>
            )}
            <table>
              <caption>解码字段</caption>
              <thead><tr><th>字段</th><th>值</th><th>来源</th></tr></thead>
              <tbody>
                {report.fields.map((field) => (
                  <tr key={field.key}>
                    <td>{field.label}</td>
                    <td>{field.missing ? "—" : field.value}</td>
                    <td>{field.source ?? field.pendingReason ?? "未给出"}</td>
                  </tr>
                ))}
              </tbody>
            </table>
            {report.captures.map((item) => (
              <div key={item.label}>
                <h3>{item.label} · {item.state}</h3>
                <p className="mono">00h {item.id00Hex ?? "—"}</p>
                <p className="mono">20h {item.id20Hex ?? "—"}</p>
                <p className="mono">40h {item.id40Hex ?? "—"}</p>
                <p className="mono">UID {shortHex(item.uniqueIdHex)}</p>
                <p className="fine">参数页 {item.parameterPageBytes ?? 0} 字节</p>
              </div>
            ))}
            <div className="row">
              <button type="button" className="primary" onClick={() => void api.confirmChip().then(() => props.onOpenChip())}>确认写入芯片定义</button>
              <button type="button" onClick={props.onOpenChip}>手工填写</button>
            </div>
          </div>
        )}
      </aside>
      </div>
    </div>
  );
}

function shortHex(value: string | null): string {
  if (!value) {
    return "—";
  }
  const parts = value.split(" ");
  return parts.length <= 24 ? value : `${parts.slice(0, 24).join(" ")} …`;
}

function HexField(props: { label: string; value: string; onChange: (value: string) => void }) {
  return (
    <label className="wide">{props.label}
      <textarea aria-label={props.label} spellCheck={false} value={props.value} onChange={(event) => props.onChange(event.target.value)} />
    </label>
  );
}

export function ChipPage() {
  const [overview, setOverview] = useState<IdentificationOverview | null>(null);
  const [drafts, setDrafts] = useState<Record<string, string>>({});
  const [note, setNote] = useState<string | null>(null);

  function load() {
    return api.identification().then((body) => {
      setOverview(body);
      const next: Record<string, string> = {};
      body.chip.fields.forEach((field) => {
        next[field.key] = field.value ?? "";
      });
      setDrafts(next);
    });
  }

  useEffect(() => {
    void load();
  }, []);

  const proposal = overview?.report;

  return (
    <div>
      <div className="page-head">
        <div>
          <p className="eyebrow">当前芯片</p>
          <h1>芯片定义</h1>
          <p className="fine">确认后的字段给后续方案沿用。空白保持空白。手工改过的来源记为手工填写。</p>
        </div>
      </div>
      <section className="panel">
        <h2>当前定义</h2>
        {proposal && <p className="fine">最近一次识别：{proposal.state}。{proposal.summary}</p>}
        <table>
          <thead><tr><th>项目</th><th>当前值</th><th>来源</th></tr></thead>
          <tbody>
            {(overview?.chip.fields ?? []).map((field) => (
              <tr key={field.key}>
                <td>{field.label}</td>
                <td>
                  <input aria-label={field.label} value={drafts[field.key] ?? ""} onChange={(event) => setDrafts({ ...drafts, [field.key]: event.target.value })} />
                </td>
                <td>{field.missing ? "未给出" : field.source}</td>
              </tr>
            ))}
          </tbody>
        </table>
        <div className="row">
          <button type="button" className="primary" onClick={() => void api.confirmChip().then(() => load()).then(() => setNote("已按最近一次解码写入。冲突和空白字段没有被填上。"))}>确认写入</button>
          <button type="button" onClick={() => void Promise.all((overview?.chip.fields ?? []).filter((field) => (drafts[field.key] ?? "") !== (field.value ?? "")).map((field) => api.overrideChip(field.key, drafts[field.key] ?? ""))).then(() => load()).then(() => setNote("手工修改已记下。"))}>保存手工修改</button>
        </div>
        {note && <p className="fine">{note}</p>}
        <p className="fine">电压、温度和 ECC 只有参数卡或参数页真的给出时才出现。测试方案以后引用这一份，不在这里猜规格。</p>
      </section>
    </div>
  );
}
