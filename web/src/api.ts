// Thin fetch wrapper for the backend API. Cookie auth + CSRF header for state-changing calls.

export class ApiError extends Error {
  constructor(public status: number, public code: string, message: string, public details?: unknown) {
    super(message);
  }
}

async function request<T>(method: string, url: string, body?: unknown, headers: Record<string, string> = {}): Promise<T> {
  const init: RequestInit = { method, credentials: 'same-origin', headers: { 'X-YLM-CSRF': '1', ...headers } };
  if (body !== undefined) {
    if (typeof body === 'string') {
      init.body = body;
    } else {
      init.body = JSON.stringify(body);
      (init.headers as Record<string, string>)['Content-Type'] = 'application/json';
    }
  }
  const res = await fetch(url, init);
  if (res.status === 401 && !url.startsWith('/api/me') && !url.startsWith('/api/setup') && !url.startsWith('/auth/')) {
    window.location.href = '/login?returnUrl=' + encodeURIComponent(window.location.pathname + window.location.search);
  }
  if (!res.ok) {
    let code = 'error';
    let message = `Fehler ${res.status}`;
    let details: unknown;
    try {
      const j = await res.json();
      code = j.code ?? code;
      message = j.message ?? message;
      details = j.details;
    } catch {
      /* not json */
    }
    throw new ApiError(res.status, code, message, details);
  }
  if (res.status === 204 || res.status === 202 && res.headers.get('content-length') === '0') return undefined as T;
  const type = res.headers.get('content-type') ?? '';
  return (type.includes('json') ? await res.json() : await res.text()) as T;
}

export const api = {
  get: <T>(url: string, headers?: Record<string, string>) => request<T>('GET', url, undefined, headers),
  post: <T>(url: string, body?: unknown, headers?: Record<string, string>) => request<T>('POST', url, body ?? {}, headers),
  put: <T>(url: string, body: unknown, headers?: Record<string, string>) => request<T>('PUT', url, body, headers),
  patch: <T>(url: string, body: unknown) => request<T>('PATCH', url, body),
  del: <T>(url: string) => request<T>('DELETE', url),
};

export type Role = 'Owner' | 'Operator' | 'Helpdesk' | 'Auditor' | 'Custom';
export type KeyStatus = 'InStock' | 'Active' | 'Locked' | 'Retired';

export interface PlatformUser {
  id: string; upn: string; displayName: string; role: Role; customRoleId?: string | null; customRoleName?: string | null; allTenants: boolean; tenantIds: string[];
  isActive: boolean; createdAt: string; lastLoginAt?: string; linked: boolean;
  loginKind: LoginKind; passkeyCount: number; emergencyReady: boolean; setupPending: boolean; setupTokenExpiresAt?: string | null; lockedUntil?: string | null;
}

export type LoginKind = 'Entra' | 'Local' | 'Emergency';
export const loginKindLabel: Record<LoginKind, string> = { Entra: 'Entra-SSO', Local: 'Lokal mit Passkey', Emergency: 'Notfallkonto' };

export interface UserPasskey { id: string; name: string; aaguid: string; createdAt: string; lastUsedAt?: string | null }

export interface Me {
  authenticated: boolean; setupComplete: boolean; organizationName?: string; version?: string; user?: PlatformUser; permissions?: string[];
}

export interface AgentPackageInfo { available: boolean; version?: string | null; size?: number | null; sha256?: string | null; downloadUrl?: string | null }

/** "1.2.3" > "1.2.0"; unbekannte oder ungültige Versionen sind nie neuer. */
export function isNewer(candidate?: string | null, current?: string | null) {
  const parse = (v?: string | null) => v?.replace(/^v/i, '').split(/[-+]/)[0].split('.').map(Number);
  const a = parse(candidate), b = parse(current);
  if (!a || !b || a.some(isNaN) || b.some(isNaN)) return false;
  for (let i = 0; i < 3; i++) { const d = (a[i] ?? 0) - (b[i] ?? 0); if (d) return d > 0; }
  return false;
}

export interface GroupRef { id: string; name: string }

export interface TenantCheck { id: string; title: string; status: 'Ok' | 'Warning' | 'Failed'; message: string; canFix: boolean; manualInstructions: string }

export interface Tenant {
  id: string; entraTenantId: string; displayName: string; defaultDomain?: string; status: string; createdAt: string;
  consentedAt?: string; consentedBy?: string; targetIncludeGroups: GroupRef[]; targetExcludeGroups: GroupRef[]; syncIntervalMinutes: number;
  lastSyncAt?: string; lastSyncSucceeded?: boolean; lastSyncMessage?: string; targetUserCount?: number; lastCheckAt?: string;
  checks: TenantCheck[]; offboardedAt?: string; dataDeletionDueAt?: string;
}

export interface Registration {
  id: string; entraUserId: string; userPrincipalName: string; userDisplayName?: string; entraMethodId: string; displayName?: string;
  registeredAt: string; registeredByName?: string; source: string; removedAt?: string; removedReason?: string; lastSeenInSyncAt?: string;
}

export interface Advisory { id: string; title: string; severity: string; url: string; affectsFido: boolean }

