export type JobStatus =
  | "pending"
  | "running"
  | "succeeded"
  | "failed"
  | "cancelled"
  | "interrupted"
  | "skipped";

export interface RecipeShape {
  schema: string;
  name: string;
  version: string;
  seed: number;
  paceMs?: number;
  topology: {
    channels: number;
    ces: number;
    luns: number;
    planes: number;
    blocks: number;
    pagesPerBlock: number;
    pageSizeBytes: number;
  };
  pattern: { kind: string; value?: number };
  target: {
    channel: number;
    ce: number;
    lun: number;
    plane: number;
    block: number;
    pageStart: number;
    pageCount: number;
  };
  inject: { mode: string; page?: number; byteOffset?: number; bit?: number };
}

export interface DeviceInfo {
  deviceId: string;
  requestedDeviceId: string;
  kind: string;
  kindText: string;
  manufacturer: string;
  model: string;
  disclaimer: string;
  substituted: boolean;
  substitutionReason: string | null;
  idHex: string;
  idLength: number;
  parameterPageLength: number;
  parameterPageMagic: string;
  status: string;
  throughput: string;
  voltage: string;
  temperature: string;
  handler: string;
  thermalChamber: string;
}

export interface JobStep {
  index: number;
  name: string;
  nameText: string;
  status: JobStatus;
  statusText: string;
  coverageText: string;
  message: string | null;
  detail: {
    pagesProgrammed?: number;
    pageCount?: number;
    totalBytes?: number;
    matchedBytes?: number;
    errorBitCount?: number;
  } | null;
}

export interface JobError {
  page: number;
  byteOffset: number;
  bit: number;
  expectedHex: string;
  actualHex: string;
}

export interface JobDetail {
  jobId: string;
  status: JobStatus;
  statusText: string;
  seed: number;
  recipeName: string;
  recipeVersion: string;
  recipeSchema: string;
  recipeSha256: string;
  recipeSnapshot: string;
  requestedDeviceId: string;
  deviceId: string;
  deviceKind: string;
  deviceKindText: string;
  deviceSubstituted: boolean;
  substitutionReason: string | null;
  errorBitCount: number;
  errorByteCount: number;
  failureReason: string | null;
  resultNature: string;
  cancelRequested: boolean;
  createdAt: string;
  startedAt: string | null;
  finishedAt: string | null;
  steps: JobStep[];
  errors: JobError[];
  overview: JobOverview;
}

export interface JobOverview {
  conclusion: string;
  conclusionSymbol: string;
  simulationLabel: string;
  simulationSymbol: string;
  currentStep: string;
  currentStepStatus: string;
  currentStepCoverage: string;
  completedSteps: number;
  totalSteps: number;
  stepProgressText: string;
  pageProgressText: string | null;
  eta: null;
  etaNote: string;
  reason: string;
  nextStep: string;
  cancelFeedback: string | null;
  errorBitCount: number;
  errorByteCount: number;
  matchedBytes: number | null;
  totalBytes: number | null;
  pagesProgrammed: number | null;
  pageCount: number | null;
  unimplemented: string[];
}

export interface JobSummary {
  jobId: string;
  status: JobStatus;
  statusText: string;
  seed: number;
  recipeName: string;
  recipeSha256: string;
  deviceId: string;
  deviceKindText: string;
  deviceSubstituted: boolean;
  errorBitCount: number;
  conclusion: string;
  conclusionSymbol: string;
  simulationLabel: string;
  resultNature: string;
  createdAt: string;
  finishedAt: string | null;
}

export interface ScopeItem {
  id: string;
  subItem: string;
  priority: string;
  bucket: string;
  status: string;
}
