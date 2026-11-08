import type { DeviceInfo, JobDetail, JobSummary, ScopeItem } from "./types";

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
      return { ok: response.ok && body.ok === true, errors: (body.errors ?? []) as string[], sha256: body.sha256 as string | undefined };
    }),
  createJob: (recipeText: string) =>
    fetch("/api/jobs?deviceId=sim-dut-1", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: recipeText
    }).then((response) => read<{ jobId: string }>(response)),
  jobs: () => fetch("/api/jobs").then((response) => read<{ jobs: JobSummary[] }>(response)),
  job: (jobId: string) => fetch(`/api/jobs/${jobId}`).then((response) => read<JobDetail>(response)),
  cancel: (jobId: string) =>
    fetch(`/api/jobs/${jobId}/cancel`, { method: "POST" }).then((response) => read<JobDetail>(response))
};
