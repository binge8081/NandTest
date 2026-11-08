import { useEffect, useMemo, useState } from "react";
import { api } from "./api";
import type { JobSummary, RecipeShape, ScopeItem } from "./types";
import type { MaterialView } from "./api";

const emptyRecipe = (): RecipeShape => ({
  schema: "nand.sim.recipe/1",
  name: "页编程比较",
  version: "1.0.0",
  seed: 1,
  paceMs: 0,
  topology: { channels: 1, ces: 1, luns: 1, planes: 1, blocks: 1, pagesPerBlock: 1, pageSizeBytes: 16 },
  pattern: { kind: "increment" },
  target: { channel: 0, ce: 0, lun: 0, plane: 0, block: 0, pageStart: 0, pageCount: 1 },
  inject: { mode: "none" }
});

function pretty(recipe: RecipeShape): string {
  return JSON.stringify(recipe, null, 2);
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
  const [recipeText, setRecipeText] = useState(() => pretty(emptyRecipe()));
  const [draft, setDraft] = useState<RecipeShape>(() => emptyRecipe());
  const [parseError, setParseError] = useState<string | null>(null);
  const [serverErrors, setServerErrors] = useState<string[]>([]);
  const [sha, setSha] = useState("");
  const [expertNote, setExpertNote] = useState<string | null>(null);
  const [discoveryReason, setDiscoveryReason] = useState("正在读取发现状态。");
  const [materials, setMaterials] = useState<MaterialView | null>(null);

  useEffect(() => {
    api.health().then((health) => setFramework(health.framework)).catch(() => setFramework(""));
    api.meta().then((meta) => setScope(meta.items)).catch(() => setScope([]));
    api.discovery().then((body) => setDiscoveryReason(body.reason)).catch(() => setDiscoveryReason("无法读取发现状态。没有发送探测。"));
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

  const remaining = useMemo(() => scope.filter((item) => !item.status.startsWith("部分")), [scope]);

  function applyText(next: string) {
    setRecipeText(next);
    try {
      const parsed = JSON.parse(next) as RecipeShape;
      setDraft(parsed);
      setParseError(null);
    } catch {
      setParseError("配方不是合法 JSON，不会执行。");
    }
  }

  function updateDraft(mutator: (recipe: RecipeShape) => void) {
    const next = structuredClone(draft);
    mutator(next);
    setDraft(next);
    setRecipeText(pretty(next));
    setParseError(null);
  }

  async function validateOnly() {
    setExpertNote(null);
    if (parseError) {
      setExpertNote("配方不是合法 JSON，未执行。");
      return;
    }
    const result = await api.validate(recipeText);
    setServerErrors(result.errors ?? []);
    setSha(result.sha256 ?? "");
    setExpertNote(result.ok ? "配方校验通过。设备未连接，执行仍保持禁用。" : "配方未通过校验，未执行。");
  }

  return (
    <div className="page">
      <header className="top">
        <div>
          <p className="eyebrow">新平台 NAND 测试</p>
          <h1>设备测试</h1>
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
            <dl>
              <dt>料号</dt><dd>{materials?.partNumber ?? "未提供"}</dd>
              <dt>真实规格</dt><dd>{materials?.realConfigEnabled ? "已启用" : "未启用"}</dd>
              <dt>额定速率</dt><dd>{materials?.rate.ratedMtPerS ?? "未提供"}</dd>
              <dt>量产验收上限</dt><dd>{materials ? `${materials.rate.massProductionCapMtPerS} MT/s` : "未提供"}</dd>
              <dt>实际覆盖</dt><dd>{materials?.rate.actualCoveredMtPerS ?? "未测"}</dd>
            </dl>
          </section>

          <section className="panel" id="simple-run">
            <h2>配方</h2>
            <p className="fine">在这里编写要下发的配方。没有连接的设备时不能执行。</p>
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
              <NumberField label="页字节" value={draft.topology?.pageSizeBytes ?? 16} min={16} max={4096} step={16} onChange={(value) => updateDraft((recipe) => { recipe.topology.pageSizeBytes = value; })} />
              <NumberField label="目标块" value={draft.target?.block ?? 0} min={0} max={31} onChange={(value) => updateDraft((recipe) => { recipe.target.block = value; })} />
              <NumberField label="起始页" value={draft.target?.pageStart ?? 0} min={0} max={63} onChange={(value) => updateDraft((recipe) => { recipe.target.pageStart = value; })} />
              <NumberField label="页数" value={draft.target?.pageCount ?? 1} min={1} max={64} onChange={(value) => updateDraft((recipe) => { recipe.target.pageCount = value; })} />
              <label>图案
                <select value={draft.pattern?.kind ?? "increment"} onChange={(event) => updateDraft((recipe) => { recipe.pattern.kind = event.target.value; })}>
                  <option value="increment">increment</option>
                  <option value="constant">constant</option>
                  <option value="prng">prng</option>
                </select>
              </label>
              <NumberField label="种子" value={draft.seed ?? 0} min={0} max={4294967295} onChange={(value) => updateDraft((recipe) => { recipe.seed = value; })} />
            </div>
            <div className="row">
              <button type="button" className="primary" disabled>执行</button>
            </div>
            <p className="fine">执行已禁用：{discoveryReason}</p>
          </section>

          <section className="panel" id="expert-recipe">
            <div className="section-head">
              <h2>专家配方</h2>
              <span className="pill">新配方</span>
            </div>
            <p className="fine">在这里编辑和校验配方文本。未知字段、越界和未批准版本会被拒绝，并且不会执行。当前校验还不是完整专业脚本。</p>
            <textarea aria-label="配方文本" spellCheck={false} value={recipeText} onChange={(event) => { applyText(event.target.value); setExpertNote(null); }} />
            {parseError && <p className="error">{parseError}</p>}
            {serverErrors.length > 0 && <ul className="errors">{serverErrors.map((error) => <li key={error}>{error}</li>)}</ul>}
            {expertNote && <p className="fine">{expertNote}</p>}
            {sha && <p className="fine">配方哈希 <span className="mono">{sha}</span></p>}
            <div className="row">
              <button type="button" onClick={() => void validateOnly()}>校验</button>
            </div>
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
        <p><a href="#/">返回设备测试</a></p>
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
