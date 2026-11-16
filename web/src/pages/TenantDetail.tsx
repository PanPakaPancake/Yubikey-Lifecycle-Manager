import { useState } from 'react';
import { Link, useNavigate, useParams, useSearchParams } from 'react-router';
import { api, fmtDate, type CustomField, type Tenant } from '../api';
import { useCan } from '../auth';
import { Badge, Card, download, ErrorBox, Field, Info, Modal, PageHeader, Spinner, useAction, useApi } from '../ui';

const statusText = { Ok: 'In Ordnung', Warning: 'Hinweis', Failed: 'Fehlt' } as const;

export default function TenantDetailPage() {
  const { id } = useParams();
  const [params] = useSearchParams();
  const can = useCan();
  const { data: t, error, setData } = useApi<Tenant>('/api/tenants/' + id, [id]);
  const act = useAction();
  const [syncResult, setSyncResult] = useState<string>();
  const [offboarding, setOffboarding] = useState(false);

  if (error) return <ErrorBox error={error} />;
  if (!t) return <Spinner label="Wird geladen …" />;
  const active = t.status === 'Active';
  const fixable = t.checks.filter((c) => c.canFix && c.status !== 'Ok');

  const runChecks = () => act.run(async () => setData(await api.post<Tenant>(`/api/tenants/${t.id}/checks`)));
  const fix = (ids: string[]) => act.run(async () => setData(await api.post<Tenant>(`/api/tenants/${t.id}/fix`, { checkIds: ids })));
  const sync = () => act.run(async () => {
    const r = await api.post<{ usersScanned: number; imported: number; updated: number; removed: number }>(`/api/tenants/${t.id}/sync`);
    setSyncResult(`${r.usersScanned} Personen geprüft, ${r.imported} Keys neu übernommen, ${r.updated} aktualisiert, ${r.removed} entfernt.`);
    setData(await api.get<Tenant>('/api/tenants/' + t.id));
  });

  return (
    <>
      <PageHeader
        title={t.displayName}
        subtitle={<><Link to="/tenants">Tenants</Link> · {t.defaultDomain} · <span className="mono small">{t.entraTenantId}</span></>}
        actions={active && <>
          {can('SyncTenants') && <button disabled={act.busy} onClick={sync}>Jetzt synchronisieren</button>}
          {can('ManageTenants') && <button onClick={() => download(`/api/tenants/${t.id}/export.zip`)}>Daten exportieren</button>}
        </>}
      />
      {params.get('onboarded') && <Info tone="ok">Der Tenant ist angebunden. Prüfen Sie unten die FIDO2-Einstellungen.</Info>}
      {!active && <Info tone="warn">Dieser Tenant wurde am {fmtDate(t.offboardedAt)} offboarded. Die Daten werden am {fmtDate(t.dataDeletionDueAt, false)} gelöscht. Ein erneuter Admin-Consent stellt ihn wieder her.</Info>}
      <ErrorBox error={act.error} />
      {syncResult && <Info tone="ok">{syncResult}</Info>}

      <Card
        title="Prüfungen"
        actions={<>
          <span className="small muted">Zuletzt {fmtDate(t.lastCheckAt)}</span>
          {active && <button disabled={act.busy} onClick={runChecks}>Erneut prüfen</button>}
          {active && can('ManageTenants') && fixable.length > 1 && <button className="primary" disabled={act.busy} onClick={() => fix(fixable.map((c) => c.id))}>Alles beheben</button>}
        </>}
      >
        <div className="checks">
          {t.checks.map((c) => (
            <div key={c.id} className="check-row">
              <span className={'dot ' + c.status} aria-label={statusText[c.status]} />
              <div style={{ flex: 1 }}>
                <strong>{c.title}</strong> <span className="small muted">{statusText[c.status]}</span>
                <div>{c.message}</div>
                {c.status !== 'Ok' && c.manualInstructions && (
                  <details><summary className="small">Manuell beheben</summary><p className="small" style={{ whiteSpace: 'pre-wrap' }}>{c.manualInstructions}</p></details>
                )}
              </div>
              {active && c.canFix && c.status !== 'Ok' && can('ManageTenants') && <button className="primary" disabled={act.busy} onClick={() => fix([c.id])}>Jetzt beheben</button>}
            </div>
          ))}
          {t.checks.length === 0 && <p className="muted">Noch nicht geprüft.</p>}
        </div>
      </Card>

      {active && (
        <div className="grid cols-2 mt">
          <SettingsCard tenant={t} onSaved={setData} />
          <Card title="Synchronisation">
            <table>
              <tbody>
                <tr><th>Letzter Sync</th><td>{fmtDate(t.lastSyncAt)} {t.lastSyncSucceeded === false && <Badge tone="error">Fehler</Badge>}</td></tr>
                {t.lastSyncMessage && <tr><th>Meldung</th><td className="small">{t.lastSyncMessage}</td></tr>}
                <tr><th>Personen in Zielgruppe</th><td>{t.targetUserCount ?? '–'}</td></tr>
                <tr><th>Admin-Consent</th><td>{fmtDate(t.consentedAt)} durch {t.consentedBy}</td></tr>
              </tbody>
            </table>
          </Card>
        </div>
      )}

      {active && <FieldsCard tenantId={t.id} editable={can('ManageProfiles')} />}
      {active && can('ManageApiKeys') && <ApiKeysCard tenantId={t.id} />}

      {active && can('ManageTenants') && (
        <Card title="Offboarding" className="mt">
          <p>Die Plattform nutzt den Tenant danach nicht mehr, API-Schlüssel werden widerrufen und die Daten nach der Aufbewahrungsfrist gelöscht. Registrierte Keys in Entra ID bleiben unverändert.</p>
          <button className="danger" onClick={() => setOffboarding(true)}>Tenant offboarden</button>
        </Card>
      )}
      {offboarding && <OffboardModal tenant={t} onClose={() => setOffboarding(false)} />}
    </>
  );
}

