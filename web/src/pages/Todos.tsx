import { useState } from 'react';
import { Link, useNavigate } from 'react-router';
import { api, fmtDate, todoLabels, type Profile, type Todo, type TodoStatus } from '../api';
import { useCan } from '../auth';
import { Badge, Empty, ErrorBox, Field, Info, Modal, PageHeader, Spinner, useAction, useApi, useLiveSearch } from '../ui';
import { useToast } from '../feedback';
import { useTenant } from '../tenant';
import type { EntraUser } from './Wizard';

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
      await api.post(`/api/todos/${t.id}/${action}`, action === 'cancel' ? { reason: 'In der Plattform verworfen' } : {});
      toast.ok(action === 'done' ? 'Als erledigt markiert.' : 'ToDo verworfen.');
      reload();
    } catch (e) { toast.error((e as Error).message); }
  };

  return (
    <>
      <PageHeader
        title="ToDo-Liste"
        subtitle="Personen, die einen Key bekommen sollen. Ein Ticketsystem kann sie über die API eintragen; nach der Registrierung wird das ToDo automatisch erledigt."
        actions={tenantId && can('RegisterKeys') && <>
          {selected.length > 0 && <button className="primary" onClick={() => setModal('batch')}>{selected.length} als Stapel registrieren</button>}
          <button onClick={() => setModal('add')}>Person eintragen</button>
        </>}
      />
      <div className="toolbar">
        <div className="tabs" role="tablist">
          <button className={status === 'open' ? 'selected' : ''} onClick={() => setStatus('open')}>Offen</button>
          <button className={status === 'all' ? 'selected' : ''} onClick={() => setStatus('all')}>Alle</button>
        </div>
      </div>
      <ErrorBox error={error} />
      {loading && !data && <Spinner />}
      {data && data.length === 0 && <Empty>{status === 'open' ? 'Keine offenen ToDos.' : 'Noch keine ToDos.'} Ein Ticketsystem trägt Personen per <code>POST /api/v1/todos</code> ein (API-Schlüssel mit ToDo-Schreibrecht, <Link to="/platform">Globale Einstellungen</Link>).</Empty>}
      {data && data.length > 0 && (
        <div className="card table-wrap" style={{ padding: 0 }}>
          <table>
            <thead>
              <tr>
                {tenantId && can('RegisterKeys') && <th style={{ width: 32 }}><input type="checkbox" aria-label="Alle offenen auswählen" checked={open.length > 0 && selected.length === open.length} onChange={(e) => setSelected(e.target.checked ? open.map((t) => t.id) : [])} /></th>}
                <th>Person</th>{!tenantId && <th>Tenant</th>}<th>Ticket</th><th>Profil · Fällig</th><th>Herkunft</th><th>Status</th><th />
              </tr>
            </thead>
            <tbody>
              {data.map((t) => (
                <tr key={t.id}>
                  {tenantId && can('RegisterKeys') && <td>{t.status === 'Open' && <input type="checkbox" aria-label={`${t.userPrincipalName} auswählen`} checked={selected.includes(t.id)} onChange={(e) => setSelected((s) => e.target.checked ? [...s, t.id] : s.filter((x) => x !== t.id))} />}</td>}
                  <td>{t.userDisplayName ?? t.userPrincipalName}<div className="small muted">{t.userPrincipalName}{t.department ? ' · ' + t.department : ''}</div>{t.note && <div className="small">{t.note}</div>}</td>
                  {!tenantId && <td>{t.tenantName}</td>}
                  <td>{t.ticketUrl ? <a href={t.ticketUrl} target="_blank" rel="noreferrer">{t.ticketId ?? 'Ticket'}</a> : t.ticketId ?? '–'}</td>
                  <td className="small">{t.profileName ?? 'Standard'}{t.dueDate && <div>fällig {new Date(t.dueDate).toLocaleDateString('de-DE')}</div>}</td>
                  <td className="small">{t.source}<div className="muted">{fmtDate(t.createdAt)}</div></td>
                  <td>
                    <Badge tone={tone[t.status]}>{todoLabels[t.status]}</Badge>
                    {t.completedAt && <div className="small muted">{t.completedByName}, {fmtDate(t.completedAt)}</div>}
                    {t.keyId && <div className="small"><Link to={'/keys/' + t.keyId}>Key ansehen</Link></div>}
                  </td>
                  <td className="actions">
                    {t.status === 'Open' && can('RegisterKeys') && <>
                      <button className="primary small" onClick={() => navigate(`/register?tenantId=${t.tenantId}&q=${encodeURIComponent(t.userPrincipalName)}`)}>Registrieren</button>
                      <button className="small" onClick={() => act(t, 'done')}>Erledigt</button>
                      <button className="small ghost" onClick={() => act(t, 'cancel')}>Verwerfen</button>
                    </>}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
      {!tenantId && open.length > 0 && <Info>Zum Abarbeiten als Stapel oben einen Tenant wählen.</Info>}
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
    toast.ok(r.created ? 'ToDo angelegt.' : 'Für diese Person gibt es schon ein offenes ToDo.');
    onDone();
  });
  return (
    <Modal title="Person eintragen" onClose={onClose} footer={<><button onClick={onClose}>Abbrechen</button><button className="primary" disabled={!user || busy} onClick={go}>Eintragen</button></>}>
      {user ? (
        <p><strong>{user.displayName ?? user.userPrincipalName}</strong> <span className="muted">{user.userPrincipalName}</span> <button className="small ghost" onClick={() => setUser(undefined)}>ändern</button></p>
      ) : (
        <>
          <Field label="Person"><input type="search" autoFocus value={q} onChange={(e) => setQ(e.target.value)} placeholder="Name oder UPN …" /></Field>
          <div className="pick">
            {results?.slice(0, 6).map((u) => <button key={u.id} onClick={() => setUser(u)}><span><strong>{u.displayName ?? u.userPrincipalName}</strong><br /><span className="small muted">{u.userPrincipalName}</span></span></button>)}
          </div>
        </>
      )}
      <div className="grid cols-2">
        <Field label="Ticket-Nummer"><input value={ticketId} onChange={(e) => setTicketId(e.target.value)} /></Field>
        <Field label="Fällig am"><input type="date" value={dueDate} onChange={(e) => setDueDate(e.target.value)} /></Field>
      </div>
      <Field label="Ticket-Link"><input value={ticketUrl} onChange={(e) => setTicketUrl(e.target.value)} placeholder="https://…" /></Field>
      <Field label="Notiz"><input value={note} onChange={(e) => setNote(e.target.value)} /></Field>
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
    <Modal title="Als Stapel registrieren" onClose={onClose} footer={<><button onClick={onClose}>Abbrechen</button><button className="primary" disabled={!def || busy} onClick={go}>Stapel anlegen</button></>}>
      <p>{todoIds.length} Person(en) aus der ToDo-Liste kommen in einen Stapel für {tenantName}. Danach Key für Key einstecken; jedes ToDo wird mit der Registrierung erledigt.</p>
      <Field label="Standardprofil" hint="Für Personen ohne Profil im ToDo und ohne Gruppenprofil">
        <select value={def} onChange={(e) => setProfileId(e.target.value)}>
          {profiles?.map((p) => <option key={p.id} value={p.id}>{p.name}</option>)}
        </select>
      </Field>
      <ErrorBox error={error} />
    </Modal>
  );
}
