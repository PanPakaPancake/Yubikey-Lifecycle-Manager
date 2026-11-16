import { useState } from 'react';
import { Link } from 'react-router';
import { api, fmtDate, type AuditEntry } from '../api';
import { useAuth } from '../auth';
import { Card, download, Empty, ErrorBox, Info, PageHeader, Spinner, TenantSelect, useAction, useApi } from '../ui';

export default function AuditPage() {
  const { me } = useAuth();
  const [tenantId, setTenantId] = useState('');
  const [action, setAction] = useState('');
  const [q, setQ] = useState('');
  const [search, setSearch] = useState('');
  const [older, setOlder] = useState<AuditEntry[]>([]);
  const [verify, setVerify] = useState<{ valid: boolean; checkedEntries: number; brokenAt?: number }>();
  const act = useAction();

  const qs = new URLSearchParams();
  if (tenantId) qs.set('tenantId', tenantId);
  if (action) qs.set('action', action);
  if (q) qs.set('q', q);
  qs.set('take', '100');
  const { data, error, loading } = useApi<AuditEntry[]>('/api/audit?' + qs.toString(), [qs.toString()]);
  const rows = [...(data ?? []), ...older];

  const loadMore = () => act.run(async () => {
    const last = rows[rows.length - 1];
    const next = new URLSearchParams(qs);
    next.set('before', String(last.sequence));
    const more = await api.get<AuditEntry[]>('/api/audit?' + next.toString());
    setOlder((o) => [...o, ...more]);
  });
  const resetFilters = (fn: () => void) => { setOlder([]); fn(); };
  const doVerify = () => act.run(async () => setVerify(await api.get('/api/audit/verify')));
  const exportQs = tenantId ? '?tenantId=' + tenantId : '';

  return (
    <>
      <PageHeader
        title="Audit-Log"
        subtitle="Jede Aktion wird unveränderbar protokolliert (Hash-Kette)."
        actions={<>
          {me.user?.allTenants && <button disabled={act.busy} onClick={doVerify}>Integrität prüfen</button>}
          <button onClick={() => download('/api/audit/export.csv' + exportQs)}>Export (CSV)</button>
        </>}
      />
      {verify && (verify.valid
        ? <Info tone="ok">Die Kette ist intakt ({verify.checkedEntries} Einträge geprüft).</Info>
        : <Info tone="error">Die Kette ist ab Eintrag {verify.brokenAt} verändert worden! ({verify.checkedEntries} Einträge geprüft)</Info>)}
      <div className="toolbar">
        <TenantSelect value={tenantId} onChange={(v) => resetFilters(() => setTenantId(v))} />
        <select value={action} onChange={(e) => resetFilters(() => setAction(e.target.value))} aria-label="Aktion">
          <option value="">Alle Aktionen</option>
          <option value="key.">Keys</option>
          <option value="pin_letter.">PIN-Briefe</option>
          <option value="tenant.">Tenants</option>
          <option value="sync.">Synchronisation</option>
          <option value="profile.">Profile</option>
          <option value="auth.">Anmeldungen</option>
          <option value="user.">Benutzer</option>
          <option value="fields.">Zusatzfelder</option>
          <option value="agent.">Agents</option>
          <option value="apikey.">API-Schlüssel</option>
          <option value="export.">Exporte</option>
          <option value="setup.">Einrichtung</option>
        </select>
        <form onSubmit={(e) => { e.preventDefault(); resetFilters(() => setQ(search.trim())); }}>
          <input type="search" placeholder="Person oder Ziel …" value={search} onChange={(e) => setSearch(e.target.value)} />
        </form>
        {loading && <Spinner />}
      </div>
      <ErrorBox error={error ?? act.error} />
      {data && rows.length === 0 && <Empty>Keine Einträge.</Empty>}
      {rows.length > 0 && (
        <Card className="table-wrap">
          <table>
            <thead><tr><th>#</th><th>Zeitpunkt</th><th>Wer</th><th>Aktion</th><th>Ziel</th><th>Details</th></tr></thead>
            <tbody>
              {rows.map((a) => (
                <tr key={a.sequence}>
                  <td className="small muted">{a.sequence}</td>
                  <td className="small" style={{ whiteSpace: 'nowrap' }}>{fmtDate(a.timestamp)}</td>
                  <td>{a.actorName ?? a.actorType}<div className="small muted">{a.actorType !== 'User' ? a.actorType : a.ipAddress}</div></td>
                  <td><code>{a.action}</code></td>
                  <td>{a.keyId ? <Link to={'/keys/' + a.keyId}>{a.target ?? 'Key'}</Link> : a.target}</td>
                  <td className="small mono" style={{ maxWidth: 420, wordBreak: 'break-word' }}>{a.details}</td>
                </tr>
              ))}
            </tbody>
          </table>
          {data && data.length === 100 && <div style={{ textAlign: 'center', marginTop: 12 }}><button disabled={act.busy} onClick={loadMore}>Ältere laden</button></div>}
        </Card>
      )}
    </>
  );
}
