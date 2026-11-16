import { useState } from 'react';
import { Link, useParams } from 'react-router';
import { api, fmtDate, lockReasons, type AgentInfo, type AuditEntry, type CustomField, type Job, type YubiKey } from '../api';
import { useCan } from '../auth';
import { Badge, Card, ErrorBox, Field, Info, Modal, PageHeader, Spinner, StatusBadge, useAction, useApi, usePolling } from '../ui';
import { CustomFieldInput } from './Keys';

const sourceLabels: Record<string, string> = {
  Platform: 'Über die Plattform registriert', EntraSync: 'Aus Entra übernommen (selbst registriert)', StockIntake: 'Wareneingang', Import: 'CSV-Import',
};

export default function KeyDetailPage() {
  const { id } = useParams();
  const can = useCan();
  const { data: key, error, reload } = useApi<YubiKey>('/api/keys/' + id, [id]);
  const { data: fields } = useApi<CustomField[]>(key ? `/api/tenants/${key.tenantId}/fields` : null, [key?.tenantId]);
  const { data: history } = useApi<AuditEntry[]>(can('ReadAudit') ? `/api/audit?keyId=${id}&take=50` : null, [id, key?.updatedAt]);
  const [modal, setModal] = useState<'lock' | 'retire' | 'reset' | 'edit' | null>(null);

  if (error) return <ErrorBox error={error} />;
  if (!key) return <Spinner label="Wird geladen …" />;
  const active = key.registrations.filter((r) => !r.removedAt);

  return (
    <>
      <PageHeader
        title={key.serialNumber ? `YubiKey ${key.serialNumber}` : 'YubiKey ohne Seriennummer'}
        subtitle={<><Link to="/keys">Inventar</Link> · {key.tenantName}</>}
        actions={<>
          {can('ManageInventory') && <button onClick={() => setModal('edit')}>Bearbeiten</button>}
          {can('LockKeys') && key.nextStates.includes('Locked') && <button className="danger" onClick={() => setModal('lock')}>Sperren</button>}
          {can('ResetKeys') && key.nextStates.includes('InStock') && key.serialNumber && <button onClick={() => setModal('reset')}>Zurücksetzen und ins Lager</button>}
          {can('ManageInventory') && key.nextStates.includes('Retired') && <button className="danger" onClick={() => setModal('retire')}>Ausmustern</button>}
        </>}
      />
      {key.needsDetails && key.status !== 'Retired' && (
        <Info>Dieser Key wurde vom Nutzer selbst registriert. Seriennummer und Firmware fehlen; das Nacherfassen per Einstecken folgt in Version 1.1.</Info>
      )}
      {key.advisories.length > 0 && (
        <Info tone="warn">
          {key.advisories.map((a) => <div key={a.id}><strong>{a.id}</strong>: {a.title} <a href={a.url} target="_blank" rel="noreferrer">Details</a></div>)}
        </Info>
      )}
      <div className="grid cols-2">
        <Card title="Key">
          <table>
            <tbody>
              <tr><th>Status</th><td><StatusBadge status={key.status} /> {key.lockReason && <Badge>{lockReasons[key.lockReason] ?? key.lockReason}</Badge>} <span className="small muted">seit {fmtDate(key.statusChangedAt)}</span>{key.statusNote && <div className="small">{key.statusNote}</div>}</td></tr>
              <tr><th>Seriennummer</th><td className="mono">{key.serialNumber ?? '–'}</td></tr>
              <tr><th>Modell</th><td>{key.model ?? '–'} {key.isFips && <Badge tone="info">FIPS</Badge>}</td></tr>
              <tr><th>Firmware</th><td className="mono">{key.firmwareVersion ?? '–'}</td></tr>
              <tr><th>AAGUID</th><td className="mono small">{key.aaGuid ?? '–'}</td></tr>
              <tr><th>PIN-Wechsel erzwungen</th><td>{key.pinChangeForced == null ? '–' : key.pinChangeForced ? 'Ja' : 'Nein'}</td></tr>
              <tr><th>Herkunft</th><td>{sourceLabels[key.source] ?? key.source}</td></tr>
              <tr><th>Erfasst</th><td>{fmtDate(key.createdAt)}</td></tr>
            </tbody>
          </table>
        </Card>
        <Card title="Zusatzangaben">
          <table>
            <tbody>
              <tr><th>Tags</th><td>{key.tags.length ? key.tags.map((t) => <span className="tag" key={t}>{t}</span>) : '–'}</td></tr>
              {fields?.map((f) => <tr key={f.key}><th>{f.label}</th><td>{formatField(f, key.customFields[f.key])}</td></tr>)}
              <tr><th>Notiz</th><td style={{ whiteSpace: 'pre-wrap' }}>{key.notes || '–'}</td></tr>
            </tbody>
          </table>
        </Card>
      </div>
      <Card title="Registrierungen" className="mt">
        {key.registrations.length === 0 ? <p className="muted">Keine Registrierung.</p> : (
          <table>
            <thead><tr><th>Person</th><th>Anzeigename in Entra</th><th>Registriert</th><th>Durch</th><th>Status</th></tr></thead>
            <tbody>
              {key.registrations.map((r) => (
                <tr key={r.id}>
                  <td>{r.userDisplayName ?? r.userPrincipalName}<div className="small muted">{r.userPrincipalName}</div></td>
                  <td>{r.displayName ?? '–'}</td>
                  <td>{fmtDate(r.registeredAt)}</td>
                  <td>{r.registeredByName ?? (r.source === 'EntraSync' ? 'Selbst registriert' : '–')}</td>
                  <td>{r.removedAt ? <span className="small muted">entfernt {fmtDate(r.removedAt)}{r.removedReason ? ` (${r.removedReason})` : ''}</span> : <Badge tone="ok">aktiv</Badge>}</td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
        {active.length === 0 && key.status === 'InStock' && can('RegisterKeys') && <p><Link className="btn primary" to={`/register?tenantId=${key.tenantId}`}>Key registrieren</Link></p>}
      </Card>
      {history && (
        <Card title="Verlauf" className="mt">
          <table>
            <tbody>
              {history.map((h) => <tr key={h.sequence}><td className="small muted">{fmtDate(h.timestamp)}</td><td><code>{h.action}</code></td><td>{h.actorName}</td><td className="small">{h.details}</td></tr>)}
            </tbody>
          </table>
        </Card>
      )}
      {modal === 'lock' && <LockModal keyItem={key} onClose={() => setModal(null)} onDone={() => { reload(); setModal(null); }} />}
      {modal === 'retire' && <RetireModal keyItem={key} onClose={() => setModal(null)} onDone={() => { reload(); setModal(null); }} />}
      {modal === 'reset' && <ResetModal keyItem={key} onClose={() => { setModal(null); reload(); }} />}
      {modal === 'edit' && <EditModal keyItem={key} fields={fields ?? []} onClose={() => setModal(null)} onDone={() => { reload(); setModal(null); }} />}
    </>
  );
}

function formatField(f: CustomField, v?: string | null) {
  if (v == null || v === '') return '–';
  if (f.type === 'Boolean') return v === 'true' ? 'Ja' : 'Nein';
  if (f.type === 'Date') return new Date(v).toLocaleDateString('de-DE');
  return v;
}

function LockModal({ keyItem, onClose, onDone }: { keyItem: YubiKey; onClose: () => void; onDone: (k: YubiKey) => void }) {
  const [reason, setReason] = useState('Lost');
  const [note, setNote] = useState('');
  const { busy, error, run } = useAction();
  const go = () => run(async () => onDone(await api.post<YubiKey>(`/api/keys/${keyItem.id}/lock`, { reason, note: note || null })));
  return (
    <Modal title="Key sperren" onClose={onClose} footer={<><button onClick={onClose}>Abbrechen</button><button className="danger" disabled={busy} onClick={go}>Sperren</button></>}>
      <Info tone="warn">Die Passkey-Registrierung wird sofort in Entra ID entfernt. Die Person kann sich mit diesem Key nicht mehr anmelden.</Info>
      <Field label="Grund">
        <select value={reason} onChange={(e) => setReason(e.target.value)}>
          {Object.entries(lockReasons).map(([k, v]) => <option key={k} value={k}>{v}</option>)}
        </select>
      </Field>
      <Field label="Notiz (optional)"><textarea value={note} onChange={(e) => setNote(e.target.value)} rows={3} /></Field>
      <ErrorBox error={error} />
    </Modal>
  );
}

function RetireModal({ keyItem, onClose, onDone }: { keyItem: YubiKey; onClose: () => void; onDone: (k: YubiKey) => void }) {
  const [note, setNote] = useState('');
  const { busy, error, run } = useAction();
  const go = () => run(async () => onDone(await api.post<YubiKey>(`/api/keys/${keyItem.id}/retire`, { note: note || null })));
  return (
    <Modal title="Key ausmustern" onClose={onClose} footer={<><button onClick={onClose}>Abbrechen</button><button className="danger" disabled={busy} onClick={go}>Ausmustern</button></>}>
      <p>Ausgemusterte Keys bleiben im Inventar sichtbar, können aber nicht mehr registriert werden.</p>
      <Field label="Notiz (optional)"><textarea value={note} onChange={(e) => setNote(e.target.value)} rows={3} /></Field>
      <ErrorBox error={error} />
    </Modal>
  );
}

function ResetModal({ keyItem, onClose }: { keyItem: YubiKey; onClose: () => void }) {
  const { data: agents, reload } = useApi<AgentInfo[]>('/api/agents');
  const online = agents?.filter((a) => a.online) ?? [];
  const [job, setJob] = useState<Job>();
  const { busy, error, run } = useAction();
  usePolling(async () => { if (job) setJob(await api.get<Job>('/api/jobs/' + job.id)); }, 1000, job?.state === 'Running');
  const agent = online[0];
  const go = () => run(async () => setJob(await api.post<Job>(`/api/keys/${keyItem.id}/reset`, { agentId: agent!.id })));
  return (
    <Modal title="Key zurücksetzen" onClose={onClose} footer={<>
      <button onClick={onClose}>{job?.state === 'Succeeded' ? 'Schließen' : 'Abbrechen'}</button>
      {!job && <button className="primary" disabled={!agent || busy} onClick={go}>Zurücksetzen starten</button>}
    </>}>
      {!job && <>
        <p>Der FIDO-Teil des Keys {keyItem.serialNumber} wird auf Werkseinstellungen zurückgesetzt, alle Registrierungen in Entra werden entfernt und der Key kommt ins Lager.</p>
        {agent ? <Info>Agent: <strong>{agent.machineName}</strong>. Stecken Sie den Key ein und folgen Sie den Hinweisen.</Info>
          : <Info tone="warn">Kein Agent online. <button className="link" onClick={reload}>Erneut prüfen</button></Info>}
      </>}
      {job && <JobProgress job={job} />}
      <ErrorBox error={error} />
    </Modal>
  );
}

export function JobProgress({ job }: { job: Job }) {
  return (
    <div>
      <div className="progress"><span style={{ width: (job.state === 'Succeeded' ? 100 : (job.stepNumber / job.totalSteps) * 100) + '%' }} /></div>
      <p><strong>{job.step}</strong> {job.state === 'Running' && <Spinner />}</p>
      {job.prompt && job.state === 'Running' && <div className="prompt">{job.prompt}</div>}
      {job.state === 'Failed' && <ErrorBox error={job.error ?? 'Vorgang fehlgeschlagen.'} />}
      {job.state === 'Succeeded' && <Info tone="ok">Abgeschlossen.</Info>}
      {job.notes.length > 0 && <ul className="small muted">{job.notes.map((n, i) => <li key={i}>{n}</li>)}</ul>}
    </div>
  );
}

function EditModal({ keyItem, fields, onClose, onDone }: { keyItem: YubiKey; fields: CustomField[]; onClose: () => void; onDone: (k: YubiKey) => void }) {
  const [tags, setTags] = useState(keyItem.tags.join(', '));
  const [notes, setNotes] = useState(keyItem.notes ?? '');
  const [values, setValues] = useState<Record<string, string>>(Object.fromEntries(Object.entries(keyItem.customFields).map(([k, v]) => [k, v ?? ''])));
  const { busy, error, run } = useAction();
  const go = () => run(async () => onDone(await api.patch<YubiKey>(`/api/keys/${keyItem.id}`, {
    tags: tags.split(',').map((t) => t.trim()).filter(Boolean), notes,
    customFields: Object.fromEntries(fields.map((f) => [f.key, values[f.key] || null])),
  })));
  return (
    <Modal title="Key bearbeiten" onClose={onClose} footer={<><button onClick={onClose}>Abbrechen</button><button className="primary" disabled={busy} onClick={go}>Speichern</button></>}>
      <Field label="Tags" hint="Mit Komma getrennt"><input value={tags} onChange={(e) => setTags(e.target.value)} /></Field>
      {fields.map((f) => (
        <Field key={f.key} label={f.label + (f.required ? ' *' : '')}>
          <CustomFieldInput field={f} value={values[f.key] ?? ''} onChange={(v) => setValues((s) => ({ ...s, [f.key]: v }))} />
        </Field>
      ))}
      <Field label="Notiz"><textarea value={notes} onChange={(e) => setNotes(e.target.value)} rows={4} /></Field>
      <ErrorBox error={error} />
    </Modal>
  );
}
