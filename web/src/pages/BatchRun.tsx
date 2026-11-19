import { useCallback, useEffect, useRef, useState } from 'react';
import { Link, useParams } from 'react-router';
import { api, ApiError, batchItemLabels, fmtDate, type AgentInfo, type Batch, type BatchItem, type BatchItemStatus, type Job, type KeyInfo, type Profile } from '../api';
import { Badge, Card, download, ErrorBox, Field, Info, Modal, PageHeader, Spinner, useAction, useApi, usePolling } from '../ui';
import { useTenant } from '../tenant';
import { JobProgress } from './KeyDetail';
import { canStartPreview, PreviewDetails, type Preview } from './Wizard';
import { BatchProgress, BatchStatusBadge, PersonSearch } from './Batches';
import { useConfirm } from '../feedback';

type NextResult =
  | { state: 'ready'; item: BatchItem; profileId: string; preview: Preview; key: KeyInfo }
  | { state: 'already_done'; item: BatchItem; key: KeyInfo }
  | { state: 'finished'; key: KeyInfo }
  | { state: 'no_match'; key: KeyInfo; message: string };

/**
 * Ablauf am Agent:
 *  waiting  – wartet auf einen (neuen) Key; sobald einer steckt, fragt der Server, für wen er ist
 *  confirm  – Prüfergebnis braucht eine Entscheidung (Profilabweichung, Reset, Key schon benutzt …)
 *  running  – Registrierung läuft
 *  blocked  – Key passt zu niemandem oder Fehler; wartet, bis ein anderer Key steckt
 */
type Phase = 'paused' | 'waiting' | 'confirm' | 'running' | 'blocked';

const itemTone: Record<BatchItemStatus, 'neutral' | 'info' | 'ok' | 'error' | 'warn'> = { Pending: 'neutral', Running: 'info', Done: 'ok', Failed: 'error', Skipped: 'warn' };

/** Auto-Start nur, wenn es nichts zu entscheiden gibt: Werkszustand, Profil passt, Person hat noch keinen Key. */
const canAutoStart = (p: Preview) =>
  p.violations.length === 0 && !p.resetPlanned && !p.key.pinIsSet && !p.inventoryKey?.activeRegistrations.length && p.userActiveKeys === 0;

