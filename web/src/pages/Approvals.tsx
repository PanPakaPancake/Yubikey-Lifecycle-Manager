import { useState } from 'react';
import { Link } from 'react-router';
import { api, approvalLabels, fmtDate, type Approval, type ApprovalStatus } from '../api';
import { useAuth, useCan } from '../auth';
import { Badge, Empty, ErrorBox, Field, Info, Modal, PageHeader, Spinner, useAction, useApi } from '../ui';
import { useToast } from '../feedback';
import { useTenant } from '../tenant';

const tone: Record<ApprovalStatus, 'warn' | 'ok' | 'error' | 'neutral' | 'info'> = { Pending: 'warn', Approved: 'ok', Rejected: 'error', Used: 'info', Cancelled: 'neutral' };

/** Vier-Augen-Freigaben für Breakglass-Registrierungen (V2). */
export default function ApprovalsPage() {
  const { tenantId, tenant } = useTenant();
  const can = useCan();
  const { me } = useAuth();
  const toast = useToast();
  const { data, error, loading, reload } = useApi<{ approval: Approval; tenantName?: string }[]>(`/api/approvals${tenantId ? '?tenantId=' + tenantId : ''}`, [tenantId]);
  const [deciding, setDeciding] = useState<{ a: Approval; approve: boolean }>();
  const cancel = async (a: Approval) => {
    try { await api.post(`/api/approvals/${a.id}/cancel`); toast.ok('Antrag zurückgezogen.'); reload(); } catch (e) { toast.error((e as Error).message); }
  };
  return (
    <>
      <PageHeader title="Freigaben" subtitle={<>Vier-Augen-Freigabe für Breakglass-Keys{tenant ? ` · ${tenant.displayName}` : ', alle Tenants'}. Einschalten unter <Link to="/tenant">Tenant-Einstellungen</Link>.</>} />
      <ErrorBox error={error} />
      {loading && !data && <Spinner />}
      {data && data.length === 0 && <Empty>Keine Freigaben. Anträge entstehen im Wizard „Geteilter Key“, wenn ein Breakglass-Key registriert wird.</Empty>}
      {data && data.length > 0 && (
        <div className="card table-wrap" style={{ padding: 0 }}>
          <table>
            <thead><tr><th>Konten</th>{!tenantId && <th>Tenant</th>}<th>Verwahrer · Grund</th><th>Beantragt</th><th>Status</th><th /></tr></thead>
            <tbody>
              {data.map(({ approval: a, tenantName }) => {
                const own = a.requestedById === me.user?.id;
                return (
                  <tr key={a.id}>
                    <td>{a.userPrincipalNames.map((u) => <div key={u}>{u}</div>)}</td>
                    {!tenantId && <td>{tenantName}</td>}
                    <td>{a.custodian}<div className="small muted">{a.reason}</div></td>
                    <td className="small">{fmtDate(a.requestedAt)}<br /><span className="muted">{a.requestedByName}</span></td>
                    <td>
                      <Badge tone={a.expired ? 'neutral' : tone[a.status]}>{a.expired ? 'Abgelaufen' : approvalLabels[a.status]}</Badge>
                      {a.decidedByName && <div className="small muted">{a.decidedByName}, {fmtDate(a.decidedAt)}{a.decisionNote ? ` · ${a.decisionNote}` : ''}</div>}
                      {a.status === 'Approved' && !a.expired && <div className="small muted">gültig bis {fmtDate(a.validUntil)}</div>}
                      {a.keyId && <div className="small"><Link to={'/keys/' + a.keyId}>Key ansehen</Link></div>}
                    </td>
                    <td className="actions">
                      {a.status === 'Pending' && can('ApproveRegistrations') && !own && <>
                        <button className="primary small" onClick={() => setDeciding({ a, approve: true })}>Freigeben</button>
                        <button className="small" onClick={() => setDeciding({ a, approve: false })}>Ablehnen</button>
                      </>}
                      {a.status === 'Pending' && own && <span className="small muted">Ihr Antrag: ein anderer Admin muss freigeben.</span>}
                      {own && (a.status === 'Pending' || (a.status === 'Approved' && !a.expired)) && <button className="small ghost" onClick={() => cancel(a)}>Zurückziehen</button>}
                    </td>
                  </tr>
                );
              })}
            </tbody>
          </table>
        </div>
      )}
      {!can('ApproveRegistrations') && <Info>Zum Freigeben braucht es das Recht „Breakglass freigeben“ (Owner oder eigene Rolle).</Info>}
      {deciding && <DecisionModal approval={deciding.a} approve={deciding.approve} onClose={() => setDeciding(undefined)} onDone={() => { setDeciding(undefined); reload(); }} />}
    </>
  );
}

function DecisionModal({ approval, approve, onClose, onDone }: { approval: Approval; approve: boolean; onClose: () => void; onDone: () => void }) {
  const [note, setNote] = useState('');
  const { busy, error, run } = useAction();
  const toast = useToast();
  const go = () => run(async () => {
    await api.post(`/api/approvals/${approval.id}/${approve ? 'approve' : 'reject'}`, { note });
    toast.ok(approve ? 'Freigegeben. Der Antragsteller kann die Registrierung jetzt 24 Stunden lang ausführen.' : 'Abgelehnt.');
    onDone();
  });
  return (
    <Modal title={approve ? 'Breakglass-Registrierung freigeben' : 'Antrag ablehnen'} onClose={onClose}
      footer={<><button onClick={onClose}>Abbrechen</button><button className={approve ? 'primary' : 'danger'} disabled={busy || (!approve && !note.trim())} onClick={go}>{approve ? 'Freigeben' : 'Ablehnen'}</button></>}>
      <p>{approval.requestedByName} möchte einen Breakglass-Key für <strong>{approval.userPrincipalNames.join(', ')}</strong> registrieren. Verwahrer: {approval.custodian}.</p>
      <p className="small muted">Grund: {approval.reason}</p>
      <Field label={approve ? 'Notiz (optional)' : 'Grund der Ablehnung *'}><input value={note} onChange={(e) => setNote(e.target.value)} /></Field>
      <ErrorBox error={error} />
    </Modal>
  );
}
