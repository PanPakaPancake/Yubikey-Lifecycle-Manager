import { useEffect, useState } from 'react';
import { Link, useNavigate, useSearchParams } from 'react-router';
import { api, fmtDate, statusLabels, usageLabels, type AgentInfo, type CustomField, type KeyStatus, type YubiKey } from '../api';
import { useCan } from '../auth';
import { Badge, download, Empty, ErrorBox, Field, formatSort, Info, LiveSearchInput, MenuButton, Modal, PageHeader, parseSort, SortTh, Spinner, StatusBadge, useAction, useApi, type SortState } from '../ui';
import { useTenant } from '../tenant';
import { Icon } from '../icons';
import { AssignmentKpis, assignmentExportItems, PeopleView, type Overview } from './Assignments';

interface KeyPage { total: number; page: number; pageSize: number; items: YubiKey[] }

/**
 * Kundenebene: Inventar des gewählten Tenants (oder aller) mit zwei Ansichten,
 * „Alle Keys“ und „Nach Person“ (wer hat welchen Key). partner: nur das Partnerlager.
 */
export default function KeysPage({ partner }: { partner?: boolean }) {
  const [params, setParams] = useSearchParams();
  const can = useCan();
  const ctx = useTenant();
  const paramTenant = params.get('tenantId');
  const { setTenantId } = ctx;
  // ?tenantId=… wählt den Tenant im Umschalter („all“ = alle Tenants) und verschwindet dann aus der Adresse.
  useEffect(() => {
    if (!paramTenant) return;
    setTenantId(paramTenant === 'all' ? '' : paramTenant);
    const next = new URLSearchParams(params);
    next.delete('tenantId');
    setParams(next, { replace: true });
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [paramTenant, setTenantId]);
  const tenantId = partner ? '' : ctx.tenantId;
  const canIntake = partner ? can('ManagePartnerStock') && can('ManageInventory') : can('ManageInventory');
  const view = !partner && params.get('view') === 'people' ? 'people' : 'keys';
  const status = params.get('status') ?? '';
  const q = params.get('q') ?? '';
  const tag = params.get('tag') ?? '';
  const needsDetails = params.get('needsDetails') === '1';
  const advisory = params.get('advisory') ?? '';
  const page = Number(params.get('page') ?? '1');
  const sort = parseSort(params.get('sort'), { key: 'updated', desc: true });
  const onSort = (s: SortState) => set('sort', s.key === 'updated' && s.desc ? '' : formatSort(s));
  const [modal, setModal] = useState<'import' | 'intake' | null>(null);
  const [selected, setSelected] = useState<Set<string>>(new Set());
  const [bulk, setBulk] = useState<BulkKind | null>(null);
  const [bulkResult, setBulkResult] = useState<BulkResult & { label: string }>();

  const set = (k: string, v: string) => {
    const next = new URLSearchParams(params);
    if (v) next.set(k, v); else next.delete(k);
    if (k !== 'page') next.delete('page');
    setParams(next);
  };

  const qs = new URLSearchParams();
  if (partner) qs.set('partner', 'true');
  else if (tenantId) qs.set('tenantId', tenantId);
  if (status) qs.set('status', status);
  if (q) qs.set('q', q);
  if (tag) qs.set('tag', tag);
  if (needsDetails) qs.set('needsDetails', 'true');
  if (advisory) qs.set('advisory', advisory);
  if (params.get('sort')) qs.set('sort', params.get('sort')!);
  qs.set('page', String(page));
  const { data, error, loading, reload } = useApi<KeyPage>(view === 'keys' ? '/api/keys?' + qs.toString() : null, [qs.toString(), view]);
  const { data: overview, error: overviewError } = useApi<Overview>(partner ? null : '/api/assignments' + (tenantId ? '?tenantId=' + tenantId : ''), [tenantId, partner]);
  // Wechsel der Ansicht startet mit leeren Filtern, weil beide Ansichten eigene Filter haben.
  const switchView = (v: 'keys' | 'people', extra: Record<string, string> = {}) => setParams(new URLSearchParams(v === 'people' ? { view: 'people', ...extra } : extra));
  const exportQs = tenantId ? '?tenantId=' + tenantId : '';
  const usage = params.get('usage') ?? '';
  const issues = params.get('issues') === '1';
  const { data: catalog } = useApi<{ id: string; title: string }[]>('/api/advisories/catalog');
  const { data: tags } = useApi<string[]>('/api/tags' + (tenantId ? '?tenantId=' + tenantId : ''), [tenantId]);
  const pages = data ? Math.max(1, Math.ceil(data.total / data.pageSize)) : 1;
  useEffect(() => { setSelected(new Set()); }, [qs.toString()]);
  const selectedKeys = data?.items.filter((k) => selected.has(k.id)) ?? [];
  const allOnPage = !!data && data.items.length > 0 && data.items.every((k) => selected.has(k.id));
  const toggle = (id: string, on: boolean) => setSelected((cur) => { const n = new Set(cur); if (on) n.add(id); else n.delete(id); return n; });
  const canBulk = can('ManageInventory') || can('RetireKeys');

  return (
    <>
      <PageHeader
        title={partner ? 'Partnerlager' : ctx.tenant ? 'Inventar ' + ctx.tenant.displayName : 'Inventar aller Tenants'}
        subtitle={partner
          ? <>Keys, die keinem Kunden zugeordnet sind{data ? ` (${data.total})` : ''}. Von hier aus weisen Sie Keys einem Tenant zu.</>
          : 'Alle Keys mit Status, und wer welchen Key gerade hat.'}
        actions={<>
          {canIntake && <button onClick={() => setModal('import')}>CSV importieren</button>}
          {!partner && can('ReadReports') && <MenuButton label="Exportieren" items={[
            { label: 'Alle Keys (CSV)', hint: 'Inventar mit Status, Tags und Zusatzfeldern', icon: 'key', onClick: () => download('/api/exports/keys.csv' + exportQs) },
            ...assignmentExportItems(tenantId, params),
          ]} />}
          {canIntake && <button className="primary" onClick={() => setModal('intake')}>Wareneingang per Einstecken</button>}
        </>}
      />
      {!partner && <ErrorBox error={overviewError} />}
      {!partner && overview && (
        <AssignmentKpis s={overview.summary} usage={view === 'people' ? usage : ''} issues={view === 'people' && issues}
          onFilter={(k, v) => switchView('people', v ? { [k]: v } : {})} />
      )}
      {!partner && (
        <div className="view-tabs" role="tablist" aria-label="Ansicht">
          <button type="button" role="tab" aria-selected={view === 'keys'} className={view === 'keys' ? 'on' : ''} onClick={() => view !== 'keys' && switchView('keys')}>
            <Icon name="key" size={16} />Alle Keys{data && view === 'keys' && <span className="count">{data.total}</span>}
          </button>
          <button type="button" role="tab" aria-selected={view === 'people'} className={view === 'people' ? 'on' : ''} onClick={() => view !== 'people' && switchView('people')}>
            <Icon name="users" size={16} />Nach Person{overview && <span className="count">{overview.summary.people}</span>}
          </button>
        </div>
      )}
      {view === 'people' ? (overview ? <PeopleView data={overview} allTenants={!tenantId} /> : !overviewError && <Spinner label="Wird geladen …" />) : <>
      <div className="toolbar">
        <select value={status} onChange={(e) => set('status', e.target.value)} aria-label="Status">
          <option value="">Alle Status</option>
          {(Object.keys(statusLabels) as KeyStatus[]).map((s) => <option key={s} value={s}>{statusLabels[s]}</option>)}
        </select>
        <select value={tag} onChange={(e) => set('tag', e.target.value)} aria-label="Tag">
          <option value="">Alle Tags</option>
          {tags?.map((t) => <option key={t} value={t}>{t}</option>)}
        </select>
        <select value={advisory} onChange={(e) => set('advisory', e.target.value)} aria-label="Sicherheitshinweis" className={advisory ? 'filter-on' : undefined}>
          <option value="">Alle Sicherheitsstände</option>
          <option value="fido">Von Sicherheitshinweis betroffen (Passkeys)</option>
          <option value="any">Irgendein Sicherheitshinweis</option>
          {catalog?.map((a) => <option key={a.id} value={a.id}>{a.id}: {a.title}</option>)}
        </select>
        <LiveSearchInput value={q} onSearch={(v) => set('q', v)} placeholder="Seriennummer, Person, Modell …" busy={loading} />
        <label className="check"><input type="checkbox" checked={needsDetails} onChange={(e) => set('needsDetails', e.target.checked ? '1' : '')} /> Nur ohne Seriennummer</label>
      </div>
      <ErrorBox error={error} />
      {selectedKeys.length > 0 && (
        <BulkBar keys={selectedKeys} onAction={setBulk} onClear={() => setSelected(new Set())} />
      )}
      {bulkResult && (
        <Info tone={bulkResult.failed.length ? 'warn' : 'ok'}>
          {bulkResult.label}: {bulkResult.done.length} erledigt{bulkResult.failed.length > 0 && <>, {bulkResult.failed.length} nicht möglich:
            <ul style={{ margin: '4px 0 0' }}>{bulkResult.failed.map((f) => <li key={f.id}><span className="mono">{f.serialNumber ?? 'ohne Seriennummer'}</span>: {f.error}</li>)}</ul></>}
          <button className="link" style={{ marginLeft: 8 }} onClick={() => setBulkResult(undefined)}>Ausblenden</button>
        </Info>
      )}
      {data && data.items.length === 0 && <Empty>{q || status || tag || needsDetails || advisory ? <>Keine Keys passen zu diesen Filtern. <button className="link" onClick={() => setParams(new URLSearchParams())}>Filter zurücksetzen</button></> : canIntake ? <>Noch keine Keys. Erfassen Sie Keys per <button className="link" onClick={() => setModal('intake')}>Wareneingang</button> oder <button className="link" onClick={() => setModal('import')}>CSV-Import</button>.</> : 'Noch keine Keys.'}</Empty>}
      {data && data.items.length > 0 && (
        <div className="card table-wrap" style={{ padding: 0 }}>
          <table>
            <thead>
              <tr>{canBulk && <th style={{ width: 32 }}><input type="checkbox" aria-label="Alle auf dieser Seite auswählen" checked={allOnPage}
                onChange={(e) => setSelected(e.target.checked ? new Set(data.items.map((k) => k.id)) : new Set())} /></th>}<SortTh label="Seriennummer" sortKey="serial" sort={sort} onSort={onSort} /><SortTh label="Modell" sortKey="model" sort={sort} onSort={onSort} /><SortTh label="Firmware" sortKey="firmware" sort={sort} onSort={onSort} /><SortTh label="Status" sortKey="status" sort={sort} onSort={onSort} /><th>Person</th>{!partner && <th>Tenant</th>}<th>Tags</th><SortTh label="Geändert" sortKey="updated" sort={sort} onSort={onSort} /></tr>
            </thead>
            <tbody>
              {data.items.map((k) => {
                const reg = k.registrations.filter((r) => !r.removedAt);
                return (
                  <tr key={k.id} className={selected.has(k.id) ? 'selected-row' : undefined}>
                    {canBulk && <td><input type="checkbox" aria-label={'Auswählen ' + (k.serialNumber ?? '')} checked={selected.has(k.id)} onChange={(e) => toggle(k.id, e.target.checked)} /></td>}
                    <td><Link to={'/keys/' + k.id} className="mono">{k.serialNumber ?? '— nicht erfasst —'}</Link>
                      {/* Nacherfassen nur dort, wo es gebraucht wird: selbst registrierter Key ohne Seriennummer und Firmware. */}
                      {k.serialNumber == null && canIntake && !partner && k.status !== 'Retired' && (
                        <Link className="btn small identify-link" to={`/keys/identify?keyId=${k.id}`} title="Vom Nutzer selbst registriert, Seriennummer und Firmware fehlen. Key einstecken und nacherfassen.">
                          <Icon name="scan" size={13} />Nacherfassen
                        </Link>
                      )}</td>
                    <td>{k.model ?? '–'}</td>
                    <td className="mono">{k.firmwareVersion ?? '–'} {k.advisories.some((a) => a.affectsFido) && <Link to={'/sicherheitshinweise#' + k.advisories.find((a) => a.affectsFido)!.id} title={k.advisories.map((a) => a.id + ': ' + a.title).join('\n')}><Badge tone="warn">Hinweis</Badge></Link>}</td>
                    <td><StatusBadge status={k.status} /></td>
                    <td>{k.usage !== 'Personal' && <div><Badge tone={k.usage === 'Breakglass' ? 'error' : 'info'}>{usageLabels[k.usage]}</Badge> <span className="small muted">{k.custodian ? 'bei ' + k.custodian : 'Verwahrer fehlt'}</span></div>}
                      {reg.length === 0 ? (k.usage === 'Personal' ? '–' : null) : reg.map((r) => <div key={r.id}>{r.userDisplayName ?? r.userPrincipalName}<div className="small muted">{r.userPrincipalName}</div></div>)}</td>
                    {!partner && <td>{k.inPartnerStock ? <Badge tone="warn">Partnerlager</Badge> : k.tenantName}</td>}
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
      </>}
      {bulk && <BulkModal kind={bulk} keys={selectedKeys} onClose={() => setBulk(null)}
        onDone={(res, label) => { setBulk(null); setBulkResult({ ...res, label }); setSelected(new Set(res.failed.map((f) => f.id))); reload(); }} />}
      {modal === 'import' && <ImportModal defaultTarget={partner ? 'partner' : tenantId} onClose={() => setModal(null)} onDone={reload} />}
      {modal === 'intake' && <IntakeModal defaultTarget={partner ? 'partner' : tenantId} onClose={() => setModal(null)} onDone={reload} />}
    </>
  );
}

/** Ziel des Wareneingangs: Tenant-ID oder 'partner' (Partnerlager). */
function TargetSelect({ value, onChange }: { value: string; onChange: (v: string) => void }) {
  const can = useCan();
  const { tenants } = useTenant();
  return (
    <select value={value} onChange={(e) => onChange(e.target.value)} aria-label="Ziel">
      {!value && <option value="">Ziel wählen …</option>}
      {can('ManagePartnerStock') && <option value="partner">Partnerlager (keinem Kunden zugeordnet)</option>}
      {tenants.filter((t) => t.status === 'Active').map((t) => <option key={t.id} value={t.id}>Lager von {t.displayName}</option>)}
    </select>
  );
}

const targetUrl = (target: string, action: string) => target === 'partner' ? `/api/partner/keys/${action}` : `/api/tenants/${target}/keys/${action}`;

function ImportModal({ defaultTarget, onClose, onDone }: { defaultTarget: string; onClose: () => void; onDone: () => void }) {
  const [tenantId, setTenantId] = useState(defaultTarget);
  const [file, setFile] = useState<File>();
  const [result, setResult] = useState<{ created: number; skipped: number; errors: string[] }>();
  const { busy, error, run } = useAction();
  const go = () => run(async () => {
    const text = await file!.text();
    setResult(await api.post(targetUrl(tenantId, 'import'), text, { 'Content-Type': 'text/csv' }));
    onDone();
  });
  return (
    <Modal title="Keys aus CSV importieren" onClose={onClose} footer={<>
      <button onClick={onClose}>Schließen</button>
      <button className="primary" disabled={!tenantId || !file || busy} onClick={go}>Importieren</button>
    </>}>
      <p className="muted">Spalten mit Semikolon getrennt, Kodierung UTF-8: <code>Seriennummer</code> (Pflicht), <code>Modell</code>, <code>Firmware</code>, <code>Tags</code> (mit Komma getrennt), <code>Notiz</code> sowie Zusatzfelder (Schlüssel oder Bezeichnung als Spaltenkopf). Importierte Keys landen im Lager.</p>
      <Field label="Ziel"><TargetSelect value={tenantId} onChange={setTenantId} /></Field>
      <p className="muted">
        <button type="button" className="link" disabled={!tenantId} onClick={() => download(targetUrl(tenantId, 'import/sample.csv'))}>Beispiel-CSV herunterladen</button>
        {' '}· enthält alle Spalten inklusive der Zusatzfelder des gewählten Ziels und drei Beispielzeilen. Die Beispielzeilen vor dem Import durch echte Keys ersetzen.
      </p>
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

function IntakeModal({ defaultTarget, onClose, onDone }: { defaultTarget: string; onClose: () => void; onDone: () => void }) {
  const navigate = useNavigate();
  const [tenantId, setTenantId] = useState(defaultTarget);
  const { data: agents, reload } = useApi<AgentInfo[]>('/api/agents');
  // Partnerlager: nur die globalen Felder.
  const { data: fields } = useApi<CustomField[]>(tenantId === 'partner' ? '/api/fields' : tenantId ? `/api/tenants/${tenantId}/fields` : null, [tenantId]);
  const online = agents?.filter((a) => a.online) ?? [];
  const [agentId, setAgentId] = useState('');
  const agent = online.find((a) => a.id === agentId) ?? online[0];
  const [tags, setTags] = useState('');
  const [notes, setNotes] = useState('');
  const [values, setValues] = useState<Record<string, string>>({});
  const [added, setAdded] = useState<YubiKey[]>([]);
  const { busy, error, run } = useAction();

  const go = () => run(async () => {
    const key = await api.post<YubiKey>(targetUrl(tenantId, 'intake'), {
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
      <Field label="Ziel"><TargetSelect value={tenantId} onChange={setTenantId} /></Field>
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

type BulkKind = 'Update' | 'Assign' | 'ToPartnerStock' | 'Retire' | 'Delete';
type BulkResult = { done: string[]; failed: { id: string; serialNumber?: number | null; error: string }[] };

function BulkBar({ keys, onAction, onClear }: { keys: YubiKey[]; onAction: (k: BulkKind) => void; onClear: () => void }) {
  const can = useCan();
  const allPartner = keys.every((k) => k.inPartnerStock);
  const anyTenant = keys.some((k) => !k.inPartnerStock);
  return (
    <div className="bulk-bar">
      <strong>{keys.length} ausgewählt</strong>
      {can('ManageInventory') && <button onClick={() => onAction('Update')}>Bearbeiten …</button>}
      {can('ManagePartnerStock') && allPartner && <button onClick={() => onAction('Assign')}>Einem Tenant zuweisen …</button>}
      {can('ManagePartnerStock') && anyTenant && <button onClick={() => onAction('ToPartnerStock')}>Ins Partnerlager …</button>}
      {can('RetireKeys') && <button onClick={() => onAction('Retire')}>Ausmustern …</button>}
      {can('ManageInventory') && <button className="danger" onClick={() => onAction('Delete')}>Löschen …</button>}
      <button className="link" onClick={onClear}>Auswahl aufheben</button>
    </div>
  );
}

const bulkTitles: Record<BulkKind, string> = {
  Update: 'Keys bearbeiten', Assign: 'Einem Tenant zuweisen', ToPartnerStock: 'Ins Partnerlager legen', Retire: 'Keys ausmustern', Delete: 'Keys löschen',
};

function BulkModal({ kind, keys, onClose, onDone }: { kind: BulkKind; keys: YubiKey[]; onClose: () => void; onDone: (r: BulkResult, label: string) => void }) {
  const { tenants } = useTenant();
  const [tenantId, setTenantId] = useState('');
  const [addTags, setAddTags] = useState('');
  const [removeTags, setRemoveTags] = useState<string[]>([]);
  const [note, setNote] = useState('');
  const [toPartner, setToPartner] = useState(false);
  const [values, setValues] = useState<Record<string, string>>({});
  const { busy, error, run } = useAction();
  const scopes = [...new Set(keys.map((k) => k.tenantId ?? 'partner'))];
  const scope = scopes.length === 1 ? scopes[0] : null;
  const { data: fields } = useApi<CustomField[]>(kind !== 'Update' || !scope ? null : scope === 'partner' ? '/api/fields' : `/api/tenants/${scope}/fields`, [kind, scope]);
  const existingTags = [...new Set(keys.flatMap((k) => k.tags))].sort();
  const go = () => run(async () => {
    const body: Record<string, unknown> = { action: kind, ids: keys.map((k) => k.id) };
    if (kind === 'Update') {
      body.addTags = addTags.split(',').map((t) => t.trim()).filter(Boolean);
      body.removeTags = removeTags;
      body.customFields = Object.fromEntries(Object.entries(values).filter(([, v]) => v !== ''));
    }
    if (kind === 'Assign') body.tenantId = tenantId;
    if (kind === 'Retire') { body.note = note || null; body.toPartnerStock = toPartner; }
    onDone(await api.post<BulkResult>('/api/keys/bulk', body), bulkTitles[kind]);
  });
  const serials = keys.slice(0, 8).map((k) => k.serialNumber ?? '–').join(', ') + (keys.length > 8 ? ` und ${keys.length - 8} weitere` : '');
  return (
    <Modal title={`${bulkTitles[kind]} (${keys.length})`} onClose={onClose} footer={<>
      <button onClick={onClose}>Abbrechen</button>
      <button className={kind === 'Delete' ? 'danger' : 'primary'} disabled={busy || (kind === 'Assign' && !tenantId)} onClick={go}>
        {kind === 'Delete' ? `${keys.length} Keys löschen` : 'Ausführen'}
      </button>
    </>}>
      <p className="small muted">Seriennummern: <span className="mono">{serials}</span></p>
      {kind === 'Update' && <>
        <Field label="Tags hinzufügen" hint="Mehrere mit Komma trennen"><input value={addTags} onChange={(e) => setAddTags(e.target.value)} placeholder="z. B. Charge 2026, Ersatz" /></Field>
        {existingTags.length > 0 && <Field label="Tags entfernen">
          <div>{existingTags.map((t) => (
            <label key={t} className="check" style={{ display: 'inline-flex', marginRight: 12, whiteSpace: 'nowrap' }}>
              <input type="checkbox" checked={removeTags.includes(t)} onChange={(e) => setRemoveTags((cur) => e.target.checked ? [...cur, t] : cur.filter((x) => x !== t))} /> {t}
            </label>))}</div>
        </Field>}
        {scope && fields && fields.length > 0 && <>
          <div className="perm-group">Zusatzfelder (leer = unverändert)</div>
          {fields.map((f) => <CustomFieldInput key={f.key} field={{ ...f, required: false }} value={values[f.key] ?? ''} onChange={(v) => setValues((cur) => ({ ...cur, [f.key]: v }))} />)}
        </>}
        {!scope && <Info>Die Auswahl umfasst Keys aus mehreren Tenants. Zusatzfelder lassen sich nur gemeinsam ändern, wenn alle Keys aus demselben Tenant stammen.</Info>}
      </>}
      {kind === 'Assign' && <Field label="Tenant">
        <select value={tenantId} onChange={(e) => setTenantId(e.target.value)}>
          <option value="">Tenant wählen …</option>
          {tenants.filter((t) => t.status === 'Active').map((t) => <option key={t.id} value={t.id}>{t.displayName}</option>)}
        </select>
      </Field>}
      {kind === 'Assign' && <p className="muted small">Die Keys liegen danach im Lager des Tenants bereit. Nur Keys mit Status „Im Lager“ werden zugewiesen.</p>}
      {kind === 'ToPartnerStock' && <p>Die Keys werden vom Kunden-Tenant getrennt und ins Partnerlager gelegt. Das geht nur für Keys im Lager oder ausgemusterte Keys ohne aktive Registrierung; die übrigen werden übersprungen.</p>}
      {kind === 'Retire' && <>
        <Field label="Notiz"><input value={note} onChange={(e) => setNote(e.target.value)} placeholder="z. B. defekt, verloren" /></Field>
        <label className="check"><input type="checkbox" checked={toPartner} onChange={(e) => setToPartner(e.target.checked)} /> Vom Kunden trennen und ins Partnerlager legen</label>
      </>}
      {kind === 'Delete' && <Info tone="warn">Gelöschte Keys verschwinden aus dem Inventar. Der Verlauf bleibt im Audit-Log erhalten. Nur Keys im Lager oder ausgemusterte Keys ohne aktive Registrierung werden gelöscht; aktive Keys bitte vorher sperren oder zurücksetzen.</Info>}
      <ErrorBox error={error} />
    </Modal>
  );
}
