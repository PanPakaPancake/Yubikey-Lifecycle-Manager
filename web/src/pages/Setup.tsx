import { useEffect, useRef, useState } from 'react';
import { api } from '../api';
import { Card, ErrorBox, Field, Info, Spinner, useAction, Steps } from '../ui';

interface SignedInAdmin { upn: string; displayName: string; tenantId: string; organizationName: string; defaultDomain?: string }

interface SetupState {
  organizationName?: string; publicBaseUrl: string; bootstrapOwnerUpn?: string; clientId?: string; homeTenantId?: string;
  certificateThumbprint?: string; certificateExpiresAt?: string; redirectUris: string[]; requiredPermissions: string[];
  signedInAdmin?: SignedInAdmin;
}

interface DeviceCode { userCode: string; verificationUri: string; expiresAt: string; intervalSeconds: number }
interface PollResult { status: 'pending' | 'signed_in' | 'declined' | 'expired' | 'failed'; message?: string; admin?: SignedInAdmin }
interface TestResult { ok: boolean; message: string; granted: string[]; missing: string[] }

interface Interactive { url: string; redirectUri: string; automatic: boolean }

type Phase = 'idle' | 'interactive' | 'code' | 'provisioning' | 'verifying' | 'done' | 'failed';

const steps = ['Setup-Token', 'Grunddaten', 'Microsoft-Anmeldung', 'Abschluss'];
// Entra needs some time until a new certificate and new consent work for app-only sign-ins.
const verifyAttempts = 12;
const verifyDelayMs = 10_000;

const sleep = (ms: number) => new Promise((r) => setTimeout(r, ms));

