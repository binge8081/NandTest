import type { DeviceInfo, JobDetail, JobSummary, ScopeItem } from "./types";

export interface SessionExecution {
  sessionId: string;
  stateText: string;
  hardwareAccepted: boolean;
  bytesEmitted: number;
  conclusion: string;
  etaNote: string;
  commands: { commandId: string; intentText: string; stateText: string; sent: boolean; reason: string; nextStep: string }[];
}

export interface MaterialView {
  realConfigEnabled: boolean;
  partNumber: string | null;
  reason: string;
  rate: { massProductionCapMtPerS: number | null; capApplies: boolean; ratedMtPerS: number | null; actualCoveredMtPerS: number | null; policySource: string };
  temperatureTemplates: { devicePermissionGranted: boolean; realDeviceAllowed: boolean; reason: string };
}

export interface ScriptDiagnostic {
  line: number;
  column: number;
  message: string;
}

export interface ScriptPlanView {
  eraseCount: number;
  programCount: number;
  readCount: number;
  compareCount: number;
  hardwareExecuted: boolean;
  executionState: string;
  actions: { index: number; line: number; op: string; block: number; page: number | null; hardwareExecuted: boolean; state: string }[];
}

export interface ScriptDocument {
  scriptId: string;
  name: string;
  body: string;
  sha256: string;
  status: string;
  eraseCount: number;
  programCount: number;
  readCount: number;
  compareCount: number;
  hardwareExecuted: boolean;
}

export interface ScriptLog {
  logId: number;
  kind: string;
  line: number | null;
  column: number | null;
  message: string;
  hardwareExecuted: boolean;
}

async function read<T>(response: Response): Promise<T> {
  const text = await response.text();
  const body = text ? JSON.parse(text) : {};
  if (!response.ok) {
    const message = body.message ?? body.errors?.join(" ") ?? response.statusText;
    const error = new Error(typeof message === "string" ? message : "请求失败");
    (error as Error & { body?: unknown; status?: number }).body = body;
    (error as Error & { status?: number }).status = response.status;
    throw error;
  }
  return body as T;
}

export function jobCsvUrl(jobId: string): string {
  return `/api/jobs/${jobId}/export.csv`;
}

export const api = {
  health: () => fetch("/api/health").then((response) => read<{ framework: string }>(response)),
  device: () => fetch("/api/devices/sim-dut-1").then((response) => read<DeviceInfo>(response)),
  discovery: () => fetch("/api/discovery").then((response) => read<{ connected: boolean; probed: boolean; reason: string; next: string }>(response)),
  meta: () =>
    fetch("/api/meta").then((response) =>
      read<{
        itemCount: number;
        implementedSubsetCount: number;
        items: ScopeItem[];
        reimplementationPolicy: string;
        expertEntryLocation: string;
        jsonEntryIsSslCompatible: boolean;
      }>(response)
    ),
  presets: () =>
    fetch("/api/recipes/presets").then((response) =>
      read<{ presets: { id: string; title: string; json: string }[] }>(response)
    ),
  validate: (recipeText: string) =>
    fetch("/api/recipes/validate", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: recipeText
    }).then(async (response) => {
      const body = await response.json();
      return {
        ok: response.ok && body.ok === true,
        errors: (body.errors ?? []) as string[],
        sha256: body.sha256 as string | undefined,
        capabilities: (body.capabilities ?? []) as { name: string; state: string; detail: string }[]
      };
    }),
  createJob: (recipeText: string) =>
    fetch("/api/jobs?deviceId=sim-dut-1", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: recipeText
    }).then((response) => read<{ jobId: string }>(response)),
  openSession: () => fetch("/api/sessions", { method: "POST" }).then((response) => read<{ sessionId: string }>(response)),
  executeSession: (sessionId: string, recipeText: string) =>
    fetch(`/api/sessions/${sessionId}/execute`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: recipeText
    }).then((response) => read<SessionExecution>(response)),
  materials: () => fetch("/api/materials").then((response) => read<MaterialView>(response)),
  scriptExample: () => fetch("/api/scripts/example").then((response) => read<{ body: string; notes: string }>(response)),
  scriptMap: () => fetch("/api/scripts/map").then((response) => read<{ items: { name: string; status: string; gap: string }[] }>(response)),
  uploadPattern: async (file: File, pageSize: number, version: string) => {
    const response = await fetch(`/api/patterns?name=${encodeURIComponent(file.name.replace(/[\\/:]/g, "-"))}&version=${encodeURIComponent(version)}&pageSize=${pageSize}`, {
      method: "POST",
      headers: { "Content-Type": "application/octet-stream" },
      body: file
    });
    return read<{ id: string; version: string; sha256: string; pageSize: number; pageCount: number; formulaNote: string }>(response);
  },
  scriptList: () => fetch("/api/scripts").then((response) => read<{ scripts: ScriptDocument[] }>(response)),
  scriptOpen: (scriptId: string) => fetch(`/api/scripts/${scriptId}`).then((response) => read<{ script: ScriptDocument; logs: ScriptLog[] }>(response)),
  scriptCheck: (body: string) =>
    fetch("/api/scripts/check", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ body })
    }).then(async (response) => {
      const payload = await response.json();
      return {
        ok: response.ok && payload.ok === true,
        diagnostics: (payload.diagnostics ?? []) as ScriptDiagnostic[],
        plan: (payload.plan ?? null) as ScriptPlanView | null,
        hardwareExecuted: false as const
      };
    }),
  scriptCreate: (body: string) =>
    fetch("/api/scripts", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ body })
    }).then((response) => read<{ ok: boolean; script: ScriptDocument; diagnostics: ScriptDiagnostic[] }>(response)),
  scriptSave: (scriptId: string, body: string) =>
    fetch(`/api/scripts/${scriptId}`, {
      method: "PUT",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ body })
    }).then((response) => read<{ ok: boolean; script: ScriptDocument; diagnostics: ScriptDiagnostic[] }>(response)),
  scriptPlan: (scriptId: string) =>
    fetch(`/api/scripts/${scriptId}/plan`, { method: "POST" }).then((response) =>
      read<{ executed: boolean; hardwareExecuted: boolean; bytesEmitted: number; plan: ScriptPlanView; logs: ScriptLog[]; script: ScriptDocument }>(response)
    ),
  scriptStop: (scriptId: string) =>
    fetch(`/api/scripts/${scriptId}/stop`, { method: "POST" }).then((response) =>
      read<{ executed: boolean; hardwareExecuted: boolean; bytesEmitted: number; script: ScriptDocument; logs: ScriptLog[] }>(response)
    ),
  draftLoopback: () => fetch("/api/management/draft/loopback", { method: "POST" }).then((response) => read<{ label: string; liveConnectAllowed: boolean }>(response)),
  jobs: () => fetch("/api/jobs").then((response) => read<{ jobs: JobSummary[] }>(response)),
  job: (jobId: string) => fetch(`/api/jobs/${jobId}`).then((response) => read<JobDetail>(response)),
  cancel: (jobId: string) =>
    fetch(`/api/jobs/${jobId}/cancel`, { method: "POST" }).then((response) => read<JobDetail>(response))
};
