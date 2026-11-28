import { useEffect, useState, type ReactNode } from 'react';
import { Link, useSearchParams } from 'react-router';
import { api, type AgentInfo, type Job, type KeyInfo, type Profile } from '../api';
import { Badge, Card, download, Empty, ErrorBox, Field, Info, PageHeader, Spinner, useAction, useApi, useLiveSearch, usePolling, Steps } from '../ui';
import { JobProgress } from './KeyDetail';
import { RegisterModes } from '../register';
import { ChooseTenant, useTenant } from '../tenant';
import { tr } from '../i18n';

export interface EntraUser { id: string; userPrincipalName: string; displayName?: string; mail?: string; department?: string; accountEnabled: boolean; activeKeys: number; entraPasskeys?: { displayName?: string; model?: string; isYubiKey: boolean }[] | null }
export interface Violation { code: string; message: string; canOverride: boolean; resolvedByReset: boolean }
export interface Preview {
  key: KeyInfo; violations: Violation[]; resetRequired: boolean; resetPlanned: boolean; willForcePinChange: boolean; displayName: string;
  inventoryKey?: { id: string; tenantId?: string | null; tenantName?: string; status: string; activeRegistrations: string[] } | null;
  user: { id: string; userPrincipalName: string; displayName?: string; department?: string; accountEnabled: boolean };
  userActiveKeys: number;
}

const stepNames = [tr("Person"), 'Key', tr("Profil & Prüfung"), tr("Registrierung"), tr("PIN-Brief")];

