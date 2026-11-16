import { useState } from 'react';
import { Link, useNavigate, useSearchParams } from 'react-router';
import { api, fmtDate, statusLabels, type AgentInfo, type CustomField, type KeyStatus, type YubiKey } from '../api';
import { useCan } from '../auth';
import { Badge, Empty, ErrorBox, Field, Info, Modal, PageHeader, Spinner, StatusBadge, TenantSelect, useAction, useApi } from '../ui';

interface KeyPage { total: number; page: number; pageSize: number; items: YubiKey[] }

export default function KeysPage() {
  const [params, setParams] = useSearchParams();
  const can = useCan();
  const tenantId = params.get('tenantId') ?? '';
  const status = params.get('status') ?? '';
  const q = params.get('q') ?? '';
  const tag = params.get('tag') ?? '';
  const needsDetails = params.get('needsDetails') === '1';
  const page = Number(params.get('page') ?? '1');
  const [search, setSearch] = useState(q);
  const [modal, setModal] = useState<'import' | 'intake' | null>(null);

  const set = (k: string, v: string) => {
    const next = new URLSearchParams(params);
    if (v) next.set(k, v); else next.delete(k);
    if (k !== 'page') next.delete('page');
    setParams(next);
  };

  const qs = new URLSearchParams();
  if (tenantId) qs.set('tenantId', tenantId);
  if (status) qs.set('status', status);
  if (q) qs.set('q', q);
  if (tag) qs.set('tag', tag);
  if (needsDetails) qs.set('needsDetails', 'true');
  qs.set('page', String(page));
  const { data, error, loading, reload } = useApi<KeyPage>('/api/keys?' + qs.toString(), [qs.toString()]);
  const { data: tags } = useApi<string[]>('/api/tags' + (tenantId ? '?tenantId=' + tenantId : ''), [tenantId]);
  const pages = data ? Math.max(1, Math.ceil(data.total / data.pageSize)) : 1;

  return (
    <>
      <PageHeader
        title="Inventar"
        subtitle={data ? `${data.total} Keys` : undefined}
        actions={can('ManageInventory') && <>
          <button onClick={() => setModal('import')}>CSV importieren</button>
          <button className="primary" onClick={() => setModal('intake')}>Wareneingang per Einstecken</button>
        </>}
      />
      <div className="toolbar">
        <TenantSelect value={tenantId} onChange={(v) => set('tenantId', v)} />
        <select value={status} onChange={(e) => set('status', e.target.value)} aria-label="Status">
          <option value="">Alle Status</option>
          {(Object.keys(statusLabels) as KeyStatus[]).map((s) => <option key={s} value={s}>{statusLabels[s]}</option>)}
        </select>
        <select value={tag} onChange={(e) => set('tag', e.target.value)} aria-label="Tag">
          <option value="">Alle Tags</option>
          {tags?.map((t) => <option key={t} value={t}>{t}</option>)}
        </select>
        <form onSubmit={(e) => { e.preventDefault(); set('q', search.trim()); }}>
          <input type="search" placeholder="Seriennummer, Person, Modell …" value={search} onChange={(e) => setSearch(e.target.value)} />
        </form>
        <label className="check"><input type="checkbox" checked={needsDetails} onChange={(e) => set('needsDetails', e.target.checked ? '1' : '')} /> Nur ohne Seriennummer</label>
        {loading && <Spinner />}
      </div>
      <ErrorBox error={error} />
      {data && data.items.length === 0 && <Empty>Keine Keys gefunden.</Empty>}
      {data && data.items.length > 0 && (
        <div className="card table-wrap" style={{ padding: 0 }}>
          <table>
            <thead>
              <tr><th>Seriennummer</th><th>Modell</th><th>Firmware</th><th>Status</th><th>Person</th><th>Tenant</th><th>Tags</th><th>Geändert</th></tr>
            </thead>
            <tbody>
              {data.items.map((k) => {
                const reg = k.registrations.filter((r) => !r.removedAt);
                return (
                  <tr key={k.id}>
                    <td><Link to={'/keys/' + k.id} className="mono">{k.serialNumber ?? '— nicht erfasst —'}</Link></td>
                    <td>{k.model ?? '–'}</td>
                    <td className="mono">{k.firmwareVersion ?? '–'} {k.advisories.some((a) => a.affectsFido) && <Badge tone="warn">Hinweis</Badge>}</td>
                    <td><StatusBadge status={k.status} /></td>
                    <td>{reg.length === 0 ? '–' : reg.map((r) => <div key={r.id}>{r.userDisplayName ?? r.userPrincipalName}<div className="small muted">{r.userPrincipalName}</div></div>)}</td>
                    <td>{k.tenantName}</td>
                    <td>{k.tags.map((t) => <span className="tag" key={t}>{t}</span>)}</td>
                    <td className="small muted">{fmtDate(k.updatedAt)}</td>
                  </tr>
                );
              })}
            </tbody>
          </table>
        </div>
      )}
      {pages > 1 && (
        <div className="toolbar" style={{ justifyContent: 'center', marginTop: 12 }}>
          <button disabled={page <= 1} onClick={() => set('page', String(page - 1))}>Zurück</button>
          <span>Seite {page} von {pages}</span>
          <button disabled={page >= pages} onClick={() => set('page', String(page + 1))}>Weiter</button>
        </div>
      )}
      {modal === 'import' && <ImportModal defaultTenant={tenantId} onClose={() => setModal(null)} onDone={reload} />}
      {modal === 'intake' && <IntakeModal defaultTenant={tenantId} onClose={() => setModal(null)} onDone={reload} />}
    </>
  );
}

