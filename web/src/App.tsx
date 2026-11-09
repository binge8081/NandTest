import { useEffect, useMemo, useState } from "react";
import { api } from "./api";
import type { ScopeItem } from "./types";
import type { LotView, MaterialView, ScriptDiagnostic, ScriptDocument, ScriptLog, ScriptPlanView } from "./api";

function configurationScript(name: string, seed: number, block: number, pages: number): string {
  return `name ${name}\nseed ${seed}\nvar block = ${block}\nvar pages = ${pages}\nerase block\nloop pages\n  program block i\n  read block i\n  compare block i\nend\n`;
}

export function App() {
  return <ProductEntry />;
}

function ProductEntry() {
  const [framework, setFramework] = useState("");
  const [scope, setScope] = useState<ScopeItem[]>([]);
  const [configName, setConfigName] = useState("页编程比较");
  const [seed, setSeed] = useState(1);
  const [block, setBlock] = useState(0);
  const [pages, setPages] = useState(1);
  const [scriptText, setScriptText] = useState(() => configurationScript("页编程比较", 1, 0, 1));
  const [diagnostics, setDiagnostics] = useState<ScriptDiagnostic[]>([]);
  const [plan, setPlan] = useState<ScriptPlanView | null>(null);
  const [logs, setLogs] = useState<ScriptLog[]>([]);
  const [scriptId, setScriptId] = useState<string | null>(null);
  const [saved, setSaved] = useState<ScriptDocument[]>([]);
  const [sha, setSha] = useState("");
  const [note, setNote] = useState<string | null>(null);
  const [discoveryReason, setDiscoveryReason] = useState("正在读取发现状态。");
  const [materials, setMaterials] = useState<MaterialView | null>(null);
  const [snapshots, setSnapshots] = useState<{ snapshotId: string; status: string; errorClass: string; bytesEmitted: number; hardwareRecovery: boolean; patternShas: string; reason: string }[]>([]);
  const [scriptMap, setScriptMap] = useState<{ name: string; status: string; gap: string }[]>([]);
  const [about, setAbout] = useState<{ version: string; commit: string; buildTimeUtc: string; runtimeMode: string; uiMatchesApi: boolean } | null>(null);

  useEffect(() => {
    api.health().then((health) => setFramework(health.framework)).catch(() => setFramework(""));
    api.meta().then((meta) => setScope(meta.items)).catch(() => setScope([]));
    api.discovery().then((body) => setDiscoveryReason(body.reason)).catch(() => setDiscoveryReason("无法读取发现状态。没有发送探测。"));
    api.materials().then(setMaterials).catch(() => setMaterials(null));
    api.scriptList().then((body) => setSaved(body.scripts)).catch(() => setSaved([]));
    api.scriptMap().then((body) => setScriptMap(body.items)).catch(() => setScriptMap([]));
    api.snapshots().then((body) => setSnapshots(body.snapshots)).catch(() => setSnapshots([]));
    api.about().then(setAbout).catch(() => setAbout(null));
  }, []);

  useEffect(() => {
    const handle = window.setTimeout(() => {
      api.scriptCheck(scriptText).then((result) => {
        setDiagnostics(result.diagnostics);
        setPlan(result.plan);
      }).catch(() => setDiagnostics([{ line: 1, column: 1, message: "校验请求失败" }]));
    }, 250);
    return () => window.clearTimeout(handle);
  }, [scriptText]);

  const remaining = useMemo(() => scope.filter((item) => !item.status.includes("代码测试")), [scope]);

  function writeConfiguration() {
    setScriptText(configurationScript(configName.trim() || "页编程比较", seed, block, pages));
    setNote("配置已写入自定义脚本。这只是文本，硬件未执行。");
    setLogs([]);
  }

  async function refreshList() {
    const body = await api.scriptList();
    setSaved(body.scripts);
  }

  async function createScript() {
    setNote(null);
    const created = await api.scriptCreate(scriptText);
    setScriptId(created.script.scriptId);
    setSha(created.script.sha256);
    setDiagnostics(created.diagnostics);
    setNote(created.ok ? "已新建。硬件未执行。" : "已保存文本，但校验未通过，不会执行。");
    await refreshList();
  }

  async function saveScript() {
    setNote(null);
    if (!scriptId) {
      await createScript();
      return;
    }
    const savedScript = await api.scriptSave(scriptId, scriptText);
    setSha(savedScript.script.sha256);
    setDiagnostics(savedScript.diagnostics);
    setPlan(null);
    setNote(savedScript.ok ? "已保存。硬件未执行。" : "已保存文本，但校验未通过，不会执行。");
    await refreshList();
  }

  async function openScript(id: string) {
    if (!id) {
      return;
    }
    const opened = await api.scriptOpen(id);
    setScriptId(opened.script.scriptId);
    setScriptText(opened.script.body);
    setSha(opened.script.sha256);
    setLogs(opened.logs);
    setConfigName(opened.script.name);
    setNote("已打开。打开不会执行硬件。");
  }

  async function planScript() {
    setNote(null);
    let id = scriptId;
    if (!id) {
      const created = await api.scriptCreate(scriptText);
      id = created.script.scriptId;
    } else {
      await api.scriptSave(id, scriptText);
    }
    setScriptId(id);
    try {
      const planned = await api.scriptPlan(id);
      setPlan(planned.plan);
      setLogs(planned.logs);
      setSha(planned.script.sha256);
      setNote(`主机计划已展开。擦除 ${planned.plan.eraseCount}，写入 ${planned.plan.programCount}，读回 ${planned.plan.readCount}，比较 ${planned.plan.compareCount}。错误计数未统计，错误分类未执行。这不是已完成的擦写读。硬件未执行。`);
      await refreshList();
      setSnapshots((await api.snapshots()).snapshots);
    } catch (error) {
      const body = (error as { body?: { diagnostics?: ScriptDiagnostic[] } }).body;
      setDiagnostics(body?.diagnostics ?? [{ line: 1, column: 1, message: "计划没有展开，硬件未执行。" }]);
      setPlan(null);
      setNote("校验未通过，没有展开计划，也没有执行。");
    }
  }

  async function stopScript() {
    if (!scriptId) {
      setNote("还没有保存的脚本。没有需要停止的发送。");
      return;
    }
    const stopped = await api.scriptStop(scriptId);
    setLogs(stopped.logs);
    setNote("已停止。停止发生在发送之前，硬件未执行。");
    await refreshList();
  }

  return (
    <div className="page">
      <header className="top">
        <div>
          <img className="brand-logo" src="/lingcun-logo.png" width={170} height={30} alt="STORLEAD" />
          <h1>领存NFA1000闪存测试仪</h1>
        </div>
        <p className="runtime">{framework || ".NET"} · 本机上位机</p>
      </header>
      <p className="fine">版本 {about?.version ?? "…"} · 提交 {about?.commit ?? "…"} · 构建 {about?.buildTimeUtc ?? "…"} · 模式 {about?.runtimeMode ?? "…"}{about?.uiMatchesApi ? " · 页面与接口提交一致" : ""}</p>
      <div className="banner" role="status">未连接。依赖实机的执行已禁用，不会改用其他介质。</div>

      <div className="layout">
        <aside className="panel device">
          <h2>发现与连接</h2>
          <span className="pill warn">未连接</span>
          <dl>
            <dt>连接状态</dt><dd>未连接</dd>
            <dt>已发现设备</dt><dd>无</dd>
            <dt>探测</dt><dd>未发送</dd>
          </dl>
          <p className="fine">{discoveryReason}</p>
          <button type="button" disabled>连接</button>
        </aside>

        <main>
          <section className="panel" id="device-params">
            <h2>器件参数</h2>
            <p className="fine">{materials?.reason ?? "正在读取参数状态。"}</p>
            <p className="fine">{materials?.temperatureTemplates?.reason ?? "三温只是未核实候选。未经 SKU 规格核实，不允许实机。"}</p>
            <p className="fine">{materials?.rate?.policySource}</p>
            <dl>
              <dt>料号</dt><dd>{materials?.partNumber ?? "未提供"}</dd>
              <dt>真实规格</dt><dd>{materials?.realConfigEnabled ? "已启用" : "未启用"}</dd>
              <dt>额定速率</dt><dd>{materials?.rate?.ratedMtPerS ?? "未提供"}</dd>
              <dt>量产验收上限</dt><dd>{materials?.rate?.capApplies && materials.rate.massProductionCapMtPerS != null ? `${materials.rate.massProductionCapMtPerS} MT/s` : "未适用"}</dd>
              <dt>实际覆盖</dt><dd>{materials?.rate?.actualCoveredMtPerS ?? "未测"}</dd>
            </dl>
          </section>

          <section className="panel" id="simple-run">
            <h2>配置</h2>
            <p className="fine">这里填写名称、种子、块和循环页数。写入后生成有界的擦写读比较脚本。没有连接的设备时不能执行。</p>
            <div className="form-grid">
              <label className="wide">配置名称
                <input value={configName} maxLength={64} onChange={(event) => setConfigName(event.target.value)} />
              </label>
              <NumberField label="种子" value={seed} min={0} max={4294967295} onChange={setSeed} />
              <NumberField label="块" value={block} min={0} max={65535} onChange={setBlock} />
              <NumberField label="循环次数" value={pages} min={1} max={32} onChange={setPages} />
            </div>
            <div className="row">
              <button type="button" onClick={writeConfiguration}>写入自定义脚本</button>
              <button type="button" className="primary" disabled>执行</button>
            </div>
            <p className="fine">执行已禁用：{discoveryReason} 未经 SKU 规格核实，不允许实机。</p>
          </section>

          <LotPanel />

          <section className="panel" id="expert-recipe">
            <div className="section-head">
              <h2>自定义脚本</h2>
              <span className="pill">主机计划</span>
            </div>
            <p className="fine">可声明变量、有界循环和 if 分支，也可写 BKERAS、PGPROG、PGRDCP、REPEAT、VSET08 这类命令。图案文件只能在这里选择上传，脚本里写 SETBUF 加资源编号，不写路径。INCRMT 等使用本机图案，与旧逐字节等价尚未证明。展开的是主机计划，不是已完成的擦写读。</p>
            <PatternUpload onUploaded={(item) => setNote(`已上传 ${item.id}，版本 ${item.version}，摘要 ${item.sha256}。可在脚本中写 SETBUF ${item.id}。`)} />
            <textarea aria-label="自定义脚本" spellCheck={false} value={scriptText} onChange={(event) => { setScriptText(event.target.value); setNote(null); setLogs([]); }} />
            {diagnostics.length > 0 && <ul className="errors">{diagnostics.map((item) => <li key={`${item.line}-${item.column}-${item.message}`}>{item.message}</li>)}</ul>}
            {note && <p className="fine">{note}</p>}
            {sha && <p className="fine">脚本哈希 <span className="mono">{sha}</span></p>}
            {plan && (
              <div>
                <p className="fine">主机计划 · {plan.executionState} · 擦除 {plan.eraseCount} · 写入 {plan.programCount} · 读回 {plan.readCount} · 比较 {plan.compareCount} · 错误计数未统计 · 错误分类未执行 · 不是已完成的擦写读 · 硬件未执行</p>
                <table>
                  <thead><tr><th>序号</th><th>行</th><th>步骤</th><th>块</th><th>页</th><th>状态</th></tr></thead>
                  <tbody>
                    {plan.actions.map((action) => (
                      <tr key={action.index}>
                        <td>{action.index}</td>
                        <td>{action.line}</td>
                        <td>{action.op}</td>
                        <td>{action.block}</td>
                        <td>{action.page ?? "—"}</td>
                        <td>{action.hardwareExecuted ? "异常" : "计划"}</td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            )}
            {logs.length > 0 && (
              <ul>
                {logs.map((item) => <li key={item.logId}>{item.kind} · {item.message}</li>)}
              </ul>
            )}
            <div className="row">
              <button type="button" onClick={() => void createScript()}>新建</button>
              <button type="button" onClick={() => void saveScript()}>保存</button>
              <label>打开
                <select value={scriptId ?? ""} onChange={(event) => void openScript(event.target.value)}>
                  <option value="">选择已保存脚本</option>
                  {saved.map((item) => <option key={item.scriptId} value={item.scriptId}>{item.name}</option>)}
                </select>
              </label>
              <button type="button" onClick={() => void planScript()}>展开主机计划</button>
              <button type="button" onClick={() => void stopScript()}>停止</button>
              {scriptId && <a href={`/api/scripts/${scriptId}/export.csv`}>导出</a>}
            </div>
            <details>
              <summary>脚本能力对照</summary>
              <ul>
                {scriptMap.map((item) => <li key={item.name}><strong>{item.name}</strong> · {item.status} · {item.gap}</li>)}
              </ul>
            </details>
          </section>

          <section className="panel" id="job-result">
            <h2>结果</h2>
            <p>未连接。没有实测指标。</p>
          </section>
        </main>
      </div>

      <section className="panel history">
        <h2>运行快照</h2>
        <p className="fine">这些是主机计划快照。发送字节为 0。主机中断不是硬件断点续传，也没有实测指标。</p>
        {snapshots.length === 0 && <p>还没有运行快照。</p>}
        {snapshots.length > 0 && (
          <table>
            <thead><tr><th>快照</th><th>状态</th><th>错误分类</th><th>发送字节</th><th>硬件恢复</th><th>图案摘要</th></tr></thead>
            <tbody>
              {snapshots.map((item) => (
                <tr key={item.snapshotId}>
                  <td className="mono">{item.snapshotId}</td>
                  <td>{item.status}</td>
                  <td>{item.errorClass}</td>
                  <td>{item.bytesEmitted}</td>
                  <td>{item.hardwareRecovery ? "是" : "否"}</td>
                  <td className="mono">{item.patternShas || "—"}</td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </section>

      <footer>
        <p>下面 {remaining.length} 项尚未实现，不能当作通过。</p>
        <details>
          <summary>查看尚未实现的项目</summary>
          <ul>
            {remaining.map((item) => (
              <li key={`${item.id}-${item.subItem}`}><strong>{item.id} {item.subItem}</strong> · {item.priority} · {item.status}</li>
            ))}
          </ul>
        </details>
        <p className="fine">内部测试不从本页进入，生产模式也不能调用它。</p>
      </footer>
    </div>
  );
}

function LotPanel() {
  const [name, setName] = useState("lot-a");
  const [retestLimit, setRetestLimit] = useState(1);
  const [candidate, setCandidate] = useState("");
  const [rawEnabled, setRawEnabled] = useState(false);
  const [quota, setQuota] = useState(64);
  const [industrial, setIndustrial] = useState(0);
  const [total, setTotal] = useState(10);
  const [label, setLabel] = useState("U1");
  const [note, setNote] = useState<string | null>(null);
  const [lot, setLot] = useState<LotView | null>(null);

  async function run(action: () => Promise<void>) {
    try {
      setNote(null);
      await action();
    } catch (error) {
      setNote(error instanceof Error ? error.message : "没有完成。");
    }
  }

  return (
    <section className="panel" id="lot">
      <h2>批次</h2>
      <p className="fine">先保存并锁定配置版本，再按该版本开批次。复测上限只能是 1 或 2。工业计划不能超过总片数的 10%。raw 默认关闭。这里的数量是主机计划，不是实测，也不分档。</p>
      <div className="form-grid">
        <label className="wide">配置名称
          <input value={name} maxLength={64} onChange={(event) => setName(event.target.value)} />
        </label>
        <label>复测上限
          <select value={retestLimit} onChange={(event) => setRetestLimit(Number(event.target.value))}>
            <option value={1}>1</option>
            <option value={2}>2</option>
          </select>
        </label>
        <label>温度候选
          <select value={candidate} onChange={(event) => setCandidate(event.target.value)}>
            <option value="">不选</option>
            <option value="commercial-0-70">commercial-0-70</option>
            <option value="commercial-0-85">commercial-0-85</option>
            <option value="industrial">industrial</option>
          </select>
        </label>
        <label>工业计划
          <input type="number" min={0} value={industrial} onChange={(event) => setIndustrial(Number(event.target.value))} />
        </label>
        <label>总片数
          <input type="number" min={1} value={total} onChange={(event) => setTotal(Number(event.target.value))} />
        </label>
      </div>
      <label className="row">
        <input type="checkbox" checked={rawEnabled} onChange={(event) => setRawEnabled(event.target.checked)} />
        打开主机 raw。限额
        <input type="number" min={1} max={65536} value={quota} disabled={!rawEnabled} onChange={(event) => setQuota(Number(event.target.value))} />
      </label>
      <div className="row">
        <button type="button" onClick={() => void run(async () => {
          const created = await api.configCreate({ name, retestLimit, temperatureCandidate: candidate, rawEnabled, rawQuotaBytes: rawEnabled ? quota : 0 });
          const locked = await api.configLock(created.configId, created.version);
          const opened = await api.lotOpen({ configId: locked.configId, version: locked.version, industrialPlanned: industrial, totalPlanned: total });
          setLot(opened);
          setNote(`已锁定版本 ${locked.version} 并开批次。器件许可没有打开。`);
        })}>锁定并开批次</button>
        <input aria-label="片号" value={label} maxLength={64} onChange={(event) => setLabel(event.target.value)} />
        <button type="button" disabled={!lot} onClick={() => void run(async () => setLot(await api.lotRecord(lot!.lotId, label)))}>记录</button>
        <button type="button" disabled={!lot} onClick={() => void run(async () => setLot(await api.lotRetest(lot!.lotId, label)))}>复测</button>
        <button type="button" disabled={!lot || lot.status !== "open"} onClick={() => void run(async () => setLot(await api.lotClose(lot!.lotId)))}>关闭批次</button>
        {lot && <a href={`/api/lots/${lot.lotId}/export.csv`}>导出</a>}
      </div>
      {note && <p className="fine">{note}</p>}
      {lot && (
        <table>
          <thead><tr><th>片号</th><th>次数</th><th>状态</th><th>换座</th><th>分档</th><th>未覆盖</th></tr></thead>
          <tbody>
            {lot.attempts.map((item) => (
              <tr key={item.attemptId}>
                <td>{item.label}</td>
                <td>{item.attemptIndex}</td>
                <td>{item.status}</td>
                <td>{item.socketChangeRequired ? "是" : "否"}</td>
                <td>{item.gradesAssigned ? item.grade : "未分档"}</td>
                <td>{item.uncovered.map((gap) => gap.code).join(" ") || "—"}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </section>
  );
}

function PatternUpload(props: { onUploaded: (item: { id: string; version: string; sha256: string }) => void }) {
  const [pageSize, setPageSize] = useState(16);
  const [version, setVersion] = useState("v1");
  const [error, setError] = useState<string | null>(null);
  return (
    <div className="row">
      <label>页长度
        <input type="number" min={1} step={1} value={pageSize} onChange={(event) => setPageSize(Number(event.target.value))} />
      </label>
      <label>版本
        <input value={version} maxLength={64} onChange={(event) => setVersion(event.target.value)} />
      </label>
      <label>选择图案文件
        <input type="file" aria-label="选择图案文件" onChange={(event) => {
          const file = event.target.files?.[0];
          if (!file) return;
          setError(null);
          void api.uploadPattern(file, pageSize, version).then(props.onUploaded).catch((reason: Error) => setError(reason.message));
        }} />
      </label>
      <p className="fine">64 KiB 是当前主机限额，不是器件页长上限。未核实的页长不能当作物理规格。</p>
      {error && <p className="error">{error}</p>}
    </div>
  );
}

function NumberField(props: { label: string; value: number; min: number; max: number; step?: number; onChange: (value: number) => void }) {
  return (
    <label>
      {props.label}
      <input type="number" min={props.min} max={props.max} step={props.step ?? 1} value={Number.isFinite(props.value) ? props.value : 0} onChange={(event) => props.onChange(Number(event.target.value))} />
    </label>
  );
}