export default function SetupPage() {
  const [token, setToken] = useState(() => sessionStorage.getItem('ylm-setup-token') ?? new URLSearchParams(window.location.search).get('token') ?? '');
  const [state, setState] = useState<SetupState>();
  const [step, setStep] = useState(0);
  const [done, setDone] = useState(false);
  const { busy, error, run, setError } = useAction();
  const h = { 'X-Setup-Token': token };

  const [org, setOrg] = useState('');
  const [baseUrl, setBaseUrl] = useState('');
  const [owner, setOwner] = useState('');
  const [addHomeTenant, setAddHomeTenant] = useState(true);

  // Automatic path: sign-in with Microsoft and app registration in the background.
  const [phase, setPhase] = useState<Phase>('idle');
  const [code, setCode] = useState<DeviceCode>();
  const [interactive, setInteractive] = useState<Interactive>();
  const [pasted, setPasted] = useState('');
  const [copied, setCopied] = useState(false);
  const [admin, setAdmin] = useState<SignedInAdmin>();
  const [provisionSteps, setProvisionSteps] = useState<string[]>([]);
  const [verifyInfo, setVerifyInfo] = useState<string>();
  const [phaseError, setPhaseError] = useState<string>();
  const cancelled = useRef(false);

  // Fallback: script or manual app registration.
  const [clientId, setClientId] = useState('');
  const [tenantId, setTenantId] = useState('');
  const [test, setTest] = useState<TestResult>();

  useEffect(() => {
    void fetch('/api/setup/status').then((r) => r.json()).then((s) => { if (s.setupComplete) setDone(true); });
    return () => { cancelled.current = true; };
  }, []);

  const loadState = async () => {
    const s = await api.get<SetupState>('/api/setup/state', h);
    setState(s);
    setOrg(s.organizationName ?? '');
    setBaseUrl(s.publicBaseUrl ?? '');
    setOwner(s.bootstrapOwnerUpn ?? '');
    setClientId(s.clientId ?? '');
    setTenantId(s.homeTenantId ?? '');
    if (s.signedInAdmin) setAdmin(s.signedInAdmin);
    return s;
  };

  const login = () => run(async () => {
    await loadState();
    try { sessionStorage.setItem('ylm-setup-token', token); } catch { /* ignore */ }
    setStep(1);
  });

  const saveBasics = () => run(async () => {
    await api.post('/api/setup/basics', { organizationName: org, publicBaseUrl: baseUrl }, h);
    await loadState();
    setStep(2);
  });

  const fail = (message: string) => {
    setPhaseError(message);
    setPhase('failed');
  };

  const verify = async () => {
    setPhase('verifying');
    for (let i = 1; i <= verifyAttempts && !cancelled.current; i++) {
      setVerifyInfo(`Anmeldung der Plattform mit dem Zertifikat wird geprüft (Versuch ${i} von ${verifyAttempts}) …`);
      try {
        const r = await api.post<TestResult>('/api/setup/verify', {}, h);
        if (r.ok && r.missing.length === 0) {
          setVerifyInfo(undefined);
          await loadState();
          setPhase('done');
          return;
        }
        if (i === verifyAttempts) {
          fail(r.ok ? 'Es fehlen noch Berechtigungen: ' + r.missing.join(', ') : r.message);
          return;
        }
      } catch (e) {
        if (i === verifyAttempts) { fail(e instanceof Error ? e.message : String(e)); return; }
      }
      await sleep(verifyDelayMs);
    }
  };

  const provision = async () => {
    setPhase('provisioning');
    try {
      const r = await api.post<{ steps: string[] }>('/api/setup/provision', {}, h);
      setProvisionSteps(r.steps);
      await loadState();
    } catch (e) {
      fail(e instanceof Error ? e.message : String(e));
      return;
    }
    await verify();
  };

  // Interaktive Anmeldung wie Connect-MgGraph: Microsoft leitet auf http://localhost zurück.
  const startSignIn = () => run(async () => {
    setPhaseError(undefined);
    setProvisionSteps([]);
    setPasted('');
    const r = await api.post<Interactive>('/api/setup/signin/interactive', {}, h);
    setInteractive(r);
    window.open(r.url, 'ylm-signin', 'width=520,height=720');
    setPhase('interactive');
  });

  const finishPasted = () => run(async () => {
    const r = await api.post<PollResult>('/api/setup/signin/complete', { url: pasted });
    if (r.status === 'signed_in') {
      setAdmin(r.admin);
      void provision();
    } else {
      throw new Error(r.message ?? 'Die Anmeldung ist fehlgeschlagen.');
    }
  });

  // Ausweichweg: Anmeldung mit Code (Device Code Flow), in vielen Tenants per Conditional Access gesperrt.
  const startDeviceCode = () => run(async () => {
    setPhaseError(undefined);
    setProvisionSteps([]);
    setCopied(false);
    setCode(await api.post<DeviceCode>('/api/setup/signin/start', {}, h));
    setPhase('code');
  });

  // Poll the sign-in while the code is shown; the server only asks Microsoft at the allowed interval.
  useEffect(() => {
    if (phase !== 'code' && phase !== 'interactive') return;
    let stop = false;
    const tick = async () => {
      while (!stop && !cancelled.current) {
        await sleep(3000);
        if (stop) return;
        try {
          const r = await api.post<PollResult>('/api/setup/signin/poll', {}, h);
          if (stop) return;
          if (r.status === 'signed_in') {
            setAdmin(r.admin);
            void provision();
            return;
          }
          if (r.status !== 'pending') { fail(r.message ?? 'Die Anmeldung ist fehlgeschlagen.'); return; }
        } catch (e) {
          fail(e instanceof Error ? e.message : String(e));
          return;
        }
      }
    };
    void tick();
    return () => { stop = true; };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [phase]);

  const copyAndOpen = async () => {
    if (!code) return;
    try { await navigator.clipboard.writeText(code.userCode); setCopied(true); } catch { /* clipboard not allowed */ }
    window.open(code.verificationUri, 'ylm-devicelogin', 'width=520,height=720');
  };

  const downloadWithToken = (url: string, filename: string) => run(async () => {
    const res = await fetch(url, { headers: h });
    if (!res.ok) throw new Error('Download fehlgeschlagen');
    const blob = await res.blob();
    const a = document.createElement('a');
    a.href = URL.createObjectURL(blob);
    a.download = filename;
    a.click();
    URL.revokeObjectURL(a.href);
  });

  const testApp = () => run(async () => {
    const r = await api.post<TestResult>('/api/setup/app-registration', { clientId, homeTenantId: tenantId }, h);
    setTest(r);
    await loadState();
  });

  const complete = () => run(async () => {
    await api.post('/api/setup/complete', { bootstrapOwnerUpn: owner, addHomeTenant }, h);
    try { sessionStorage.removeItem('ylm-setup-token'); } catch { /* ignore */ }
    setDone(true);
  });

  if (done) {
    return (
      <div className="center-page">
        <div className="login-card card">
          <h1>Einrichtung abgeschlossen</h1>
          <p>Melden Sie sich jetzt mit dem Konto des ersten Owners an{owner ? <> (<strong>{owner}</strong>)</> : null}.</p>
          <a className="btn primary" href="/login">Zur Anmeldung</a>
        </div>
      </div>
    );
  }

  const homeTenantName = admin && admin.tenantId === state?.homeTenantId ? admin.organizationName : undefined;
  const working = phase === 'interactive' || phase === 'code' || phase === 'provisioning' || phase === 'verifying';

  return (
    <div className="main" style={{ maxWidth: 860, margin: '0 auto' }}>
      <h1>Einrichtung der Installation</h1>
      <p className="muted">Einmalig: Grunddaten festlegen, bei Microsoft anmelden, und die Plattform legt ihre App-Registrierung im Heim-Tenant selbst an.</p>
      <Steps names={steps} current={step} />
      <ErrorBox error={error} />

      {step === 0 && (
        <Card title="Setup-Token">
          <p>Das Setup-Token steht beim ersten Start im Server-Log (<code>docker compose logs server</code>).</p>
          <Field label="Setup-Token"><input value={token} onChange={(e) => setToken(e.target.value.trim())} autoFocus /></Field>
          <button className="primary" disabled={!token || busy} onClick={login}>Weiter</button>
        </Card>
      )}

      {step === 1 && (
        <Card title="Grunddaten">
          <Field label="Name der Organisation"><input value={org} onChange={(e) => setOrg(e.target.value)} /></Field>
          <Field label="Öffentliche Adresse" hint="Unter dieser HTTPS-Adresse erreichen Admins und Agents die Plattform.">
            <input value={baseUrl} onChange={(e) => setBaseUrl(e.target.value)} placeholder="https://ylm.example.com" />
          </Field>
          <button className="primary" disabled={!org || !baseUrl || busy} onClick={saveBasics}>Speichern und weiter</button>
        </Card>
      )}

      {step === 2 && state && (
        <>
          <Card title="Bei Microsoft anmelden">
            {phase === 'idle' && (
              <>
                <p>
                  <strong>Voraussetzung:</strong> ein Konto des Heim-Tenants mit der Rolle <strong>Globaler Administrator</strong>
                  (alternativ Anwendungsadministrator zusammen mit Privileged Role Administrator, für den Admin-Consent der Anwendungsberechtigungen).
                  Nach der Anmeldung legt die Plattform im Hintergrund an:
                </p>
                <ul>
                  <li>das App-Zertifikat (der private Schlüssel bleibt auf dem Server),</li>
                  <li>die mandantenfähige App-Registrierung mit Umleitungs-URIs und Zertifikat,</li>
                  <li>alle benötigten Berechtigungen inklusive Admin-Consent.</li>
                </ul>
                {state.clientId && (
                  <Info tone="info">
                    Es ist bereits eine App-Registrierung hinterlegt (<code>{state.clientId}</code>). Eine erneute Anmeldung im selben Tenant aktualisiert diese App.
                    {' '}<button className="link" onClick={() => void verify()}>Nur prüfen</button>
                  </Info>
                )}
                <button className="primary" disabled={busy} onClick={startSignIn}>{busy ? <Spinner label="Wird gestartet …" /> : 'Bei Microsoft anmelden'}</button>
                <p className="muted" style={{ marginTop: 8 }}>
                  Die Anmeldung läuft wie bei <code>Connect-MgGraph</code> über die Microsoft-App „Microsoft Graph Command Line Tools“, weil die Plattform vor der Einrichtung noch keine eigene App hat.
                  Microsoft fragt dabei die Berechtigungen zum Anlegen der App ab. Ihr Konto wird nur für diese Einrichtung verwendet, das Token wird nicht gespeichert.
                </p>
              </>
            )}

            {phase === 'interactive' && interactive && (
              <>
                <p>1. Im geöffneten Fenster mit dem Admin-Konto anmelden und die angefragten Berechtigungen bestätigen.
                  {' '}<a href={interactive.url} target="ylm-signin" rel="noreferrer">Fenster erneut öffnen</a></p>
                {interactive.automatic ? (
                  <>
                    <p>2. Danach geht es hier automatisch weiter.</p>
                    <Spinner label="Warte auf die Anmeldung bei Microsoft …" />
                  </>
                ) : (
                  <>
                    <p>
                      2. Microsoft leitet danach auf <code>http://localhost/?code=…</code> weiter. Der Browser zeigt dort „Seite nicht erreichbar“, das ist richtig.
                      Bitte die <strong>komplette Adresse aus der Adresszeile</strong> kopieren und hier einfügen:
                    </p>
                    <Field label="Adresse aus der Adresszeile">
                      <input value={pasted} onChange={(e) => setPasted(e.target.value.trim())} placeholder="http://localhost/?code=…&state=…" autoFocus />
                    </Field>
                    <button className="primary" disabled={busy || !pasted.includes('code=')} onClick={finishPasted}>{busy ? <Spinner label="Wird geprüft …" /> : 'Anmeldung abschließen'}</button>
                  </>
                )}
                <div className="actions" style={{ marginTop: 12 }}>
                  <button className="link" onClick={() => setPhase('idle')}>Abbrechen</button>
                </div>
              </>
            )}

            {phase === 'code' && code && (
              <>
                <p>1. Code kopieren und die Microsoft-Anmeldeseite öffnen:</p>
                <div style={{ display: 'flex', gap: 12, alignItems: 'center', flexWrap: 'wrap', margin: '8px 0 16px' }}>
                  <code style={{ fontSize: '1.6rem', letterSpacing: '.15em', padding: '6px 14px' }}>{code.userCode}</code>
                  <button className="primary" onClick={() => void copyAndOpen()}>{copied ? 'Kopiert, Seite geöffnet' : 'Code kopieren und Anmeldeseite öffnen'}</button>
                </div>
                <p>2. Dort den Code einfügen, mit dem Admin-Konto anmelden und die angefragten Berechtigungen bestätigen.</p>
                <p className="muted">Falls sich kein Fenster öffnet: <a href={code.verificationUri} target="_blank" rel="noreferrer">{code.verificationUri}</a>. Der Code ist bis {new Date(code.expiresAt).toLocaleTimeString('de-DE', { hour: '2-digit', minute: '2-digit' })} Uhr gültig.</p>
                <Spinner label="Warte auf die Anmeldung bei Microsoft …" />
              </>
            )}

            {(phase === 'provisioning' || phase === 'verifying' || phase === 'done' || phase === 'failed') && (
              <>
                {admin && <p>Angemeldet als <strong>{admin.displayName || admin.upn}</strong> ({admin.upn}) im Tenant <strong>{admin.organizationName}</strong>.</p>}
                {phase === 'provisioning' && <Spinner label="App-Registrierung, Berechtigungen und Admin-Consent werden angelegt …" />}
                {provisionSteps.length > 0 && <ul>{provisionSteps.map((s) => <li key={s}>✓ {s}</li>)}</ul>}
                {phase === 'verifying' && verifyInfo && <Spinner label={verifyInfo} />}
                {phase === 'done' && <Info tone="ok">Fertig: Die Plattform meldet sich mit ihrem Zertifikat an, alle Berechtigungen sind erteilt.</Info>}
                {phase === 'failed' && (
                  <>
                    <Info tone="error">{phaseError}</Info>
                    <div className="actions" style={{ marginTop: 8 }}>
                      <button className="primary" onClick={() => { setPhase('idle'); setError(undefined); }}>Erneut versuchen</button>
                      {state.clientId && <button onClick={() => void verify()}>Nur erneut prüfen</button>}
                    </div>
                  </>
                )}
              </>
            )}

            {phase === 'done' && (
              <div className="actions" style={{ marginTop: 12 }}>
                <button className="primary" onClick={() => setStep(3)}>Weiter</button>
              </div>
            )}
          </Card>

          <details style={{ margin: '16px 0' }}>
            <summary>Alternativ: per PowerShell-Skript, manuell oder mit Code anmelden</summary>
            <Card>
              <p>
                Nur nötig, wenn die Anmeldung oben bei Ihnen nicht geht (z. B. weil die Microsoft-App „Microsoft Graph Command Line Tools“ gesperrt ist) oder Sie die App selbst anlegen möchten.
                Das Skript als Globaler Administrator in PowerShell ausführen (<code>Connect-MgGraph</code> meldet Sie im Browser an). Es legt die mandantenfähige App an, lädt das Zertifikat hoch und erteilt den Admin-Consent.
              </p>
              <div className="actions">
                <button onClick={() => downloadWithToken('/api/setup/app-registration.ps1', 'ylm-app-registration.ps1')}>Skript herunterladen</button>
                <button onClick={() => downloadWithToken('/api/setup/certificate.cer', 'ylm-app.cer')}>Öffentliches Zertifikat (.cer)</button>
              </div>
              <details style={{ margin: '12px 0' }}>
                <summary>Manuell im Entra Admin Center anlegen</summary>
                <ol>
                  <li>Entra Admin Center → App-Registrierungen → Neue Registrierung, Kontotyp „Konten in einem beliebigen Organisationsverzeichnis“.</li>
                  <li>Umleitungs-URIs (Web): {state.redirectUris.map((u) => <code key={u} style={{ display: 'block' }}>{u}</code>)}</li>
                  <li>Zertifikat (.cer, Button oben) unter „Zertifikate &amp; Geheimnisse“ hochladen.</li>
                  <li>Anwendungsberechtigungen für Microsoft Graph: {state.requiredPermissions.map((p) => <code key={p} style={{ display: 'block' }}>{p}</code>)}</li>
                  <li>Delegierte Berechtigungen: <code>openid</code>, <code>profile</code>, <code>email</code>, <code>User.Read</code>; dann Admin-Consent erteilen.</li>
                </ol>
              </details>
              <div className="grid cols-2">
                <Field label="Anwendungs-ID (Client-ID)"><input value={clientId} onChange={(e) => setClientId(e.target.value.trim())} /></Field>
                <Field label="Verzeichnis-ID (Heim-Tenant)"><input value={tenantId} onChange={(e) => setTenantId(e.target.value.trim())} /></Field>
              </div>
              <button className="primary" disabled={!clientId || !tenantId || busy || working} onClick={testApp}>{busy ? <Spinner label="Wird geprüft …" /> : 'Speichern und testen'}</button>
              {test && (
                <div style={{ marginTop: 12 }}>
                  <Info tone={test.ok && test.missing.length === 0 ? 'ok' : 'warn'}>{test.message}</Info>
                  {test.missing.length > 0 && <p>Fehlende Berechtigungen: {test.missing.map((m) => <code key={m}>{m} </code>)}</p>}
                  {test.ok && <button className="primary" onClick={() => setStep(3)}>Weiter</button>}
                </div>
              )}
              <details style={{ marginTop: 16 }}>
                <summary>Anmeldung mit Code (Device Code)</summary>
                <p className="muted">Für Rechner ohne Browser. In vielen Tenants per Conditional Access gesperrt („Authentifizierungsflüsse“).</p>
                <button disabled={busy || working} onClick={startDeviceCode}>Mit Code anmelden</button>
              </details>
            </Card>
          </details>
        </>
      )}

      {step === 3 && state && (
        <Card title="Abschluss">
          <Field label="Erster Owner (UPN)" hint="Dieses Konto wird bei der ersten Anmeldung automatisch Owner. Vorgeschlagen ist das Konto, mit dem Sie sich eben angemeldet haben.">
            <input value={owner} onChange={(e) => setOwner(e.target.value.trim())} placeholder="admin@contoso.com" />
          </Field>
          <label style={{ display: 'flex', gap: 8, alignItems: 'flex-start', margin: '12px 0' }}>
            <input type="checkbox" checked={addHomeTenant} onChange={(e) => setAddHomeTenant(e.target.checked)} style={{ width: 'auto', marginTop: 4 }} />
            <span>
              Heim-Tenant <strong>{homeTenantName ?? state.homeTenantId}</strong> gleich als ersten Tenant anbinden
              <span className="muted" style={{ display: 'block' }}>Der Admin-Consent ist hier schon erteilt. Die Tenant-Prüfung (FIDO2-Richtlinie, YubiKey-Modelle) läuft direkt danach.</span>
            </span>
          </label>
          <ul>
            <li>Organisation: <strong>{state.organizationName}</strong></li>
            <li>Adresse: <code>{state.publicBaseUrl}</code></li>
            <li>App: <code>{state.clientId}</code> im Tenant <code>{state.homeTenantId}</code></li>
          </ul>
          <Info tone="warn">Nach dem Abschluss ist das Setup-Token ungültig. Weitere Änderungen nehmen Owner in der Plattform vor.</Info>
          <button className="primary" disabled={busy || !owner.includes('@')} onClick={complete}>Einrichtung abschließen</button>
        </Card>
      )}
    </div>
  );
}