function ImportModal({ defaultTenant, onClose, onDone }: { defaultTenant: string; onClose: () => void; onDone: () => void }) {
  const [tenantId, setTenantId] = useState(defaultTenant);
  const [file, setFile] = useState<File>();
  const [result, setResult] = useState<{ created: number; skipped: number; errors: string[] }>();
  const { busy, error, run } = useAction();
  const go = () => run(async () => {
    const text = await file!.text();
    setResult(await api.post(`/api/tenants/${tenantId}/keys/import`, text, { 'Content-Type': 'text/csv' }));
    onDone();
  });
  return (
    <Modal title="Keys aus CSV importieren" onClose={onClose} footer={<>
      <button onClick={onClose}>Schließen</button>
      <button className="primary" disabled={!tenantId || !file || busy} onClick={go}>Importieren</button>
    </>}>
      <p className="muted">Spalten mit Semikolon getrennt: <code>Seriennummer</code> (Pflicht), <code>Modell</code>, <code>Firmware</code>, <code>Tags</code> (mit Komma getrennt), <code>Notiz</code> sowie Zusatzfelder (Schlüssel oder Bezeichnung als Spaltenkopf). Importierte Keys landen im Lager.</p>
      <Field label="Tenant"><TenantSelect value={tenantId} onChange={setTenantId} allowAll={false} /></Field>
      <Field label="Datei"><input type="file" accept=".csv,text/csv" onChange={(e) => setFile(e.target.files?.[0])} /></Field>
      <ErrorBox error={error} />
      {result && (
        <Info tone={result.errors.length ? 'warn' : 'ok'}>
          {result.created} angelegt, {result.skipped} bereits vorhanden übersprungen.
          {result.errors.length > 0 && <ul>{result.errors.slice(0, 20).map((e) => <li key={e}>{e}</li>)}</ul>}
        </Info>
      )}
    </Modal>
  );
}

