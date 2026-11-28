import { useState } from 'react';
import { Link, useNavigate } from 'react-router';
import { api, fmtDate, todoLabels, type Profile, type Todo, type TodoStatus } from '../api';
import { useCan } from '../auth';
import { Badge, Empty, ErrorBox, Field, Info, Modal, PageHeader, Spinner, useAction, useApi, useLiveSearch } from '../ui';
import { useToast } from '../feedback';
import { useTenant } from '../tenant';
import type { EntraUser } from './Wizard';
import { locale, tr } from '../i18n';

const tone: Record<TodoStatus, 'warn' | 'ok' | 'neutral'> = { Open: 'warn', Done: 'ok', Cancelled: 'neutral' };

/** ToDo-Liste (V2): Personen, die einen Key bekommen sollen; kommt aus dem Ticketsystem (API) oder wird von Hand angelegt. */
export default function TodosPage() {
  const { tenantId, tenant } = useTenant();
  const can = useCan();
  const toast = useToast();
  const navigate = useNavigate();
  const [status, setStatus] = useState<'open' | 'all'>('open');
  const { data, error, loading, reload } = useApi<Todo[]>(`/api/todos?status=${status === 'open' ? 'Open' : 'all'}${tenantId ? '&tenantId=' + tenantId : ''}`, [tenantId, status]);
  const [selected, setSelected] = useState<string[]>([]);
  const [modal, setModal] = useState<'add' | 'batch' | null>(null);
  const open = data?.filter((t) => t.status === 'Open') ?? [];

  const act = async (t: Todo, action: 'done' | 'cancel') => {
    try {
      await api.post(`/api/todos/${t.id}/${action}`, action === 'cancel' ? { reason: tr("In der Plattform verworfen") } : {});
      toast.ok(action === 'done' ? tr("Als erledigt markiert.") : tr("ToDo verworfen."));
      reload();
    } catch (e) { toast.error((e as Error).message); }
  };

  return (
    <>
      <PageHeader
        title={tr("ToDo-Liste")}
        subtitle={tr("Personen, die einen Key bekommen sollen. Ein Ticketsystem kann sie über die API eintragen; nach der Registrierung wird das ToDo automatisch erledigt.")}
        actions={tenantId && can('RegisterKeys') && <>
          {selected.length > 0 && <button className="primary" onClick={() => setModal('batch')}>{tr("{length} als Stapel registrieren", { length: selected.length })}</button>}
          <button onClick={() => setModal('add')}>{tr("Person eintragen")}</button>
        </>}
      />
      <div className="toolbar">
        <div className="tabs" role="tablist">
          <button className={status === 'open' ? 'selected' : ''} onClick={() => setStatus('open')}>{tr("Offen")}</button>
          <button className={status === 'all' ? 'selected' : ''} onClick={() => setStatus('all')}>{tr("Alle")}</button>
        </div>
      </div>
      <ErrorBox error={error} />
      {loading && !data && <Spinner />}
      {data && data.length === 0 && <Empty>{status === 'open' ? tr("Keine offenen ToDos.") : tr("Noch keine ToDos.")} {tr("Ein Ticketsystem trägt Personen per")} <code>{tr("POST /api/v1/todos")}</code> {tr("ein (API-Schlüssel mit dem Recht „ToDo-Liste schreiben“,")} <Link to="/platform">{tr("Globale Einstellungen")}</Link>).</Empty>}
      {data && data.length > 0 && (
        <div className="card table-wrap" style={{ padding: 0 }}>
          <table>
            <thead>
              <tr>
                {tenantId && can('RegisterKeys') && <th style={{ width: 32 }}><input type="checkbox" aria-label={tr("Alle offenen auswählen")} checked={open.length > 0 && selected.length === open.length} onChange={(e) => setSelected(e.target.checked ? open.map((t) => t.id) : [])} /></th>}
                <th>{tr("Person")}</th>{!tenantId && <th>{tr("Tenant")}</th>}<th>{tr("Ticket")}</th><th>{tr("Profil · Fällig")}</th><th>{tr("Herkunft")}</th><th>{tr("Status")}</th><th />
              </tr>
            </thead>
            <tbody>
              {data.map((t) => (
                <tr key={t.id}>
                  {tenantId && can('RegisterKeys') && <td>{t.status === 'Open' && <input type="checkbox" aria-label={tr("{userPrincipalName} auswählen", { userPrincipalName: t.userPrincipalName })} checked={selected.includes(t.id)} onChange={(e) => setSelected((s) => e.target.checked ? [...s, t.id] : s.filter((x) => x !== t.id))} />}</td>}
                  <td>{t.userDisplayName ?? t.userPrincipalName}<div className="small muted">{t.userPrincipalName}{t.department ? ' · ' + t.department : ''}</div>{t.note && <div className="small">{t.note}</div>}</td>
                  {!tenantId && <td>{t.tenantName}</td>}
                  <td>{t.ticketUrl ? <a href={t.ticketUrl} target="_blank" rel="noreferrer">{t.ticketId ?? tr("Ticket")}</a> : t.ticketId ?? '–'}</td>
                  <td className="small">{t.profileName ?? tr("Standard")}{t.dueDate && <div>{tr("fällig {n}", { n: new Date(t.dueDate).toLocaleDateString(locale()) })}</div>}</td>
                  <td className="small">{t.source}<div className="muted">{fmtDate(t.createdAt)}</div></td>
                  <td>
                    <Badge tone={tone[t.status]}>{todoLabels[t.status]}</Badge>
                    {t.completedAt && <div className="small muted">{t.completedByName}, {fmtDate(t.completedAt)}</div>}
                    {t.keyId && <div className="small"><Link to={'/keys/' + t.keyId}>{tr("Key ansehen")}</Link></div>}
                  </td>
                  <td className="actions">
                    {t.status === 'Open' && can('RegisterKeys') && <>
                      <button className="primary small" onClick={() => navigate(`/register?tenantId=${t.tenantId}&q=${encodeURIComponent(t.userPrincipalName)}`)}>{tr("Registrieren")}</button>
                      <button className="small" onClick={() => act(t, 'done')}>{tr("Erledigt")}</button>
                      <button className="small ghost" onClick={() => act(t, 'cancel')}>{tr("Verwerfen")}</button>
                    </>}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
      {!tenantId && open.length > 0 && <Info>{tr("Zum Abarbeiten als Stapel oben einen Tenant wählen.")}</Info>}
      {modal === 'add' && tenantId && <AddTodoModal tenantId={tenantId} onClose={() => setModal(null)} onDone={() => { setModal(null); reload(); }} />}
      {modal === 'batch' && tenantId && <BatchModal tenantId={tenantId} tenantName={tenant?.displayName} todoIds={selected} onClose={() => setModal(null)} />}
    </>
  );
}

function AddTodoModal({ tenantId, onClose, onDone }: { tenantId: string; onClose: () => void; onDone: () => void }) {
  const [q, setQ] = useState('');
  const [user, setUser] = useState<EntraUser>();
  const [ticketId, setTicketId] = useState('');
  const [ticketUrl, setTicketUrl] = useState('');
  const [note, setNote] = useState('');
  const [dueDate, setDueDate] = useState('');
  const { results } = useLiveSearch<EntraUser>((term) => `/api/tenants/${tenantId}/users?q=${encodeURIComponent(term)}`, q);
  const { busy, error, run } = useAction();
  const toast = useToast();
  const go = () => run(async () => {
    const r = await api.post<{ created: boolean }>(`/api/tenants/${tenantId}/todos`, { entraUserId: user!.id, ticketId: ticketId || null, ticketUrl: ticketUrl || null, note: note || null, dueDate: dueDate || null });
    toast.ok(r.created ? tr("ToDo angelegt.") : tr("Für diese Person gibt es schon ein offenes ToDo."));
    onDone();
  });
  return (
    <Modal title={tr("Person eintragen")} onClose={onClose} footer={<><button onClick={onClose}>{tr("Abbrechen")}</button><button className="primary" disabled={!user || busy} onClick={go}>{tr("Eintragen")}</button></>}>
      {user ? (
        <p><strong>{user.displayName ?? user.userPrincipalName}</strong> <span className="muted">{user.userPrincipalName}</span> <button className="small ghost" onClick={() => setUser(undefined)}>{tr("ändern")}</button></p>
      ) : (
        <>
          <Field label={tr("Person")}><input type="search" autoFocus value={q} onChange={(e) => setQ(e.target.value)} placeholder={tr("Name oder UPN …")} /></Field>
          <div className="pick">
            {results?.slice(0, 6).map((u) => <button key={u.id} onClick={() => setUser(u)}><span><strong>{u.displayName ?? u.userPrincipalName}</strong><br /><span className="small muted">{u.userPrincipalName}</span></span></button>)}
          </div>
        </>
      )}
      <div className="grid cols-2">
        <Field label={tr("Ticket-Nummer")}><input value={ticketId} onChange={(e) => setTicketId(e.target.value)} /></Field>
        <Field label={tr("Fällig am")}><input type="date" value={dueDate} onChange={(e) => setDueDate(e.target.value)} /></Field>
      </div>
      <Field label={tr("Ticket-Link")}><input value={ticketUrl} onChange={(e) => setTicketUrl(e.target.value)} placeholder="https://…" /></Field>
      <Field label={tr("Notiz")}><input value={note} onChange={(e) => setNote(e.target.value)} /></Field>
      <ErrorBox error={error} />
    </Modal>
  );
}

function BatchModal({ tenantId, tenantName, todoIds, onClose }: { tenantId: string; tenantName?: string; todoIds: string[]; onClose: () => void }) {
  const { data: profiles } = useApi<Profile[]>('/api/profiles?tenantId=' + tenantId, [tenantId]);
  const [profileId, setProfileId] = useState('');
  const def = profileId || (profiles?.find((p) => p.isDefault && p.tenantId === tenantId) ?? profiles?.find((p) => p.isDefault) ?? profiles?.[0])?.id || '';
  const { busy, error, run } = useAction();
  const navigate = useNavigate();
  const go = () => run(async () => {
    const b = await api.post<{ id: string }>(`/api/tenants/${tenantId}/todos/batch`, { todoIds, defaultProfileId: def });
    navigate('/batches/' + b.id);
  });
  return (
    <Modal title={tr("Als Stapel registrieren")} onClose={onClose} footer={<><button onClick={onClose}>{tr("Abbrechen")}</button><button className="primary" disabled={!def || busy} onClick={go}>{tr("Stapel anlegen")}</button></>}>
      <p>{tr("{length} Person(en) aus der ToDo-Liste kommen in einen Stapel für {tenantName}. Danach Key für Key einstecken; jedes ToDo wird mit der Registrierung erledigt.", { length: todoIds.length, tenantName })}</p>
      <Field label={tr("Standardprofil")} hint={tr("Für Personen ohne Profil im ToDo und ohne Gruppenprofil")}>
        <select value={def} onChange={(e) => setProfileId(e.target.value)}>
          {profiles?.map((p) => <option key={p.id} value={p.id}>{p.name}</option>)}
        </select>
      </Field>
      <ErrorBox error={error} />
    </Modal>
  );
}
