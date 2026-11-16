import { useState } from 'react';
import { Link, useSearchParams } from 'react-router';
import { api, fmtDate, type Tenant } from '../api';
import { useCan } from '../auth';
import { Badge, Empty, ErrorBox, Field, Modal, PageHeader, Spinner, useAction, useApi } from '../ui';

export default function TenantsPage() {
  const [params] = useSearchParams();
  const can = useCan();
  const { data, error, loading } = useApi<Tenant[]>('/api/tenants');
  const [adding, setAdding] = useState(false);
  const consentError = params.get('consentError');

  return (
    <>
      <PageHeader title="Tenants" subtitle="Angebundene Entra-ID-Umgebungen" actions={can('ManageTenants') && <button className="primary" onClick={() => setAdding(true)}>Tenant hinzufügen</button>} />
      <ErrorBox error={consentError ? 'Onboarding fehlgeschlagen: ' + consentError : error} />
      {loading && !data && <Spinner />}
      {data && data.length === 0 && <Empty>Noch kein Tenant angebunden.</Empty>}
      {data && data.length > 0 && (
        <div className="card table-wrap" style={{ padding: 0 }}>
          <table>
            <thead><tr><th>Tenant</th><th>Domain</th><th>Prüfungen</th><th>Zielgruppe</th><th>Letzter Sync</th><th>Status</th></tr></thead>
            <tbody>
              {data.map((t) => {
                const failed = t.checks.filter((c) => c.status === 'Failed').length;
                const warn = t.checks.filter((c) => c.status === 'Warning').length;
                return (
                  <tr key={t.id}>
                    <td><Link to={'/tenants/' + t.id}>{t.displayName}</Link></td>
                    <td className="small">{t.defaultDomain}</td>
                    <td>{failed > 0 ? <Badge tone="error">{failed} offen</Badge> : warn > 0 ? <Badge tone="warn">{warn} Hinweis(e)</Badge> : t.checks.length ? <Badge tone="ok">alles ok</Badge> : '–'}</td>
                    <td>{t.targetGroupName ?? 'Alle Benutzer'}{t.targetUserCount != null && <span className="muted small"> ({t.targetUserCount})</span>}</td>
                    <td>{t.lastSyncSucceeded === false ? <Badge tone="error">Fehler</Badge> : <span className="small">{fmtDate(t.lastSyncAt)}</span>}</td>
                    <td>{t.status === 'Active' ? <Badge tone="ok">aktiv</Badge> : <Badge>offboarded, Löschung {fmtDate(t.dataDeletionDueAt, false)}</Badge>}</td>
                  </tr>
                );
              })}
            </tbody>
          </table>
        </div>
      )}
      {adding && <AddTenantModal onClose={() => setAdding(false)} />}
    </>
  );
}

function AddTenantModal({ onClose }: { onClose: () => void }) {
  const [hint, setHint] = useState('');
  const { busy, error, run } = useAction();
  const go = () => run(async () => {
    const { url } = await api.post<{ url: string }>('/api/tenants/consent', { tenantHint: hint || null });
    window.location.href = url;
  });
  return (
    <Modal title="Tenant hinzufügen" onClose={onClose} footer={<><button onClick={onClose}>Abbrechen</button><button className="primary" disabled={busy} onClick={go}>Weiter zu Microsoft</button></>}>
      <ol>
        <li>Sie werden zu Microsoft weitergeleitet. Melden Sie sich dort als <strong>Globaler Administrator</strong> des Kunden-Tenants an.</li>
        <li>Erteilen Sie der App „Yubikey Lifecycle Manager“ die angezeigten Berechtigungen (Admin-Consent).</li>
        <li>Danach prüft die Plattform die FIDO2-Einstellungen des Tenants und zeigt, was noch fehlt.</li>
      </ol>
      <Field label="Tenant-Domain oder -ID (optional)" hint="Hilft Microsoft, direkt den richtigen Tenant anzuzeigen.">
        <input value={hint} onChange={(e) => setHint(e.target.value)} placeholder="contoso.onmicrosoft.com" />
      </Field>
      <ErrorBox error={error} />
    </Modal>
  );
}