function SettingsCard({ tenant, onSaved }: { tenant: Tenant; onSaved: (t: Tenant) => void }) {
  const can = useCan();
  const [name, setName] = useState(tenant.displayName);
  const [interval, setInterval] = useState(tenant.syncIntervalMinutes);
  const [groupQ, setGroupQ] = useState('');
  const [groups, setGroups] = useState<{ id: string; displayName: string }[]>();
  const [group, setGroup] = useState<{ id: string; name: string } | null>(tenant.targetGroupId ? { id: tenant.targetGroupId, name: tenant.targetGroupName ?? tenant.targetGroupId } : null);
  const { busy, error, run } = useAction();
  const [saved, setSaved] = useState(false);
  const editable = can('ManageTenants');

  const searchGroups = () => run(async () => setGroups(await api.get(`/api/tenants/${tenant.id}/groups?q=${encodeURIComponent(groupQ)}`)));
  const save = () => run(async () => {
    onSaved(await api.patch<Tenant>(`/api/tenants/${tenant.id}`, { displayName: name, syncIntervalMinutes: interval, targetGroupId: group?.id ?? '', targetGroupName: group?.name ?? null }));
    setSaved(true);
  });

  return (
    <Card title="Einstellungen">
      <Field label="Anzeigename"><input value={name} disabled={!editable} onChange={(e) => { setName(e.target.value); setSaved(false); }} /></Field>
      <Field label="Zielgruppe" hint="Personen, die einen YubiKey bekommen sollen. Grundlage für Abdeckung und Prüfung der FIDO2-Richtlinie.">
        <div className="toolbar" style={{ margin: 0 }}>
          <span>{group ? <strong>{group.name}</strong> : 'Alle Benutzer'}</span>
          {group && editable && <button className="link" onClick={() => { setGroup(null); setSaved(false); }}>entfernen</button>}
        </div>
      </Field>
      {editable && (
        <form className="toolbar" onSubmit={(e) => { e.preventDefault(); if (groupQ.trim()) searchGroups(); }}>
          <input type="search" placeholder="Gruppe suchen …" value={groupQ} onChange={(e) => setGroupQ(e.target.value)} />
          <button type="submit" disabled={!groupQ.trim()}>Suchen</button>
        </form>
      )}
      {groups && (
        <div className="pick" style={{ marginBottom: 12 }}>
          {groups.map((g) => <button key={g.id} className={group?.id === g.id ? 'selected' : ''} onClick={() => { setGroup({ id: g.id, name: g.displayName }); setGroups(undefined); setSaved(false); }}>{g.displayName}</button>)}
          {groups.length === 0 && <span className="muted">Keine Gruppe gefunden.</span>}
        </div>
      )}
      <Field label="Sync-Intervall (Minuten)"><input type="number" min={5} max={1440} value={interval} disabled={!editable} onChange={(e) => { setInterval(Number(e.target.value)); setSaved(false); }} /></Field>
      <ErrorBox error={error} />
      {editable && <button className="primary" disabled={busy} onClick={save}>{saved ? 'Gespeichert' : 'Speichern'}</button>}
    </Card>
  );
}