export default function WizardPage() {
  const [params] = useSearchParams();
  const [step, setStep] = useState(0);
  // Kundenebene: Tenant kommt aus dem Umschalter oben (Link mit ?tenantId= setzt ihn).
  const { tenantId, setTenantId } = useTenant();
  const paramTenant = params.get('tenantId');
  useEffect(() => { if (paramTenant) setTenantId(paramTenant); }, [paramTenant, setTenantId]);
  const [user, setUser] = useState<EntraUser>();
  const [agentId, setAgentId] = useState('');
  const [profileId, setProfileId] = useState('');
  const [suggestion, setSuggestion] = useState<{ profileId?: string | null; groupName?: string | null }>();
  const [preview, setPreview] = useState<Preview>();
  const [overrides, setOverrides] = useState<string[]>([]);
  const [performReset, setPerformReset] = useState(false);
  const [job, setJob] = useState<Job>();
  const { busy, error, run, setError } = useAction();

  usePolling(async () => {
    if (!job) return;
    const j = await api.get<Job>('/api/jobs/' + job.id);
    setJob(j);
    if (j.state === 'Succeeded' && j.pinLetterId) setStep(4);
  }, 1000, job?.state === 'Running');

  const restart = () => {
    setStep(0); setUser(undefined); setPreview(undefined); setOverrides([]); setPerformReset(false); setJob(undefined); setError(undefined);
  };

  // Group level: a profile assigned to one of the person's groups is preselected.
  const suggestProfile = async (u: EntraUser) => {
    setSuggestion(undefined);
    setProfileId('');
    try {
      const s = await api.get<{ profileId?: string | null; groupName?: string | null }>(`/api/tenants/${tenantId}/users/${encodeURIComponent(u.id)}/suggested-profile`);
      setSuggestion(s);
      if (s.profileId) setProfileId(s.profileId);
    } catch {
      /* no suggestion: the default profile applies */
    }
  };

  const loadPreview = () => run(async () => {
    const p = await api.post<Preview>(`/api/tenants/${tenantId}/registrations/preview`, { entraUserId: user!.id, profileId, agentId });
    setPreview(p);
    setPerformReset(p.resetPlanned);
    setOverrides([]);
  });

  const start = () => run(async () => {
    const j = await api.post<Job>(`/api/tenants/${tenantId}/registrations`, {
      entraUserId: user!.id, profileId, agentId, overrides, performReset, expectedSerialNumber: preview?.key.serialNumber ?? null,
    });
    setJob(j);
    setStep(3);
  });

  const canStart = !!preview && canStartPreview(preview, overrides, performReset);

  return (
    <>
      <PageHeader title={tr("Key registrieren")} subtitle={tr("Im Auftrag einer Person (Registrierung on Behalf)")} actions={step > 0 && <button onClick={restart}>{tr("Neu beginnen")}</button>} />
      {step === 0 && <RegisterModes />}
      <Steps names={stepNames} current={step} />
      <ErrorBox error={error} />

      {step === 0 && <PersonStep tenantId={tenantId} initialQuery={params.get('q') ?? ''} onPick={(u) => { setUser(u); setStep(1); void suggestProfile(u); }} />}

      {step === 1 && <KeyStep agentId={agentId} setAgentId={setAgentId} onNext={() => setStep(2)} onBack={() => setStep(0)} />}

      {step === 2 && user && (
        <ProfileStep tenantId={tenantId} profileId={profileId} suggestion={suggestion} setProfileId={(id) => { setProfileId(id); setPreview(undefined); }}>
          <div className="actions">
            <button onClick={() => setStep(1)}>{tr("Zurück")}</button>
            <button disabled={!profileId || busy} onClick={loadPreview}>{preview ? tr("Erneut prüfen") : tr("Prüfen")}</button>
          </div>
          {preview && (
            <div style={{ marginTop: 16 }}>
              <PreviewDetails preview={preview} overrides={overrides} setOverrides={setOverrides} performReset={performReset} setPerformReset={setPerformReset} />
              <button className="primary" disabled={!canStart || busy} onClick={start}>{tr("Registrierung starten")}</button>
            </div>
          )}
        </ProfileStep>
      )}

      {step === 3 && job && (
        <Card title={tr("Registrierung läuft")}>
          <JobProgress job={job} />
          {job.state === 'Failed' && <div className="actions"><button onClick={() => { setJob(undefined); setStep(2); }}>{tr("Zurück zur Prüfung")}</button></div>}
        </Card>
      )}

      {step === 4 && job && (
        <Card title={tr("Fertig")}>
          <Info tone="ok">{tr("Der Key ist für {n} registriert.", { n: user?.displayName ?? user?.userPrincipalName })}</Info>
          {job.notes.length > 0 && <ul className="small muted">{job.notes.map((n, i) => <li key={i}>{n}</li>)}</ul>}
          <p>{tr("Laden Sie den PIN-Brief jetzt herunter. Die PIN wird nicht gespeichert; der Brief ist 30 Minuten abrufbar.")}</p>
          <div className="actions">
            <button className="primary" onClick={() => download('/api/pin-letters/' + job.pinLetterId)}>{tr("PIN-Brief (PDF)")}</button>
            {job.keyId && <Link className="btn" to={'/keys/' + job.keyId}>{tr("Key ansehen")}</Link>}
            <button onClick={restart}>{tr("Nächsten Key registrieren")}</button>
          </div>
        </Card>
      )}
    </>
  );
}

function PersonStep({ tenantId, onPick, initialQuery = '' }: { tenantId: string; onPick: (u: EntraUser) => void; initialQuery?: string }) {
  const [q, setQ] = useState(initialQuery);
  const { results: users, busy, error } = useLiveSearch<EntraUser>(tenantId ? (term) => `/api/tenants/${tenantId}/users?q=${encodeURIComponent(term)}` : null, q);
  return (
    <Card title={tr("Für wen wird der Key registriert?")}>
      <div className="toolbar">
        <input type="search" placeholder={tr("Name, UPN oder Mail eintippen (ab 2 Zeichen)")} value={q} onChange={(e) => setQ(e.target.value)} style={{ flex: 1 }} autoFocus />
      </div>
      {!tenantId && <ChooseTenant purpose={tr("Für die Registrierung")} />}
      <ErrorBox error={error} />
      {busy && <Spinner label={tr("Suche in Entra ID …")} />}
      {!busy && users && users.length === 0 && <Empty>{tr("Keine Person gefunden.")}</Empty>}
      {users && users.length > 0 && (
        <div className="pick">
          {users.map((u) => (
            <button key={u.id} disabled={!u.accountEnabled} onClick={() => onPick(u)}>
              <span><strong>{u.displayName ?? u.userPrincipalName}</strong><br /><span className="small muted">{u.userPrincipalName}{u.department ? ' · ' + u.department : ''}</span></span>
              <span className="badges"><PersonKeys u={u} /></span>
            </button>
          ))}
        </div>
      )}
    </Card>
  );
}

