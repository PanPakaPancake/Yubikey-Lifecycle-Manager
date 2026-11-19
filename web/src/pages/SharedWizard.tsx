import { useEffect, useState } from 'react';
import { Link, useSearchParams } from 'react-router';
import { api, approvalLabels, fmtDate, usageLabels, type Approval, type Job, type KeyInfo, type KeyUsage, type Tenant } from '../api';
import { Badge, Card, download, Empty, ErrorBox, Field, Info, PageHeader, Spinner, Steps, useAction, useApi, useLiveSearch, usePolling } from '../ui';
import { useCan } from '../auth';
import { useTenant } from '../tenant';
import { useToast } from '../feedback';
import { JobProgress } from './KeyDetail';
import { KeyStep, ProfileStep, type EntraUser, type Violation } from './Wizard';

interface SharedPreview {
  key: KeyInfo; violations: Violation[]; resetRequired: boolean; resetPlanned: boolean; willForcePinChange: boolean;
  users: { id: string; userPrincipalName: string; displayName?: string | null; department?: string | null; accountEnabled: boolean; alreadyOnKey: boolean }[];
  inventoryKey?: { id: string; tenantName?: string | null; status: string; usage: KeyUsage; custodian?: string | null; activeRegistrations: string[] } | null;
  remainingSlots?: number | null; needsCurrentPin: boolean; approvalRequired: boolean; approvals: Approval[]; problems: string[];
}

interface Picked { id: string; userPrincipalName: string; displayName?: string }

const stepNames = ['Konten & Verwahrer', 'Key', 'Profil & Prüfung', 'Registrierung', 'PIN-Brief'];

