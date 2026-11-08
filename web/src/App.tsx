import { useEffect, useMemo, useState } from "react";
import { api } from "./api";
import type { JobSummary, ScopeItem } from "./types";
import type { MaterialView, ScriptDiagnostic, ScriptDocument, ScriptLog, ScriptPlanView } from "./api";

function configurationScript(name: string, seed: number, block: number, pages: number): string {
  return `name ${name}\nseed ${seed}\nvar block = ${block}\nvar pages = ${pages}\nerase block\nloop pages\n  program block i\n  read block i\n  compare block i\nend\n`;
}

export function App() {
  const [entry, setEntry] = useState(window.location.hash === "#/dev-fixture" ? "fixture" : "product");
  useEffect(() => {
    const onHash = () => setEntry(window.location.hash === "#/dev-fixture" ? "fixture" : "product");
    window.addEventListener("hashchange", onHash);
    return () => window.removeEventListener("hashchange", onHash);
  }, []);
  return entry === "fixture" ? <FixtureEntry /> : <ProductEntry />;
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
  const [scriptMap, setScriptMap] = useState<{ name: string; status: string; gap: string }[]>([]);

  useEffect(() => {
    api.health().then((health) => setFramework(health.framework)).catch(() => setFramework(""));
    api.meta().then((meta) => setScope(meta.items)).catch(() => setScope([]));
    api.discovery().then((body) => setDiscoveryReason(body.reason)).catch(() => setDiscoveryReason("无法读取发现状态。没有发送探测。"));
    api.materials().then(setMaterials).catch(() => setMaterials(null));
    api.scriptList().then((body) => setSaved(body.scripts)).catch(() => setSaved([]));
    api.scriptMap().then((body) => setScriptMap(body.items)).catch(() => setScriptMap([]));
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

  const remaining = useMemo(() => scope.filter((item) => !item.status.startsWith("部分")), [scope]);

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
      setNote(`主机计划已展开。擦除 ${planned.plan.eraseCount}，写入 ${planned.plan.programCount}，读回 ${planned.plan.readCount}，比较 ${planned.plan.compareCount}。错误计数未统计。硬件未执行。`);
      await refreshList();
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

          <section className="panel" id="expert-recipe">
            <div className="section-head">
              <h2>自定义脚本</h2>
              <span className="pill">主机计划</span>
            </div>
            <p className="fine">可声明变量、有界循环和 if 分支。校验会标出行列。展开的是主机计划，硬件未执行。当前脚本不是完整专业脚本。</p>
            <textarea aria-label="自定义脚本" spellCheck={false} value={scriptText} onChange={(event) => { setScriptText(event.target.value); setNote(null); setLogs([]); }} />
            {diagnostics.length > 0 && <ul className="errors">{diagnostics.map((item) => <li key={`${item.line}-${item.column}-${item.message}`}>{item.message}</li>)}</ul>}
            {note && <p className="fine">{note}</p>}
            {sha && <p className="fine">脚本哈希 <span className="mono">{sha}</span></p>}
            {plan && (
              <div>
                <p className="fine">主机计划 · {plan.executionState} · 擦除 {plan.eraseCount} · 写入 {plan.programCount} · 读回 {plan.readCount} · 比较 {plan.compareCount} · 错误计数未统计 · 硬件未执行</p>
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
        <h2>结果记录</h2>
        <p className="fine">没有实测记录。未连接设备时不会生成实测指标。</p>
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
        <p><a href="#/dev-fixture">自动化测试夹具</a>是单独入口，不是本页的设备，也不会在无设备时自动使用。</p>
      </footer>
    </div>
  );
}

function FixtureEntry() {
  const [jobs, setJobs] = useState<JobSummary[]>([]);
  useEffect(() => {
    api.jobs().then((body) => setJobs(body.jobs)).catch(() => setJobs([]));
  }, []);
  return (
    <div className="page">
      <header className="top">
        <div>
          <p className="eyebrow">非产品入口</p>
          <h1>自动化测试夹具</h1>
        </div>
      </header>
      <div className="banner" role="status">这里只查看测试夹具留下的记录。来源保持为测试夹具，不能改标成实测。生产页面不会自动进入这里。</div>
      <section className="panel">
        <p className="fine">当前产品模式不在此启动夹具，也不会因为没有设备而改用夹具执行。</p>
        {jobs.length === 0 && <p>没有测试夹具记录。</p>}
        {jobs.length > 0 && (
          <table>
            <thead>
              <tr><th>记录编号</th><th>名称</th><th>来源</th><th>结论</th></tr>
            </thead>
            <tbody>
              {jobs.map((job) => (
                <tr key={job.jobId}>
                  <td className="mono">{job.jobId}</td>
                  <td>{job.recipeName}</td>
                  <td>测试夹具</td>
                  <td>{job.conclusion}</td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
        <p><a href="#/">返回领存NFA1000闪存测试仪</a></p>
      </section>
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