/** Keys in the inventory plus passkeys Entra knows about (also ones registered elsewhere or not yet synced). */
function PersonKeys({ u }: { u: EntraUser }) {
  if (!u.accountEnabled) return <Badge tone="error">{tr("deaktiviert")}</Badge>;
  const entra = u.entraPasskeys ?? [];
  const yubi = entra.filter((p) => p.isYubiKey).length;
  const other = entra.length - yubi;
  const title = entra.map((p) => [p.displayName, p.model].filter(Boolean).join(' · ')).join('\n');
  const parts: ReactNode[] = [];
  if (u.activeKeys > 0) parts.push(<Badge key="inv" tone="info">{tr("{activeKeys} Key(s) im Inventar", { activeKeys: u.activeKeys })}</Badge>);
  if (yubi > u.activeKeys) parts.push(<span key="y" title={title}><Badge tone="warn">{tr("{yubi} YubiKey(s) in Entra", { yubi })}</Badge></span>);
  if (other > 0) parts.push(<span key="o" title={title}><Badge>{tr("{other} anderer Passkey{n}", { other, n: other > 1 ? 's' : '' })}</Badge></span>);
  if (u.entraPasskeys === null || u.entraPasskeys === undefined) parts.push(<Badge key="u">{tr("Entra-Status unbekannt")}</Badge>);
  if (parts.length === 0) parts.push(<Badge key="n">{tr("kein Key")}</Badge>);
  return <>{parts}</>;
}

export function KeyStep({ agentId, setAgentId, onNext, onBack }: { agentId: string; setAgentId: (v: string) => void; onNext: () => void; onBack: () => void }) {
  const { data: agents, error, reload } = useApi<AgentInfo[]>('/api/agents');
  usePolling(reload, 2000);
  const online = agents?.filter((a) => a.online) ?? [];
  const agent = online.find((a) => a.id === agentId) ?? online[0];
  useEffect(() => { if (agent && agent.id !== agentId) setAgentId(agent.id); }, [agent, agentId, setAgentId]);
  // Ask the tray app once per agent to show "insert key" while no key is present.
  const waitingFor = agent && !agent.key ? agent.id : undefined;
  useEffect(() => {
    if (waitingFor) void api.post(`/api/agents/${waitingFor}/request-key`, {}).catch(() => undefined);
  }, [waitingFor]);
  return (
    <Card title={tr("YubiKey einstecken")}>
      <ErrorBox error={error} />
      {agents && online.length === 0 && (
        <Info tone="warn">{tr("Auf Ihrem PC ist kein Agent verbunden. Starten Sie die Tray-App oder")} <Link to="/agents">{tr("koppeln Sie einen Agent")}</Link>.</Info>
      )}
      {online.length > 1 && (
        <Field label={tr("Agent")}>
          <select value={agent?.id} onChange={(e) => setAgentId(e.target.value)}>
            {online.map((a) => <option key={a.id} value={a.id}>{a.machineName}</option>)}
          </select>
        </Field>
      )}
      {agent && (agent.key ? (
        <div className={'keycard' + (!agent.key.hasFido2 ? ' error' : !agent.key.supportsForcePinChange ? ' warn' : '')}>
          <div className="keyglyph"><span /></div>
          <div>
            <strong>{agent.key.model ?? 'YubiKey'}</strong>
            <div>{tr("Seriennummer")} <span className="mono">{agent.key.serialNumber ?? '–'}</span> {tr("· Firmware")} <span className="mono">{agent.key.firmwareVersion}</span></div>
            <div className="small muted">{agent.key.pinIsSet ? tr("FIDO-PIN ist gesetzt") : tr("Keine FIDO-PIN gesetzt (Werkszustand)")}</div>
            {!agent.key.supportsForcePinChange && (
              <div className="key-warning"><span className="violation-icon" aria-hidden>!</span> {tr("Firmware {firmwareVersion}: Der PIN-Wechsel kann nicht erzwungen werden (erst ab 5.7).", { firmwareVersion: agent.key.firmwareVersion })}</div>
            )}
            {!agent.key.hasFido2 && <Badge tone="error">{tr("Kein FIDO2")}</Badge>}
          </div>
        </div>
      ) : (
        <div className="waiting-key">
          <Spinner />
          <div>
            <strong>{tr("Bitte jetzt den YubiKey an {machineName} einstecken.", { machineName: agent.machineName })}</strong>
            <div className="small muted">{tr("Der Agent wartet auf den Key. Bitte nur einen Key einstecken. Den Fortschritt zeigt auf Wunsch auch das Statusfenster der Tray-App (Doppelklick auf das Symbol).")}</div>
          </div>
        </div>
      ))}
      {agent?.busy && <Info tone="warn">{tr("Der Agent ist beschäftigt: {busy}", { busy: agent.busy })}</Info>}
      <div className="actions" style={{ marginTop: 12 }}>
        <button onClick={onBack}>{tr("Zurück")}</button>
        <button className="primary" disabled={!agent?.key || !!agent.busy} onClick={onNext}>{tr("Weiter")}</button>
      </div>
    </Card>
  );
}

