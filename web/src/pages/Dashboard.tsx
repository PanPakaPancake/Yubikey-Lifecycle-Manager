import { Link } from 'react-router';
import { fmtDate, statusLabels, type KeyStatus } from '../api';
import { useCan } from '../auth';
import type { ReactNode } from 'react';
import { Badge, Card, download, Empty, ErrorBox, PageHeader, Spinner, useApi } from '../ui';
import { Icon } from '../icons';
import { useTenant } from '../tenant';

interface DashboardData {
  generatedAt: string; byStatus: Record<string, number>; inStock: number; total: number; needsDetails: number;
  targetUsers?: number; usersWithKey: number; coveragePercent?: number;
  issuedPerMonth: { month: string; count: number }[];
  firmware: { firmware: string; count: number; advisories: string[]; vulnerable: boolean }[];
  vulnerableKeys: number; forcePinChangeUnsupported: number; openTodos: number; pendingApprovals: number;
  tenants: { id: string; displayName: string; lastSyncAt?: string; lastSyncSucceeded?: boolean; targetUserCount?: number; active: number; inStock: number; usersWithKey: number; checksFailed: number }[];
}

const monthLabel = (m: string) => new Date(m + '-01').toLocaleDateString('de-DE', { month: 'short', year: '2-digit' });