function IntakeModal({ defaultTenant, onClose, onDone }: { defaultTenant: string; onClose: () => void; onDone: () => void }) {
  const navigate = useNavigate();
  const [tenantId, setTenantId] = useState(defaultTenant);
  const { data: agents, reload } = useApi<AgentInfo[]>('/api/agents');
  const { data: fields } = useApi<CustomField[]>(tenantId ? `/api/tenants/${tenantId}/fields` : null, [tenantId]);
  const online = agents?.filter((a) => a.online) ?? [];
  const [agentId, setAgentId] = useState('');
  const agent = online.find((a) => a.id === agentId) ?? online[0];
  const [tags, setTags] = useState('');
  const [notes, setNotes] = useState('');
  const [values, setValues] = useState<Record<string, string>>({});
  const [added, setAdded] = useState<YubiKey[]>([]);
  const { busy, error, run } = useAction();

  const go = () => run(async () => {
    const key = await api.post<YubiKey>(`/api/tenants/${tenantId}/keys/intake`, {
      agentId: agent!.id, tags: tags.split(',').map((t) => t.trim()).filter(Boolean), notes: notes || null,
      customFields: Object.fromEntries(Object.entries(values).map(([k, v]) => [k, v || null])),
    });
    setAdded((a) => [key, ...a]);
    onDone();
  });

  return (
    <Modal title="Wareneingang per Einstecken" onClose={onClose} footer={<>
      <button onClick={onClose}>Fertig</button>
      <button className="primary" disabled={!tenantId || !agent || busy} onClick={go}>{busy ? <Spinner label="Key wird gelesen …" /> : 'Eingesteckten Key erfassen'}</button>
    </>}>
      <p className="muted">Stecken Sie neue Keys nacheinander am Admin-PC ein. Seriennummer, Modell und Firmware liest der Agent aus; der Key wird nicht verändert.</p>
      <Field label="Tenant"><TenantSelect value={tenantId} onChange={setTenantId} allowAll={false} /></Field>
      {online.length === 0 ? (
        <Info tone="warn">Kein Agent online. <button className="link" onClick={reload}>Erneut prüfen</button> · <a href="/agents">Agent koppeln</a></Info>
      ) : (
        <Field label="Agent">
          <select value={agent?.id} onChange={(e) => setAgentId(e.target.value)}>
            {online.map((a) => <option key={a.id} value={a.id}>{a.machineName}{a.key ? ` – Key ${a.key.serialNumber ?? ''} eingesteckt` : ' – kein Key'}</option>)}
          </select>
        </Field>
      )}
      <div className="grid cols-2">
        <Field label="Tags" hint="Mit Komma getrennt; gelten für alle folgenden Keys."><input value={tags} onChange={(e) => setTags(e.target.value)} /></Field>
        <Field label="Notiz"><input value={notes} onChange={(e) => setNotes(e.target.value)} /></Field>
        {fields?.map((f) => (
          <Field key={f.key} label={f.label + (f.required ? ' *' : '')}>
            <CustomFieldInput field={f} value={values[f.key] ?? ''} onChange={(v) => setValues((s) => ({ ...s, [f.key]: v }))} />
          </Field>
        ))}
      </div>
      <ErrorBox error={error} />
      {added.length > 0 && (
        <Info tone="ok">
          Erfasst: {added.map((k) => <button key={k.id} className="link" style={{ marginRight: 8 }} onClick={() => navigate('/keys/' + k.id)}>{k.serialNumber}</button>)}
        </Info>
      )}
    </Modal>
  );
}

export function CustomFieldInput({ field, value, onChange }: { field: CustomField; value: string; onChange: (v: string) => void }) {
  switch (field.type) {
    case 'Number': return <input type="number" value={value} onChange={(e) => onChange(e.target.value)} />;
    case 'Date': return <input type="date" value={value} onChange={(e) => onChange(e.target.value)} />;
    case 'Boolean': return (
      <select value={value} onChange={(e) => onChange(e.target.value)}>
        <option value="">–</option><option value="true">Ja</option><option value="false">Nein</option>
      </select>
    );
    case 'Select': return (
      <select value={value} onChange={(e) => onChange(e.target.value)}>
        <option value="">–</option>
        {field.options.map((o) => <option key={o} value={o}>{o}</option>)}
      </select>
    );
    default: return <input value={value} onChange={(e) => onChange(e.target.value)} />;
  }
}