export function ProfileStep({ tenantId, profileId, setProfileId, suggestion, children }: {
  tenantId: string; profileId: string; setProfileId: (v: string) => void; suggestion?: { profileId?: string | null; groupName?: string | null }; children: ReactNode;
}) {
  const { data: profiles, error } = useApi<Profile[]>('/api/profiles?tenantId=' + tenantId, [tenantId]);
  useEffect(() => {
    if (profiles && !profileId) {
      const def = profiles.find((p) => p.isDefault && p.tenantId === tenantId) ?? profiles.find((p) => p.isDefault) ?? profiles[0];
      if (def) setProfileId(def.id);
    }
  }, [profiles, profileId, setProfileId, tenantId]);
  const selected = profiles?.find((p) => p.id === profileId);
  return (
    <Card title={tr("Profil wählen und prüfen")}>
      <ErrorBox error={error} />
      <Field label={tr("Registrierungsprofil")}>
        <select value={profileId} onChange={(e) => setProfileId(e.target.value)}>
          {profiles?.map((p) => <option key={p.id} value={p.id}>{p.name}{p.tenantId ? '' : tr(" (alle Tenants)")}</option>)}
        </select>
      </Field>
      {suggestion?.profileId && suggestion.profileId === profileId && (
        <Info>{tr("Vorgeschlagen, weil die Person Mitglied der Gruppe")} <strong>{suggestion.groupName}</strong> {tr("ist (Profile nach Gruppe im Tenant).")}</Info>
      )}
      {selected && (
        <p className="small muted">
          {selected.pinStrategy === 'Random' ? tr("Zufällige PIN mit {pinLength} Ziffern", { pinLength: selected.pinLength }) : tr("Feste Standard-PIN")}
          {selected.forcePinChange ? tr(" · PIN-Wechsel erzwingen") : ''}
          {selected.factoryResetBeforeRegistration ? tr(" · Werksreset vorher") : ''}
          {selected.minimumFirmware ? tr(" · Firmware ab {minimumFirmware}", { minimumFirmware: selected.minimumFirmware }) : ''}
        </p>
      )}
      {children}
    </Card>
  );
}

/** No blocking violations left after reset and confirmed overrides, and the key is not registered for someone else. */
export function canStartPreview(preview: Preview, overrides: string[], performReset: boolean) {
  const blocking = preview.violations.filter((v) => !(v.resolvedByReset && performReset) && !overrides.includes(v.code));
  return blocking.length === 0 && !preview.inventoryKey?.activeRegistrations.length;
}