/** Geteilter Key (V1.1): mehrere Konten auf einem Key, Pflichtfeld Verwahrer; Breakglass optional mit Vier-Augen-Freigabe. */
export default function SharedWizardPage() {
  const [params] = useSearchParams();
  const { tenantId, setTenantId } = useTenant();
  const paramTenant = params.get('tenantId');
  useEffect(() => { if (paramTenant) setTenantId(paramTenant); }, [paramTenant, setTenantId]);
  const { data: tenant } = useApi<Tenant>(tenantId ? `/api/tenants/${tenantId}` : null, [tenantId]);
  const [step, setStep] = useState(0);
  const [usage, setUsage] = useState<KeyUsage>(params.get('usage') === 'Breakglass' ? 'Breakglass' : 'Shared');
  const [custodian, setCustodian] = useState(params.get('custodian') ?? '');
  const [accounts, setAccounts] = useState<Picked[]>([]);
  const [agentId, setAgentId] = useState('');
  const [profileId, setProfileId] = useState('');
  const [preview, setPreview] = useState<SharedPreview>();
  const [overrides, setOverrides] = useState<string[]>([]);
  const [performReset, setPerformReset] = useState(false);
  const [currentPin, setCurrentPin] = useState('');
  const [approvalId, setApprovalId] = useState('');
  const [job, setJob] = useState<Job>();
  const { busy, error, run, setError } = useAction();

  usePolling(async () => {
    if (!job) return;
    const j = await api.get<Job>('/api/jobs/' + job.id);
    setJob(j);
    if (j.state === 'Succeeded') setStep(4);
  }, 1000, job?.state === 'Running');

  const restart = () => {
    setStep(0); setAccounts([]); setPreview(undefined); setOverrides([]); setPerformReset(false); setCurrentPin(''); setApprovalId(''); setJob(undefined); setError(undefined);
  };

  const loadPreview = () => run(async () => {
    const p = await api.post<SharedPreview>(`/api/tenants/${tenantId}/shared-registrations/preview`, { entraUserIds: accounts.map((a) => a.id), profileId, agentId, usage });
    setPreview(p);
    setPerformReset(p.resetPlanned);
    setOverrides([]);
    setApprovalId(p.approvals[0]?.id ?? '');
  });

  const start = () => run(async () => {
    const j = await api.post<Job>(`/api/tenants/${tenantId}/shared-registrations`, {
      entraUserIds: accounts.map((a) => a.id), profileId, agentId, usage, custodian, overrides, performReset,
      expectedSerialNumber: preview?.key.serialNumber ?? null, currentPin: currentPin || null, approvalId: approvalId || null,
    });
    setJob(j);
    setStep(3);
  });

  const blocking = preview ? preview.violations.filter((v) => !(v.resolvedByReset && performReset) && !overrides.includes(v.code)) : [];
  const pinMissing = !!preview && preview.needsCurrentPin && !performReset && currentPin.length < 4;
  const approvalMissing = !!preview && preview.approvalRequired && !approvalId;
  const canStart = !!preview && blocking.length === 0 && (performReset || preview.problems.length === 0) && !pinMissing && !approvalMissing;

  return (
    <>
      <PageHeader
        title="Geteilter Key"
        subtitle="Mehrere Konten auf einem Key, z. B. für Produktionsmitarbeiter oder Breakglass-Konten. Ausgabe und Rückgabe regelt der Verwahrer."
        actions={step > 0 ? <button onClick={restart}>Neu beginnen</button> : <Link className="btn" to="/register">Für eine Person registrieren</Link>}
      />
      <Steps names={stepNames} current={step} />
      <ErrorBox error={error} />
      {!tenantId && <Info>Bitte oben im Umschalter den Kunden-Tenant wählen.</Info>}

      {step === 0 && tenantId && (
        <Card title="Welche Konten kommen auf den Key?">
          <div className="tabs" role="tablist" aria-label="Nutzung">
            {(['Shared', 'Breakglass'] as KeyUsage[]).map((u) => (
              <button key={u} type="button" role="tab" aria-selected={usage === u} className={usage === u ? 'selected' : ''} onClick={() => setUsage(u)}>{usageLabels[u]}</button>
            ))}
          </div>
          <p className="small muted" style={{ marginTop: 8 }}>
            {usage === 'Shared'
              ? 'Ein Key für mehrere Personen, z. B. an einem Schichtarbeitsplatz. Der Verwahrer gibt ihn aus und nimmt ihn zurück.'
              : 'Notfallzugang für Breakglass-Konten. Der Key gehört in einen Tresor; der Verwahrer ist verantwortlich.'}
            {usage === 'Breakglass' && tenant?.requireBreakglassApproval && <> <strong>Dieser Tenant verlangt eine Vier-Augen-Freigabe.</strong></>}
          </p>
          <Field label="Verwahrer *" hint="Wer den Key aufbewahrt, z. B. „Dieter Leiter (Produktion)“ oder „IT-Tresor Raum 2.14“">
            <input value={custodian} onChange={(e) => setCustodian(e.target.value)} />
          </Field>
          <AccountPicker tenantId={tenantId} accounts={accounts} setAccounts={setAccounts} />
          <div className="actions" style={{ marginTop: 12 }}>
            <button className="primary" disabled={accounts.length === 0 || !custodian.trim()} onClick={() => setStep(1)}>Weiter</button>
          </div>
        </Card>
      )}

      {step === 1 && <KeyStep agentId={agentId} setAgentId={setAgentId} onNext={() => setStep(2)} onBack={() => setStep(0)} />}

      {step === 2 && (
        <ProfileStep tenantId={tenantId} profileId={profileId} setProfileId={(id) => { setProfileId(id); setPreview(undefined); }}>
          <div className="actions">
            <button onClick={() => setStep(1)}>Zurück</button>
            <button disabled={!profileId || busy} onClick={loadPreview}>{preview ? 'Erneut prüfen' : 'Prüfen'}</button>
          </div>
          {preview && (
            <div style={{ marginTop: 16 }}>
              <table>
                <tbody>
                  <tr><th>Nutzung</th><td>{usageLabels[usage]} · Verwahrer <strong>{custodian}</strong></td></tr>
                  <tr><th>Key</th><td>{preview.key.model} · Seriennummer <span className="mono">{preview.key.serialNumber}</span> · Firmware <span className="mono">{preview.key.firmwareVersion}</span></td></tr>
                  <tr><th>Freie Passkey-Plätze</th><td>{preview.remainingSlots ?? 'unbekannt'}{preview.remainingSlots != null && <span className="small muted"> (je nach Firmware 25 bis 100 insgesamt)</span>}</td></tr>
                  <tr><th>Konten</th><td>{preview.users.map((u) => <div key={u.id}>{u.displayName ?? u.userPrincipalName} <span className="muted small">{u.userPrincipalName}</span> {u.alreadyOnKey && <Badge tone="warn">schon auf dem Key</Badge>} {!u.accountEnabled && <Badge tone="error">deaktiviert</Badge>}</div>)}</td></tr>
                  <tr><th>PIN</th><td>{preview.needsCurrentPin && !performReset ? 'Bleibt unverändert (aktuelle PIN wird gebraucht)' : preview.willForcePinChange ? 'Neu, Wechsel wird erzwungen' : 'Neu'}</td></tr>
                </tbody>
              </table>
              {preview.inventoryKey && preview.inventoryKey.activeRegistrations.length > 0 && (
                <Info>Schon auf dem Key ({usageLabels[preview.inventoryKey.usage]}{preview.inventoryKey.custodian ? `, Verwahrer ${preview.inventoryKey.custodian}` : ''}): {preview.inventoryKey.activeRegistrations.join(', ')}</Info>
              )}
              {preview.problems.map((p) => <Info key={p} tone={performReset ? 'warn' : 'error'}>{p}</Info>)}
              {preview.violations.length > 0 && (
                <div className="alert warn">
                  <strong>Der Key passt nicht vollständig zum Profil:</strong>
                  {preview.violations.map((v) => (
                    <div key={v.code} className={'violation-row' + (!v.canOverride && !v.resolvedByReset ? ' blocking' : '')}>
                      <span className="violation-icon" aria-hidden>!</span>
                      <div>{v.message}{v.resolvedByReset && <div className="small muted">Wird durch einen Werksreset behoben.</div>}</div>
                      {v.canOverride && !(v.resolvedByReset && performReset) && (
                        <label className="check">
                          <input type="checkbox" checked={overrides.includes(v.code)} onChange={(e) => setOverrides((o) => e.target.checked ? [...o, v.code] : o.filter((x) => x !== v.code))} />
                          Für diesen Key bewusst überschreiben
                        </label>
                      )}
                    </div>
                  ))}
                </div>
              )}
              {preview.needsCurrentPin && !performReset && (
                <Field label="Aktuelle PIN des Keys *" hint="Beim Verwahrer erfragen. Wird nur für diesen Vorgang an den Agent gegeben und nicht gespeichert. Steht der erzwungene PIN-Wechsel noch aus, muss der Verwahrer die PIN zuerst ändern.">
                  <input type="password" autoComplete="off" inputMode="numeric" value={currentPin} onChange={(e) => setCurrentPin(e.target.value)} style={{ maxWidth: 220 }} />
                </Field>
              )}
              {(preview.resetRequired || preview.resetPlanned || preview.key.pinIsSet) && (
                <label className="check" style={{ margin: '12px 0' }}>
                  <input type="checkbox" checked={performReset} disabled={preview.resetRequired} onChange={(e) => setPerformReset(e.target.checked)} />
                  FIDO-Werksreset vor der Registrierung {preview.resetRequired && '(erforderlich)'}
                </label>
              )}
              {performReset && <Info tone="error"><strong>Achtung:</strong> Der Reset löscht alle Passkeys auf dem Key unwiderruflich; vorhandene Konten werden auch in Entra entfernt.</Info>}
              {preview.approvalRequired && (
                <ApprovalBox tenantId={tenantId} accounts={accounts} custodian={custodian} approvals={preview.approvals} approvalId={approvalId} setApprovalId={setApprovalId} onRequested={loadPreview} />
              )}
              <button className="primary" disabled={!canStart || busy} onClick={start}>Registrierung starten</button>
            </div>
          )}
        </ProfileStep>
      )}

      {step === 3 && job && (
        <Card title="Registrierung läuft">
          <JobProgress job={job} />
          {job.state === 'Failed' && <div className="actions"><button onClick={() => { setJob(undefined); setStep(2); }}>Zurück zur Prüfung</button></div>}
        </Card>
      )}

      {step === 4 && job && (
        <Card title="Fertig">
          <Info tone="ok">{accounts.length} Konto/Konten sind auf dem Key registriert. Verwahrer: {custodian}.</Info>
          {job.notes.length > 0 && <ul className="small muted">{job.notes.map((n, i) => <li key={i}>{n}</li>)}</ul>}
          {job.pinLetterId && <p>Laden Sie den PIN-Brief für den Verwahrer jetzt herunter. Die PIN wird nicht gespeichert; der Brief ist 30 Minuten abrufbar.</p>}
          <div className="actions">
            {job.pinLetterId && <button className="primary" onClick={() => download('/api/pin-letters/' + job.pinLetterId)}>PIN-Brief (PDF)</button>}
            {job.keyId && <Link className="btn" to={'/keys/' + job.keyId}>Key ansehen</Link>}
            <button onClick={restart}>Nächsten geteilten Key</button>
          </div>
        </Card>
      )}
    </>
  );
}

