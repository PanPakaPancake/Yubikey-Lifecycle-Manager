import { useEffect, useRef, useState } from 'react';
import { Link, useLocation } from 'react-router';
import { api, fieldTypes, fmtDate, keyFromLabel, type AgentUpdatePolicy, type CustomField, type GlobalField, type Tenant } from '../api';
import { useAuth, useCan } from '../auth';
import { Badge, Card, download, ErrorBox, Info, Modal, PageHeader, Spinner, useAction, useApi } from '../ui';
import { FieldExplanation } from './TenantDetail';
import { useConfirm } from '../feedback';
import { BrandingCard } from './Branding';

/** Platform level: settings that apply to the whole installation and are inherited by the tenants. */
export default function PlatformPage() {
  const can = useCan();
  const { me } = useAuth();
  const location = useLocation();
  useEffect(() => {
    if (location.hash) document.getElementById(location.hash.slice(1))?.scrollIntoView({ behavior: 'smooth' });
  }, [location.hash]);

  return (
    <>
      <PageHeader title="Globale Einstellungen" subtitle="Partnerebene: Einstellungen für die ganze Installation. Tenants erben sie und können sie teilweise anpassen." />
      <Card title="Aufbau">
        <div className="hierarchy">
          <span className="level current">Plattform</span><span aria-hidden>→</span><span className="level">Tenant</span><span aria-hidden>→</span><span className="level">Gruppe</span>
        </div>
        <dl className="explain">
          <dt>Plattform</dt><dd>API-Schlüssel, globale Zusatzfelder, <Link to="/profiles">Profile für alle Tenants</Link>, <Link to="/users">Benutzer und Rollen</Link>.</dd>
          <dt>Tenant</dt><dd>Zielgruppe, FIDO2-Prüfung, Synchronisation, eigene Profile und Zusatzfelder. Geerbte Zusatzfelder kann ein Tenant verändern oder deaktivieren.</dd>
          <dt>Gruppe</dt><dd>Profil pro Entra-Gruppe: Der Registrierungs-Assistent schlägt es für Mitglieder vor. Einstellbar im jeweiligen Tenant.</dd>
        </dl>
      </Card>
      {can('ManageSettings') && <UpdatesCard />}
      {can('ManageSettings') && <AppCertificateCard />}
      {can('ManageSettings') && <LoginSettingsCard />}
      <BrandingCard editable={!!can('ManageSettings')} />
      <GlobalFieldsCard editable={can('ManageProfiles') && !!me.user?.allTenants} />
      {can('ManageApiKeys') && <ApiKeysCard />}
      {can('ManageApiKeys') && <Info>Webhooks, Mail-Alarme und Mail-Berichte stehen unter <Link to="/partner/notifications">Benachrichtigungen</Link>.</Info>}
    </>
  );
}

