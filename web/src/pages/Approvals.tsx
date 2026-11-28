import { useState } from 'react';
import { Link } from 'react-router';
import { api, approvalLabels, fmtDate, removalLabels, type Approval, type ApprovalStatus } from '../api';
import { useAuth, useCan } from '../auth';
import { Badge, Empty, ErrorBox, Field, Info, Modal, PageHeader, Spinner, useAction, useApi } from '../ui';
import { useToast } from '../feedback';
import { useTenant } from '../tenant';
import { tr } from '../i18n';

const tone: Record<ApprovalStatus, 'warn' | 'ok' | 'error' | 'neutral' | 'info'> = { Pending: 'warn', Approved: 'ok', Rejected: 'error', Used: 'info', Cancelled: 'neutral' };

/** Vier-Augen-Freigaben für Breakglass-Keys: Registrierung (V2) und Sperren/Zurücksetzen/Konto entfernen. */

/** Was beantragt wurde, als kurzer Text. */
export function approvalWhat(a: Approval) {
  return a.kind === 'KeyRemoval' ? `${removalLabels[a.removalAction ?? 'Lock']}` : tr("Registrieren");
}
export default function ApprovalsPage() {
  const { tenantId, tenant } = useTenant();
  const can = useCan();
  const { me } = useAuth();
  const toast = useToast();
  const { data, error, loading, reload } = useApi<{ approval: Approval; tenantName?: string }[]>(`/api/approvals${tenantId ? '?tenantId=' + tenantId : ''}`, [tenantId]);
  const [deciding, setDeciding] = useState<{ a: Approval; approve: boolean }>();
  const cancel = async (a: Approval) => {
    try { await api.post(`/api/approvals/${a.id}/cancel`); toast.ok(tr("Antrag zurückgezogen.")); reload(); } catch (e) { toast.error((e as Error).message); }
  };
  return (
    <>
      <PageHeader title={tr("Freigaben")} subtitle={<>{tr("Vier-Augen-Freigabe für Breakglass-Keys")}{tenant ? ` · ${tenant.displayName}` : tr(", alle Tenants")}{tr(". Einschalten unter")} <Link to="/tenant">{tr("Tenant-Einstellungen")}</Link>.</>} />
      <ErrorBox error={error} />
      {loading && !data && <Spinner />}
      {data && data.length === 0 && <Empty>{tr("Keine Freigaben. Anträge entstehen im Wizard „Geteilter Key“, wenn ein Breakglass-Key registriert wird, und an der Key-Ansicht, wenn ein Breakglass-Key gesperrt, zurückgesetzt oder um ein Konto erleichtert werden soll.")}</Empty>}
      {data && data.length > 0 && (
        <div className="card table-wrap" style={{ padding: 0 }}>
          <table>
            <thead><tr><th>{tr("Aktion")}</th><th>{tr("Konten")}</th>{!tenantId && <th>{tr("Tenant")}</th>}<th>{tr("Verwahrer · Grund")}</th><th>{tr("Beantragt")}</th><th>{tr("Status")}</th><th /></tr></thead>
            <tbody>
              {data.map(({ approval: a, tenantName }) => {
                const own = a.requestedById === me.user?.id;
                return (
                  <tr key={a.id}>
                    <td><Badge tone={a.kind === 'KeyRemoval' ? 'error' : 'info'}>{approvalWhat(a)}</Badge></td>
                    <td>{a.userPrincipalNames.map((u) => <div key={u}>{u}</div>)}</td>
                    {!tenantId && <td>{tenantName}</td>}
                    <td>{a.custodian}<div className="small muted">{a.reason}</div></td>
                    <td className="small">{fmtDate(a.requestedAt)}<br /><span className="muted">{a.requestedByName}</span></td>
                    <td>
                      <Badge tone={a.expired ? 'neutral' : tone[a.status]}>{a.expired ? tr("Abgelaufen") : approvalLabels[a.status]}</Badge>
                      {a.decidedByName && <div className="small muted">{a.decidedByName}, {fmtDate(a.decidedAt)}{a.decisionNote ? ` · ${a.decisionNote}` : ''}</div>}
                      {a.status === 'Approved' && !a.expired && <div className="small muted">{tr("gültig bis {validUntil}", { validUntil: fmtDate(a.validUntil) })}</div>}
                      {a.keyId && <div className="small"><Link to={'/keys/' + a.keyId}>{tr("Key ansehen")}</Link></div>}
                    </td>
                    <td className="actions">
                      {a.status === 'Pending' && can('ApproveRegistrations') && !own && <>
                        <button className="primary small" onClick={() => setDeciding({ a, approve: true })}>{tr("Freigeben")}</button>
                        <button className="small" onClick={() => setDeciding({ a, approve: false })}>{tr("Ablehnen")}</button>
                      </>}
                      {a.status === 'Pending' && own && <span className="small muted">{tr("Ihr Antrag: ein anderer Admin muss freigeben.")}</span>}
                      {own && (a.status === 'Pending' || (a.status === 'Approved' && !a.expired)) && <button className="small ghost" onClick={() => cancel(a)}>{tr("Zurückziehen")}</button>}
                    </td>
                  </tr>
                );
              })}
            </tbody>
          </table>
        </div>
      )}
      {!can('ApproveRegistrations') && <Info>{tr("Zum Freigeben braucht es das Recht „Breakglass freigeben“ (Owner oder eigene Rolle).")}</Info>}
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
    toast.ok(approve ? tr("Freigegeben. Der Antragsteller kann {n} jetzt 24 Stunden lang einmal ausführen.", { n: approval.kind === 'KeyRemoval' ? tr("die Aktion") : tr("die Registrierung") }) : tr("Abgelehnt."));
    onDone();
  });
  return (
    <Modal title={approve ? (approval.kind === 'KeyRemoval' ? tr("Breakglass-Key: {approval} freigeben", { approval: approvalWhat(approval) }) : tr("Breakglass-Registrierung freigeben")) : tr("Antrag ablehnen")} onClose={onClose}
      footer={<><button onClick={onClose}>{tr("Abbrechen")}</button><button className={approve ? 'primary' : 'danger'} disabled={busy || (!approve && !note.trim())} onClick={go}>{approve ? tr("Freigeben") : tr("Ablehnen")}</button></>}>
      {approval.kind === 'KeyRemoval'
        ? <p>{tr("{requestedByName} möchte den Breakglass-Key", { requestedByName: approval.requestedByName })} {approval.removalAction === 'RemoveAccount' ? <>{tr("um das Konto")} <strong>{approval.userPrincipalNames.join(', ')}</strong> {tr("erleichtern")}</> : <>{approval.removalAction === 'Reset' ? tr("zurücksetzen") : tr("sperren")}{tr(". Betroffen:")} <strong>{approval.userPrincipalNames.join(', ')}</strong></>}{tr(". Die Passkeys werden danach in Entra gelöscht; der Notfallzugang funktioniert dann nicht mehr. Verwahrer: {n}.", { n: approval.custodian ?? '–' })}</p>
        : <p>{tr("{requestedByName} möchte einen Breakglass-Key für", { requestedByName: approval.requestedByName })} <strong>{approval.userPrincipalNames.join(', ')}</strong> {tr("registrieren. Verwahrer: {custodian}.", { custodian: approval.custodian })}</p>}
      <p className="small muted">{tr("Grund: {reason}", { reason: approval.reason })}</p>
      <Field label={approve ? tr("Notiz (optional)") : tr("Grund der Ablehnung *")}><input value={note} onChange={(e) => setNote(e.target.value)} /></Field>
      <ErrorBox error={error} />
    </Modal>
  );
}