function AccountPicker({ tenantId, accounts, setAccounts }: { tenantId: string; accounts: Picked[]; setAccounts: (fn: (a: Picked[]) => Picked[]) => void }) {
  const [q, setQ] = useState('');
  const { results, busy, error } = useLiveSearch<EntraUser>((term) => `/api/tenants/${tenantId}/users?q=${encodeURIComponent(term)}`, q);
  return (
    <>
      <Field label="Konten" hint="Name, UPN oder Mail eintippen und Konten hinzufügen. Höchstens 25 pro Vorgang.">
        <input type="search" value={q} onChange={(e) => setQ(e.target.value)} placeholder="Konto suchen …" />
      </Field>
      <ErrorBox error={error} />
      {busy && <Spinner label="Suche in Entra ID …" />}
      {results && results.length > 0 && (
        <div className="pick">
          {results.filter((u) => !accounts.some((a) => a.id === u.id)).slice(0, 8).map((u) => (
            <button key={u.id} disabled={!u.accountEnabled} onClick={() => { setAccounts((a) => [...a, { id: u.id, userPrincipalName: u.userPrincipalName, displayName: u.displayName }]); setQ(''); }}>
              <span><strong>{u.displayName ?? u.userPrincipalName}</strong><br /><span className="small muted">{u.userPrincipalName}</span></span>
              <span className="badges">{u.accountEnabled ? <Badge tone="info">hinzufügen</Badge> : <Badge tone="error">deaktiviert</Badge>}</span>
            </button>
          ))}
        </div>
      )}
      {accounts.length === 0 ? <p className="small muted">Noch keine Konten ausgewählt.</p> : (
        <div className="chips" style={{ marginTop: 8 }}>
          {accounts.map((a) => (
            <span className="chip" key={a.id}>
              {a.displayName ?? a.userPrincipalName}
              <button type="button" className="chip-x" aria-label={`${a.userPrincipalName} entfernen`} onClick={() => setAccounts((list) => list.filter((x) => x.id !== a.id))}>×</button>
            </span>
          ))}
        </div>
      )}
    </>
  );
}