const fieldTypes: Record<CustomField['type'], string> = { Text: 'Text', Number: 'Zahl', Date: 'Datum', Select: 'Auswahl', Boolean: 'Ja/Nein' };

function FieldsCard({ tenantId, editable }: { tenantId: string; editable: boolean }) {
  const { data, reload } = useApi<CustomField[]>(`/api/tenants/${tenantId}/fields`, [tenantId]);
  const [draft, setDraft] = useState<CustomField[] | null>(null);
  const { busy, error, run } = useAction();
  const list = draft ?? data ?? [];
  const update = (i: number, patch: Partial<CustomField>) => setDraft(list.map((f, j) => (j === i ? { ...f, ...patch } : f)));
  const save = () => run(async () => {
    await api.put(`/api/tenants/${tenantId}/fields`, list.map((f) => ({ key: f.key, label: f.label, type: f.type, required: f.required, options: f.type === 'Select' ? f.options : [] })));
    await reload();
    setDraft(null);
  });

  return (
    <Card title="Zusatzfelder für Keys" className="mt" actions={editable && <button onClick={() => setDraft([...list, { key: '', label: '', type: 'Text', required: false, options: [] }])}>Feld hinzufügen</button>}>
      {list.length === 0 && <p className="muted">Keine Zusatzfelder. Beispiele: Kostenstelle, Standort, Inventarnummer.</p>}
      {list.length > 0 && (
        <table>
          <thead><tr><th>Bezeichnung</th><th>Schlüssel</th><th>Typ</th><th>Optionen</th><th>Pflicht</th><th /></tr></thead>
          <tbody>
            {list.map((f, i) => (
              <tr key={i}>
                <td><input value={f.label} disabled={!editable} onChange={(e) => update(i, { label: e.target.value, key: f.id ? f.key : e.target.value.toLowerCase().replace(/[^a-z0-9_-]+/g, '_').replace(/^_+|_+$/g, '') })} /></td>
                <td><input value={f.key} disabled={!editable || !!f.id} onChange={(e) => update(i, { key: e.target.value })} className="mono" /></td>
                <td>
                  <select value={f.type} disabled={!editable} onChange={(e) => update(i, { type: e.target.value as CustomField['type'] })}>
                    {Object.entries(fieldTypes).map(([k, v]) => <option key={k} value={k}>{v}</option>)}
                  </select>
                </td>
                <td>{f.type === 'Select' && <input value={f.options.join(', ')} disabled={!editable} placeholder="A, B, C" onChange={(e) => update(i, { options: e.target.value.split(',').map((o) => o.trim()).filter(Boolean) })} />}</td>
                <td><input type="checkbox" checked={f.required} disabled={!editable} onChange={(e) => update(i, { required: e.target.checked })} /></td>
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

interface ApiKeyRow { id: string; name: string; prefix: string; createdAt: string; createdBy: string; lastUsedAt?: string; expiresAt?: string; revokedAt?: string }

function ApiKeysCard({ tenantId }: { tenantId: string }) {
  const { data, reload } = useApi<ApiKeyRow[]>(`/api/tenants/${tenantId}/api-keys`, [tenantId]);
  const [name, setName] = useState('');
  const [days, setDays] = useState(365);
  const [created, setCreated] = useState<string>();
  const { busy, error, run } = useAction();
  const create = () => run(async () => {
    const r = await api.post<{ apiKey: string }>(`/api/tenants/${tenantId}/api-keys`, { name, expiresInDays: days || null });
    setCreated(r.apiKey);
    setName('');
    reload();
  });
  const revoke = (id: string) => run(async () => { await api.del(`/api/tenants/${tenantId}/api-keys/${id}`); reload(); });
  return (
    <Card title="API-Schlüssel (Lese-API)" className="mt" actions={<a className="small" href="/api/docs" target="_blank" rel="noreferrer">API-Dokumentation</a>}>
      <p className="muted small">Externe Tools (z. B. Ticketsystem) lesen Inventar und Kennzahlen dieses Tenants über <code>/api/v1</code> mit dem Header <code>X-Api-Key</code>. Die API kann nichts ändern.</p>
      {created && <Info tone="ok">Neuer Schlüssel – nur jetzt sichtbar: <code style={{ wordBreak: 'break-all' }}>{created}</code> <button className="link" onClick={() => void navigator.clipboard?.writeText(created)}>Kopieren</button></Info>}
      <form className="toolbar" onSubmit={(e) => { e.preventDefault(); create(); }}>
        <input placeholder="Name, z. B. Ticketsystem" value={name} onChange={(e) => setName(e.target.value)} />
        <select value={days} onChange={(e) => setDays(Number(e.target.value))} aria-label="Gültigkeit">
          <option value={90}>90 Tage</option><option value={365}>1 Jahr</option><option value={730}>2 Jahre</option><option value={0}>unbegrenzt</option>
        </select>
        <button type="submit" disabled={!name.trim() || busy}>Erstellen</button>
      </form>
      <ErrorBox error={error} />
      {data && data.length > 0 && (
        <table>
          <thead><tr><th>Name</th><th>Präfix</th><th>Erstellt</th><th>Zuletzt genutzt</th><th>Gültig bis</th><th /></tr></thead>
          <tbody>
            {data.map((k) => (
              <tr key={k.id} style={{ opacity: k.revokedAt ? 0.55 : 1 }}>
                <td>{k.name}</td>
                <td className="mono">ylm_{k.prefix}_…</td>
                <td className="small">{fmtDate(k.createdAt, false)} · {k.createdBy}</td>
                <td className="small">{fmtDate(k.lastUsedAt)}</td>
                <td className="small">{k.expiresAt ? fmtDate(k.expiresAt, false) : 'unbegrenzt'}</td>
                <td>{k.revokedAt ? <Badge>widerrufen</Badge> : <button className="link" onClick={() => revoke(k.id)}>Widerrufen</button>}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </Card>
  );
}

function OffboardModal({ tenant, onClose }: { tenant: Tenant; onClose: () => void }) {
  const navigate = useNavigate();
  const [confirmName, setConfirmName] = useState('');
  const [result, setResult] = useState<{ removeServicePrincipalUrl: string; instructions: string }>();
  const { busy, error, run } = useAction();
  const go = () => run(async () => setResult(await api.post(`/api/tenants/${tenant.id}/offboard`, { confirm: true })));
  if (result) {
    return (
      <Modal title="Tenant offboarded" onClose={() => navigate('/tenants')} footer={<button className="primary" onClick={() => navigate('/tenants')}>Fertig</button>}>
        <p>{result.instructions}</p>
        <a className="btn" href={result.removeServicePrincipalUrl} target="_blank" rel="noreferrer">Unternehmensanwendung im Entra Admin Center öffnen</a>
      </Modal>
    );
  }
  return (
    <Modal title="Tenant offboarden" onClose={onClose} footer={<><button onClick={onClose}>Abbrechen</button><button className="danger" disabled={confirmName !== tenant.displayName || busy} onClick={go}>Offboarden</button></>}>
      <Info tone="warn">Exportieren Sie die Daten vorher, wenn Sie sie aufbewahren müssen.</Info>
      <Field label={`Zur Bestätigung den Namen „${tenant.displayName}“ eingeben`}><input value={confirmName} onChange={(e) => setConfirmName(e.target.value)} /></Field>
      <ErrorBox error={error} />
    </Modal>
  );
}
