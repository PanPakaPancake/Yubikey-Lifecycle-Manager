import { useMemo, type ReactNode } from 'react';
import { Link, useSearchParams } from 'react-router';
import { fmtDate, usageLabels, type KeyUsage } from '../api';
import { useCan } from '../auth';
import { Badge, CountUp, download, Empty, formatSort, LiveSearchInput, parseSort, SortTh, type MenuItem, type SortState } from '../ui';
import { Icon, type IconName } from '../icons';

export interface AssignmentAccount { entraUserId: string; userPrincipalName: string; userDisplayName?: string | null; registeredAt: string; registeredByName?: string | null; source: string }
export interface AssignmentKey {
  keyId: string; tenantId: string; tenantName: string; serialNumber?: number | null; model?: string | null; firmwareVersion?: string | null;
  status: string; usage: KeyUsage; holder?: string | null; holderUpn?: string | null; custodian?: string | null;
  accounts: AssignmentAccount[]; since?: string | null; pinChangeForced?: boolean | null; advisories: string[]; fidoAdvisory: boolean; issues: string[];
}
export interface Summary { keys: number; people: number; personal: number; shared: number; breakglass: number; peopleWithSeveralKeys: number; withoutSerial: number; withAdvisory: number; withIssues: number }
export interface Overview { generatedAt: string; summary: Summary; keys: AssignmentKey[] }

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

/** Kennzahlen zur Ausgabe. „Geteilt“ und „Auffälligkeiten“ filtern die Personenansicht. */
export function AssignmentKpis({ s, usage, issues, onFilter }: { s: Summary; usage: string; issues: boolean; onFilter: (k: 'usage' | 'issues', v: string) => void }) {
  return (
    <>
      <div className="assign-kpis">
        <KpiTile icon="key" label="Ausgegebene Keys" value={s.keys} sub={s.withoutSerial ? `${s.withoutSerial} ohne Seriennummer` : undefined} />
        <KpiTile icon="users" label="Personen mit Key" value={s.people} sub={s.peopleWithSeveralKeys ? `${s.peopleWithSeveralKeys} mit mehreren Keys` : 'jede Person ein Key'} />
        <KpiTile icon="share" label="Geteilt / Breakglass" value={`${s.shared} / ${s.breakglass}`} sub="beim Verwahrer"
          active={usage === 'Shared'} onClick={s.shared ? () => onFilter('usage', usage === 'Shared' ? '' : 'Shared') : undefined} />
        <KpiTile icon="alert" label="Auffälligkeiten" value={s.withIssues} tone={s.withIssues ? 'warn' : 'ok'}
          sub={s.withIssues ? 'z. B. Verwahrer fehlt' : 'alles stimmig'} active={issues} onClick={s.withIssues ? () => onFilter('issues', issues ? '' : '1') : undefined} />
      </div>
      {s.keys > 0 && <UsageBar s={s} />}
    </>
  );
}

/** Exportmenü „Wer hat welchen Key“ mit den Filtern der Personenansicht. */
export function assignmentExportItems(tenantId: string, params: URLSearchParams): MenuItem[] {
  const qs = new URLSearchParams();
  if (tenantId) qs.set('tenantId', tenantId);
  if (params.get('view') === 'people') {
    if (params.get('q')) qs.set('q', params.get('q')!);
    if (params.get('usage')) qs.set('usage', params.get('usage')!);
    if (params.get('issues') === '1') qs.set('issues', 'true');
  }
  const exp = (format: 'csv' | 'xlsx' | 'pdf') => download(`/api/exports/assignments.${format}?` + qs.toString());
  return [
    { label: 'Wer hat welchen Key (Excel)', hint: 'Eine Zeile pro Person und Key, mit Filterzeile', icon: 'users', onClick: () => exp('xlsx') },
    { label: 'Wer hat welchen Key (PDF)', hint: 'Zum Drucken oder Ablegen, A4 quer', icon: 'scroll', onClick: () => exp('pdf') },
    { label: 'Wer hat welchen Key (CSV)', hint: 'Für andere Programme (Semikolon, UTF-8)', icon: 'download', onClick: () => exp('csv') },
  ];
}