export default function BatchRunPage() {
  const { id } = useParams();
  const { setTenantId } = useTenant();
  const { data: batch, error: loadError, reload, setData: setBatch } = useApi<Batch>('/api/batches/' + id, [id]);
  useEffect(() => { if (batch?.tenantId) setTenantId(batch.tenantId); }, [batch?.tenantId, setTenantId]);
  const { data: profiles } = useApi<Profile[]>(batch ? '/api/profiles?tenantId=' + batch.tenantId : null, [batch?.tenantId]);
  const { data: agents, reload: reloadAgents } = useApi<AgentInfo[]>('/api/agents');
  usePolling(reloadAgents, 1500);
  usePolling(reload, 5000, !!batch && batch.status === 'Open');

  const [agentId, setAgentId] = useState('');
  const online = agents?.filter((a) => a.online) ?? [];
  const agent = online.find((a) => a.id === agentId) ?? online[0];
  useEffect(() => { if (agent && agent.id !== agentId) setAgentId(agent.id); }, [agent, agentId]);

  const [phase, setPhase] = useState<Phase>('paused');
  const [auto, setAuto] = useState(true);
  const [current, setCurrent] = useState<Extract<NextResult, { state: 'ready' }>>();
  const [message, setMessage] = useState<{ tone: 'ok' | 'warn' | 'error' | 'info'; text: string; item?: BatchItem }>();
  const [overrides, setOverrides] = useState<string[]>([]);
  const [performReset, setPerformReset] = useState(false);
  const [job, setJob] = useState<Job>();
  const [forcedItem, setForcedItem] = useState<string>();
  const [adding, setAdding] = useState(false);
  const { busy, error, run, setError } = useAction();
  const confirm = useConfirm();
  // Serial of the key we already acted on; a new check starts only for another key or after re-plugging.
  const handled = useRef<number | null | undefined>(undefined);
  const checking = useRef(false);

  const pluggedSerial = agent?.key ? agent.key.serialNumber ?? null : undefined;
  useEffect(() => {
    if (pluggedSerial === undefined) handled.current = undefined;
  }, [pluggedSerial]);

  const register = useCallback(async (next: Extract<NextResult, { state: 'ready' }>, ov: string[], reset: boolean) => {
    const j = await api.post<Job>(`/api/batches/${id}/items/${next.item.id}/register`, {
      agentId: agent!.id, overrides: ov, performReset: reset, expectedSerialNumber: next.preview.key.serialNumber ?? null,
    });
    setJob(j);
    setPhase('running');
  }, [id, agent]);

  const check = useCallback(async (itemId?: string) => {
    if (!agent?.key || checking.current) return;
    checking.current = true;
    handled.current = agent.key.serialNumber ?? null;
    setError(undefined);
    setMessage(undefined);
    try {
      const next = await api.post<NextResult>(`/api/batches/${id}/next`, { agentId: agent.id, itemId: itemId ?? null });
      setForcedItem(undefined);
      if (next.state === 'ready') {
        setCurrent(next);
        setOverrides([]);
        setPerformReset(next.preview.resetPlanned);
        if (auto && canAutoStart(next.preview)) await register(next, [], false);
        else setPhase('confirm');
      } else if (next.state === 'already_done') {
        setMessage({ tone: 'ok', text: `Dieser Key ist bereits für ${next.item.userDisplayName ?? next.item.userPrincipalName} registriert. Bitte abziehen und den nächsten Key einstecken.`, item: next.item });
        setPhase('waiting');
      } else if (next.state === 'finished') {
        setMessage({ tone: 'ok', text: 'Alle Personen im Stapel sind erledigt.' });
        setPhase('paused');
      } else {
        setMessage({ tone: 'warn', text: next.message });
        setPhase('blocked');
      }
    } catch (e) {
      setMessage({ tone: 'error', text: e instanceof ApiError || e instanceof Error ? e.message : String(e) });
      setPhase('blocked');
    } finally {
      checking.current = false;
    }
  }, [agent, id, auto, register, setError]);

  // Waiting: as soon as a key we have not handled yet is plugged in, find its person.
  useEffect(() => {
    if ((phase === 'waiting' || phase === 'blocked') && pluggedSerial !== undefined && pluggedSerial !== handled.current && !agent?.busy) void check(forcedItem);
  }, [phase, pluggedSerial, agent?.busy, check, forcedItem]);

  // Ask the tray app to show "insert key" while we wait.
  useEffect(() => {
    if (phase === 'waiting' && agent && !agent.key) void api.post(`/api/agents/${agent.id}/request-key`, {}).catch(() => undefined);
  }, [phase, agent?.id, agent?.key]); // eslint-disable-line react-hooks/exhaustive-deps

  usePolling(async () => {
    if (!job) return;
    const j = await api.get<Job>('/api/jobs/' + job.id);
    setJob(j);
    if (j.state === 'Running') return;
    const fresh = await api.get<Batch>('/api/batches/' + id);
    setBatch(fresh);
    const item = fresh.items.find((i) => i.id === current?.item.id);
    if (j.state === 'Succeeded') {
      setMessage({ tone: 'ok', text: `${item?.userDisplayName ?? item?.userPrincipalName} ist registriert. Key abziehen, mit dem PIN-Brief zur Seite legen und den nächsten Key einstecken.`, item });
      setJob(undefined);
      setCurrent(undefined);
      setPhase(fresh.status === 'Open' ? 'waiting' : 'paused');
    } else {
      setPhase('confirm');
    }
  }, 1000, phase === 'running' && job?.state === 'Running');

  const itemAction = (item: BatchItem, action: 'skip' | 'requeue' | 'remove') => run(async () => {
    const url = `/api/batches/${id}/items/${item.id}` + (action === 'remove' ? '' : '/' + action);
    setBatch(action === 'remove' ? await api.del<Batch>(url) : await api.post<Batch>(url));
    if (current?.item.id === item.id && action === 'skip') { setCurrent(undefined); setJob(undefined); setPhase('waiting'); handled.current = undefined; }
  });

  const patchItem = (item: BatchItem, body: object) => run(async () => setBatch(await api.patch<Batch>(`/api/batches/${id}/items/${item.id}`, body)));

  const setStatus = (status: 'Completed' | 'Cancelled' | 'Open') => run(async () => {
    setBatch(await api.post<Batch>(`/api/batches/${id}/status`, { status }));
    setPhase('paused');
  });

  const registerNow = (item: BatchItem) => {
    setForcedItem(item.id);
    handled.current = undefined;
    setCurrent(undefined);
    setJob(undefined);
    setMessage({ tone: 'info', text: `Als Nächstes: ${item.userDisplayName ?? item.userPrincipalName}. Bitte den Key einstecken.` });
    setPhase('waiting');
  };

  if (loadError) return <ErrorBox error={loadError} />;
  if (!batch) return <Spinner label="Stapel wird geladen …" />;

  const profileName = (pid?: string | null) => profiles?.find((p) => p.id === (pid ?? batch.defaultProfileId))?.name ?? '–';
  const nextUp = batch.items.find((i) => i.id === forcedItem) ?? batch.items.find((i) => i.status === 'Pending' && !i.expectedSerialNumber);
  const lettersReady = batch.items.filter((i) => i.pinLetterAvailable).length;
  const open = batch.status === 'Open';
  const running = phase === 'running' || batch.items.some((i) => i.status === 'Running');

  return (
    <>
      <PageHeader
        title={batch.name}
        subtitle={<>{batch.source ? batch.source + ' · ' : ''}angelegt {fmtDate(batch.createdAt)} von {batch.createdByName} · Standardprofil {profileName(batch.defaultProfileId)} · <BatchStatusBadge status={batch.status} /></>}
        actions={<>
          <Link className="btn" to="/batches">Alle Stapel</Link>
          <button disabled={lettersReady === 0} title={lettersReady === 0 ? 'Keine PIN-Briefe mehr im Speicher' : undefined}
            onClick={() => download(`/api/batches/${id}/pin-letters`)}>Alle PIN-Briefe ({lettersReady})</button>
        </>}
      />
      <Card>
        <BatchProgress counts={batch.counts} />
        <p className="small muted" style={{ marginBottom: 0 }}>
          PIN-Briefe liegen nur im Arbeitsspeicher, höchstens 4 Stunden und nur für den Admin, der registriert hat. Bitte zwischendurch oder am Ende alle zusammen herunterladen.
        </p>
      </Card>

      {open && (
        <Card title="Keys registrieren">
          {agents && online.length === 0 && <Info tone="warn">Auf Ihrem PC ist kein Agent verbunden. Starten Sie die Tray-App oder <Link to="/agents">koppeln Sie einen Agent</Link>.</Info>}
          {online.length > 1 && (
            <Field label="Agent">
              <select value={agent?.id} onChange={(e) => setAgentId(e.target.value)} disabled={phase !== 'paused'}>
                {online.map((a) => <option key={a.id} value={a.id}>{a.machineName}</option>)}
              </select>
            </Field>
          )}
          <label className="check" style={{ marginBottom: 12 }}>
            <input type="checkbox" checked={auto} onChange={(e) => setAuto(e.target.checked)} />
            Automatisch starten, wenn ein neuer Key ohne Rückfrage passt (Werkszustand, Profil erfüllt, Person hat noch keinen Key)
          </label>

          {phase === 'paused' && (
            <div className="actions">
              <button className="primary" disabled={!agent || batch.counts.pending + batch.counts.failed === 0}
                onClick={() => { handled.current = undefined; setMessage(undefined); setPhase('waiting'); }}>
                Abarbeiten starten
              </button>
              {batch.counts.pending + batch.counts.failed === 0 && <span className="muted small">Keine offenen Personen.</span>}
            </div>
          )}

          {message && (
            <Info tone={message.tone}>
              {message.text}
              {message.item?.pinLetterAvailable && message.item.pinLetterId && (
                <> <button className="link" onClick={() => download('/api/pin-letters/' + message.item!.pinLetterId)}>PIN-Brief einzeln öffnen</button></>
              )}
            </Info>
          )}

          {(phase === 'waiting' || phase === 'blocked') && agent && (
            agent.key && pluggedSerial === handled.current ? (
              <div className="waiting-key"><Spinner /><div><strong>Bitte den Key abziehen und den nächsten einstecken.</strong></div></div>
            ) : !agent.key ? (
              <div className="waiting-key">
                <Spinner />
                <div>
                  <strong>Bitte den nächsten YubiKey an {agent.machineName} einstecken.</strong>
                  <div className="small muted">
                    {nextUp ? <>Ein Key ohne feste Zuordnung geht an <strong>{nextUp.userDisplayName ?? nextUp.userPrincipalName}</strong>{nextUp.expectedSerialNumber ? ` (Key ${nextUp.expectedSerialNumber})` : ''}.</> : 'Offen sind nur noch Personen mit fester Seriennummer.'}
                    {' '}Bitte immer nur einen Key einstecken.
                  </div>
                </div>
              </div>
            ) : <Spinner label="Key wird geprüft …" />
          )}

          {phase === 'confirm' && current && (
            <div>
              <h3 style={{ marginTop: 0 }}>Für {current.item.userDisplayName ?? current.item.userPrincipalName} · Profil {profileName(current.profileId)}</h3>
              {job?.state === 'Failed' && <ErrorBox error={job.error ?? 'Registrierung fehlgeschlagen.'} />}
              <PreviewDetails preview={current.preview} overrides={overrides} setOverrides={setOverrides} performReset={performReset} setPerformReset={setPerformReset} />
              <div className="actions">
                <button className="primary" disabled={busy || !canStartPreview(current.preview, overrides, performReset)}
                  onClick={() => run(() => register(current, overrides, performReset))}>Registrieren</button>
                <button disabled={busy} onClick={() => { setJob(undefined); void check(current.item.id); }}>Erneut prüfen</button>
                <button disabled={busy} onClick={() => itemAction(current.item, 'skip')}>Person überspringen</button>
              </div>
            </div>
          )}

          {phase === 'running' && job && <JobProgress job={job} />}

          <ErrorBox error={error} />
          {phase !== 'paused' && phase !== 'running' && (
            <div className="actions" style={{ marginTop: 12 }}>
              <button onClick={() => { setPhase('paused'); setCurrent(undefined); setMessage(undefined); setForcedItem(undefined); }}>Pause</button>
            </div>
          )}
        </Card>
      )}

      <Card
        title={`Personen (${batch.items.length})`}
        actions={<>
          {open && <button onClick={() => setAdding(true)}>Personen hinzufügen</button>}
          {open && !running && <button onClick={() => setStatus('Completed')}>Stapel abschließen</button>}
          {open && !running && <button className="danger" onClick={async () => { if (await confirm({ title: 'Stapel abbrechen?', message: 'Bereits registrierte Keys bleiben registriert.', confirmLabel: 'Stapel abbrechen', danger: true })) void setStatus('Cancelled'); }}>Abbrechen</button>}
          {batch.status !== 'Open' && <button onClick={() => setStatus('Open')}>Wieder öffnen</button>}
        </>}
      >
        <div className="table-wrap">
          <table>
            <thead><tr><th>#</th><th>Person</th><th>Profil</th><th>Key</th><th>Status</th><th /></tr></thead>
            <tbody>
              {batch.items.map((i, n) => (
                <tr key={i.id} className={current?.item.id === i.id ? 'selected-row' : ''}>
                  <td className="muted">{n + 1}</td>
                  <td><strong>{i.userDisplayName ?? i.userPrincipalName}</strong><br /><span className="small muted">{i.userPrincipalName}{i.department ? ' · ' + i.department : ''}</span></td>
                  <td>
                    {open && (i.status === 'Pending' || i.status === 'Failed' || i.status === 'Skipped') ? (
                      <select value={i.profileId ?? ''} aria-label="Profil" onChange={(e) => patchItem(i, e.target.value ? { profileId: e.target.value } : { clearProfile: true })}>
                        <option value="">Stapel-Standard: {profileName(null)}</option>
                        {profiles?.map((p) => <option key={p.id} value={p.id}>{p.name}</option>)}
                      </select>
                    ) : profileName(i.profileId)}
                    {i.profileId && i.profileGroupName && <div className="small muted">{i.profileGroupName === 'CSV' ? 'aus CSV' : 'Gruppe ' + i.profileGroupName}</div>}
                  </td>
                  <td>
                    {i.serialNumber ? <span className="mono">{i.serialNumber}</span> : i.expectedSerialNumber ? <span title="Fester Key aus der Liste"><span className="mono">{i.expectedSerialNumber}</span> <Badge>fest</Badge></span> : <span className="muted">beliebig</span>}
                    {open && i.status === 'Pending' && !i.expectedSerialNumber && <SerialEdit onSave={(s) => patchItem(i, { expectedSerialNumber: s })} />}
                    {open && i.status === 'Pending' && i.expectedSerialNumber && <button className="link small" onClick={() => patchItem(i, { clearSerial: true })}>lösen</button>}
                  </td>
                  <td>
                    <Badge tone={itemTone[i.status]}>{batchItemLabels[i.status]}</Badge>
                    {i.completedAt && i.status === 'Done' && <div className="small muted">{fmtDate(i.completedAt)}{i.completedByName ? ' · ' + i.completedByName : ''}</div>}
                    {i.error && i.status === 'Failed' && <div className="small tone-warn" style={{ maxWidth: 360 }}>{i.error}</div>}
                    {i.note && <div className="small muted" style={{ maxWidth: 360 }}>{i.note}</div>}
                  </td>
                  <td><div className="actions" style={{ flexWrap: 'wrap', justifyContent: 'flex-end', margin: 0 }}>
                    {i.status === 'Done' && i.keyId && <Link className="small" to={'/keys/' + i.keyId}>Key</Link>}
                    {i.status === 'Done' && i.pinLetterAvailable && <button className="link small" onClick={() => download('/api/pin-letters/' + i.pinLetterId)}>PIN-Brief</button>}
                    {open && (i.status === 'Pending' || i.status === 'Failed') && phase !== 'running' && <button className="link small" disabled={!agent} onClick={() => registerNow(i)}>Als Nächstes</button>}
                    {open && (i.status === 'Pending' || i.status === 'Failed') && <button className="link small" onClick={() => itemAction(i, 'skip')}>Überspringen</button>}
                    {open && (i.status === 'Skipped' || i.status === 'Failed') && <button className="link small" onClick={() => itemAction(i, 'requeue')}>Wieder einreihen</button>}
                    {open && (i.status === 'Pending' || i.status === 'Skipped') && <button className="link small" onClick={() => itemAction(i, 'remove')}>Entfernen</button>}
                  </div></td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      </Card>

      {adding && (
        <AddPeopleModal batch={batch} onClose={() => setAdding(false)} onDone={(b) => setBatch(b)} />
      )}
    </>
  );
}

function SerialEdit({ onSave }: { onSave: (serial: number) => void }) {
  const [editing, setEditing] = useState(false);
  const [value, setValue] = useState('');
  if (!editing) return <> <button className="link small" onClick={() => setEditing(true)}>festlegen</button></>;
  return (
    <form style={{ display: 'inline-flex', gap: 4, marginLeft: 6 }} onSubmit={(e) => { e.preventDefault(); if (/^\d+$/.test(value.trim())) { onSave(Number(value.trim())); setEditing(false); } }}>
      <input className="mono" style={{ width: 110 }} inputMode="numeric" autoFocus value={value} onChange={(e) => setValue(e.target.value)} placeholder="Seriennummer" />
      <button type="submit" className="small">OK</button>
    </form>
  );
}

function AddPeopleModal({ batch, onClose, onDone }: { batch: Batch; onClose: () => void; onDone: (b: Batch) => void }) {
  const { busy, error, run } = useAction();
  const [added, setAdded] = useState<string[]>([]);
  const add = (userId: string) => run(async () => {
    const r = await api.post<{ candidates: { entraUserId: string; userPrincipalName: string; displayName?: string; department?: string; profileId?: string | null; profileGroupName?: string | null }[] }>(
      `/api/tenants/${batch.tenantId}/batches/candidates`, { userIds: [userId] });
    const c = r.candidates[0];
    if (!c) return;
    onDone(await api.post<Batch>(`/api/batches/${batch.id}/items`, {
      items: [{ entraUserId: c.entraUserId, userPrincipalName: c.userPrincipalName, userDisplayName: c.displayName, department: c.department, profileId: c.profileId ?? null, profileGroupName: c.profileGroupName }],
    }));
    setAdded((a) => [...a, c.displayName ?? c.userPrincipalName]);
  });
  return (
    <Modal title="Personen hinzufügen" onClose={onClose} footer={<button onClick={onClose}>Fertig</button>}>
      <p className="small muted">Neue Personen kommen ans Ende der Liste.</p>
      <PersonSearch tenantId={batch.tenantId} exclude={batch.items.map((i) => i.entraUserId)} onPick={(uid) => void add(uid)} />
      {busy && <Spinner />}
      <ErrorBox error={error} />
      {added.length > 0 && <Info tone="ok">Hinzugefügt: {added.join(', ')}</Info>}
    </Modal>
  );
}