export interface YubiKey {
  id: string; tenantId?: string | null; tenantName?: string | null; inPartnerStock: boolean; serialNumber?: number; firmwareVersion?: string; model?: string; formFactor?: string;
  aaGuid?: string; isFips: boolean; status: KeyStatus; statusLabel: string; lockReason?: string; statusNote?: string; source: string;
  needsDetails: boolean; tags: string[]; customFields: Record<string, string | null>; notes?: string; profileId?: string;
  pinChangeForced?: boolean; createdAt: string; updatedAt: string; statusChangedAt: string; advisories: Advisory[];
  nextStates: KeyStatus[]; registrations: Registration[];
}

export interface KeyInfo {
  serialNumber?: number; firmwareVersion: string; formFactor?: string; model?: string; aaGuid?: string; hasFido2: boolean; isFips: boolean;
  pinIsSet: boolean; supportsForcePinChange: boolean; minimumPinLength?: number; remainingDiscoverableCredentials?: number;
}

export interface AgentInfo {
  id: string; machineName: string; agentVersion?: string; createdAt: string; lastSeenAt?: string; online: boolean; key?: KeyInfo; busy?: string;
}

export interface Profile {
  id: string; tenantId?: string; name: string; description?: string; pinStrategy: 'Random' | 'Fixed'; pinLength: number; hasFixedPin: boolean;
  forcePinChange: boolean; factoryResetBeforeRegistration: boolean; displayNameTemplate: string; allowedModels: string[];
  minimumFirmware?: string; isDefault: boolean;
}

export interface Job {
  id: string; kind: string; state: 'Running' | 'Succeeded' | 'Failed'; step: string; prompt?: string; stepNumber: number; totalSteps: number;
  error?: string; errorCode?: string; keyId?: string; pinLetterId?: string; notes: string[];
}

export interface AuditEntry {
  sequence: number; timestamp: string; actorType: string; actorName?: string; action: string; tenantId?: string; keyId?: string;
  target?: string; details?: string; ipAddress?: string; hash: string;
}

export type CustomFieldType = 'Text' | 'Number' | 'Date' | 'Select' | 'Boolean';

export interface CustomField { id?: string; key: string; label: string; type: CustomFieldType; required: boolean; options: string[] }

/** A field as seen in one tenant after inheritance from the platform level. */
export interface ResolvedField extends CustomField {
  id: string; origin: 'Global' | 'Tenant'; state: 'Inherited' | 'Modified' | 'Disabled' | 'Local'; isActive: boolean;
  inherited?: { label: string; required: boolean; options: string[] } | null;
}

export interface GlobalField extends CustomField { id: string; tenantsModified: number; tenantsDisabled: number }

export const fieldTypes: Record<CustomFieldType, string> = { Text: 'Text', Number: 'Zahl', Date: 'Datum', Select: 'Auswahl', Boolean: 'Ja/Nein' };

/** Converts a label into a suggestion for the technical key. */
export const keyFromLabel = (label: string) =>
  label.toLowerCase().replace(/ä/g, 'ae').replace(/ö/g, 'oe').replace(/ü/g, 'ue').replace(/ß/g, 'ss')
    .replace(/[^a-z0-9_-]+/g, '_').replace(/^_+|_+$/g, '').slice(0, 40);

export const statusLabels: Record<KeyStatus, string> = {
  InStock: 'Im Lager', Active: 'Zugewiesen/Aktiv', Locked: 'Gesperrt', Retired: 'Ausgemustert',
};

export const lockReasons: Record<string, string> = { Lost: 'Verloren', Defective: 'Defekt', Stolen: 'Gestohlen', Other: 'Sonstiges' };

export const fmtDate = (s?: string | null, withTime = true) =>
  s ? new Date(s).toLocaleString('de-DE', withTime ? { dateStyle: 'medium', timeStyle: 'short' } : { dateStyle: 'medium' }) : '–';

export type BatchStatus = 'Open' | 'Completed' | 'Cancelled';
export type BatchItemStatus = 'Pending' | 'Running' | 'Done' | 'Failed' | 'Skipped';
export interface BatchCounts { total: number; pending: number; running: number; done: number; failed: number; skipped: number }

export interface BatchItem {
  id: string; position: number; entraUserId: string; userPrincipalName: string; userDisplayName?: string | null; department?: string | null;
  profileId?: string | null; profileGroupName?: string | null; expectedSerialNumber?: number | null; status: BatchItemStatus;
  jobId?: string | null; keyId?: string | null; serialNumber?: number | null; pinChangeForced?: boolean | null; error?: string | null; errorCode?: string | null;
  note?: string | null; completedAt?: string | null; completedByName?: string | null; pinLetterId?: string | null; pinLetterAvailable: boolean;
}

export interface BatchSummary { id: string; name: string; source?: string | null; status: BatchStatus; createdAt: string; createdByName?: string | null; completedAt?: string | null; counts: BatchCounts }

export interface Batch extends BatchSummary { tenantId: string; defaultProfileId: string; items: BatchItem[] }

export interface BatchCandidate {
  entraUserId: string; userPrincipalName: string; displayName?: string | null; department?: string | null; accountEnabled: boolean; activeKeys: number;
  entraYubiKeys?: number | null; entraOtherPasskeys?: number | null; profileId?: string | null; profileGroupName?: string | null; expectedSerialNumber?: number | null;
}

export const batchStatusLabels: Record<BatchStatus, string> = { Open: 'Offen', Completed: 'Abgeschlossen', Cancelled: 'Abgebrochen' };
export const batchItemLabels: Record<BatchItemStatus, string> = { Pending: 'Offen', Running: 'Läuft', Done: 'Registriert', Failed: 'Fehlgeschlagen', Skipped: 'Übersprungen' };
