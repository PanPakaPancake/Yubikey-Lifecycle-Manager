import { Link } from 'react-router';
import { fmtDate, statusLabels, type KeyStatus } from '../api';
import { useCan } from '../auth';
import { Badge, Card, download, Empty, ErrorBox, PageHeader, Spinner, useApi } from '../ui';
import { useTenant } from '../tenant';

interface DashboardData {
  generatedAt: string; byStatus: Record<string, number>; inStock: number; total: number; needsDetails: number;
  targetUsers?: number; usersWithKey: number; coveragePercent?: number;
  issuedPerMonth: { month: string; count: number }[];
  firmware: { firmware: string; count: number; advisories: string[]; vulnerable: boolean }[];
  vulnerableKeys: number; forcePinChangeUnsupported: number;
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
          <div className="grid cols-4">
            <div className="card kpi">
              <div className="label">Abdeckung Zielgruppe</div>
              <div className="value">{data.coveragePercent != null ? data.coveragePercent.toFixed(0) + ' %' : '–'}</div>
              <div className="sub">{data.usersWithKey} von {data.targetUsers ?? '?'} Personen mit Key</div>
              {data.coveragePercent != null && <div className="meter"><span style={{ width: Math.min(100, data.coveragePercent) + '%' }} /></div>}
            </div>
            <div className="card kpi">
              <div className="label">Keys aktiv</div>
              <div className="value">{data.byStatus.Active ?? 0}</div>
              <div className="sub">{data.total} Keys gesamt</div>
            </div>
            <div className="card kpi">
              <div className="label">Im Lager</div>
              <div className="value">{data.inStock}</div>
              <div className="sub">{data.byStatus.Locked ?? 0} gesperrt, {data.byStatus.Retired ?? 0} ausgemustert</div>
            </div>
            <div className="card kpi">
              <div className="label">Firmware mit Sicherheitshinweis</div>
              <div className="value" style={{ color: data.vulnerableKeys ? 'var(--warn)' : undefined }}>{data.vulnerableKeys}</div>
              <div className="sub">{data.forcePinChangeUnsupported} ohne erzwingbaren PIN-Wechsel</div>
            </div>
          </div>

          {data.needsDetails > 0 && (
            <div className="alert info" style={{ marginTop: 16 }}>
              {data.needsDetails} selbst registrierte Keys ohne Seriennummer/Firmware. <Link to="/keys?needsDetails=1">Im Inventar ansehen</Link>
            </div>
          )}

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
                    <tr key={s}><td><span className={'badge status-' + s}>{statusLabels[s]}</span></td><td style={{ textAlign: 'right' }}>{data.byStatus[s] ?? 0}</td></tr>
                  ))}
                </tbody>
              </table>
            </Card>
          </div>

          <div className="grid cols-2" style={{ marginTop: 16 }}>
            <Card title="Firmware-Stände">
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
