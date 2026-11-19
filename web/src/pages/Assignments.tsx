import { useMemo, type ReactNode } from 'react';
import { Link, useSearchParams } from 'react-router';
import { fmtDate, usageLabels, type KeyUsage } from '../api';
import { useCan } from '../auth';
import { Badge, download, Empty, ErrorBox, formatSort, LiveSearchInput, PageHeader, parseSort, SortTh, Spinner, useApi, type SortState } from '../ui';
import { Icon, type IconName } from '../icons';
import { useTenant } from '../tenant';

export interface AssignmentAccount { entraUserId: string; userPrincipalName: string; userDisplayName?: string | null; registeredAt: string; registeredByName?: string | null; source: string }
export interface AssignmentKey {
  keyId: string; tenantId: string; tenantName: string; serialNumber?: number | null; model?: string | null; firmwareVersion?: string | null;
  status: string; usage: KeyUsage; holder?: string | null; holderUpn?: string | null; custodian?: string | null;
  accounts: AssignmentAccount[]; since?: string | null; pinChangeForced?: boolean | null; advisories: string[]; fidoAdvisory: boolean; issues: string[];
}
interface Summary { keys: number; people: number; personal: number; shared: number; breakglass: number; peopleWithSeveralKeys: number; withoutSerial: number; withAdvisory: number; withIssues: number }
interface Overview { generatedAt: string; summary: Summary; keys: AssignmentKey[] }

/** Eine Zeile der Personenansicht: eine Person mit einem Key (Keys ohne Konto erscheinen einmal ohne Person). */
interface PersonRow { id: string; key: AssignmentKey; account?: AssignmentAccount; keyCount: number }

const collator = new Intl.Collator('de', { sensitivity: 'base', numeric: true });

/** Gleiche Suche wie im Backend (AssignmentService.Matches), damit Ansicht und Export übereinstimmen. */
function matches(k: AssignmentKey, terms: string[]) {
  return terms.every((term) => {
    const has = (v?: string | number | null) => v != null && String(v).toLocaleLowerCase('de').includes(term);
    return has(k.serialNumber) || has(k.holder) || has(k.custodian) || has(k.model) || has(k.tenantName)
      || k.accounts.some((a) => has(a.userPrincipalName) || has(a.userDisplayName));
  });
}

