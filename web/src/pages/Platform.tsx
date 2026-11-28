import { useEffect, useRef, useState } from 'react';
import { Link, useLocation, useNavigate } from 'react-router';
import { api, fieldTypes, fmtDate, keyFromLabel, type AgentUpdatePolicy, type CustomField, type GlobalField, type Tenant } from '../api';
import { useAuth, useCan } from '../auth';
import { Badge, Card, download, ErrorBox, Info, Modal, PageHeader, Spinner, useAction, useApi } from '../ui';
import { FieldExplanation } from './TenantDetail';
import { useConfirm } from '../feedback';
import { BrandingCard } from './Branding';
import { LetterEditorCard } from './LetterEditor';
import { HostingCard } from './Hosting';
import { tr } from '../i18n';

type Tab = 'general' | 'hosting' | 'security' | 'updates' | 'api';
/** Sprungziele (#updates, #certificate, …) aus anderen Seiten öffnen den passenden Bereich. */
const anchorTab: Record<string, Tab> = {
  general: 'general', fields: 'general', branding: 'general',
  hosting: 'hosting',
  security: 'security', certificate: 'security', login: 'security',
  updates: 'updates',
  api: 'api',
};

/** Platform level: settings that apply to the whole installation and are inherited by the tenants. */
export default function PlatformPage() {
  const can = useCan();
  const { me } = useAuth();
  const location = useLocation();
  const navigate = useNavigate();
  const admin = !!can('ManageSettings');
  const tabs: { id: Tab; label: string; show: boolean }[] = [
    { id: 'general', label: tr("Allgemein"), show: true },
    { id: 'hosting', label: tr("Adresse und Zertifikat"), show: admin },
    { id: 'security', label: tr("Anmeldung und Sicherheit"), show: admin },
    { id: 'updates', label: tr("Updates"), show: admin },
    { id: 'api', label: 'API', show: !!can('ManageApiKeys') },
  ];
  const anchor = location.hash.slice(1);
  const requested = anchorTab[anchor] ?? 'general';
  const tab = tabs.find((t) => t.id === requested && t.show)?.id ?? 'general';
  useEffect(() => {
    if (anchor && anchorTab[anchor] !== anchor) document.getElementById(anchor)?.scrollIntoView({ behavior: 'smooth' });
  }, [anchor]);

  return (
    <>
      <PageHeader title={tr("Globale Einstellungen")} subtitle={tr("Partnerebene: Einstellungen für die ganze Installation. Tenants erben sie und können sie teilweise anpassen.")} />
      <div className="tabs" role="tablist" aria-label={tr("Bereiche")}>
        {tabs.filter((t) => t.show).map((t) => (
          <button key={t.id} role="tab" aria-selected={tab === t.id} className={tab === t.id ? 'selected' : ''}
            onClick={() => navigate({ hash: t.id === 'general' ? '' : t.id }, { replace: true })}>{t.label}</button>
        ))}
      </div>
      {tab === 'general' && <>
        <Card title={tr("Aufbau")}>
          <div className="hierarchy">
            <span className="level current">{tr("Plattform")}</span><span aria-hidden>→</span><span className="level">{tr("Tenant")}</span><span aria-hidden>→</span><span className="level">{tr("Gruppe")}</span>
          </div>
          <dl className="explain">
            <dt>{tr("Plattform")}</dt><dd>{tr("API-Schlüssel, globale Zusatzfelder,")} <Link to="/profiles">{tr("Profile für alle Tenants")}</Link>, <Link to="/users">{tr("Benutzer und Rollen")}</Link>.</dd>
            <dt>{tr("Tenant")}</dt><dd>{tr("Zielgruppe, FIDO2-Prüfung, Synchronisation, eigene Profile und Zusatzfelder. Geerbte Zusatzfelder kann ein Tenant verändern oder deaktivieren.")}</dd>
            <dt>{tr("Gruppe")}</dt><dd>{tr("Profil pro Entra-Gruppe: Der Registrierungs-Assistent schlägt es für Mitglieder vor. Einstellbar im jeweiligen Tenant.")}</dd>
          </dl>
        </Card>
        <BrandingCard editable={admin} />
        <LetterEditorCard editable={admin} />
        <GlobalFieldsCard editable={can('ManageProfiles') && !!me.user?.allTenants} />
      </>}
      {tab === 'hosting' && <HostingCard />}
      {tab === 'security' && <>
        <LoginSettingsCard />
        <AppCertificateCard />
      </>}
      {tab === 'updates' && <UpdatesCard />}
      {tab === 'api' && <>
        <ApiKeysCard />
        <Info>{tr("Webhooks, Mail-Alarme und Mail-Berichte stehen unter")} <Link to="/partner/notifications">{tr("Benachrichtigungen")}</Link>.</Info>
      </>}
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
    <Card title={<span id="fields">{tr("Globale Zusatzfelder für Keys")}</span>} className="mt"
      actions={editable && <button onClick={() => setDraft([...list, { key: '', label: '', type: 'Text', required: false, options: [] }])}>{tr("Feld hinzufügen")}</button>}>
      <p className="muted small" style={{ marginTop: 0 }}>
        {tr("Diese Felder gelten in allen Tenants. Ein Tenant kann ein Feld für sich verändern (Bezeichnung, Pflicht, Optionen) oder deaktivieren und eigene, lokale Felder ergänzen. Wird ein Feld hier entfernt, verschwinden auch die Anpassungen in den Tenants; bereits gespeicherte Werte an den Keys bleiben erhalten.")}
      </p>
      <FieldExplanation />
      {!editable && <Info>{tr("Globale Felder können Owner mit Zugriff auf alle Tenants bearbeiten.")}</Info>}
      {list.length === 0 && <p className="muted">{tr("Keine globalen Zusatzfelder. Beispiele: Kostenstelle, Standort, Inventarnummer.")}</p>}
      {list.length > 0 && (
        <table>
          <thead><tr><th>{tr("Bezeichnung")}</th><th>{tr("Schlüssel")}</th><th>{tr("Typ")}</th><th>{tr("Optionen")}</th><th>{tr("Pflicht")}</th><th>{tr("In Tenants")}</th><th /></tr></thead>
          <tbody>
            {list.map((f, i) => (
              <tr key={f.id ?? 'n' + i}>
                <td><input value={f.label} disabled={!editable} placeholder={tr("z. B. Kostenstelle")} onChange={(e) => update(i, { label: e.target.value, key: f.id ? f.key : keyFromLabel(e.target.value) })} /></td>
                <td><input value={f.key} disabled={!editable || !!f.id} className="mono" onChange={(e) => update(i, { key: e.target.value })} title={f.id ? tr("Der Schlüssel kann nach dem Speichern nicht mehr geändert werden.") : tr("Wird aus der Bezeichnung vorgeschlagen.")} /></td>
                <td>
                  <select value={f.type} disabled={!editable} onChange={(e) => update(i, { type: e.target.value as CustomField['type'] })}>
                    {Object.entries(fieldTypes).map(([k, v]) => <option key={k} value={k}>{v}</option>)}
                  </select>
                </td>
                <td>{f.type === 'Select' && <input value={f.options.join(', ')} disabled={!editable} placeholder={tr("A, B, C")} onChange={(e) => update(i, { options: e.target.value.split(',').map((o) => o.trim()).filter(Boolean) })} />}</td>
                <td><input type="checkbox" checked={f.required} disabled={!editable} onChange={(e) => update(i, { required: e.target.checked })} /></td>
                <td className="small">
                  {!f.id ? <span className="muted">{tr("neu")}</span> : (f.tenantsModified || f.tenantsDisabled) ? (
                    <>
                      {!!f.tenantsModified && <Badge tone="warn">{tr("{tenantsModified} verändert", { tenantsModified: f.tenantsModified })}</Badge>}{' '}
                      {!!f.tenantsDisabled && <Badge tone="error">{tr("{tenantsDisabled} deaktiviert", { tenantsDisabled: f.tenantsDisabled })}</Badge>}
                    </>
                  ) : <span className="muted">{tr("überall geerbt")}</span>}
                </td>
                <td>{editable && <button className="link" onClick={() => setDraft(list.filter((_, j) => j !== i))}>{tr("Entfernen")}</button>}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
      <ErrorBox error={error} />
      {draft && <div className="actions" style={{ marginTop: 12 }}><button onClick={() => setDraft(null)}>{tr("Verwerfen")}</button><button className="primary" disabled={busy} onClick={save}>{tr("Felder speichern")}</button></div>}
    </Card>
  );
}

interface ApiKeyRow {
  id: string; name: string; prefix: string; createdAt: string; createdBy: string; lastUsedAt?: string; expiresAt?: string; revokedAt?: string;
  allTenants: boolean; scopes: string[]; tenants: { id: string; displayName: string }[];
}

interface ApiScopeInfo { id: string; area: string; label: string; description: string; write: boolean }

/** Rechte, die keine Vorlage mitbringt und die nur einzeln vergeben werden (greifen in Entra ein). */
const SEPARATE = ['keys.lock', 'certificate.write'];

/** Vorlagen für häufige Fälle; danach lässt sich jedes Recht einzeln an- und abwählen. „Keys sperren“ gehört zu keiner Vorlage. */
function presets(catalog: ApiScopeInfo[]) {
  const read = catalog.filter((s) => !s.write).map((s) => s.id);
  return [
    { id: 'read', label: tr("Nur lesen"), scopes: read },
    { id: 'todo', label: tr("Lesen + ToDo"), scopes: [...read, 'todos.write'] },
    { id: 'all', label: tr("Lesen + Schreiben"), scopes: catalog.map((s) => s.id).filter((id) => !SEPARATE.includes(id)) },
  ];
}

const withoutSeparate = (scopes: string[]) => scopes.filter((x) => !SEPARATE.includes(x));

const sameSet = (a: string[], b: string[]) => a.length === b.length && a.every((x) => b.includes(x));

/** Kurzfassung der Rechte für die Tabelle. */
function rightsSummary(scopes: string[], catalog: ApiScopeInfo[]) {
  const preset = presets(catalog).find((p) => sameSet(p.scopes, withoutSeparate(scopes)));
  const base = preset ? [preset.label] : catalog.filter((s) => scopes.includes(s.id) && !SEPARATE.includes(s.id)).map((s) => s.label);
  return [...base, ...catalog.filter((s) => SEPARATE.includes(s.id) && scopes.includes(s.id)).map((s) => s.label)];
}

export function ApiRightsEditor({ scopes, setScopes, catalog }: { scopes: string[]; setScopes: (v: string[]) => void; catalog: ApiScopeInfo[] }) {
  const toggle = (id: string, on: boolean) => setScopes(on ? [...scopes, id] : scopes.filter((x) => x !== id));
  const active = presets(catalog).find((p) => sameSet(p.scopes, withoutSeparate(scopes)))?.id ?? 'custom';
  // Eine Vorlage lässt die einzeln vergebenen Rechte, wie sie sind.
  const applyPreset = (p: string[]) => setScopes([...p, ...scopes.filter((x) => SEPARATE.includes(x))]);
  const column = (title: string, items: ApiScopeInfo[]) => (
    <div>
      <span className="field-label">{title}</span>
      {items.map((s) => (
        <label key={s.id} className="check" title={s.description} style={{ alignItems: 'flex-start' }}>
          <input type="checkbox" style={{ flexShrink: 0, marginTop: 3 }} checked={scopes.includes(s.id)} onChange={(e) => toggle(s.id, e.target.checked)} />
          <span>{s.label} <span className="mono muted small">{s.id}</span><br /><span className="muted small">{s.description}</span></span>
        </label>
      ))}
    </div>
  );
  return (
    <div className="field-group">
      <span className="field-label">{tr("Rechte")}</span>
      <div className="segmented" role="group" aria-label={tr("Vorlage")} style={{ marginBottom: 8 }}>
        {presets(catalog).map((p) => <button type="button" key={p.id} className={active === p.id ? 'on' : ''} onClick={() => applyPreset(p.scopes)}>{p.label}</button>)}
        <button type="button" className={active === 'custom' ? 'on' : ''} disabled>{tr("Eigene Auswahl")}</button>
      </div>
      <div style={{ display: 'grid', gridTemplateColumns: 'repeat(auto-fit, minmax(260px, 1fr))', gap: 16 }}>
        {column(tr("Lesen"), catalog.filter((s) => !s.write))}
        {column(tr("Schreiben"), catalog.filter((s) => s.write && !SEPARATE.includes(s.id)))}
      </div>
      <div style={{ marginTop: 12 }}>
        {column(tr("Einzeln vergeben (nicht in den Vorlagen)"), catalog.filter((s) => SEPARATE.includes(s.id)))}
      </div>
      {scopes.includes('keys.lock') && <Info tone="warn">{tr("„Keys sperren“ entfernt den Passkey in Entra: Die Person kann sich mit dem Key nicht mehr anmelden. Nur für Tools vergeben, denen Sie das zutrauen, z. B. dem Ticketsystem für Verlustmeldungen.")}</Info>}
    </div>
  );
}

export function ScopeEditor({ allTenants, setAllTenants, tenantIds, setTenantIds, tenants, canAll }: {
  allTenants: boolean; setAllTenants: (v: boolean) => void; tenantIds: string[]; setTenantIds: (v: string[]) => void; tenants: Tenant[]; canAll: boolean;
}) {
  return (
    <div className="field-group">
      <span className="field-label">{tr("Gilt für")}</span>
      <label className="check"><input type="radio" checked={allTenants} disabled={!canAll} onChange={() => setAllTenants(true)} /> {tr("Alle Tenants, auch künftig angebundene")}</label>
      <label className="check"><input type="radio" checked={!allTenants} onChange={() => setAllTenants(false)} /> {tr("Nur ausgewählte Tenants")}</label>
      {!allTenants && (
        <div style={{ paddingLeft: 26 }}>
          {tenants.map((t) => (
            <label key={t.id} className="check">
              <input type="checkbox" checked={tenantIds.includes(t.id)} onChange={(e) => setTenantIds(e.target.checked ? [...tenantIds, t.id] : tenantIds.filter((x) => x !== t.id))} />
              {t.displayName} {t.defaultDomain && <span className="muted small">({t.defaultDomain})</span>}
            </label>
          ))}
          {tenants.length === 0 && <span className="muted small">{tr("Noch kein Tenant angebunden.")}</span>}
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
  const { data: catalog } = useApi<ApiScopeInfo[]>('/api/api-keys/scopes');
  const tenants = (allTenants ?? []).filter((t) => t.status === 'Active');
  const [name, setName] = useState('');
  const [days, setDays] = useState(365);
  const [all, setAll] = useState(canAll);
  const [ids, setIds] = useState<string[]>([]);
  const [scopes, setScopes] = useState<string[] | null>(null);
  const readOnly = catalog ? presets(catalog)[0].scopes : [];
  const chosen = scopes ?? readOnly;
  const [created, setCreated] = useState<string>();
  const [editing, setEditing] = useState<ApiKeyRow | null>(null);
  const { busy, error, run } = useAction();

  const create = () => run(async () => {
    const r = await api.post<{ apiKey: string }>('/api/api-keys', { name, expiresInDays: days || null, allTenants: all, tenantIds: all ? [] : ids, scopes: chosen });
    setCreated(r.apiKey);
    setName('');
    setIds([]);
    setScopes(null);
    reload();
  });
  const revoke = (id: string) => run(async () => { await api.del(`/api/api-keys/${id}`); reload(); });

  return (
    <Card title={<span id="api">{tr("API-Schlüssel")}</span>} className="mt" actions={<a className="small" href="/api/docs" target="_blank" rel="noreferrer">{tr("API-Dokumentation")}</a>}>
      <p className="muted small" style={{ marginTop: 0 }}>
        {tr("Externe Tools (z. B. Ticketsystem, RMM, SIEM) nutzen")} <code>{tr("/api/v1")}</code> {tr("mit dem Header")} <code>{tr("X-Api-Key")}</code>{tr(". Jeder Schlüssel bekommt eigene Rechte, ähnlich wie eine Rolle: z. B. nur lesen für alle Tenants, nur lesen für einen Tenant, oder lesen und schreiben. Schreiben heißt: Personen auf die")} <Link to="/todos">{tr("ToDo-Liste")}</Link> {tr("setzen, Tags und Zusatzfelder pflegen und Keys sperren. Zurücksetzen, Ausmustern und Löschen gibt es über die API nicht. Jede Änderung über die API steht im Audit-Log mit dem Schlüssel als Akteur. Mit dem Parameter")} <code>{tr("tenant")}</code> {tr("(ID oder Domain) fragt ein Tool gezielt einen Tenant ab;")} <code>{tr("GET /api/v1/me")}</code> {tr("zeigt Tenants und Rechte des Schlüssels.")}
      </p>
      {created && <Info tone="ok">{tr("Neuer Schlüssel, nur jetzt sichtbar:")} <code style={{ wordBreak: 'break-all' }}>{created}</code> <button className="link" onClick={() => void navigator.clipboard?.writeText(created)}>{tr("Kopieren")}</button></Info>}
      <form onSubmit={(e) => { e.preventDefault(); create(); }}>
        <div className="toolbar" style={{ marginBottom: 6 }}>
          <input placeholder={tr("Name, z. B. Ticketsystem")} value={name} onChange={(e) => setName(e.target.value)} aria-label={tr("Name")} />
          <select value={days} onChange={(e) => setDays(Number(e.target.value))} aria-label={tr("Gültigkeit")}>
            <option value={90}>{tr("90 Tage")}</option><option value={365}>{tr("1 Jahr")}</option><option value={730}>{tr("2 Jahre")}</option><option value={0}>{tr("unbegrenzt")}</option>
          </select>
        </div>
        <ScopeEditor allTenants={all} setAllTenants={setAll} tenantIds={ids} setTenantIds={setIds} tenants={tenants} canAll={canAll} />
        {catalog && <ApiRightsEditor scopes={chosen} setScopes={setScopes} catalog={catalog} />}
        <button type="submit" className="primary" disabled={!name.trim() || busy || (!all && ids.length === 0) || chosen.length === 0}>{tr("Schlüssel erstellen")}</button>
      </form>
      <ErrorBox error={error} />
      {!data && <Spinner />}
      {data && data.length > 0 && (
        <table className="mt">
          <thead><tr><th>{tr("Name")}</th><th>{tr("Präfix")}</th><th>{tr("Gilt für")}</th><th>{tr("Rechte")}</th><th>{tr("Erstellt")}</th><th>{tr("Zuletzt genutzt")}</th><th>{tr("Gültig bis")}</th><th /></tr></thead>
          <tbody>
            {data.map((k) => (
              <tr key={k.id} style={{ opacity: k.revokedAt ? 0.55 : 1 }}>
                <td>{k.name}</td>
                <td className="mono">{tr("ylm_{prefix}_…", { prefix: k.prefix })}</td>
                <td className="small">{k.allTenants ? <Badge tone="info">{tr("Alle Tenants")}</Badge> : k.tenants.map((t) => t.displayName).join(', ') || <span className="muted">{tr("kein Tenant")}</span>}</td>
                <td className="small">
                  <span style={{ display: 'inline-flex', flexWrap: 'wrap', gap: 4 }}>
                    {rightsSummary(k.scopes, catalog ?? []).map((l) => <Badge key={l} tone={catalog?.some((s) => s.write && k.scopes.includes(s.id)) ? 'warn' : 'info'}>{l}</Badge>)}
                  </span>
                </td>
                <td className="small">{fmtDate(k.createdAt, false)} · {k.createdBy}</td>
                <td className="small">{fmtDate(k.lastUsedAt)}</td>
                <td className="small">{k.expiresAt ? fmtDate(k.expiresAt, false) : 'unbegrenzt'}</td>
                <td>
                  {k.revokedAt ? <Badge>{tr("widerrufen")}</Badge> : (
                    <span style={{ display: 'inline-flex', gap: 10 }}>
                      <button className="link" onClick={() => setEditing(k)}>{tr("Rechte ändern")}</button>
                      <button className="link" onClick={() => revoke(k.id)}>{tr("Widerrufen")}</button>
                    </span>
                  )}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
      {editing && catalog && <ScopeModal keyRow={editing} tenants={tenants} canAll={canAll} catalog={catalog} onClose={() => setEditing(null)} onSaved={() => { setEditing(null); reload(); }} />}
    </Card>
  );
}

function ScopeModal({ keyRow, tenants, canAll, catalog, onClose, onSaved }: { keyRow: ApiKeyRow; tenants: Tenant[]; canAll: boolean; catalog: ApiScopeInfo[]; onClose: () => void; onSaved: () => void }) {
  const [all, setAll] = useState(keyRow.allTenants);
  const [ids, setIds] = useState(keyRow.tenants.map((t) => t.id));
  const [scopes, setScopes] = useState(keyRow.scopes);
  const { busy, error, run } = useAction();
  const save = () => run(async () => { await api.put(`/api/api-keys/${keyRow.id}/scope`, { allTenants: all, tenantIds: all ? [] : ids, scopes }); onSaved(); });
  return (
    <Modal title={tr("Rechte von „{name}“", { name: keyRow.name })} onClose={onClose}
      footer={<><button onClick={onClose}>{tr("Abbrechen")}</button><button className="primary" disabled={busy || (!all && ids.length === 0) || scopes.length === 0} onClick={save}>{tr("Speichern")}</button></>}>
      <ScopeEditor allTenants={all} setAllTenants={setAll} tenantIds={ids} setTenantIds={setIds} tenants={tenants} canAll={canAll} />
      <ApiRightsEditor scopes={scopes} setScopes={setScopes} catalog={catalog} />
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
const certLabel = { ok: tr("Gültig"), warning: tr("Läuft bald ab"), critical: tr("Läuft in Kürze ab"), expired: tr("Abgelaufen"), missing: tr("Nicht vorhanden") } as const;

/** App-Zertifikat: Anmeldung der Plattform in allen Tenants. Warnung, automatische Erneuerung, Fallback von Hand. */
function AppCertificateCard() {
  const { data, reload } = useApi<CertificateStatus>('/api/platform/certificate');
  const { busy, error, run } = useAction();
  const act = (path: string) => run(async () => { await api.post('/api/platform/certificate/' + path, {}); reload(); });
  const discard = () => run(async () => { await api.del('/api/platform/certificate/next'); reload(); });
  if (!data || data.state === 'missing') return null;
  return (
    <Card title={<span id="certificate">{tr("App-Zertifikat")}</span>}
      actions={data.autoRenewPossible && !data.nextThumbprint && <button disabled={busy} onClick={() => act('renew')}>{tr("Jetzt erneuern")}</button>}>
      <p className="small muted">{tr("Mit diesem Zertifikat meldet sich die Plattform in jedem angebundenen Tenant bei Microsoft Graph an. Läuft es ab, funktionieren Sync, Registrierung und Rollout nicht mehr.")}</p>
      <dl className="props">
        <dt>{tr("Status")}</dt><dd><Badge tone={certTone[data.state]}>{certLabel[data.state]}</Badge>{data.daysLeft != null && data.daysLeft >= 0 && <span className="muted small"> {tr("noch {daysLeft} Tage", { daysLeft: data.daysLeft })}</span>}</dd>
        <dt>{tr("Gültig bis")}</dt><dd>{data.notAfter ? fmtDate(data.notAfter) : '–'}</dd>
        <dt>{tr("Fingerabdruck")}</dt><dd className="mono small">{data.thumbprint}</dd>
        <dt>{tr("Erneuerung")}</dt><dd>{data.autoRenew
          ? <>{tr("automatisch {renewalDays} Tage vor dem Ablauf", { renewalDays: data.renewalDays })}</>
          : <>{tr("nur Warnung (automatische Erneuerung abgeschaltet:")} <span className="mono">{tr("YLM_CERT_AUTO_RENEW=false")}</span>)</>}</dd>
      </dl>
      {data.lastError && <Info tone="error">{tr("Letzter Versuch {n} fehlgeschlagen: {lastError}", { n: data.lastAttemptAt ? fmtDate(data.lastAttemptAt) : '', lastError: data.lastError })}</Info>}
      {data.nextThumbprint ? (
        <Info tone="warn">
          <strong>{tr("Neues Zertifikat wartet auf Entra")}</strong> (<span className="mono">{data.nextThumbprint}</span>{tr("). Die Plattform prüft jede Minute und schaltet um, sobald Entra es annimmt. Bei der Erneuerung von Hand: Zertifikat herunterladen und im Entra Admin Center unter App-Registrierungen, YLM-App, „Zertifikate und Geheimnisse“ hochladen, dann aktivieren.")}
          <div className="toolbar mt">
            <button onClick={() => download('/api/platform/certificate/next.cer')}>{tr("Zertifikat herunterladen (.cer)")}</button>
            <button disabled={busy} onClick={() => act('activate')}>{tr("Jetzt aktivieren")}</button>
            <button className="ghost" disabled={busy} onClick={discard}>{tr("Verwerfen")}</button>
          </div>
        </Info>
      ) : (data.state !== 'ok' || !data.autoRenewPossible || data.lastError) && (
        <p className="small">{tr("Klappt die automatische Erneuerung nicht (zum Beispiel weil das Zertifikat schon abgelaufen ist): ")}
          <button className="link" disabled={busy} onClick={() => act('prepare')}>{tr("Neues Zertifikat von Hand vorbereiten")}</button></p>
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
    <Card title={tr("Anmeldung an der Plattform")}>
      <dl className="explain">
        <dt>{tr("Entra-SSO")}</dt><dd>{tr("Konten aus dem Heim-Tenant melden sich über Microsoft an (passwortlos mit YubiKey).")}</dd>
        <dt>{tr("Lokale Konten (Passkey)")}</dt><dd>{tr("Anmeldung mit Passkey direkt an der Plattform, unabhängig von Entra. Empfohlen für Kundenzugriffe und Breakglass.")}</dd>
        <dt>{tr("Lokale Konten (Passwort + TOTP)")}</dt><dd>{tr("Passwort plus Authenticator-Code, wenn kein Passkey möglich ist.")}</dd>
      </dl>
      {data && (
        <label className="check">
          <input type="checkbox" checked={data.entraSsoEnabled} disabled={busy || !data.entraConfigured} onChange={(e) => toggle(e.target.checked)} />
          {tr("Anmeldung mit Entra-SSO erlauben")} {!data.entraConfigured && <span className="muted small">{tr("(keine App-Registrierung eingerichtet)")}</span>}
        </label>
      )}
      <p className="small muted">{tr("Lokale Konten legen Sie unter")} <Link to="/users">{tr("Benutzer & Rollen")}</Link> {tr("an. Entra-SSO lässt sich nur abschalten, wenn ein Owner ein eingerichtetes lokales Konto hat.")}</p>
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
  AgentDecides: tr("Automatisch, am PC abschaltbar (Standard)"),
  Always: tr("Immer automatisch"),
  Never: tr("Keine automatischen Updates"),
};

function AgentUpdates({ agent, onChanged }: { agent: UpdateInfo['agent']; onChanged: () => void }) {
  const { busy, error, run } = useAction();
  const setPolicy = (policy: AgentUpdatePolicy) => run(async () => { await api.put('/api/platform/updates/agent-policy', { policy }); onChanged(); });
  const parts = [
    agent.outdated > 0 ? tr("{outdated} von {agents} veraltet", { outdated: agent.outdated, agents: agent.agents }) : tr("alle {agents} aktuell", { agents: agent.agents }),
    agent.autoUpdateOff > 0 && tr("{autoUpdateOff} mit Auto-Update aus", { autoUpdateOff: agent.autoUpdateOff }),
    agent.failed > 0 && tr("{failed} mit Updatefehler", { failed: agent.failed }),
  ].filter(Boolean).join(' · ');
  return (
    <>
      <tr><th>{tr("Agent im Paket")}</th><td>{agent.bundledVersion
        ? <><span className="mono">{agent.bundledVersion}</span> · {parts}</>
        : <span className="muted">{tr("nicht enthalten (lokaler Build)")}</span>}</td></tr>
      <tr><th>{tr("Agent-Updates")}</th><td>
        <select value={agent.policy} disabled={busy} onChange={(e) => setPolicy(e.target.value as AgentUpdatePolicy)}>
          {(Object.keys(policyLabels) as AgentUpdatePolicy[]).map((p) => <option key={p} value={p}>{policyLabels[p]}</option>)}
        </select>
        <div className="small muted">{tr("Agents fragen alle 4 Stunden nach einer neuen Version. „Jetzt aktualisieren“ im Tray oder unter „Mein Agent“ geht immer.")}</div>
        <ErrorBox error={error} />
        {agent.list.length > 0 && (
          <details><summary className="small muted">{tr("Alle Agents ({length})", { length: agent.list.length })}</summary>
            <table>
              <thead><tr><th>{tr("PC")}</th><th>{tr("Benutzer")}</th><th>{tr("Version")}</th><th>{tr("Auto-Update")}</th><th>{tr("Zuletzt gesehen")}</th></tr></thead>
              <tbody>{agent.list.map((a) => (
                <tr key={a.id}>
                  <td>{a.machineName} {a.online ? <Badge tone="ok">{tr("online")}</Badge> : <Badge>{tr("offline")}</Badge>}</td>
                  <td>{a.owner ?? '–'}</td>
                  <td><span className="mono">{a.agentVersion ?? '?'}</span> {a.outdated && <Badge tone="warn">{tr("veraltet")}</Badge>}</td>
                  <td>{a.autoUpdate == null ? <span className="muted small">{tr("unbekannt (vor 0.5)")}</span> : a.autoUpdate ? 'an' : <Badge>{tr("aus")}</Badge>}
                    {a.updateError && <div className="small" title={a.updateError}><Badge tone="error">{tr("Fehler")}</Badge> {a.updateError.length > 80 ? a.updateError.slice(0, 80) + ' …' : a.updateError}</div>}</td>
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
    if (!(await confirm({ title: tr("Auf Version {latestVersion} aktualisieren?", { latestVersion: data!.latestVersion }), message: tr("Die Plattform ist dabei etwa eine Minute nicht erreichbar. Laufende Registrierungen vorher abschließen."), confirmLabel: tr("Jetzt aktualisieren") }))) return;
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
    if (!(await confirm({ title: tr("Sicherheitsupdates jetzt einspielen?"), message: tr("Lädt alle Images der installierten Version neu und startet geänderte Container neu. Die Plattform ist dabei kurz nicht erreichbar."), confirmLabel: tr("Jetzt einspielen") }))) return;
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
    <Card title={tr("Version und Updates")} actions={<button disabled={busy || phase !== 'idle'} onClick={() => setRefresh((r) => r + 1)}>{tr("Jetzt prüfen")}</button>}>
      <ErrorBox error={error ?? actionError} />
      {!data && !error && <Spinner />}
      {data && <>
        <table>
          <tbody>
            <tr><th>{tr("Installiert")}</th><td className="mono">{data.currentVersion}</td></tr>
            <tr><th>{tr("Neueste Version")}</th><td>{data.latestVersion
              ? <><span className="mono">{data.latestVersion}</span> {data.updateAvailable ? <Badge tone="warn">{tr("Update verfügbar")}</Badge> : <Badge tone="ok">{tr("aktuell")}</Badge>}
                {data.publishedAt && <span className="small muted"> {tr("vom {n}", { n: fmtDate(data.publishedAt, false) })}</span>}
                {data.updateAvailable && <> · <Link to="/versionen">{tr("Was ist neu?")}</Link></>}{data.releaseUrl && <> · <a href={data.releaseUrl} target="_blank" rel="noreferrer">{tr("auf GitHub")}</a></>}</>
              : <span className="muted">{data.error ?? 'unbekannt'}</span>}</td></tr>
            <AgentUpdates agent={data.agent} onChanged={reload} />
            <tr><th>{tr("Update per Klick")}</th><td>{!data.updater.configured
              ? <span className="muted">{tr("nicht eingerichtet (")}<code>{tr("UPDATER_TOKEN")}</code> {tr("in")} <code>{tr(".env")}</code> {tr("fehlt)")}</span>
              : status && (status.state === 'unreachable' || status.state === 'error')
                ? <Badge tone="warn">{tr("Updater nicht erreichbar")}</Badge>
                : <><Badge tone="ok">{tr("bereit")}</Badge>{!data.updateAvailable && <span className="small muted"> {tr("· der Knopf erscheint, sobald eine neue Version verfügbar ist")}</span>}</>}</td></tr>
            {data.updater.configured && status && status.state !== 'unreachable' && status.state !== 'error' && (
              <tr><th>{tr("Sicherheitsupdates der Container")}</th><td>
                {sec === undefined
                  ? <span className="small muted">{tr("Älterer Updater: Installationsskript einmal erneut ausführen, dann spielt er täglich die Sicherheitsupdates der Basis-Images ein.")}</span>
                  : <>{sec.schedule ? <>{tr("täglich um {schedule} Uhr", { schedule: sec.schedule })}</> : <span className="muted">{tr("automatisch aus (")}<code>{tr("YLM_AUTO_REFRESH")}</code>)</span>}
                    {sec.lastAt && <span className="small muted"> {tr("· zuletzt {lastAt}", { lastAt: fmtDate(sec.lastAt) })} {sec.result === 'failed' ? <Badge tone="error">{tr("fehlgeschlagen")}</Badge> : sec.result === 'running' ? <Badge>{tr("läuft")}</Badge> : <Badge tone="ok">{tr("ok")}</Badge>}</span>}
                    {' '}<button className="small" disabled={busy || phase !== 'idle' || status.state === 'running'} onClick={securityRefresh}>{tr("Jetzt einspielen")}</button>
                    <div className="small muted">{tr("Lädt die Images der installierten Version mit den neuesten Patches (Basis-Images werden wöchentlich und bei Schwachstellen neu gebaut). Den Host selbst (Kernel, Docker) bitte separat aktuell halten.")}</div></>}
              </td></tr>
            )}
            <tr><th>{tr("Geprüft")}</th><td className="small muted">{fmtDate(data.checkedAt)}</td></tr>
          </tbody>
        </table>
        {phase !== 'idle' && <Info>{phase === 'restarting' ? tr("Der Server startet neu … Die Seite lädt automatisch, sobald er wieder läuft.") : kind === 'refresh' ? tr("Sicherheitsupdates laufen: Images werden geladen …") : tr("Update läuft: neues Image wird geladen …")}</Info>}
        {data.updateAvailable && phase === 'idle' && (data.updater.configured
          ? <button className="primary" disabled={busy || status?.state === 'running'} onClick={apply}>{tr("Jetzt auf {latestVersion} aktualisieren", { latestVersion: data.latestVersion })}</button>
          : <Info>{tr("Für das Update per Klick fehlt der Updater-Container: in")} <code>{tr(".env")}</code> <code>{tr("UPDATER_TOKEN")}</code> {tr("setzen (z. B.")} <code>{tr("openssl rand -hex 32")}</code>{tr(") und")} <code>{tr("docker compose up -d")}</code> {tr("ausführen. Alternativ von Hand:")} <code>{tr("docker compose pull && docker compose up -d")}</code>.</Info>)}
        {data.updater.configured && status && (status.state === 'unreachable' || status.state === 'error') && <Info tone="warn">{status.message}</Info>}
        {status?.state === 'failed' && phase === 'idle' && <Info tone="error">{tr("Das letzte Update ist fehlgeschlagen. Details im Protokoll unten.")}</Info>}
        {(log.length > 0 || (status?.state === 'failed' && status.log?.length)) && (
          <details open={phase !== 'idle' || status?.state === 'failed'}><summary className="small muted">{tr("Protokoll")}</summary>
            <pre className="script" style={{ maxHeight: 220, overflow: 'auto' }}>{(log.length ? log : status?.log ?? []).join('\n')}</pre>
          </details>
        )}
      </>}
    </Card>
    </div>
  );
}