/** Result of the check before a registration: person, key, profile violations, overrides and the optional factory reset. */
export function PreviewDetails({ preview, overrides, setOverrides, performReset, setPerformReset }: {
  preview: Preview; overrides: string[]; setOverrides: (fn: (o: string[]) => string[]) => void; performReset: boolean; setPerformReset: (v: boolean) => void;
}) {
  return (
    <>
      <table>
        <tbody>
          <tr><th>{tr("Person")}</th><td>{preview.user.displayName} <span className="muted">({preview.user.userPrincipalName})</span> {!preview.user.accountEnabled && <Badge tone="error">{tr("deaktiviert")}</Badge>}</td></tr>
          <tr><th>{tr("Key")}</th><td>{tr("{model} · Seriennummer", { model: preview.key.model })} <span className="mono">{preview.key.serialNumber}</span> {tr("· Firmware")} <span className="mono">{preview.key.firmwareVersion}</span></td></tr>
          <tr><th>{tr("Anzeigename in Entra")}</th><td>{preview.displayName}</td></tr>
          <tr><th>{tr("PIN-Wechsel erzwingen")}</th><td>{preview.willForcePinChange ? tr("Ja") : tr("Nein")}</td></tr>
        </tbody>
      </table>
      {preview.userActiveKeys > 0 && <Info tone="warn">{tr("{displayName} hat bereits {userActiveKeys} aktive(n) Key(s). Die Registrierung fügt einen weiteren hinzu.", { displayName: preview.user.displayName, userActiveKeys: preview.userActiveKeys })}</Info>}
      {preview.inventoryKey && (
        preview.inventoryKey.activeRegistrations.length > 0
          ? <Info tone="error">{tr("Dieser Key ist bereits registriert für {n}. Bitte zuerst", { n: preview.inventoryKey.activeRegistrations.join(', ') })} <Link to={'/keys/' + preview.inventoryKey.id}>{tr("zurücksetzen")}</Link> {tr("oder weitere Konten über")} <Link to="/register/shared">{tr("Geteilter Key")}</Link> {tr("hinzufügen.")}</Info>
          : <Info>{tr("Key ist im Inventar (")}{preview.inventoryKey.tenantName ?? tr("Partnerlager, wird diesem Tenant zugewiesen")}{tr(", Status {status}) und wird zugewiesen.", { status: preview.inventoryKey.status })}</Info>
      )}
      {preview.violations.length === 0 ? <Info tone="ok">{tr("Der Key erfüllt das Profil.")}</Info> : (
        <div className="alert warn">
          <strong>{tr("Der Key passt nicht vollständig zum Profil:")}</strong>
          {preview.violations.map((v) => (
            <div key={v.code} className={'violation-row' + (!v.canOverride && !v.resolvedByReset ? ' blocking' : '')}>
              <span className="violation-icon" aria-hidden>!</span>
              <div>{v.message}{v.resolvedByReset && <div className="small muted">{tr("Wird durch einen Werksreset behoben.")}</div>}</div>
              {v.canOverride && !(v.resolvedByReset && performReset) && (
                <label className="check">
                  <input type="checkbox" checked={overrides.includes(v.code)} onChange={(e) => setOverrides((o) => e.target.checked ? [...o, v.code] : o.filter((x) => x !== v.code))} />
                  {tr("Für diesen Key bewusst überschreiben")}
                </label>
              )}
              {!v.canOverride && !v.resolvedByReset && <Badge tone="error">{tr("nicht überschreibbar")}</Badge>}
            </div>
          ))}
        </div>
      )}
      {/* A key in factory state needs no reset: only offer it when the key is already in use or the profile asks for it. */}
      {(preview.resetRequired || preview.resetPlanned || preview.key.pinIsSet) && <>
        <label className="check" style={{ margin: '12px 0' }}>
          <input type="checkbox" checked={performReset} disabled={preview.resetRequired} onChange={(e) => setPerformReset(e.target.checked)} />
          {tr("FIDO-Werksreset vor der Registrierung")} {preview.resetRequired && '(erforderlich)'}
        </label>
        {performReset && <Info tone="error"><strong>{tr("Achtung:")}</strong> {tr("Der Reset löscht alle Passkeys auf dem Key unwiderruflich. Der Key muss dafür kurz abgezogen und wieder eingesteckt werden.")}</Info>}
      </>}
    </>
  );
}