function GlobalFieldsCard({ editable }: { editable: boolean }) {
  const { data, reload } = useApi<GlobalField[]>('/api/fields');
  const [draft, setDraft] = useState<CustomField[] | null>(null);
  const { busy, error, run } = useAction();
  const list: (CustomField & Partial<GlobalField>)[] = draft ?? data ?? [];
  const update = (i: number, patch: Partial<CustomField>) => setDraft(list.map((f, j) => (j === i ? { ...f, ...patch } : f)));
  const save = () => run(async () => {
    await api.put('/api/fields', list.map((f) => ({ key: f.key, label: f.label, type: f.type, required: f.required, options: f.type === 'Select' ? f.options : [] })));
    await reload();
    setDraft(null);
  });

  return (
    <Card title={<span id="fields">Globale Zusatzfelder für Keys</span>} className="mt"
      actions={editable && <button onClick={() => setDraft([...list, { key: '', label: '', type: 'Text', required: false, options: [] }])}>Feld hinzufügen</button>}>
      <p className="muted small" style={{ marginTop: 0 }}>
        Diese Felder gelten in allen Tenants. Ein Tenant kann ein Feld für sich verändern (Bezeichnung, Pflicht, Optionen) oder deaktivieren und eigene, lokale Felder ergänzen.
        Wird ein Feld hier entfernt, verschwinden auch die Anpassungen in den Tenants; bereits gespeicherte Werte an den Keys bleiben erhalten.
      </p>
      <FieldExplanation />
      {!editable && <Info>Globale Felder können Owner mit Zugriff auf alle Tenants bearbeiten.</Info>}
      {list.length === 0 && <p className="muted">Keine globalen Zusatzfelder. Beispiele: Kostenstelle, Standort, Inventarnummer.</p>}
      {list.length > 0 && (
        <table>
          <thead><tr><th>Bezeichnung</th><th>Schlüssel</th><th>Typ</th><th>Optionen</th><th>Pflicht</th><th>In Tenants</th><th /></tr></thead>
          <tbody>
            {list.map((f, i) => (
              <tr key={f.id ?? 'n' + i}>
                <td><input value={f.label} disabled={!editable} placeholder="z. B. Kostenstelle" onChange={(e) => update(i, { label: e.target.value, key: f.id ? f.key : keyFromLabel(e.target.value) })} /></td>
                <td><input value={f.key} disabled={!editable || !!f.id} className="mono" onChange={(e) => update(i, { key: e.target.value })} title={f.id ? 'Der Schlüssel kann nach dem Speichern nicht mehr geändert werden.' : 'Wird aus der Bezeichnung vorgeschlagen.'} /></td>
                <td>
                  <select value={f.type} disabled={!editable} onChange={(e) => update(i, { type: e.target.value as CustomField['type'] })}>
                    {Object.entries(fieldTypes).map(([k, v]) => <option key={k} value={k}>{v}</option>)}
                  </select>
                </td>
                <td>{f.type === 'Select' && <input value={f.options.join(', ')} disabled={!editable} placeholder="A, B, C" onChange={(e) => update(i, { options: e.target.value.split(',').map((o) => o.trim()).filter(Boolean) })} />}</td>
                <td><input type="checkbox" checked={f.required} disabled={!editable} onChange={(e) => update(i, { required: e.target.checked })} /></td>
                <td className="small">
                  {!f.id ? <span className="muted">neu</span> : (f.tenantsModified || f.tenantsDisabled) ? (
                    <>
                      {!!f.tenantsModified && <Badge tone="warn">{f.tenantsModified} verändert</Badge>}{' '}
                      {!!f.tenantsDisabled && <Badge tone="error">{f.tenantsDisabled} deaktiviert</Badge>}
                    </>
                  ) : <span className="muted">überall geerbt</span>}
                </td>
                <td>{editable && <button className="link" onClick={() => setDraft(list.filter((_, j) => j !== i))}>Entfernen</button>}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
      <ErrorBox error={error} />
      {draft && <div className="actions" style={{ marginTop: 12 }}><button onClick={() => setDraft(null)}>Verwerfen</button><button className="primary" disabled={busy} onClick={save}>Felder speichern</button></div>}
    </Card>
  );
}

interface ApiKeyRow {
  id: string; name: string; prefix: string; createdAt: string; createdBy: string; lastUsedAt?: string; expiresAt?: string; revokedAt?: string;
  allTenants: boolean; allowTodoWrite: boolean; tenants: { id: string; displayName: string }[];
}

export function ScopeEditor({ allTenants, setAllTenants, tenantIds, setTenantIds, tenants, canAll }: {
  allTenants: boolean; setAllTenants: (v: boolean) => void; tenantIds: string[]; setTenantIds: (v: string[]) => void; tenants: Tenant[]; canAll: boolean;
}) {
  return (
    <div className="field-group">
      <span className="field-label">Gilt für</span>
      <label className="check"><input type="radio" checked={allTenants} disabled={!canAll} onChange={() => setAllTenants(true)} /> Alle Tenants, auch künftig angebundene</label>
      <label className="check"><input type="radio" checked={!allTenants} onChange={() => setAllTenants(false)} /> Nur ausgewählte Tenants</label>
      {!allTenants && (
        <div style={{ paddingLeft: 26 }}>
          {tenants.map((t) => (
            <label key={t.id} className="check">
              <input type="checkbox" checked={tenantIds.includes(t.id)} onChange={(e) => setTenantIds(e.target.checked ? [...tenantIds, t.id] : tenantIds.filter((x) => x !== t.id))} />
              {t.displayName} {t.defaultDomain && <span className="muted small">({t.defaultDomain})</span>}
            </label>
          ))}
          {tenants.length === 0 && <span className="muted small">Noch kein Tenant angebunden.</span>}
        </div>
      )}
    </div>
  );
}

function ApiKeysCard() {
  const { me } = useAuth();
  const canAll = !!me.user?.allTenants;
  const { data, reload } = useApi<ApiKeyRow[]>('/api/api-keys');
  const { data: allTenants } = useApi<Tenant[]>('/api/tenants');
  const tenants = (allTenants ?? []).filter((t) => t.status === 'Active');
  const [name, setName] = useState('');
  const [days, setDays] = useState(365);
  const [all, setAll] = useState(canAll);
  const [ids, setIds] = useState<string[]>([]);
  const [todoWrite, setTodoWrite] = useState(false);
  const [created, setCreated] = useState<string>();
  const [editing, setEditing] = useState<ApiKeyRow | null>(null);
  const { busy, error, run } = useAction();

  const create = () => run(async () => {
    const r = await api.post<{ apiKey: string }>('/api/api-keys', { name, expiresInDays: days || null, allTenants: all, tenantIds: all ? [] : ids, allowTodoWrite: todoWrite });
    setCreated(r.apiKey);
    setName('');
    setIds([]);
    setTodoWrite(false);
    reload();
  });
  const revoke = (id: string) => run(async () => { await api.del(`/api/api-keys/${id}`); reload(); });

  return (
    <Card title={<span id="api">API-Schlüssel</span>} className="mt" actions={<a className="small" href="/api/docs" target="_blank" rel="noreferrer">API-Dokumentation</a>}>
      <p className="muted small" style={{ marginTop: 0 }}>
        Externe Tools (z. B. Ticketsystem, RMM) lesen Inventar und Kennzahlen über <code>/api/v1</code> mit dem Header <code>X-Api-Key</code>. Die API ändert keine Keys. Einzige Schreibfunktion ist die <Link to="/todos">ToDo-Liste</Link>: ein Ticketsystem trägt Personen ein, die einen Key bekommen sollen. Das muss am Schlüssel ausdrücklich erlaubt werden.
        Ein Schlüssel gilt für alle Tenants oder nur für ausgewählte; mit dem Parameter <code>tenant</code> (ID oder Domain) fragt ein Tool gezielt einen Tenant ab.
      </p>
      {created && <Info tone="ok">Neuer Schlüssel, nur jetzt sichtbar: <code style={{ wordBreak: 'break-all' }}>{created}</code> <button className="link" onClick={() => void navigator.clipboard?.writeText(created)}>Kopieren</button></Info>}
      <form onSubmit={(e) => { e.preventDefault(); create(); }}>
        <div className="toolbar" style={{ marginBottom: 6 }}>
          <input placeholder="Name, z. B. Ticketsystem" value={name} onChange={(e) => setName(e.target.value)} aria-label="Name" />
          <select value={days} onChange={(e) => setDays(Number(e.target.value))} aria-label="Gültigkeit">
            <option value={90}>90 Tage</option><option value={365}>1 Jahr</option><option value={730}>2 Jahre</option><option value={0}>unbegrenzt</option>
          </select>
        </div>
        <ScopeEditor allTenants={all} setAllTenants={setAll} tenantIds={ids} setTenantIds={setIds} tenants={tenants} canAll={canAll} />
        <label className="check" style={{ marginBottom: 10 }}><input type="checkbox" checked={todoWrite} onChange={(e) => setTodoWrite(e.target.checked)} /> Darf Personen auf die ToDo-Liste setzen (POST /api/v1/todos)</label>
        <button type="submit" className="primary" disabled={!name.trim() || busy || (!all && ids.length === 0)}>Schlüssel erstellen</button>
      </form>
      <ErrorBox error={error} />
      {!data && <Spinner />}
      {data && data.length > 0 && (
        <table className="mt">
          <thead><tr><th>Name</th><th>Präfix</th><th>Gilt für</th><th>Erstellt</th><th>Zuletzt genutzt</th><th>Gültig bis</th><th /></tr></thead>
          <tbody>
            {data.map((k) => (
              <tr key={k.id} style={{ opacity: k.revokedAt ? 0.55 : 1 }}>
                <td>{k.name}</td>
                <td className="mono">ylm_{k.prefix}_…</td>
                <td className="small">{k.allTenants ? <Badge tone="info">Alle Tenants</Badge> : k.tenants.map((t) => t.displayName).join(', ') || <span className="muted">kein Tenant</span>}{k.allowTodoWrite && <div><Badge tone="warn">ToDo schreiben</Badge></div>}</td>
                <td className="small">{fmtDate(k.createdAt, false)} · {k.createdBy}</td>
                <td className="small">{fmtDate(k.lastUsedAt)}</td>
                <td className="small">{k.expiresAt ? fmtDate(k.expiresAt, false) : 'unbegrenzt'}</td>
                <td>
                  {k.revokedAt ? <Badge>widerrufen</Badge> : (
                    <span style={{ display: 'inline-flex', gap: 10 }}>
                      <button className="link" onClick={() => setEditing(k)}>Rechte ändern</button>
                      <button className="link" onClick={() => revoke(k.id)}>Widerrufen</button>
                    </span>
                  )}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
      {editing && <ScopeModal keyRow={editing} tenants={tenants} canAll={canAll} onClose={() => setEditing(null)} onSaved={() => { setEditing(null); reload(); }} />}
    </Card>
  );
}

function ScopeModal({ keyRow, tenants, canAll, onClose, onSaved }: { keyRow: ApiKeyRow; tenants: Tenant[]; canAll: boolean; onClose: () => void; onSaved: () => void }) {
  const [all, setAll] = useState(keyRow.allTenants);
  const [ids, setIds] = useState(keyRow.tenants.map((t) => t.id));
  const [todoWrite, setTodoWrite] = useState(keyRow.allowTodoWrite);
  const { busy, error, run } = useAction();
  const save = () => run(async () => { await api.put(`/api/api-keys/${keyRow.id}/scope`, { allTenants: all, tenantIds: all ? [] : ids, allowTodoWrite: todoWrite }); onSaved(); });
  return (
    <Modal title={`Geltung von „${keyRow.name}“`} onClose={onClose}
      footer={<><button onClick={onClose}>Abbrechen</button><button className="primary" disabled={busy || (!all && ids.length === 0)} onClick={save}>Speichern</button></>}>
      <ScopeEditor allTenants={all} setAllTenants={setAll} tenantIds={ids} setTenantIds={setIds} tenants={tenants} canAll={canAll} />
      <label className="check"><input type="checkbox" checked={todoWrite} onChange={(e) => setTodoWrite(e.target.checked)} /> Darf Personen auf die ToDo-Liste setzen</label>
      <ErrorBox error={error} />
    </Modal>
  );
}

export type CertificateStatus = {
  state: 'ok' | 'warning' | 'critical' | 'expired' | 'missing';
  thumbprint?: string; notAfter?: string; daysLeft?: number; renewalDays: number;
  autoRenew: boolean; autoRenewPossible: boolean;
  nextCreatedAt?: string; nextThumbprint?: string; lastAttemptAt?: string; lastError?: string;
};

const certTone = { ok: 'ok', warning: 'warn', critical: 'error', expired: 'error', missing: 'neutral' } as const;
const certLabel = { ok: 'Gültig', warning: 'Läuft bald ab', critical: 'Läuft in Kürze ab', expired: 'Abgelaufen', missing: 'Nicht vorhanden' } as const;

/** App-Zertifikat: Anmeldung der Plattform in allen Tenants. Warnung, automatische Erneuerung, Fallback von Hand. */
function AppCertificateCard() {
  const { data, reload } = useApi<CertificateStatus>('/api/platform/certificate');
  const { busy, error, run } = useAction();
  const act = (path: string) => run(async () => { await api.post('/api/platform/certificate/' + path, {}); reload(); });
  const discard = () => run(async () => { await api.del('/api/platform/certificate/next'); reload(); });
  if (!data || data.state === 'missing') return null;
  return (
    <Card title={<span id="certificate">App-Zertifikat</span>}
      actions={data.autoRenewPossible && !data.nextThumbprint && <button disabled={busy} onClick={() => act('renew')}>Jetzt erneuern</button>}>
      <p className="small muted">Mit diesem Zertifikat meldet sich die Plattform in jedem angebundenen Tenant bei Microsoft Graph an. Läuft es ab, funktionieren Sync, Registrierung und Rollout nicht mehr.</p>
      <dl className="props">
        <dt>Status</dt><dd><Badge tone={certTone[data.state]}>{certLabel[data.state]}</Badge>{data.daysLeft != null && data.daysLeft >= 0 && <span className="muted small"> noch {data.daysLeft} Tage</span>}</dd>
        <dt>Gültig bis</dt><dd>{data.notAfter ? fmtDate(data.notAfter) : '–'}</dd>
        <dt>Fingerabdruck</dt><dd className="mono small">{data.thumbprint}</dd>
        <dt>Erneuerung</dt><dd>{data.autoRenew
          ? <>automatisch {data.renewalDays} Tage vor dem Ablauf</>
          : <>nur Warnung (automatische Erneuerung abgeschaltet: <span className="mono">YLM_CERT_AUTO_RENEW=false</span>)</>}</dd>
      </dl>
      {data.lastError && <Info tone="error">Letzter Versuch {data.lastAttemptAt ? fmtDate(data.lastAttemptAt) : ''} fehlgeschlagen: {data.lastError}</Info>}
      {data.nextThumbprint ? (
        <Info tone="warn">
          <strong>Neues Zertifikat wartet auf Entra</strong> (<span className="mono">{data.nextThumbprint}</span>). Die Plattform prüft jede Minute und schaltet um, sobald Entra es annimmt.
          Bei der Erneuerung von Hand: Zertifikat herunterladen und im Entra Admin Center unter App-Registrierungen, YLM-App, „Zertifikate und Geheimnisse“ hochladen, dann aktivieren.
          <div className="toolbar mt">
            <button onClick={() => download('/api/platform/certificate/next.cer')}>Zertifikat herunterladen (.cer)</button>
            <button disabled={busy} onClick={() => act('activate')}>Jetzt aktivieren</button>
            <button className="ghost" disabled={busy} onClick={discard}>Verwerfen</button>
          </div>
        </Info>
      ) : (data.state !== 'ok' || !data.autoRenewPossible || data.lastError) && (
        <p className="small">Klappt die automatische Erneuerung nicht (zum Beispiel weil das Zertifikat schon abgelaufen ist):{' '}
          <button className="link" disabled={busy} onClick={() => act('prepare')}>Neues Zertifikat von Hand vorbereiten</button></p>
      )}
      <ErrorBox error={error} />
    </Card>
  );
}

function LoginSettingsCard() {
  const { data, reload } = useApi<{ entraSsoEnabled: boolean; entraConfigured: boolean }>('/api/platform/login');
  const { busy, error, run } = useAction();
  const toggle = (on: boolean) => run(async () => { await api.put('/api/platform/login', { entraSsoEnabled: on }); reload(); });
  return (
    <Card title="Anmeldung an der Plattform">
      <dl className="explain">
        <dt>Entra-SSO</dt><dd>Konten aus dem Heim-Tenant melden sich über Microsoft an (passwortlos mit YubiKey).</dd>
        <dt>Lokale Konten (Passkey)</dt><dd>Anmeldung mit Passkey direkt an der Plattform, unabhängig von Entra. Empfohlen für Kundenzugriffe und Breakglass.</dd>
        <dt>Lokale Konten (Passwort + TOTP)</dt><dd>Passwort plus Authenticator-Code, wenn kein Passkey möglich ist.</dd>
      </dl>
      {data && (
        <label className="check">
          <input type="checkbox" checked={data.entraSsoEnabled} disabled={busy || !data.entraConfigured} onChange={(e) => toggle(e.target.checked)} />
          Anmeldung mit Entra-SSO erlauben {!data.entraConfigured && <span className="muted small">(keine App-Registrierung eingerichtet)</span>}
        </label>
      )}
      <p className="small muted">Lokale Konten legen Sie unter <Link to="/users">Benutzer &amp; Rollen</Link> an. Entra-SSO lässt sich nur abschalten, wenn ein Owner ein eingerichtetes lokales Konto hat.</p>
      <ErrorBox error={error} />
    </Card>
  );
}

type UpdaterStatus = {
  state: 'idle' | 'running' | 'done' | 'failed' | 'unreachable' | 'error'; message?: string; version?: string; log?: string[]; finishedAt?: string | null;
  /** Neuere Updater: tägliche Sicherheitsupdates der Container. */
  refresh?: { schedule?: string | null; lastAt?: string | null; result?: 'running' | 'done' | 'failed' | null; trigger?: string | null };
};
export type UpdateInfo = {
  currentVersion: string; latestVersion?: string | null; updateAvailable: boolean; releaseUrl?: string | null; publishedAt?: string | null; notes?: string | null;
  error?: string | null; checkedAt: string;
  updater: { configured: boolean; status?: UpdaterStatus | null };
  agent: {
    bundledVersion?: string | null; latestVersion?: string | null; agents: number; outdated: number; autoUpdateOff: number; failed: number;
    policy: AgentUpdatePolicy; list: AgentRow[];
  };
};
type AgentRow = {
  id: string; machineName: string; agentVersion?: string | null; autoUpdate?: boolean | null; updateError?: string | null;
  lastSeenAt?: string | null; owner?: string | null; online: boolean; outdated: boolean;
};

const policyLabels: Record<AgentUpdatePolicy, string> = {
  AgentDecides: 'Automatisch, am PC abschaltbar (Standard)',
  Always: 'Immer automatisch',
  Never: 'Keine automatischen Updates',
};

function AgentUpdates({ agent, onChanged }: { agent: UpdateInfo['agent']; onChanged: () => void }) {
  const { busy, error, run } = useAction();
  const setPolicy = (policy: AgentUpdatePolicy) => run(async () => { await api.put('/api/platform/updates/agent-policy', { policy }); onChanged(); });
  const parts = [
    agent.outdated > 0 ? `${agent.outdated} von ${agent.agents} veraltet` : `alle ${agent.agents} aktuell`,
    agent.autoUpdateOff > 0 && `${agent.autoUpdateOff} mit Auto-Update aus`,
    agent.failed > 0 && `${agent.failed} mit Updatefehler`,
  ].filter(Boolean).join(' · ');
  return (
    <>
      <tr><th>Agent im Paket</th><td>{agent.bundledVersion
        ? <><span className="mono">{agent.bundledVersion}</span> · {parts}</>
        : <span className="muted">nicht enthalten (lokaler Build)</span>}</td></tr>
      <tr><th>Agent-Updates</th><td>
        <select value={agent.policy} disabled={busy} onChange={(e) => setPolicy(e.target.value as AgentUpdatePolicy)}>
          {(Object.keys(policyLabels) as AgentUpdatePolicy[]).map((p) => <option key={p} value={p}>{policyLabels[p]}</option>)}
        </select>
        <div className="small muted">Agents fragen alle 4 Stunden nach einer neuen Version. „Jetzt aktualisieren“ im Tray oder unter „Agent“ geht immer.</div>
        <ErrorBox error={error} />
        {agent.list.length > 0 && (
          <details><summary className="small muted">Alle Agents ({agent.list.length})</summary>
            <table>
              <thead><tr><th>PC</th><th>Benutzer</th><th>Version</th><th>Auto-Update</th><th>Zuletzt gesehen</th></tr></thead>
              <tbody>{agent.list.map((a) => (
                <tr key={a.id}>
                  <td>{a.machineName} {a.online ? <Badge tone="ok">online</Badge> : <Badge>offline</Badge>}</td>
                  <td>{a.owner ?? '–'}</td>
                  <td><span className="mono">{a.agentVersion ?? '?'}</span> {a.outdated && <Badge tone="warn">veraltet</Badge>}</td>
                  <td>{a.autoUpdate == null ? <span className="muted small">unbekannt (vor 0.5)</span> : a.autoUpdate ? 'an' : <Badge>aus</Badge>}
                    {a.updateError && <div className="small" title={a.updateError}><Badge tone="error">Fehler</Badge> {a.updateError.length > 80 ? a.updateError.slice(0, 80) + ' …' : a.updateError}</div>}</td>
                  <td className="small muted">{fmtDate(a.lastSeenAt ?? undefined)}</td>
                </tr>))}
              </tbody>
            </table>
          </details>
        )}
      </td></tr>
    </>
  );
}

function UpdatesCard() {
  const [refresh, setRefresh] = useState(0);
  const { data, error, reload } = useApi<UpdateInfo>('/api/platform/updates' + (refresh ? '?refresh=true' : ''), [refresh]);
  const { busy, error: actionError, run } = useAction();
  const confirm = useConfirm();
  const [phase, setPhase] = useState<'idle' | 'running' | 'restarting'>('idle');
  const [kind, setKind] = useState<'update' | 'refresh'>('update');
  const [log, setLog] = useState<string[]>([]);
  const [from, setFrom] = useState<string>();
  const doneChecks = useRef(0);

  // Während des Updates: Updater-Status abfragen; wenn der Server neu startet, auf die neue Version warten.
  useEffect(() => {
    if (phase === 'idle') return;
    const t = setInterval(async () => {
      try {
        const s = await api.get<{ currentVersion: string; status?: UpdaterStatus }>('/api/platform/updates/status');
        if (s.status?.log) setLog(s.status.log);
        if (s.currentVersion !== from || (kind === 'refresh' && phase === 'restarting')) { window.location.reload(); return; }
        if (s.status?.state === 'failed') { setPhase('idle'); reload(); }
        // Fertig, aber derselbe Server läuft noch: Image war schon aktuell oder der Neustart steht noch aus.
        if (s.status?.state === 'done') { doneChecks.current += 1; if (doneChecks.current > 24) { setPhase('idle'); reload(); } }
      } catch {
        setPhase('restarting');
      }
    }, 2500);
    return () => clearInterval(t);
  }, [phase, from, kind, reload]);

  const apply = async () => {
    if (!(await confirm({ title: `Auf Version ${data!.latestVersion} aktualisieren?`, message: 'Die Plattform ist dabei etwa eine Minute nicht erreichbar. Laufende Registrierungen vorher abschließen.', confirmLabel: 'Jetzt aktualisieren' }))) return;
    await run(async () => {
      setFrom(data!.currentVersion);
      await api.post('/api/platform/updates/apply', { version: data!.latestVersion });
      setKind('update');
      setLog([]);
      doneChecks.current = 0;
      setPhase('running');
    });
  };

  const securityRefresh = async () => {
    if (!(await confirm({ title: 'Sicherheitsupdates jetzt einspielen?', message: 'Lädt alle Images der installierten Version neu und startet geänderte Container neu. Die Plattform ist dabei kurz nicht erreichbar.', confirmLabel: 'Jetzt einspielen' }))) return;
    await run(async () => {
      setFrom(data!.currentVersion);
      await api.post('/api/platform/updates/refresh');
      setKind('refresh');
      setLog([]);
      doneChecks.current = 0;
      setPhase('running');
    });
  };

  const status = data?.updater.status;
  // Aus der Übersicht oder dem Versionsverlauf kommend (/platform#updates): direkt zu dieser Karte springen.
  const location = useLocation();
  const loaded = data != null;
  useEffect(() => {
    if (loaded && location.hash === '#updates') document.getElementById('updates')?.scrollIntoView({ behavior: 'smooth', block: 'start' });
  }, [loaded, location.hash]);
  const sec = status?.refresh;
  return (
    <div id="updates" className="anchor-target">
    <Card title="Version und Updates" actions={<button disabled={busy || phase !== 'idle'} onClick={() => setRefresh((r) => r + 1)}>Jetzt prüfen</button>}>
      <ErrorBox error={error ?? actionError} />
      {!data && !error && <Spinner />}
      {data && <>
        <table>
          <tbody>
            <tr><th>Installiert</th><td className="mono">{data.currentVersion}</td></tr>
            <tr><th>Neueste Version</th><td>{data.latestVersion
              ? <><span className="mono">{data.latestVersion}</span> {data.updateAvailable ? <Badge tone="warn">Update verfügbar</Badge> : <Badge tone="ok">aktuell</Badge>}
                {data.publishedAt && <span className="small muted"> vom {fmtDate(data.publishedAt, false)}</span>}
                {data.updateAvailable && <> · <Link to="/versionen">Was ist neu?</Link></>}{data.releaseUrl && <> · <a href={data.releaseUrl} target="_blank" rel="noreferrer">auf GitHub</a></>}</>
              : <span className="muted">{data.error ?? 'unbekannt'}</span>}</td></tr>
            <AgentUpdates agent={data.agent} onChanged={reload} />
            <tr><th>Update per Klick</th><td>{!data.updater.configured
              ? <span className="muted">nicht eingerichtet (<code>UPDATER_TOKEN</code> in <code>.env</code> fehlt)</span>
              : status && (status.state === 'unreachable' || status.state === 'error')
                ? <Badge tone="warn">Updater nicht erreichbar</Badge>
                : <><Badge tone="ok">bereit</Badge>{!data.updateAvailable && <span className="small muted"> · der Knopf erscheint, sobald eine neue Version verfügbar ist</span>}</>}</td></tr>
            {data.updater.configured && status && status.state !== 'unreachable' && status.state !== 'error' && (
              <tr><th>Sicherheitsupdates der Container</th><td>
                {sec === undefined
                  ? <span className="small muted">Älterer Updater: Installationsskript einmal erneut ausführen, dann spielt er täglich die Sicherheitsupdates der Basis-Images ein.</span>
                  : <>{sec.schedule ? <>täglich um {sec.schedule} Uhr</> : <span className="muted">automatisch aus (<code>YLM_AUTO_REFRESH</code>)</span>}
                    {sec.lastAt && <span className="small muted"> · zuletzt {fmtDate(sec.lastAt)} {sec.result === 'failed' ? <Badge tone="error">fehlgeschlagen</Badge> : sec.result === 'running' ? <Badge>läuft</Badge> : <Badge tone="ok">ok</Badge>}</span>}
                    {' '}<button className="small" disabled={busy || phase !== 'idle' || status.state === 'running'} onClick={securityRefresh}>Jetzt einspielen</button>
                    <div className="small muted">Lädt die Images der installierten Version mit den neuesten Patches (Basis-Images werden wöchentlich und bei Schwachstellen neu gebaut). Den Host selbst (Kernel, Docker) bitte separat aktuell halten.</div></>}
              </td></tr>
            )}
            <tr><th>Geprüft</th><td className="small muted">{fmtDate(data.checkedAt)}</td></tr>
          </tbody>
        </table>
        {phase !== 'idle' && <Info>{phase === 'restarting' ? 'Der Server startet neu … Die Seite lädt automatisch, sobald er wieder läuft.' : kind === 'refresh' ? 'Sicherheitsupdates laufen: Images werden geladen …' : 'Update läuft: neues Image wird geladen …'}</Info>}
        {data.updateAvailable && phase === 'idle' && (data.updater.configured
          ? <button className="primary" disabled={busy || status?.state === 'running'} onClick={apply}>Jetzt auf {data.latestVersion} aktualisieren</button>
          : <Info>Für das Update per Klick fehlt der Updater-Container: in <code>.env</code> <code>UPDATER_TOKEN</code> setzen (z. B. <code>openssl rand -hex 32</code>) und <code>docker compose up -d</code> ausführen. Alternativ von Hand: <code>docker compose pull &amp;&amp; docker compose up -d</code>.</Info>)}
        {data.updater.configured && status && (status.state === 'unreachable' || status.state === 'error') && <Info tone="warn">{status.message}</Info>}
        {status?.state === 'failed' && phase === 'idle' && <Info tone="error">Das letzte Update ist fehlgeschlagen. Details im Protokoll unten.</Info>}
        {(log.length > 0 || (status?.state === 'failed' && status.log?.length)) && (
          <details open={phase !== 'idle' || status?.state === 'failed'}><summary className="small muted">Protokoll</summary>
            <pre className="script" style={{ maxHeight: 220, overflow: 'auto' }}>{(log.length ? log : status?.log ?? []).join('\n')}</pre>
          </details>
        )}
      </>}
    </Card>
    </div>
  );
}
