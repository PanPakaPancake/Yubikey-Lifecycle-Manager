import { useEffect, useState } from 'react';
import { Link, useLocation } from 'react-router';
import { api, fieldTypes, fmtDate, keyFromLabel, type CustomField, type GlobalField, type Tenant } from '../api';
import { useAuth, useCan } from '../auth';
import { Badge, Card, ErrorBox, Info, Modal, PageHeader, Spinner, useAction, useApi } from '../ui';
import { FieldExplanation } from './TenantDetail';

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
      <PageHeader title="Plattform" subtitle="Einstellungen für die ganze Installation. Tenants erben sie und können sie teilweise anpassen." />
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
      <GlobalFieldsCard editable={can('ManageProfiles') && !!me.user?.allTenants} />
      {can('ManageApiKeys') && <ApiKeysCard />}
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
  allTenants: boolean; tenants: { id: string; displayName: string }[];
}

function ScopeEditor({ allTenants, setAllTenants, tenantIds, setTenantIds, tenants, canAll }: {
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
  const [created, setCreated] = useState<string>();
  const [editing, setEditing] = useState<ApiKeyRow | null>(null);
  const { busy, error, run } = useAction();

  const create = () => run(async () => {
    const r = await api.post<{ apiKey: string }>('/api/api-keys', { name, expiresInDays: days || null, allTenants: all, tenantIds: all ? [] : ids });
    setCreated(r.apiKey);
    setName('');
    setIds([]);
    reload();
  });
  const revoke = (id: string) => run(async () => { await api.del(`/api/api-keys/${id}`); reload(); });

  return (
    <Card title={<span id="api">API-Schlüssel (Lese-API)</span>} className="mt" actions={<a className="small" href="/api/docs" target="_blank" rel="noreferrer">API-Dokumentation</a>}>
      <p className="muted small" style={{ marginTop: 0 }}>
        Externe Tools (z. B. Ticketsystem, RMM) lesen Inventar und Kennzahlen über <code>/api/v1</code> mit dem Header <code>X-Api-Key</code>. Die API kann nichts ändern.
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
                <td className="small">{k.allTenants ? <Badge tone="info">Alle Tenants</Badge> : k.tenants.map((t) => t.displayName).join(', ') || <span className="muted">kein Tenant</span>}</td>
                <td className="small">{fmtDate(k.createdAt, false)} · {k.createdBy}</td>
                <td className="small">{fmtDate(k.lastUsedAt)}</td>
                <td className="small">{k.expiresAt ? fmtDate(k.expiresAt, false) : 'unbegrenzt'}</td>
                <td>
                  {k.revokedAt ? <Badge>widerrufen</Badge> : (
                    <span style={{ display: 'inline-flex', gap: 10 }}>
                      <button className="link" onClick={() => setEditing(k)}>Geltung ändern</button>
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
  const { busy, error, run } = useAction();
  const save = () => run(async () => { await api.put(`/api/api-keys/${keyRow.id}/scope`, { allTenants: all, tenantIds: all ? [] : ids }); onSaved(); });
  return (
    <Modal title={`Geltung von „${keyRow.name}“`} onClose={onClose}
      footer={<><button onClick={onClose}>Abbrechen</button><button className="primary" disabled={busy || (!all && ids.length === 0)} onClick={save}>Speichern</button></>}>
      <ScopeEditor allTenants={all} setAllTenants={setAll} tenantIds={ids} setTenantIds={setIds} tenants={tenants} canAll={canAll} />
      <ErrorBox error={error} />
    </Modal>
  );
}