/** Wer hat gerade welchen Key? Für den gewählten Tenant oder (ohne Auswahl) über alle Tenants. */
export default function AssignmentsPage() {
  const ctx = useTenant();
  const can = useCan();
  const [params, setParams] = useSearchParams();
  const tenantId = ctx.tenantId;
  const q = params.get('q') ?? '';
  const usage = (params.get('usage') ?? '') as KeyUsage | '';
  const issues = params.get('issues') === '1';
  const view = params.get('view') === 'keys' ? 'keys' : 'people';
  const sort = parseSort(params.get('sort'), { key: 'person', desc: false });
  const set = (k: string, v: string) => {
    const next = new URLSearchParams(params);
    if (v) next.set(k, v); else next.delete(k);
    setParams(next, { replace: k === 'q' });
  };
  const onSort = (s: SortState) => set('sort', s.key === 'person' && !s.desc ? '' : formatSort(s));

  const { data, error, loading } = useApi<Overview>('/api/assignments' + (tenantId ? '?tenantId=' + tenantId : ''), [tenantId]);
  const allTenants = !tenantId;

  const filtered = useMemo(() => {
    if (!data) return [];
    const terms = q.trim().toLocaleLowerCase('de').split(/\s+/).filter(Boolean);
    return data.keys.filter((k) => (!usage || k.usage === usage) && (!issues || k.issues.length > 0) && (terms.length === 0 || matches(k, terms)));
  }, [data, q, usage, issues]);

  const people = useMemo(() => {
    const perPerson = new Map<string, number>();
    for (const k of data?.keys ?? []) for (const a of k.accounts) perPerson.set(k.tenantId + '|' + a.entraUserId, (perPerson.get(k.tenantId + '|' + a.entraUserId) ?? 0) + 1);
    const rows: PersonRow[] = [];
    for (const k of filtered) {
      if (k.accounts.length === 0) rows.push({ id: k.keyId, key: k, keyCount: 0 });
      for (const a of k.accounts) rows.push({ id: k.keyId + a.entraUserId, key: k, account: a, keyCount: perPerson.get(k.tenantId + '|' + a.entraUserId) ?? 1 });
    }
    const dir = sort.desc ? -1 : 1;
    const name = (r: PersonRow) => r.account ? (r.account.userDisplayName ?? r.account.userPrincipalName) : '￿';
    const by: Record<string, (a: PersonRow, b: PersonRow) => number> = {
      person: (a, b) => collator.compare(name(a), name(b)),
      serial: (a, b) => (a.key.serialNumber ?? Infinity) - (b.key.serialNumber ?? Infinity),
      since: (a, b) => (a.account?.registeredAt ?? '').localeCompare(b.account?.registeredAt ?? ''),
      tenant: (a, b) => collator.compare(a.key.tenantName, b.key.tenantName) || collator.compare(name(a), name(b)),
      usage: (a, b) => a.key.usage.localeCompare(b.key.usage) || collator.compare(name(a), name(b)),
    };
    return rows.sort((a, b) => dir * (by[sort.key] ?? by.person)(a, b));
  }, [filtered, data, sort.key, sort.desc]);

  const keys = useMemo(() => {
    const dir = sort.desc ? -1 : 1;
    const by: Record<string, (a: AssignmentKey, b: AssignmentKey) => number> = {
      person: (a, b) => collator.compare(a.holder ?? '￿', b.holder ?? '￿'),
      serial: (a, b) => (a.serialNumber ?? Infinity) - (b.serialNumber ?? Infinity),
      since: (a, b) => (a.since ?? '').localeCompare(b.since ?? ''),
      tenant: (a, b) => collator.compare(a.tenantName, b.tenantName),
      usage: (a, b) => a.usage.localeCompare(b.usage),
    };
    return [...filtered].sort((a, b) => dir * (by[sort.key] ?? by.person)(a, b));
  }, [filtered, sort.key, sort.desc]);

  const exportQs = new URLSearchParams();
  if (tenantId) exportQs.set('tenantId', tenantId);
  if (q) exportQs.set('q', q);
  if (usage) exportQs.set('usage', usage);
  if (issues) exportQs.set('issues', 'true');
  const exp = (format: 'csv' | 'xlsx' | 'pdf') => download(`/api/exports/assignments.${format}?` + exportQs.toString());
  const filteredHint = q || usage || issues;
  const s = data?.summary;

  return (
    <>
      <PageHeader
        title="Wer hat welchen Key?"
        subtitle={<>{ctx.tenant ? ctx.tenant.displayName : 'Alle Tenants'}{data && <> · Stand {fmtDate(data.generatedAt)}</>}</>}
        actions={can('ReadReports') && (
          <div className="btn-group" role="group" aria-label="Exportieren">
            <span className="btn-group-label"><Icon name="download" size={15} />Export{filteredHint ? ' (gefiltert)' : ''}</span>
            <button onClick={() => exp('xlsx')}>Excel</button>
            <button onClick={() => exp('csv')}>CSV</button>
            <button onClick={() => exp('pdf')}>PDF</button>
          </div>
        )}
      />
      <ErrorBox error={error} />
      {!data && loading && <Spinner label="Wird geladen …" />}
      {s && (
        <>
          <div className="assign-kpis">
            <KpiTile icon="key" label="Ausgegebene Keys" value={s.keys} />
            <KpiTile icon="users" label="Personen mit Key" value={s.people} sub={s.peopleWithSeveralKeys ? `${s.peopleWithSeveralKeys} mit mehreren Keys` : 'jede Person ein Key'} />
            <KpiTile icon="share" label="Geteilt / Breakglass" value={`${s.shared} / ${s.breakglass}`} sub="beim Verwahrer"
              active={usage === 'Shared'} onClick={s.shared ? () => set('usage', usage === 'Shared' ? '' : 'Shared') : undefined} />
            <KpiTile icon="alert" label="Auffälligkeiten" value={s.withIssues} tone={s.withIssues ? 'warn' : 'ok'}
              sub={s.withIssues ? 'z. B. Verwahrer fehlt' : 'alles stimmig'} active={issues} onClick={s.withIssues ? () => set('issues', issues ? '' : '1') : undefined} />
          </div>
          {s.keys > 0 && <UsageBar s={s} />}
        </>
      )}
      {data && (
        <>
          <div className="toolbar">
            <LiveSearchInput value={q} onSearch={(v) => set('q', v)} placeholder="Person, UPN, Seriennummer, Verwahrer …" />
            <div className="segmented" role="radiogroup" aria-label="Nutzung">
              {(['', 'Personal', 'Shared', 'Breakglass'] as const).map((u) => (
                <button key={u || 'all'} type="button" role="radio" aria-checked={usage === u} className={usage === u ? 'on' : ''} onClick={() => set('usage', u)}>
                  {u ? usageLabels[u] : 'Alle'}
                </button>
              ))}
            </div>
            <label className="check"><input type="checkbox" checked={issues} onChange={(e) => set('issues', e.target.checked ? '1' : '')} /> Nur Auffälligkeiten</label>
            <div className="segmented push-right" role="radiogroup" aria-label="Ansicht">
              <button type="button" role="radio" aria-checked={view === 'people'} className={view === 'people' ? 'on' : ''} onClick={() => set('view', '')}><Icon name="user" size={15} />Nach Person</button>
              <button type="button" role="radio" aria-checked={view === 'keys'} className={view === 'keys' ? 'on' : ''} onClick={() => set('view', 'keys')}><Icon name="key" size={15} />Nach Key</button>
            </div>
          </div>

          {filtered.length === 0 ? (
            <Empty>
              {data.keys.length === 0
                ? <>Noch keine ausgegebenen Keys. {can('RegisterKeys') && <Link to="/register">Ersten Key registrieren</Link>}</>
                : <>Nichts gefunden. <button className="link" onClick={() => setParams(new URLSearchParams(view === 'keys' ? { view } : {}))}>Filter zurücksetzen</button></>}
            </Empty>
          ) : view === 'people' ? (
            <div className="card table-wrap" style={{ padding: 0 }}>
              <table className="assign-table">
                <thead>
                  <tr>
                    <SortTh label="Person" sortKey="person" sort={sort} onSort={onSort} />
                    <SortTh label="Key" sortKey="serial" sort={sort} onSort={onSort} />
                    <SortTh label="Nutzung" sortKey="usage" sort={sort} onSort={onSort} />
                    <SortTh label="Seit" sortKey="since" sort={sort} onSort={onSort} />
                    {allTenants && <SortTh label="Tenant" sortKey="tenant" sort={sort} onSort={onSort} />}
                    <th>Hinweise</th>
                  </tr>
                </thead>
                <tbody>
                  {people.map((r, i) => (
                    <tr key={r.id} style={{ animationDelay: Math.min(i, 20) * 18 + 'ms' }}>
                      <td>{r.account ? <Person name={r.account.userDisplayName} upn={r.account.userPrincipalName} extra={r.keyCount > 1 && <Badge tone="info">{r.keyCount} Keys</Badge>} />
                        : <span className="muted">– niemand –</span>}</td>
                      <td><KeyCell k={r.key} /></td>
                      <td><UsageCell k={r.key} /></td>
                      <td className="small">{r.account ? fmtDate(r.account.registeredAt, false) : '–'}</td>
                      {allTenants && <td>{r.key.tenantName}</td>}
                      <td><Hints k={r.key} /></td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          ) : (
            <div className="card table-wrap" style={{ padding: 0 }}>
              <table className="assign-table">
                <thead>
                  <tr>
                    <SortTh label="Key" sortKey="serial" sort={sort} onSort={onSort} />
                    <SortTh label="Hat den Key" sortKey="person" sort={sort} onSort={onSort} />
                    <SortTh label="Nutzung" sortKey="usage" sort={sort} onSort={onSort} />
                    <th>Konten auf dem Key</th>
                    <SortTh label="Seit" sortKey="since" sort={sort} onSort={onSort} />
                    {allTenants && <SortTh label="Tenant" sortKey="tenant" sort={sort} onSort={onSort} />}
                    <th>Hinweise</th>
                  </tr>
                </thead>
                <tbody>
                  {keys.map((k, i) => (
                    <tr key={k.keyId} style={{ animationDelay: Math.min(i, 20) * 18 + 'ms' }}>
                      <td><KeyCell k={k} /></td>
                      <td>{k.holder
                        ? <Person name={k.usage === 'Personal' ? k.holder : k.custodian} upn={k.usage === 'Personal' ? k.holderUpn : undefined} extra={k.usage !== 'Personal' && <span className="small muted">Verwahrer</span>} />
                        : <span className="muted">– niemand –</span>}</td>
                      <td><Badge tone={k.usage === 'Breakglass' ? 'error' : k.usage === 'Shared' ? 'info' : 'neutral'}>{usageLabels[k.usage]}</Badge></td>
                      <td>{k.usage === 'Personal' ? <span className="muted small">{k.accounts.length > 1 ? `${k.accounts.length} Konten` : '–'}</span> : (
                        <div className="account-chips">{k.accounts.map((a) => <span key={a.entraUserId} className="tag" title={a.userPrincipalName}>{a.userDisplayName ?? a.userPrincipalName}</span>)}</div>
                      )}</td>
                      <td className="small">{fmtDate(k.since, false)}</td>
                      {allTenants && <td>{k.tenantName}</td>}
                      <td><Hints k={k} /></td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}
          {filtered.length > 0 && <p className="small muted" style={{ marginTop: 8 }}>
            {view === 'people' ? `${people.length} Zeile(n)` : `${keys.length} Key(s)`}{filteredHint ? ` von ${data.keys.length} ausgegebenen Keys` : ''}. Gesperrte, ausgemusterte und Lager-Keys stehen im <Link to="/keys">Inventar</Link>.
          </p>}
        </>
      )}
    </>
  );
}

function KpiTile({ icon, label, value, sub, tone, active, onClick }: { icon: IconName; label: string; value: ReactNode; sub?: string; tone?: 'warn' | 'ok'; active?: boolean; onClick?: () => void }) {
  const body = <>
    <span className={'kpi-icon' + (tone ? ' tone-' + tone : '')}><Icon name={icon} size={18} /></span>
    <span className="kpi-text">
      <span className="label">{label}</span>
      <span className={'value' + (tone === 'warn' ? ' tone-text-warn' : '')}>{value}</span>
      {sub && <span className="sub">{sub}</span>}
    </span>
  </>;
  if (!onClick) return <div className="card kpi kpi-row">{body}</div>;
  return <button type="button" className={'card kpi kpi-row kpi-link' + (active ? ' kpi-active' : '')} aria-pressed={active} onClick={onClick}>{body}</button>;
}

/** Anteil persönlicher, geteilter und Breakglass-Keys als gestapelter Balken. */
function UsageBar({ s }: { s: Summary }) {
  const parts = [
    { key: 'personal', label: 'Persönlich', n: s.personal },
    { key: 'shared', label: 'Geteilt', n: s.shared },
    { key: 'breakglass', label: 'Breakglass', n: s.breakglass },
  ].filter((p) => p.n > 0);
  return (
    <div className="usage-bar-wrap" aria-label="Verteilung nach Nutzung">
      <div className="usage-bar">{parts.map((p) => <span key={p.key} className={'seg-' + p.key} style={{ flexGrow: p.n }} title={`${p.label}: ${p.n}`} />)}</div>
      <div className="usage-legend">{parts.map((p) => <span key={p.key}><i className={'seg-' + p.key} />{p.label} {p.n}</span>)}
        {s.withoutSerial > 0 && <span className="muted"><Link to="/keys/identify">{s.withoutSerial} ohne Seriennummer</Link></span>}
        {s.withAdvisory > 0 && <span className="muted">{s.withAdvisory} mit Firmware-Hinweis</span>}
      </div>
    </div>
  );
}

function Person({ name, upn, extra }: { name?: string | null; upn?: string | null; extra?: ReactNode }) {
  const label = name ?? upn ?? '';
  return (
    <div className="person">
      <span className="avatar sm" aria-hidden>{initials(label)}</span>
      <span>
        <strong>{label}</strong> {extra}
        {upn && upn !== label && <div className="small muted">{upn}</div>}
      </span>
    </div>
  );
}

function KeyCell({ k }: { k: AssignmentKey }) {
  return (
    <div>
      <Link to={'/keys/' + k.keyId} className={k.serialNumber != null ? 'mono' : undefined}>{k.serialNumber ?? 'ohne Seriennummer'}</Link>
      <div className="small muted">{[k.model, k.firmwareVersion].filter(Boolean).join(' · ') || '–'}</div>
    </div>
  );
}

function UsageCell({ k }: { k: AssignmentKey }) {
  if (k.usage === 'Personal') return <span className="muted small">Persönlich</span>;
  return (
    <div>
      <Badge tone={k.usage === 'Breakglass' ? 'error' : 'info'}>{usageLabels[k.usage]}</Badge>
      <div className="small muted">{k.custodian ? 'bei ' + k.custodian : 'Verwahrer fehlt'}</div>
    </div>
  );
}

function Hints({ k }: { k: AssignmentKey }) {
  if (k.issues.length === 0 && !k.fidoAdvisory && k.pinChangeForced !== false) return <span className="muted small">–</span>;
  return (
    <div className="hints">
      {k.issues.map((i) => <Badge key={i} tone="warn">{i}</Badge>)}
      {k.fidoAdvisory && <Badge tone="warn">{k.advisories.join(', ')}</Badge>}
      {k.pinChangeForced === false && <Badge>PIN-Wechsel nicht erzwungen</Badge>}
    </div>
  );
}

function initials(name: string) {
  const parts = name.replace(/@.*/, '').replace(/[^\p{L}\p{N}\s.-]/gu, ' ').trim().split(/[\s.-]+/).filter(Boolean);
  return ((parts[0]?.[0] ?? '') + (parts.length > 1 ? parts[parts.length - 1][0] : '')).toUpperCase() || '?';
}
