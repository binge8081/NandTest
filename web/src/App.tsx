import { useEffect, useMemo, useState } from "react";
import { api, jobCsvUrl, type MaterialView, type SessionExecution } from "./api";
import type { DeviceInfo, JobDetail, JobSummary, RecipeShape, ScopeItem } from "./types";

const emptyRecipe = (): RecipeShape => ({
  schema: "nand.sim.recipe/1",
  name: "基础擦写读成功",
  version: "1.0.0",
  seed: 42,
  paceMs: 250,
  topology: { channels: 1, ces: 1, luns: 1, planes: 1, blocks: 2, pagesPerBlock: 4, pageSizeBytes: 64 },
  pattern: { kind: "increment" },
  target: { channel: 0, ce: 0, lun: 0, plane: 0, block: 0, pageStart: 0, pageCount: 4 },
  inject: { mode: "none" }
});

function pretty(recipe: RecipeShape): string {
  return JSON.stringify(recipe, null, 2);
}

export function App() {
  const [device, setDevice] = useState<DeviceInfo | null>(null);
  const [framework, setFramework] = useState("");
  const [scope, setScope] = useState<ScopeItem[]>([]);
  const [presets, setPresets] = useState<{ id: string; title: string; json: string }[]>([]);
  const [recipeText, setRecipeText] = useState(() => pretty({ ...emptyRecipe(), paceMs: 250 }));
  const [draft, setDraft] = useState<RecipeShape>(() => emptyRecipe());
  const [parseError, setParseError] = useState<string | null>(null);
  const [serverErrors, setServerErrors] = useState<string[]>([]);
  const [sha, setSha] = useState<string>("");
  const [jobs, setJobs] = useState<JobSummary[]>([]);
  const [selectedId, setSelectedId] = useState<string | null>(null);
  const [detail, setDetail] = useState<JobDetail | null>(null);
  const [notice, setNotice] = useState<{ reason: string; next: string } | null>(null);
  const [expertNote, setExpertNote] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [session, setSession] = useState<SessionExecution | null>(null);
  const [materials, setMaterials] = useState<MaterialView | null>(null);

  useEffect(() => {
    api.device().then(setDevice).catch((error: Error) => setNotice({ reason: error.message, next: "确认本机 API 已启动，然后刷新此页。" }));
    api.health().then((health) => setFramework(health.framework)).catch(() => setFramework(""));
    api.meta().then((meta) => setScope(meta.items)).catch(() => setScope([]));
    api.presets().then((body) => setPresets(body.presets)).catch(() => setPresets([]));
    api.jobs().then((body) => setJobs(body.jobs)).catch(() => setJobs([]));
    api.materials().then(setMaterials).catch(() => setMaterials(null));
  }, []);

  useEffect(() => {
    const handle = window.setTimeout(() => {
      if (parseError) {
        setServerErrors([]);
        setSha("");
        return;
      }
      api.validate(recipeText).then((result) => {
        setServerErrors(result.errors ?? []);
        setSha(result.sha256 ?? "");
      }).catch(() => setServerErrors(["校验请求失败"]));
    }, 250);
    return () => window.clearTimeout(handle);
  }, [recipeText, parseError]);

  useEffect(() => {
    let stop = false;
    let timer = 0;
    const tick = async () => {
      try {
        const list = await api.jobs();
        if (stop) return;
        setJobs(list.jobs);
        if (selectedId) {
          const job = await api.job(selectedId);
          if (stop) return;
          setDetail(job);
          timer = window.setTimeout(tick, job.status === "pending" || job.status === "running" ? 400 : 2000);
          return;
        }
      } catch (error) {
        if (!stop) setNotice({ reason: error instanceof Error ? error.message : "刷新失败", next: "稍后会自动重试。若持续失败，请确认服务仍在运行。" });
      }
      timer = window.setTimeout(tick, 2000);
    };
    void tick();
    return () => {
      stop = true;
      window.clearTimeout(timer);
    };
  }, [selectedId]);

  const active = detail?.status === "pending" || detail?.status === "running";
  const remaining = useMemo(() => scope.filter((item) => !item.status.startsWith("部分")), [scope]);

  function applyText(next: string) {
    setRecipeText(next);
    try {
      const parsed = JSON.parse(next) as RecipeShape;
      setDraft(parsed);
      setParseError(null);
    } catch {
      setParseError("配方不是合法 JSON，服务不会执行。");
    }
  }

  function updateDraft(mutator: (recipe: RecipeShape) => void) {
    const next = structuredClone(draft);
    mutator(next);
    setDraft(next);
    setRecipeText(pretty(next));
    setParseError(null);
  }

  function loadPreset(json: string) {
    const parsed = JSON.parse(json) as RecipeShape;
    parsed.paceMs = 250;
    applyText(pretty(parsed));
    setNotice(null);
  }

  async function validateOnly() {
    setExpertNote(null);
    if (parseError) {
      setExpertNote("专家配方不是合法 JSON，未执行。");
      return;
    }
    const result = await api.validate(recipeText);
    setServerErrors(result.errors ?? []);
    setSha(result.sha256 ?? "");
    setExpertNote(result.ok ? "专家配方已校验，尚未执行。" : "专家配方未通过校验，未执行。");
  }

  async function run() {
    setBusy(true);
    setNotice(null);
    try {
      const opened = await api.openSession();
      const executed = await api.executeSession(opened.sessionId, recipeText);
      setSession(executed);
      setSelectedId(null);
    } catch (error) {
      const body = (error as { body?: { details?: string[]; message?: string } }).body;
      setServerErrors(body?.details ?? []);
      const status = (error as { status?: number }).status;
      const reason = body?.message ?? (error instanceof Error ? error.message : "未能启动");
      const next = status === 409
        ? "等待当前任务结束，或先取消它，然后再运行。本次没有开始新的写入。"
        : "按列出的字段修改配方，再点「校验并不运行」。本次没有执行。"
      setNotice({ reason, next });
    } finally {
      setBusy(false);
    }
  }

  async function cancel() {
    if (!selectedId) return;
    setNotice({ reason: "已发送取消。", next: "等待状态变为已取消。尚未开始的页不会继续写入，这里不估计剩余时间。" });
    try {
      const job = await api.cancel(selectedId);
      setDetail(job);
    } catch (error) {
      setNotice({ reason: error instanceof Error ? error.message : "取消失败", next: "刷新本页查看任务是否仍在执行，然后再试一次。" });
    }
  }

  return (
    <div className="page">
      <header className="top">
        <div>
          <p className="eyebrow">新平台 NAND 测试 · Windows 11 x64 上位机</p>
          <h1>NAND 测试</h1>
        </div>
        <p className="runtime">{framework || ".NET"} · SQLite · 中文操作台</p>
      </header>
      <div className="banner" role="status">
        设备来源是新平台实机路径，当前未接入。器件 datasheet 与平台 ICD 不能互相代替；真实规格未启用。管理草案只读，默认禁止实机连接。测试夹具结果不能标实机通过。
      </div>
      {materials && (
        <p className="fine">器件资料：{materials.realConfigEnabled ? "已启用" : "未启用"}。{materials.reason} 量产验收上限 {materials.rate.massProductionCapMtPerS} MT/s，额定 {materials.rate.ratedMtPerS ?? "未提供"}，实际覆盖 {materials.rate.actualCoveredMtPerS ?? "未测"}。</p>
      )}
      {session && (
        <section className="panel">
          <h2>设备会话 {session.conclusion}</h2>
          <p className="fine">会话 {session.sessionId} · 状态 {session.stateText} · 已发送字节 {session.bytesEmitted} · 实机通过 {session.hardwareAccepted ? "是" : "否"}。{session.etaNote}</p>
          <ul>
            {session.commands.map((command) => (
              <li key={command.commandId}>{command.intentText} · {command.stateText} · {command.sent ? "已发送" : "未发送"}。{command.reason} {command.nextStep}</li>
            ))}
          </ul>
        </section>
      )}
      {notice && (
        <div className="notice" role="alert">
          <p><strong>原因</strong> {notice.reason}</p>
          <p><strong>下一步</strong> {notice.next}</p>
        </div>
      )}

      <div className="layout">
        <aside className="panel device">
          <h2>设备</h2>
          {device && (
            <>
              <span className="pill warn">{device.kindText}</span>
              <dl>
                <dt>设备编号</dt><dd>{device.deviceId}</dd>
                <dt>厂商 / 型号</dt><dd>{device.manufacturer} {device.model}</dd>
                <dt>标识</dt><dd className="mono">{device.idHex}</dd>
                <dt>参数页</dt><dd>合成 {device.parameterPageMagic}，{device.parameterPageLength} 字节</dd>
                <dt>状态</dt><dd>{device.status}</dd>
                <dt>吞吐</dt><dd>{device.throughput}</dd>
                <dt>电压</dt><dd>{device.voltage}</dd>
                <dt>温度</dt><dd>{device.temperature}</dd>
                <dt>Handler / 温箱</dt><dd>{device.handler} / {device.thermalChamber}</dd>
              </dl>
              <p className="fine">{device.disclaimer}</p>
            </>
          )}
        </aside>

        <main>
          <section className="panel" id="simple-run">
            <div className="section-head">
              <h2>简单操作</h2>
              <div className="row">
                {presets.map((preset) => (
                  <button key={preset.id} type="button" onClick={() => loadPreset(preset.json)}>{preset.title}</button>
                ))}
              </div>
            </div>
            <p className="fine">在本页直接选预设、改最小拓扑和图案，然后运行。不需要切到独立脚本软件。进度、取消和结果都留在下面。</p>
            <div className="form-grid">
              <label className="wide">配方名称
                <input value={draft.name ?? ""} maxLength={64} onChange={(event) => updateDraft((recipe) => { recipe.name = event.target.value; })} />
              </label>
              <NumberField label="通道" value={draft.topology?.channels ?? 1} min={1} max={4} onChange={(value) => updateDraft((recipe) => { recipe.topology.channels = value; })} />
              <NumberField label="CE" value={draft.topology?.ces ?? 1} min={1} max={4} onChange={(value) => updateDraft((recipe) => { recipe.topology.ces = value; })} />
              <NumberField label="LUN" value={draft.topology?.luns ?? 1} min={1} max={2} onChange={(value) => updateDraft((recipe) => { recipe.topology.luns = value; })} />
              <NumberField label="Plane" value={draft.topology?.planes ?? 1} min={1} max={2} onChange={(value) => updateDraft((recipe) => { recipe.topology.planes = value; })} />
              <NumberField label="块数" value={draft.topology?.blocks ?? 1} min={1} max={32} onChange={(value) => updateDraft((recipe) => { recipe.topology.blocks = value; })} />
              <NumberField label="每块页数" value={draft.topology?.pagesPerBlock ?? 1} min={1} max={64} onChange={(value) => updateDraft((recipe) => { recipe.topology.pagesPerBlock = value; })} />
              <NumberField label="页字节" value={draft.topology?.pageSizeBytes ?? 64} min={16} max={4096} step={16} onChange={(value) => updateDraft((recipe) => { recipe.topology.pageSizeBytes = value; })} />
              <NumberField label="目标块" value={draft.target?.block ?? 0} min={0} max={31} onChange={(value) => updateDraft((recipe) => { recipe.target.block = value; })} />
              <NumberField label="起始页" value={draft.target?.pageStart ?? 0} min={0} max={63} onChange={(value) => updateDraft((recipe) => { recipe.target.pageStart = value; })} />
              <NumberField label="页数" value={draft.target?.pageCount ?? 1} min={1} max={64} onChange={(value) => updateDraft((recipe) => { recipe.target.pageCount = value; })} />
              <label>图案
                <select value={draft.pattern?.kind ?? "increment"} onChange={(event) => updateDraft((recipe) => {
                  recipe.pattern.kind = event.target.value;
                  if (event.target.value === "constant") recipe.pattern.value = recipe.pattern.value ?? 165;
                  else delete recipe.pattern.value;
                })}>
                  <option value="increment">increment</option>
                  <option value="constant">constant</option>
                  <option value="prng">prng</option>
                </select>
              </label>
              {draft.pattern?.kind === "constant" && (
                <NumberField label="常数值" value={draft.pattern.value ?? 0} min={0} max={255} onChange={(value) => updateDraft((recipe) => { recipe.pattern.value = value; })} />
              )}
              <NumberField label="种子" value={draft.seed ?? 0} min={0} max={4294967295} onChange={(value) => updateDraft((recipe) => { recipe.seed = value; })} />
              <NumberField label="演示节拍 ms" value={draft.paceMs ?? 0} min={0} max={2000} onChange={(value) => updateDraft((recipe) => { recipe.paceMs = value; })} />
              <label>注入
                <select value={draft.inject?.mode ?? "none"} onChange={(event) => updateDraft((recipe) => {
                  recipe.inject.mode = event.target.value;
                  if (event.target.value === "bit_flip") {
                    recipe.inject.page = recipe.target.pageStart;
                    recipe.inject.byteOffset = 3;
                    recipe.inject.bit = 0;
                  } else {
                    delete recipe.inject.page;
                    delete recipe.inject.byteOffset;
                    delete recipe.inject.bit;
                  }
                })}>
                  <option value="none">无</option>
                  <option value="bit_flip">翻转 1 位</option>
                </select>
              </label>
              {draft.inject?.mode === "bit_flip" && (
                <>
                  <NumberField label="注入页" value={draft.inject.page ?? 0} min={0} max={63} onChange={(value) => updateDraft((recipe) => { recipe.inject.page = value; })} />
                  <NumberField label="字节" value={draft.inject.byteOffset ?? 0} min={0} max={4095} onChange={(value) => updateDraft((recipe) => { recipe.inject.byteOffset = value; })} />
                  <NumberField label="位" value={draft.inject.bit ?? 0} min={0} max={7} onChange={(value) => updateDraft((recipe) => { recipe.inject.bit = value; })} />
                </>
              )}
            </div>
            <p className="fine">拓扑上限只用于这块最小模拟介质，不是 1056/528 量产拓扑。演示节拍只为了看逐步进度，不是器件忙时间。</p>
            <div className="row">
              <button type="button" className="primary" disabled={busy || active || !!parseError} onClick={() => void run()}>提交到设备会话</button>
              <button type="button" disabled={!active} onClick={() => void cancel()}>取消</button>
            </div>
          </section>

          <section className="panel" id="expert-recipe">
            <div className="section-head">
              <h2>专家配方</h2>
              <span className="pill warn">受限 JSON · 不是 SSL</span>
            </div>
            <p className="fine">编辑和校验嵌在本测试页，执行后仍用上面的运行按钮，进度和结果留在本页。这是白名单 JSON（nand.sim.recipe/1），不是旧 SSL 语法，也不表示 SSL 兼容已经完成。未知字段、脚本和数组会被拒绝且不会执行。</p>
            <textarea aria-label="专家受限 JSON" spellCheck={false} value={recipeText} onChange={(event) => { applyText(event.target.value); setExpertNote(null); }} />
            {parseError && <p className="error">{parseError}</p>}
            {serverErrors.length > 0 && (
              <ul className="errors">
                {serverErrors.map((error) => <li key={error}>{error}</li>)}
              </ul>
            )}
            {expertNote && <p className="fine">{expertNote}</p>}
            {sha && <p className="fine">配方哈希 <span className="mono">{sha}</span></p>}
            <div className="row">
              <button type="button" onClick={() => void validateOnly()}>校验并不运行</button>
            </div>
          </section>

          <section className="panel" id="job-result">
            <div className="section-head">
              <h2>本次任务</h2>
              {detail && <StatusMark symbol={detail.overview.conclusionSymbol} text={detail.overview.conclusion} tone={detail.status} />}
            </div>
            {!detail && <p className="fine">还没有选中任务。运行后会显示当前步骤、进度、结论和可展开的明细。</p>}
            {detail && (
              <>
                <p className="marks">
                  <StatusMark symbol={detail.overview.simulationSymbol} text={detail.overview.simulationLabel} tone="simulation" />
                  <StatusMark symbol={detail.overview.conclusionSymbol} text={detail.overview.conclusion} tone={detail.status} />
                  <span className="fine">任务状态：{detail.statusText}</span>
                </p>
                <div className="progress" aria-live="polite">
                  <p><strong>当前步骤</strong> {detail.overview.currentStep} · {detail.overview.currentStepStatus}{detail.overview.currentStepCoverage === detail.overview.currentStepStatus ? "" : ` · ${detail.overview.currentStepCoverage}`}</p>
                  <p><strong>总体进度</strong> {detail.overview.stepProgressText}{detail.overview.pageProgressText ? ` · ${detail.overview.pageProgressText}` : ""}</p>
                  <p className="fine">{detail.overview.etaNote}</p>
                  {detail.overview.cancelFeedback && <p><strong>取消反馈</strong> {detail.overview.cancelFeedback}</p>}
                </div>
                <div className="conclusion">
                  <p><strong>结论</strong> {detail.overview.conclusionSymbol} {detail.overview.conclusion}。{detail.overview.reason}</p>
                  <p><strong>下一步</strong> {detail.overview.nextStep}</p>
                </div>
                <dl className="summary">
                  <dt>任务编号</dt><dd className="mono">{detail.jobId}</dd>
                  <dt>错误位数</dt><dd>{detail.overview.errorBitCount}</dd>
                  <dt>错误字节数</dt><dd>{detail.overview.errorByteCount}</dd>
                  <dt>比较字节</dt><dd>{detail.overview.matchedBytes === null ? "尚未比较" : `${detail.overview.matchedBytes}/${detail.overview.totalBytes}`}</dd>
                  <dt>已写入页</dt><dd>{detail.overview.pagesProgrammed === null ? "尚未写入" : `${detail.overview.pagesProgrammed}/${detail.overview.pageCount}`}</dd>
                  <dt>种子</dt><dd>{detail.seed}</dd>
                  <dt>配方哈希</dt><dd className="mono">{detail.recipeSha256}</dd>
                  <dt>结果性质</dt><dd>{detail.resultNature}</dd>
                </dl>
                <p className="fine">上面的计数与 CSV 的错误位数、错误字节数和任务编号一致。没有剩余时间估计。</p>
                {detail.substitutionReason && <p className="notice">原因：{detail.substitutionReason} 下一步：结果只代表模拟替身，不能当作这块未知硬件的实测。</p>}
                <section className="unimplemented" aria-label="未实现分析">
                  <h3>未实现分析</h3>
                  <ul>
                    {detail.overview.unimplemented.map((item) => <li key={item}>{item}，本版不展示图表。</li>)}
                  </ul>
                </section>
                <details>
                  <summary>展开逐步结果</summary>
                  <table>
                    <caption>步骤</caption>
                    <thead>
                      <tr><th>步骤序号</th><th>步骤名称</th><th>步骤状态</th><th>步骤覆盖</th><th>步骤说明</th></tr>
                    </thead>
                    <tbody>
                      {detail.steps.map((step) => (
                        <tr key={step.name}>
                          <td>{step.index}</td>
                          <td>{step.nameText}</td>
                          <td><StatusMark symbol={symbolFor(step.status)} text={step.statusText} tone={step.status} /></td>
                          <td>{step.coverageText}</td>
                          <td>{step.message}</td>
                        </tr>
                      ))}
                    </tbody>
                  </table>
                </details>
                <details open={detail.errors.length > 0}>
                  <summary>展开错误位置（{detail.errors.length}）</summary>
                  <table>
                    <caption>错误位置</caption>
                    <thead>
                      <tr><th>页</th><th>字节偏移</th><th>位</th><th>期望值</th><th>实际值</th></tr>
                    </thead>
                    <tbody>
                      {detail.errors.length === 0 && <tr><td colSpan={5}>没有不一致的位</td></tr>}
                      {detail.errors.map((error) => (
                        <tr key={`${error.page}-${error.byteOffset}-${error.bit}`}>
                          <td>{error.page}</td>
                          <td>{error.byteOffset}</td>
                          <td>{error.bit}</td>
                          <td className="mono">{error.expectedHex}</td>
                          <td className="mono">{error.actualHex}</td>
                        </tr>
                      ))}
                    </tbody>
                  </table>
                </details>
                <details>
                  <summary>展开配方快照</summary>
                  <p className="fine">任务编号 {detail.jobId} · 配方哈希 {detail.recipeSha256}</p>
                  <pre className="snapshot">{detail.recipeSnapshot}</pre>
                </details>
                <a className="csv" href={jobCsvUrl(detail.jobId)}>下载 CSV（任务编号、结论、计数与本页一致）</a>
              </>
            )}
          </section>
        </main>
      </div>

      <section className="panel history">
        <h2>历史</h2>
        <p className="fine">任务、配方快照、种子和逐步结果写在 SQLite。刷新页面或重启服务后仍可查询。未完成的任务在重启后标为已中断，不会接着写。</p>
        {jobs.length === 0 && <p className="fine">还没有历史任务。</p>}
        {jobs.length > 0 && (
          <table>
            <thead>
              <tr><th>任务编号</th><th>配方名称</th><th>种子</th><th>结论</th><th>错误位数</th><th>数据</th></tr>
            </thead>
            <tbody>
              {jobs.map((job) => (
                <tr key={job.jobId} className={job.jobId === selectedId ? "selected" : ""} onClick={() => setSelectedId(job.jobId)}>
                  <td className="mono">{job.jobId}</td>
                  <td>{job.recipeName}</td>
                  <td>{job.seed}</td>
                  <td><StatusMark symbol={job.conclusionSymbol} text={job.conclusion} tone={job.status} /></td>
                  <td>{job.errorBitCount}</td>
                  <td>{job.simulationLabel}</td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </section>

      <footer>
        <p>本页只交付首个模拟闭环。旧能力仍在 19 号全量清单里，后续按合法资料独立重写，不因界面不同而删除。下面 {remaining.length} 项这次没有实现，也不作验收通过。专家 JSON 不是 SSL 兼容。</p>
        <details>
          <summary>查看未实现范围</summary>
          <ul>
            {remaining.map((item) => (
              <li key={`${item.id}-${item.subItem}`}><strong>{item.id} {item.subItem}</strong> · {item.priority} · {item.status}</li>
            ))}
          </ul>
        </details>
      </footer>
    </div>
  );
}

function StatusMark(props: { symbol: string; text: string; tone: string }) {
  return (
    <span className={`mark ${props.tone}`}>
      <span aria-hidden="true">{props.symbol}</span> {props.text}
    </span>
  );
}

function symbolFor(status: string): string {
  switch (status) {
    case "succeeded": return "✓";
    case "failed": return "✕";
    case "cancelled": return "■";
    case "interrupted": return "!";
    case "running": return "…";
    case "pending": return "○";
    default: return "–";
  }
}

function NumberField(props: { label: string; value: number; min: number; max: number; step?: number; onChange: (value: number) => void }) {
  return (
    <label>
      {props.label}
      <input
        type="number"
        min={props.min}
        max={props.max}
        step={props.step ?? 1}
        value={Number.isFinite(props.value) ? props.value : 0}
        onChange={(event) => props.onChange(Number(event.target.value))}
      />
    </label>
  );
}
