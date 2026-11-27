import { useEffect, useState } from 'react';
import { Link } from 'react-router';
import { api, fmtDate, type AuditEntry } from '../api';
import { useAuth } from '../auth';
import { Card, download, Empty, ErrorBox, Info, LiveSearchInput, PageHeader, useAction, useApi } from '../ui';
import { useToast } from '../feedback';
import { useTenant } from '../tenant';
import { tr } from '../i18n';

/** Einträge des gewählten Tenants oder (ohne Auswahl) alle Einträge inkl. plattformweiter Ereignisse. */
export default function AuditPage() {
  const { me } = useAuth();
  const ctx = useTenant();
  const tenantId = ctx.tenantId;
  useEffect(() => setOlder([]), [tenantId]);
  const [action, setAction] = useState('');
  const [q, setQ] = useState('');
  const [older, setOlder] = useState<AuditEntry[]>([]);
  const [verify, setVerify] = useState<{ valid: boolean; checkedEntries: number; brokenAt?: number }>();
  const act = useAction();
  const toast = useToast();

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
  const doVerify = () => act.run(async () => {
    const r = await api.get<{ valid: boolean; checkedEntries: number; brokenAt?: number }>('/api/audit/verify');
    setVerify(r);
    if (r.valid) toast.ok(tr("Die Kette ist intakt ({checkedEntries} Einträge geprüft).", { checkedEntries: r.checkedEntries }));
  });
  const exportQs = tenantId ? '?tenantId=' + tenantId : '';

  return (
    <>
      <PageHeader
        title={tr("Audit-Log")}
        subtitle={tr("Jede Aktion wird unveränderbar protokolliert (Hash-Kette).")}
        actions={<>
          {me.user?.allTenants && <button disabled={act.busy} onClick={doVerify}>{tr("Integrität prüfen")}</button>}
          <button onClick={() => download('/api/audit/export.csv' + exportQs)}>{tr("Export (CSV)")}</button>
        </>}
      />
      {verify && !verify.valid && (
        <Info tone="error">{tr("Die Kette ist ab Eintrag {brokenAt} verändert worden! ({checkedEntries} Einträge geprüft)", { brokenAt: verify.brokenAt, checkedEntries: verify.checkedEntries })}</Info>)}
      <div className="toolbar">
        <select value={action} onChange={(e) => resetFilters(() => setAction(e.target.value))} aria-label={tr("Aktion")}>
          <option value="">{tr("Alle Aktionen")}</option>
          <option value="key.">{tr("Keys")}</option>
          <option value="pin_letter.">{tr("PIN-Briefe")}</option>
          <option value="tenant.">{tr("Tenants")}</option>
          <option value="sync.">{tr("Synchronisation")}</option>
          <option value="profile.">{tr("Profile")}</option>
          <option value="auth.">{tr("Anmeldungen")}</option>
          <option value="user.">{tr("Benutzer")}</option>
          <option value="fields.">{tr("Zusatzfelder")}</option>
          <option value="agent.">{tr("Agents")}</option>
          <option value="apikey.">{tr("API-Schlüssel")}</option>
          <option value="export.">{tr("Exporte")}</option>
          <option value="setup.">{tr("Einrichtung")}</option>
        </select>
        <LiveSearchInput value={q} onSearch={(v) => resetFilters(() => setQ(v))} placeholder={tr("Person oder Ziel …")} busy={loading} />
      </div>
      <ErrorBox error={error ?? act.error} />
      {data && rows.length === 0 && <Empty>{tr("Keine Einträge.")}</Empty>}
      {rows.length > 0 && (
        <Card className="table-wrap">
          <table>
            <thead><tr><th>#</th><th>{tr("Zeitpunkt")}</th><th>{tr("Wer")}</th><th>{tr("Aktion")}</th><th>{tr("Ziel")}</th><th>{tr("Details")}</th></tr></thead>
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
          {data && data.length === 100 && <div style={{ textAlign: 'center', marginTop: 12 }}><button disabled={act.busy} onClick={loadMore}>{tr("Ältere laden")}</button></div>}
        </Card>
      )}
    </>
  );
}
