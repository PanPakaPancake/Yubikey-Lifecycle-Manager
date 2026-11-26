import { useState, type ReactNode } from 'react';
import { Link, useNavigate, useParams } from 'react-router';
import { api, approvalLabels, fmtDate, lockReasons, removalLabels, usageLabels, type AgentInfo, type Approval, type AuditEntry, type CustomField, type Job, type Registration, type RemovalAction, type YubiKey } from '../api';
import { useAuth, useCan } from '../auth';
import { BreakglassSheetModal } from './BreakglassSheet';
import { Badge, Card, ErrorBox, Field, Info, Modal, PageHeader, Spinner, StatusBadge, useAction, useApi, usePolling } from '../ui';
import { CustomFieldInput } from './Keys';
import { useTenant } from '../tenant';
import { useConfirm, useToast } from '../feedback';
import { Icon } from '../icons';

const sourceLabels: Record<string, string> = {
  Platform: 'Über die Plattform registriert', EntraSync: 'Aus Entra übernommen (selbst registriert)', StockIntake: 'Wareneingang', Import: 'CSV-Import',
};

export default function KeyDetailPage() {
  const { id } = useParams();
  const can = useCan();
  const { data, error, reload } = useApi<{ key: YubiKey; history: AuditEntry[]; removal?: { required: boolean; approvals: Approval[] } }>('/api/keys/' + id, [id]);
  const key = data?.key;
  const history = data?.history;
  const removal = data?.removal;
  const { me } = useAuth();
  // Partnerlager: nur globale Felder.
  const { data: fields } = useApi<CustomField[]>(key ? (key.tenantId ? `/api/tenants/${key.tenantId}/fields` : '/api/fields') : null, [key?.id, key?.tenantId]);
  const [modal, setModal] = useState<'lock' | 'retire' | 'reset' | 'edit' | 'toPartner' | 'assign' | 'delete' | 'sheet' | null>(null);
  // Vier-Augen bei Breakglass-Keys: genutzte Freigabe für Sperren/Zurücksetzen, offener Antrag im Dialog.
  const [approvalId, setApprovalId] = useState<string>();
  const [ask, setAsk] = useState<{ action: RemovalAction; registration?: Registration }>();
  const confirm = useConfirm();
  const toast = useToast();

  const mine = (action: RemovalAction, registrationId?: string) => removal?.approvals.find((a) => a.requestedById === me.user?.id && a.removalAction === action
    && (action !== 'RemoveAccount' || a.registrationId === registrationId));
  /** Ohne nötige Freigabe direkt weiter; mit gültiger eigener Freigabe weiter mit deren Id; sonst beantragen bzw. Stand zeigen. */
  const gate = (action: RemovalAction, proceed: (approvalId?: string) => void, registration?: Registration) => {
    if (!removal?.required) return proceed();
    const a = mine(action, registration?.id);
    if (a && a.status === 'Approved' && !a.expired) return proceed(a.id);
    setAsk({ action, registration });
  };
  const removeAccount = async (r: Registration, approval?: string) => {
    if (!await confirm({ title: 'Konto entfernen?', message: `Der Passkey von ${r.userPrincipalName} wird in Entra gelöscht. Die anderen Konten auf dem Key bleiben.`, confirmLabel: 'Entfernen', danger: true })) return;
    try { await api.del(`/api/keys/${key!.id}/registrations/${r.id}${approval ? '?approvalId=' + approval : ''}`); toast.ok('Konto entfernt.'); reload(); } catch (e) { toast.error((e as Error).message); }
  };

  if (error) return <ErrorBox error={error} />;
  if (!key) return <Spinner label="Wird geladen …" />;
  const active = key.registrations.filter((r) => !r.removedAt);

  return (
    <>
      <PageHeader
        title={key.serialNumber ? `YubiKey ${key.serialNumber}` : 'YubiKey ohne Seriennummer'}
        subtitle={key.inPartnerStock
          ? <><Link to="/partner/stock">Partnerlager</Link> · keinem Kunden zugeordnet</>
          : <><Link to="/keys">Inventar</Link> · {key.tenantName}</>}
        actions={<>
          {(key.inPartnerStock ? can('ManagePartnerStock') : can('ManageInventory')) && <button onClick={() => setModal('edit')}>Bearbeiten</button>}
          {key.inPartnerStock && key.status === 'InStock' && can('ManagePartnerStock') && can('ManageInventory') && <button className="primary" onClick={() => setModal('assign')}>Einem Tenant zuweisen</button>}
          {!key.inPartnerStock && (key.status === 'InStock' || key.status === 'Retired') && can('ManagePartnerStock') && can('ManageInventory')
            && key.registrations.every((r) => r.removedAt) && <button onClick={() => setModal('toPartner')}>Ins Partnerlager</button>}
          {key.usage === 'Breakglass' && <button onClick={() => setModal('sheet')}>Notfallblatt drucken</button>}
          {can('LockKeys') && key.nextStates.includes('Locked') && <button className="danger" onClick={() => gate('Lock', (a) => { setApprovalId(a); setModal('lock'); })}>Sperren</button>}
          {can('ResetKeys') && key.nextStates.includes('InStock') && key.serialNumber && <button onClick={() => gate('Reset', (a) => { setApprovalId(a); setModal('reset'); })}>Zurücksetzen …</button>}
          {can('RetireKeys') && key.nextStates.includes('Retired') && <button className="danger" onClick={() => setModal('retire')}>Ausmustern …</button>}
          {can('ManageInventory') && (key.status === 'InStock' || key.status === 'Retired') && key.registrations.every((r) => r.removedAt) && <button className="danger" onClick={() => setModal('delete')}>Löschen …</button>}
        </>}
      />
      {key.needsDetails && key.status !== 'Retired' && (
        <Info>
          Dieser Key wurde vom Nutzer selbst registriert. Seriennummer und Firmware fehlen, weil Entra sie nicht kennt.
          {can('ManageInventory') && key.tenantId && <> <Link className="btn small" to={`/keys/identify?keyId=${key.id}`}>Jetzt nacherfassen</Link></>}
        </Info>
      )}
      {key.advisories.length > 0 && (
        <Info tone="warn">
          {key.advisories.map((a) => <div key={a.id}><strong>{a.id}</strong>: {a.title} <a href={a.url} target="_blank" rel="noreferrer">Details</a></div>)}
        </Info>
      )}
      {removal?.required && (
        <Info tone="warn">
          <strong>Vier-Augen-Schutz:</strong> Dieser Breakglass-Key lässt sich nur mit Freigabe eines zweiten Admins sperren, zurücksetzen oder um ein Konto erleichtern.
          {removal.approvals.map((a) => (
            <div key={a.id} className="small">
              {removalLabels[a.removalAction ?? 'Lock']}{a.removalAction === 'RemoveAccount' ? ` (${a.userPrincipalNames.join(', ')})` : ''}: {approvalLabels[a.status]}, beantragt von {a.requestedByName}
              {a.status === 'Approved' && <> · freigegeben von {a.decidedByName}, gültig bis {fmtDate(a.validUntil)}</>}
            </div>
          ))}
        </Info>
      )}
      <AccountsCard keyItem={key} active={active} onRemove={can('LockKeys') && key.usage !== 'Personal' ? (r) => gate('RemoveAccount', (a) => removeAccount(r, a), r) : undefined}>
        {active.length === 0 && key.status === 'InStock' && key.tenantId && can('RegisterKeys') && <p className="actions"><Link className="btn primary" to={`/register?tenantId=${key.tenantId}`}>Key registrieren</Link><Link className="btn" to={`/register/shared?tenantId=${key.tenantId}`}>Als geteilten Key registrieren</Link></p>}
        {key.inPartnerStock && key.status === 'InStock' && <p className="muted small">Keys aus dem Partnerlager werden bei der Registrierung automatisch dem Tenant zugewiesen.</p>}
      </AccountsCard>
      <div className="grid cols-2">
        <Card title="Key">
          <table>
            <tbody>
              <tr><th>Status</th><td><StatusBadge status={key.status} /> {key.lockReason && <Badge>{lockReasons[key.lockReason] ?? key.lockReason}</Badge>} <span className="small muted">seit {fmtDate(key.statusChangedAt)}</span>{key.statusNote && <div className="small">{key.statusNote}</div>}</td></tr>
              <tr><th>Seriennummer</th><td className="mono">{key.serialNumber ?? '–'}</td></tr>
              <tr><th>Modell</th><td>{key.model ?? '–'} {key.isFips && <Badge tone="info">FIPS</Badge>}</td></tr>
              <tr><th>Firmware</th><td className="mono">{key.firmwareVersion ?? '–'}</td></tr>
              <tr><th>AAGUID</th><td className="mono small">{key.aaGuid ?? '–'}</td></tr>
              <tr><th>Nutzung</th><td>{usageLabels[key.usage] ?? key.usage}{key.usage !== 'Personal' && <div className="small">Verwahrer: <strong>{key.custodian ?? '–'}</strong></div>}</td></tr>
              <tr><th>PIN-Wechsel erzwungen</th><td>{key.pinChangeForced == null ? '–' : key.pinChangeForced ? 'Ja' : 'Nein'}</td></tr>
              <tr><th>Herkunft</th><td>{sourceLabels[key.source] ?? key.source}</td></tr>
              <tr><th>Erfasst</th><td>{fmtDate(key.createdAt)}{key.identifiedAt && <div className="small muted">nacherfasst {fmtDate(key.identifiedAt)}</div>}</td></tr>
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
      {history && history.length > 0 && (
        <Card title="Verlauf" className="mt">
          <table>
            <tbody>
              {history.map((h) => <tr key={h.sequence}><td className="small muted">{fmtDate(h.timestamp)}</td><td><code>{h.action}</code></td><td>{h.actorName}</td><td className="small">{h.details}</td></tr>)}
            </tbody>
          </table>
        </Card>
      )}
      {modal === 'delete' && <DeleteModal keyItem={key} onClose={() => setModal(null)} />}
      {modal === 'lock' && <LockModal keyItem={key} approvalId={approvalId} onClose={() => setModal(null)} onDone={() => { reload(); setModal(null); }} />}
      {modal === 'retire' && <RetireModal keyItem={key} onClose={() => setModal(null)} onDone={() => { reload(); setModal(null); }} />}
      {modal === 'reset' && <ResetModal keyItem={key} approvalId={approvalId} onClose={() => { setModal(null); reload(); }} />}
      {modal === 'sheet' && <BreakglassSheetModal context={{ tenantName: key.tenantName, serialNumber: key.serialNumber, custodian: key.custodian, accounts: active }} onClose={() => setModal(null)} />}
      {ask && <RemovalApprovalModal keyItem={key} action={ask.action} registration={ask.registration} pending={mine(ask.action, ask.registration?.id)}
        onClose={() => setAsk(undefined)} onDone={() => { setAsk(undefined); reload(); }} />}
      {modal === 'toPartner' && <ToPartnerModal keyItem={key} onClose={() => setModal(null)} onDone={() => { reload(); setModal(null); }} />}
      {modal === 'assign' && <AssignModal keyItem={key} onClose={() => setModal(null)} onDone={() => { reload(); setModal(null); }} />}
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

/** Vier-Augen-Antrag für eine entfernende Aktion an einem Breakglass-Key, oder Stand des offenen Antrags. */
function RemovalApprovalModal({ keyItem, action, registration, pending, onClose, onDone }: {
  keyItem: YubiKey; action: RemovalAction; registration?: Registration; pending?: Approval; onClose: () => void; onDone: () => void;
}) {
  const [reason, setReason] = useState('');
  const { busy, error, run } = useAction();
  const toast = useToast();
  const what = action === 'RemoveAccount' ? `Konto ${registration?.userPrincipalName} entfernen` : removalLabels[action];
  const submit = () => run(async () => {
    await api.post(`/api/keys/${keyItem.id}/approvals`, { action, registrationId: registration?.id, reason });
    toast.ok('Antrag gestellt. Ein zweiter Admin muss ihn unter „Freigaben“ freigeben.');
    onDone();
  });
  const withdraw = () => run(async () => {
    await api.post(`/api/approvals/${pending!.id}/cancel`);
    toast.ok('Antrag zurückgezogen.');
    onDone();
  });
  if (pending) return (
    <Modal title="Wartet auf Freigabe" onClose={onClose} footer={<><button className="ghost" disabled={busy} onClick={withdraw}>Antrag zurückziehen</button><button className="primary" onClick={onClose}>OK</button></>}>
      <p>Ihr Antrag „{what}“ wartet darauf, dass ein zweiter Admin mit dem Recht „Breakglass freigeben“ ihn unter <Link to="/approvals">Freigaben</Link> freigibt. Danach können Sie die Aktion 24 Stunden lang einmal ausführen.</p>
      <p className="small muted">Beantragt {fmtDate(pending.requestedAt)} · Grund: {pending.reason}</p>
      <ErrorBox error={error} />
    </Modal>
  );
  return (
    <Modal title="Vier-Augen-Freigabe nötig" onClose={onClose} footer={<><button onClick={onClose}>Abbrechen</button><button className="primary" disabled={busy || !reason.trim()} onClick={submit}>Freigabe beantragen</button></>}>
      <p>Dies ist ein <strong>Breakglass-Key</strong> (Verwahrer: {keyItem.custodian ?? '–'}). „{what}“ löscht Notfallzugänge in Entra und braucht deshalb die Freigabe eines zweiten Admins.</p>
      <Field label="Grund *" hint="z. B. Key verloren, Ticket 4711"><input value={reason} onChange={(e) => setReason(e.target.value)} autoFocus /></Field>
      <ErrorBox error={error} />
    </Modal>
  );
}

function LockModal({ keyItem, approvalId, onClose, onDone }: { keyItem: YubiKey; approvalId?: string; onClose: () => void; onDone: (k: YubiKey) => void }) {
  const [reason, setReason] = useState('Lost');
  const [note, setNote] = useState('');
  const { busy, error, run } = useAction();
  const go = () => run(async () => onDone(await api.post<YubiKey>(`/api/keys/${keyItem.id}/lock`, { reason, note: note || null, approvalId })));
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

/** Wahl beim Einlagern/Ausmustern: beim Kunden bleiben oder ins Partnerlager trennen. */
function StockChoice({ keyItem, toPartner, setToPartner, retire }: { keyItem: YubiKey; toPartner: boolean; setToPartner: (v: boolean) => void; retire?: boolean }) {
  const can = useCan();
  if (keyItem.inPartnerStock || !can('ManagePartnerStock')) return null;
  return (
    <Field label="Wohin mit dem Key?">
      <label className={'choice' + (!toPartner ? ' selected' : '')}>
        <input type="radio" name="stock-target" checked={!toPartner} onChange={() => setToPartner(false)} />
        <span><strong>Beim Kunden belassen ({keyItem.tenantName})</strong><br />
          <span className="small muted">{retire ? 'Der Key bleibt ausgemustert im Inventar des Kunden sichtbar.' : 'Der Key bleibt dem Kunden zugeordnet und liegt dort im Lager bereit für die nächste Vergabe.'}</span></span>
      </label>
      <label className={'choice' + (toPartner ? ' selected' : '')}>
        <input type="radio" name="stock-target" checked={toPartner} onChange={() => setToPartner(true)} />
        <span><strong>Vom Kunden trennen, ins Partnerlager</strong><br />
          <span className="small muted">{retire ? 'Z. B. Rückläufer, die der Partner entsorgt oder prüft. Der Key verschwindet aus dem Inventar des Kunden.' : 'Der Key gehört danach keinem Kunden mehr und kann später jedem Tenant zugewiesen werden.'}</span></span>
      </label>
    </Field>
  );
}

function ToPartnerModal({ keyItem, onClose, onDone }: { keyItem: YubiKey; onClose: () => void; onDone: (k: YubiKey) => void }) {
  const { busy, error, run } = useAction();
  const go = () => run(async () => onDone(await api.post<YubiKey>(`/api/keys/${keyItem.id}/to-partner-stock`, {})));
  return (
    <Modal title="Ins Partnerlager legen" onClose={onClose} footer={<><button onClick={onClose}>Abbrechen</button><button className="primary" disabled={busy} onClick={go}>Ins Partnerlager</button></>}>
      <p>Der Key wird von <strong>{keyItem.tenantName}</strong> getrennt und liegt danach im allgemeinen Partnerlager. Von dort kann er jedem Tenant zugewiesen werden.</p>
      <p className="muted small">Tenant-eigene Zusatzfelder bleiben am Key gespeichert, sind im Partnerlager aber nicht sichtbar. Das zugeordnete Profil wird entfernt.</p>
      <ErrorBox error={error} />
    </Modal>
  );
}

function AssignModal({ keyItem, onClose, onDone }: { keyItem: YubiKey; onClose: () => void; onDone: (k: YubiKey) => void }) {
  const { tenants, tenantId: current } = useTenant();
  const [tenantId, setTenantId] = useState(current);
  const { busy, error, run } = useAction();
  const go = () => run(async () => onDone(await api.post<YubiKey>(`/api/keys/${keyItem.id}/assign`, { tenantId })));
  return (
    <Modal title="Einem Tenant zuweisen" onClose={onClose} footer={<><button onClick={onClose}>Abbrechen</button><button className="primary" disabled={!tenantId || busy} onClick={go}>Zuweisen</button></>}>
      <p>Der Key {keyItem.serialNumber} liegt danach im Lager des Tenants bereit.</p>
      <Field label="Tenant">
        <select value={tenantId} onChange={(e) => setTenantId(e.target.value)}>
          {!tenantId && <option value="">Tenant wählen …</option>}
          {tenants.filter((t) => t.status === 'Active').map((t) => <option key={t.id} value={t.id}>{t.displayName}</option>)}
        </select>
      </Field>
      <ErrorBox error={error} />
    </Modal>
  );
}

function RetireModal({ keyItem, onClose, onDone }: { keyItem: YubiKey; onClose: () => void; onDone: (k: YubiKey) => void }) {
  const [note, setNote] = useState('');
  const [toPartner, setToPartner] = useState(false);
  const { busy, error, run } = useAction();
  const go = () => run(async () => onDone(await api.post<YubiKey>(`/api/keys/${keyItem.id}/retire`, { note: note || null, toPartnerStock: toPartner })));
  return (
    <Modal title="Key ausmustern" onClose={onClose} footer={<><button onClick={onClose}>Abbrechen</button><button className="danger" disabled={busy} onClick={go}>Ausmustern</button></>}>
      <p>Ausgemusterte Keys bleiben im Inventar sichtbar, können aber nicht mehr registriert werden.</p>
      <StockChoice keyItem={keyItem} toPartner={toPartner} setToPartner={setToPartner} retire />
      <Field label="Notiz (optional)"><textarea value={note} onChange={(e) => setNote(e.target.value)} rows={3} /></Field>
      <ErrorBox error={error} />
    </Modal>
  );
}

function ResetModal({ keyItem, approvalId, onClose }: { keyItem: YubiKey; approvalId?: string; onClose: () => void }) {
  const { data: agents, reload } = useApi<AgentInfo[]>('/api/agents');
  const online = agents?.filter((a) => a.online) ?? [];
  const [job, setJob] = useState<Job>();
  const { busy, error, run } = useAction();
  usePolling(async () => { if (job) setJob(await api.get<Job>('/api/jobs/' + job.id)); }, 1000, job?.state === 'Running');
  const agent = online[0];
  const [toPartner, setToPartner] = useState(false);
  const go = () => run(async () => setJob(await api.post<Job>(`/api/keys/${keyItem.id}/reset`, { agentId: agent!.id, toPartnerStock: toPartner, approvalId })));
  return (
    <Modal title="Key zurücksetzen" onClose={onClose} footer={<>
      <button onClick={onClose}>{job?.state === 'Succeeded' ? 'Schließen' : 'Abbrechen'}</button>
      {!job && <button className="primary" disabled={!agent || busy} onClick={go}>Zurücksetzen starten</button>}
    </>}>
      {!job && <>
        <p>Der FIDO-Teil des Keys {keyItem.serialNumber} wird auf Werkseinstellungen zurückgesetzt, alle Registrierungen in Entra werden entfernt und der Key kommt ins Lager.</p>
        <StockChoice keyItem={keyItem} toPartner={toPartner} setToPartner={setToPartner} />
        {agent ? <Info>Agent: <strong>{agent.machineName}</strong>. Stecken Sie den Key ein und folgen Sie den Hinweisen.</Info>
          : <Info tone="warn">Kein Agent online. <button className="link" onClick={reload}>Erneut prüfen</button></Info>}
      </>}
      {job && <JobProgress job={job} />}
      <ErrorBox error={error} />
    </Modal>
  );
}

/** Animated instruction for the action expected at the key (unplug, plug in, touch). */
function KeyPrompt({ text }: { text: string }) {
  const t = text.toLowerCase();
  const kind = t.includes('abziehen') ? 'unplug' : t.includes('einstecken') || t.includes('anstecken') ? 'plug' : t.includes('berühren') ? 'touch' : 'wait';
  return (
    <div className={'prompt key-prompt ' + kind} role="status" aria-live="polite">
      <span className="key-anim" aria-hidden><span className="key-body" /><span className="key-dot" /></span>
      <span>{text}</span>
    </div>
  );
}

export function JobProgress({ job }: { job: Job }) {
  return (
    <div>
      <div className="progress"><span style={{ width: (job.state === 'Succeeded' ? 100 : (job.stepNumber / job.totalSteps) * 100) + '%' }} /></div>
      <p><strong>{job.step}</strong> {job.state === 'Running' && <Spinner />}</p>
      {job.prompt && job.state === 'Running' && <KeyPrompt text={job.prompt} />}
      {job.state === 'Failed' && <ErrorBox error={job.error ?? 'Vorgang fehlgeschlagen.'} />}
      {job.state === 'Succeeded' && <Info tone="ok">Abgeschlossen.</Info>}
      {job.notes.length > 0 && <ul className="small muted">{job.notes.map((n, i) => <li key={i}>{n}</li>)}</ul>}
    </div>
  );
}

function EditModal({ keyItem, fields, onClose, onDone }: { keyItem: YubiKey; fields: CustomField[]; onClose: () => void; onDone: (k: YubiKey) => void }) {
  const [tags, setTags] = useState(keyItem.tags.join(', '));
  const [notes, setNotes] = useState(keyItem.notes ?? '');
  const [custodian, setCustodian] = useState(keyItem.custodian ?? '');
  const [values, setValues] = useState<Record<string, string>>(Object.fromEntries(Object.entries(keyItem.customFields).map(([k, v]) => [k, v ?? ''])));
  const { busy, error, run } = useAction();
  const go = () => run(async () => onDone(await api.patch<YubiKey>(`/api/keys/${keyItem.id}`, {
    tags: tags.split(',').map((t) => t.trim()).filter(Boolean), notes, ...(keyItem.usage !== 'Personal' ? { custodian } : {}),
    customFields: Object.fromEntries(fields.map((f) => [f.key, values[f.key] || null])),
  })));
  return (
    <Modal title="Key bearbeiten" onClose={onClose} footer={<><button onClick={onClose}>Abbrechen</button><button className="primary" disabled={busy} onClick={go}>Speichern</button></>}>
      {keyItem.usage !== 'Personal' && <Field label="Verwahrer *" hint="Wer den geteilten Key aufbewahrt, z. B. Abteilungsleiter"><input value={custodian} onChange={(e) => setCustodian(e.target.value)} /></Field>}
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

function DeleteModal({ keyItem, onClose }: { keyItem: YubiKey; onClose: () => void }) {
  const navigate = useNavigate();
  const { busy, error, run } = useAction();
  const go = () => run(async () => {
    await api.del('/api/keys/' + keyItem.id);
    navigate(keyItem.inPartnerStock ? '/partner/stock' : '/keys');
  });
  return (
    <Modal title="Key löschen" onClose={onClose} footer={<><button onClick={onClose}>Abbrechen</button><button className="danger" disabled={busy} onClick={go}>Endgültig löschen</button></>}>
      <p>Key <span className="mono">{keyItem.serialNumber ?? 'ohne Seriennummer'}</span> aus dem Inventar löschen? Der Verlauf bleibt im Audit-Log erhalten.</p>
      <ErrorBox error={error} />
    </Modal>
  );
}

/** Konten auf dem Key: aktive als Karten (bei geteilten Keys mit Verwahrer), frühere eingeklappt. */
function AccountsCard({ keyItem: key, active, onRemove, children }: { keyItem: YubiKey; active: Registration[]; onRemove?: (r: Registration) => void; children?: ReactNode }) {
  const can = useCan();
  const removed = key.registrations.filter((r) => r.removedAt);
  const shared = key.usage !== 'Personal';
  return (
    <Card className="accounts-card" title={<>Konten auf diesem Key <span className="count-pill">{active.length}</span></>}
      actions={shared && key.status === 'Active' && key.tenantId && can('RegisterKeys') && <Link className="btn" to={`/register/shared?tenantId=${key.tenantId}&usage=${key.usage}&custodian=${encodeURIComponent(key.custodian ?? '')}`}>Weitere Konten hinzufügen</Link>}>
      {shared && (
        <div className={'custodian-bar ' + (key.usage === 'Breakglass' ? 'tone-error' : 'tone-info')}>
          <Icon name={key.usage === 'Breakglass' ? 'shield' : 'share'} />
          <span><strong>{usageLabels[key.usage]}</strong> · physisch bei <strong>{key.custodian ?? 'unbekannt (Verwahrer fehlt)'}</strong></span>
          <span className="muted small push-right">{active.length === 1 ? '1 Konto' : `${active.length} Konten`} melden sich mit diesem Key an</span>
        </div>
      )}
      {active.length === 0 ? <p className="muted">Auf diesem Key ist kein Konto registriert.</p> : (
        <div className="account-grid">
          {active.map((r, i) => (
            <div key={r.id} className="account-tile" style={{ animationDelay: i * 50 + 'ms' }}>
              <span className="avatar" aria-hidden>{initials(r.userDisplayName ?? r.userPrincipalName)}</span>
              <div className="account-main">
                <strong>{r.userDisplayName ?? r.userPrincipalName}</strong>
                <div className="small muted">{r.userPrincipalName}</div>
                <div className="account-meta small">
                  <span title="Anzeigename des Passkeys in Entra"><Icon name="key" size={13} />{r.displayName ?? '–'}</span>
                  <span><Icon name="check" size={13} />{fmtDate(r.registeredAt, false)} · {r.registeredByName ?? (r.source === 'EntraSync' ? 'selbst registriert' : '–')}</span>
                </div>
              </div>
              {onRemove && <button className="small danger" onClick={() => onRemove(r)}>Entfernen</button>}
            </div>
          ))}
        </div>
      )}
      {removed.length > 0 && (
        <details className="removed-accounts">
          <summary>Frühere Registrierungen ({removed.length})</summary>
          <table>
            <tbody>
              {removed.map((r) => (
                <tr key={r.id}>
                  <td>{r.userDisplayName ?? r.userPrincipalName}<div className="small muted">{r.userPrincipalName}</div></td>
                  <td className="small">registriert {fmtDate(r.registeredAt, false)}</td>
                  <td className="small muted">entfernt {fmtDate(r.removedAt, false)}{r.removedReason ? ` (${r.removedReason})` : ''}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </details>
      )}
      {children}
    </Card>
  );
}

function initials(name: string) {
  const parts = name.replace(/@.*/, '').replace(/[^\p{L}\p{N}\s.-]/gu, ' ').trim().split(/[\s.-]+/).filter(Boolean);
  return ((parts[0]?.[0] ?? '') + (parts.length > 1 ? parts[parts.length - 1][0] : '')).toUpperCase() || '?';
}