/** Personenansicht des Inventars: wer hat gerade welchen Key, mit Verwahrern geteilter Keys und Auffälligkeiten. */
export function PeopleView({ data, allTenants }: { data: Overview; allTenants: boolean }) {
  const can = useCan();
  const [params, setParams] = useSearchParams();
  const q = params.get('q') ?? '';
  const usage = (params.get('usage') ?? '') as KeyUsage | '';
  const issues = params.get('issues') === '1';
  const sort = parseSort(params.get('sort'), { key: 'person', desc: false });
  const set = (k: string, v: string) => {
    const next = new URLSearchParams(params);
    if (v) next.set(k, v); else next.delete(k);
    setParams(next, { replace: k === 'q' });
  };
  const onSort = (s: SortState) => set('sort', s.key === 'person' && !s.desc ? '' : formatSort(s));

  const filtered = useMemo(() => {
    const terms = q.trim().toLocaleLowerCase('de').split(/\s+/).filter(Boolean);
    return data.keys.filter((k) => (!usage || k.usage === usage) && (!issues || k.issues.length > 0) && (terms.length === 0 || matches(k, terms)));
  }, [data, q, usage, issues]);

  const people = useMemo(() => {
    const perPerson = new Map<string, number>();
    for (const k of data.keys) for (const a of k.accounts) perPerson.set(k.tenantId + '|' + a.entraUserId, (perPerson.get(k.tenantId + '|' + a.entraUserId) ?? 0) + 1);
    const rows: PersonRow[] = [];
    for (const k of filtered) {
      if (k.accounts.length === 0) rows.push({ id: k.keyId, key: k, keyCount: 0 });
      for (const a of k.accounts) rows.push({ id: k.keyId + a.entraUserId, key: k, account: a, keyCount: perPerson.get(k.tenantId + '|' + a.entraUserId) ?? 1 });
    }
    const dir = sort.desc ? -1 : 1;
    const name = (r: PersonRow) => r.account ? (r.account.userDisplayName ?? r.account.userPrincipalName) : '\uffff';
    const by: Record<string, (a: PersonRow, b: PersonRow) => number> = {
      person: (a, b) => collator.compare(name(a), name(b)),
      serial: (a, b) => (a.key.serialNumber ?? Infinity) - (b.key.serialNumber ?? Infinity),
      since: (a, b) => (a.account?.registeredAt ?? '').localeCompare(b.account?.registeredAt ?? ''),
      tenant: (a, b) => collator.compare(a.key.tenantName, b.key.tenantName) || collator.compare(name(a), name(b)),
      usage: (a, b) => a.key.usage.localeCompare(b.key.usage) || collator.compare(name(a), name(b)),
    };
    return rows.sort((a, b) => dir * (by[sort.key] ?? by.person)(a, b));
  }, [filtered, data, sort.key, sort.desc]);
  const filteredHint = q || usage || issues;

  return (
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
      </div>

      {filtered.length === 0 ? (
        <Empty>
          {data.keys.length === 0
            ? <>Noch keine ausgegebenen Keys. {can('RegisterKeys') && <Link to="/register">Ersten Key registrieren</Link>}</>
            : <>Nichts gefunden. <button className="link" onClick={() => setParams(new URLSearchParams({ view: 'people' }))}>Filter zurücksetzen</button></>}
        </Empty>
      ) : (
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
      )}
      {filtered.length > 0 && <p className="small muted" style={{ marginTop: 8 }}>
        {people.length} Zeile(n){filteredHint ? ` aus ${data.keys.length} ausgegebenen Keys` : ''} · Stand {fmtDate(data.generatedAt)}. Lager-, gesperrte und ausgemusterte Keys zeigt die Ansicht „Alle Keys“.
      </p>}
    </>
  );
}

function KpiTile({ icon, label, value, sub, tone, active, onClick }: { icon: IconName; label: string; value: ReactNode; sub?: string; tone?: 'warn' | 'ok'; active?: boolean; onClick?: () => void }) {
  const body = <>
    <span className={'kpi-icon' + (tone ? ' tone-' + tone : '')}><Icon name={icon} size={18} /></span>
    <span className="kpi-text">
      <span className="label">{label}</span>
      <span className={'value' + (tone === 'warn' ? ' tone-text-warn' : '')}><CountUp value={value} /></span>
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