/** Vier-Augen-Freigabe: vorhandene Freigabe wählen oder neue beantragen. */
function ApprovalBox({ tenantId, accounts, custodian, approvals, approvalId, setApprovalId, onRequested }: {
  tenantId: string; accounts: Picked[]; custodian: string; approvals: Approval[]; approvalId: string; setApprovalId: (v: string) => void; onRequested: () => void;
}) {
  const [reason, setReason] = useState('');
  const { busy, error, run } = useAction();
  const toast = useToast();
  const can = useCan();
  const { data: mine, reload } = useApi<{ approval: Approval }[]>(`/api/approvals?tenantId=${tenantId}`, [tenantId]);
  const pending = mine?.map((m) => m.approval).filter((a) => a.status === 'Pending') ?? [];
  const request = () => run(async () => {
    await api.post(`/api/tenants/${tenantId}/approvals`, { entraUserIds: accounts.map((a) => a.id), custodian, reason });
    toast.ok('Freigabe beantragt. Ein zweiter Admin muss sie unter „Freigaben“ erteilen.');
    setReason('');
    reload();
    onRequested();
  });
  return (
    <div className="alert info" style={{ margin: '12px 0' }}>
      <strong>Vier-Augen-Freigabe nötig</strong>
      <p className="small">Dieser Tenant verlangt für Breakglass-Keys die Freigabe eines zweiten Admins. Eine Freigabe gilt 24 Stunden und für einen Lauf.</p>
      {approvals.length > 0 ? (
        <Field label="Freigabe">
          <select value={approvalId} onChange={(e) => setApprovalId(e.target.value)}>
            {approvals.map((a) => <option key={a.id} value={a.id}>{a.userPrincipalNames.join(', ')} · freigegeben von {a.decidedByName} · gültig bis {fmtDate(a.validUntil)}</option>)}
          </select>
        </Field>
      ) : pending.length > 0 ? (
        <p>Offener Antrag: {pending.map((a) => <span key={a.id}><Badge tone="warn">{approvalLabels[a.status]}</Badge> {a.userPrincipalNames.join(', ')} </span>)}. Nach der Freigabe hier „Erneut prüfen“.</p>
      ) : can('RegisterKeys') && (
        <>
          <Field label="Grund *"><input value={reason} onChange={(e) => setReason(e.target.value)} placeholder="z. B. Neue Notfallkonten nach Audit" /></Field>
          <button disabled={!reason.trim() || busy} onClick={request}>Freigabe beantragen</button>
        </>
      )}
      <ErrorBox error={error} />
      {approvals.length === 0 && pending.length === 0 && mine && mine.length === 0 && <Empty>Noch keine Freigaben.</Empty>}
    </div>
  );
}