/** Kundenebene: gewählter Tenant aus dem Umschalter; scope="all": Partnerebene über alle Kunden. */
export default function DashboardPage({ scope }: { scope?: 'all' }) {
  const ctx = useTenant();
  const tenantId = scope === 'all' ? '' : ctx.tenantId;
  const can = useCan();
  const q = tenantId ? '?tenantId=' + tenantId : '';
  const { data, error, loading } = useApi<DashboardData>('/api/dashboard' + q, [tenantId]);
  // Kennzahlen führen ins passend gefilterte Inventar (auf Partnerebene: über alle Tenants).
  const keysLink = (status: KeyStatus) => '/keys?status=' + status + (scope === 'all' ? '&tenantId=all' : '');
  const maxMonth = Math.max(1, ...(data?.issuedPerMonth.map((m) => m.count) ?? [1]));

  return (
    <>
      <PageHeader
        title={scope === 'all' ? 'Übersicht aller Kunden' : ctx.tenant ? 'Übersicht ' + ctx.tenant.displayName : 'Übersicht'}
        subtitle={data ? 'Stand ' + fmtDate(data.generatedAt) : undefined}
        actions={<>
          {can('ReadReports') && <>
            <button onClick={() => download('/api/exports/keys.csv' + q)}>Keys (CSV)</button>
            <button onClick={() => download('/api/exports/registrations.csv' + q)}>Registrierungen (CSV)</button>
            <button className="primary" onClick={() => download('/api/exports/dashboard.pdf' + q)}>Bericht (PDF)</button>
          </>}
        </>}
      />
      <ErrorBox error={error} />
      {!data && loading && <Spinner label="Wird geladen …" />}
      {data && (
        <>
          <ActionItems data={data} keysLink={keysLink} />

          <div className="grid cols-4">
            <Kpi label="Abdeckung Zielgruppe" value={data.coveragePercent != null ? data.coveragePercent.toFixed(0) + ' %' : '–'}
              sub={`${data.usersWithKey} von ${data.targetUsers ?? '?'} Personen mit Key`} to={scope === 'all' ? undefined : '/assignments'}>
              {data.coveragePercent != null && <div className="meter"><span style={{ width: Math.min(100, data.coveragePercent) + '%' }} /></div>}
            </Kpi>
            <Kpi label="Keys aktiv" value={data.byStatus.Active ?? 0} sub={`${data.total} Keys gesamt`} to={keysLink('Active')} />
            <Kpi label="Im Lager" value={data.inStock} sub={`${data.byStatus.Locked ?? 0} gesperrt, ${data.byStatus.Retired ?? 0} ausgemustert`} to={keysLink('InStock')} />
            <Kpi label="Firmware mit Sicherheitshinweis" value={data.vulnerableKeys} tone={data.vulnerableKeys ? 'warn' : undefined}
              sub={`${data.forcePinChangeUnsupported} ohne erzwingbaren PIN-Wechsel`} to={data.vulnerableKeys ? '#firmware' : undefined} />
          </div>

          <div className="grid cols-2" style={{ marginTop: 16 }}>
            <Card title="Ausgegebene Keys pro Monat">
              <div className="bars">
                {data.issuedPerMonth.map((m) => (
                  <div className="bar" key={m.month} title={`${monthLabel(m.month)}: ${m.count}`}>
                    <span className="n">{m.count || ''}</span>
                    <span className="fill" style={{ height: (m.count / maxMonth) * 100 + '%' }} />
                    <span className="m">{monthLabel(m.month)}</span>
                  </div>
                ))}
              </div>
            </Card>
            <Card title="Status">
              <table>
                <tbody>
                  {(Object.keys(statusLabels) as KeyStatus[]).map((s) => (
                    <tr key={s}><td><Link to={keysLink(s)} className={'badge status-' + s}>{statusLabels[s]}</Link></td><td className="num">{data.byStatus[s] ?? 0}</td></tr>
                  ))}
                </tbody>
              </table>
            </Card>
          </div>

          <div className="grid cols-2" style={{ marginTop: 16 }}>
            <Card title={<span id="firmware">Firmware-Stände</span>}>
              {data.firmware.length === 0 ? <Empty>Noch keine Firmware-Daten.</Empty> : (
                <table>
                  <thead><tr><th>Firmware</th><th>Keys</th><th>Hinweise</th></tr></thead>
                  <tbody>
                    {data.firmware.map((f) => (
                      <tr key={f.firmware}>
                        <td className="mono">{f.firmware}</td>
                        <td>{f.count}</td>
                        <td>{f.advisories.map((a) => <Badge key={a} tone={f.vulnerable ? 'warn' : 'neutral'}>{a}</Badge>)}</td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              )}
            </Card>
            <Card title="Tenants">
              {data.tenants.length === 0 ? <Empty>Noch kein Tenant angebunden. <Link to="/tenants">Tenant hinzufügen</Link></Empty> : (
                <table>
                  <thead><tr><th>Tenant</th><th>Aktiv</th><th>Lager</th><th>Abdeckung</th><th>Sync</th></tr></thead>
                  <tbody>
                    {data.tenants.map((t) => (
                      <tr key={t.id}>
                        <td><Link to={'/tenants/' + t.id}>{t.displayName}</Link> {t.checksFailed > 0 && <Badge tone="error">{t.checksFailed} Prüfung(en)</Badge>}</td>
                        <td>{t.active}</td>
                        <td>{t.inStock}</td>
                        <td>{t.targetUserCount ? Math.round((t.usersWithKey / t.targetUserCount) * 100) + ' %' : '–'}</td>
                        <td>{t.lastSyncSucceeded === false ? <Badge tone="error">Fehler</Badge> : <span className="small muted">{fmtDate(t.lastSyncAt)}</span>}</td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              )}
            </Card>
          </div>
        </>
      )}
    </>
  );
}

function Kpi({ label, value, sub, to, tone, children }: { label: string; value: ReactNode; sub: string; to?: string; tone?: 'warn'; children?: ReactNode }) {
  const body = <>
    <div className="label">{label}</div>
    <div className={'value' + (tone ? ' tone-text-' + tone : '')}>{value}</div>
    <div className="sub">{sub}</div>
    {children}
  </>;
  if (!to) return <div className="card kpi">{body}</div>;
  if (to.startsWith('#')) return <a className="card kpi kpi-link" href={to}>{body}</a>;
  return <Link className="card kpi kpi-link" to={to}>{body}</Link>;
}

/** „Handlungsbedarf“: alles, was ein Admin heute anfassen sollte, mit direktem Link. */
function ActionItems({ data, keysLink }: { data: DashboardData; keysLink: (s: KeyStatus) => string }) {
  const items: { tone: 'error' | 'warn' | 'info'; text: ReactNode; to: string; cta: string }[] = [];
  for (const t of data.tenants) {
    if (t.checksFailed > 0) items.push({ tone: 'error', text: <><strong>{t.displayName}</strong>: {t.checksFailed} FIDO2-Prüfung(en) fehlgeschlagen</>, to: '/tenants/' + t.id, cta: 'Beheben' });
    if (t.lastSyncSucceeded === false) items.push({ tone: 'error', text: <><strong>{t.displayName}</strong>: letzte Synchronisation fehlgeschlagen</>, to: '/tenants/' + t.id, cta: 'Ansehen' });
  }
  if ((data.byStatus.Locked ?? 0) > 0) items.push({ tone: 'warn', text: <>{data.byStatus.Locked} gesperrte(r) Key(s)</>, to: keysLink('Locked'), cta: 'Im Inventar' });
  if (data.vulnerableKeys > 0) items.push({ tone: 'warn', text: <>{data.vulnerableKeys} Key(s) mit Firmware-Sicherheitshinweis</>, to: '#firmware', cta: 'Firmware-Stände' });
  if (data.pendingApprovals > 0) items.push({ tone: 'warn', text: <>{data.pendingApprovals} Breakglass-Antrag/Anträge warten auf Freigabe</>, to: '/approvals', cta: 'Freigaben' });
  if (data.openTodos > 0) items.push({ tone: 'info', text: <>{data.openTodos} offene(s) ToDo(s): Personen, die einen Key bekommen sollen</>, to: '/todos', cta: 'ToDo-Liste' });
  if (data.needsDetails > 0) items.push({ tone: 'info', text: <>{data.needsDetails} selbst registrierte Key(s) ohne Seriennummer/Firmware</>, to: '/keys/identify', cta: 'Nacherfassen' });

  if (items.length === 0) return (
    <div className="action-items all-good"><span className="ai-icon tone-ok"><Icon name="check" size={16} /></span>Kein Handlungsbedarf. Alle Prüfungen und Synchronisationen sind in Ordnung.</div>
  );
  return (
    <section className="action-items" aria-label="Handlungsbedarf">
      <h2>Handlungsbedarf <span className="count">{items.length}</span></h2>
      <ul>
        {items.map((i, n) => (
          <li key={n}>
            <span className={'ai-icon tone-' + i.tone}><Icon name={i.tone === 'info' ? 'info' : 'alert'} size={15} /></span>
            <span className="ai-text">{i.text}</span>
            {i.to.startsWith('#') ? <a href={i.to}>{i.cta}</a> : <Link to={i.to}>{i.cta}</Link>}
          </li>
        ))}
      </ul>
    </section>
  );
}
