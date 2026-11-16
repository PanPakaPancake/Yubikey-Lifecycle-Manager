import { useEffect, useState } from 'react';
import { api } from '../api';
import { Card, ErrorBox, Field, Info, Spinner, useAction } from '../ui';

interface SetupState {
  organizationName?: string; publicBaseUrl: string; bootstrapOwnerUpn?: string; clientId?: string; homeTenantId?: string;
  certificateThumbprint?: string; certificateExpiresAt?: string; redirectUris: string[]; requiredPermissions: string[];
}

const steps = ['Setup-Token', 'Grunddaten', 'Zertifikat', 'App-Registrierung', 'Abschluss'];

export default function SetupPage() {
  const [token, setToken] = useState(() => sessionStorage.getItem('ylm-setup-token') ?? new URLSearchParams(window.location.search).get('token') ?? '');
  const [state, setState] = useState<SetupState>();
  const [step, setStep] = useState(0);
  const [done, setDone] = useState(false);
  const { busy, error, run } = useAction();
  const h = { 'X-Setup-Token': token };

  const [org, setOrg] = useState('');
  const [baseUrl, setBaseUrl] = useState('');
  const [owner, setOwner] = useState('');
  const [clientId, setClientId] = useState('');
  const [tenantId, setTenantId] = useState('');
  const [test, setTest] = useState<{ ok: boolean; message: string; granted: string[]; missing: string[] }>();

  useEffect(() => {
    void fetch('/api/setup/status').then((r) => r.json()).then((s) => { if (s.setupComplete) setDone(true); });
  }, []);

  const loadState = async () => {
    const s = await api.get<SetupState>('/api/setup/state', h);
    setState(s);
    setOrg(s.organizationName ?? '');
    setBaseUrl(s.publicBaseUrl ?? '');
    setOwner(s.bootstrapOwnerUpn ?? '');
    setClientId(s.clientId ?? '');
    setTenantId(s.homeTenantId ?? '');
    return s;
  };

  const login = () => run(async () => {
    await loadState();
    try { sessionStorage.setItem('ylm-setup-token', token); } catch { /* ignore */ }
    setStep(1);
  });

  const saveBasics = () => run(async () => {
    await api.post('/api/setup/basics', { organizationName: org, publicBaseUrl: baseUrl, bootstrapOwnerUpn: owner }, h);
    await loadState();
    setStep(2);
  });

  const createCert = () => run(async () => {
    await api.post('/api/setup/certificate', {}, h);
    await loadState();
  });

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
    setTest(await api.post('/api/setup/app-registration', { clientId, homeTenantId: tenantId }, h));
    await loadState();
  });

  const complete = () => run(async () => {
    await api.post('/api/setup/complete', {}, h);
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

  return (
    <div className="main" style={{ maxWidth: 860, margin: '0 auto' }}>
      <h1>Einrichtung der Installation</h1>
      <p className="muted">Einmalig: Grunddaten, App-Zertifikat und die App-Registrierung im Heim-Tenant anlegen.</p>
      <ol className="steps">
        {steps.map((s, i) => <li key={s} className={'step ' + (i < step ? 'done' : i === step ? 'current' : '')}>{s}</li>)}
      </ol>
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
          <Field label="Erster Owner (UPN)" hint="Dieses Konto wird bei der ersten Anmeldung automatisch Owner.">
            <input value={owner} onChange={(e) => setOwner(e.target.value)} placeholder="admin@contoso.com" />
          </Field>
          <button className="primary" disabled={!org || !baseUrl || !owner || busy} onClick={saveBasics}>Speichern und weiter</button>
        </Card>
      )}

      {step === 2 && state && (
        <Card title="App-Zertifikat">
          <p>Die Plattform meldet sich mit einem Zertifikat bei Microsoft Graph an. Der private Schlüssel verlässt den Server nicht.</p>
          {state.certificateThumbprint ? (
            <Info tone="ok">Zertifikat vorhanden: <code>{state.certificateThumbprint}</code>, gültig bis {new Date(state.certificateExpiresAt!).toLocaleDateString('de-DE')}</Info>
          ) : (
            <button className="primary" disabled={busy} onClick={createCert}>Zertifikat erzeugen</button>
          )}
          <div className="actions" style={{ marginTop: 12 }}>
            <button disabled={!state.certificateThumbprint} onClick={() => downloadWithToken('/api/setup/certificate.cer', 'ylm-app.cer')}>Öffentliches Zertifikat (.cer)</button>
            <button className="primary" disabled={!state.certificateThumbprint} onClick={() => setStep(3)}>Weiter</button>
          </div>
        </Card>
      )}

      {step === 3 && state && (
        <Card title="App-Registrierung im Heim-Tenant">
          <p>Führen Sie das Skript als Globaler Administrator oder Privileged Role Administrator in PowerShell aus. Es legt die mandantenfähige App an, lädt das Zertifikat hoch und erteilt den Admin-Consent.</p>
          <button onClick={() => downloadWithToken('/api/setup/app-registration.ps1', 'ylm-app-registration.ps1')}>Skript herunterladen</button>
          <details style={{ margin: '12px 0' }}>
            <summary>Manuell anlegen</summary>
            <ol>
              <li>Entra Admin Center → App-Registrierungen → Neue Registrierung, Kontotyp „Konten in einem beliebigen Organisationsverzeichnis“.</li>
              <li>Umleitungs-URIs (Web): {state.redirectUris.map((u) => <code key={u} style={{ display: 'block' }}>{u}</code>)}</li>
              <li>Zertifikat (.cer aus dem vorherigen Schritt) unter „Zertifikate &amp; Geheimnisse“ hochladen.</li>
              <li>Anwendungsberechtigungen für Microsoft Graph: {state.requiredPermissions.map((p) => <code key={p} style={{ display: 'block' }}>{p}</code>)}</li>
              <li>Delegierte Berechtigungen: <code>openid</code>, <code>profile</code>, <code>email</code>; dann Admin-Consent erteilen.</li>
            </ol>
          </details>
          <div className="grid cols-2">
            <Field label="Anwendungs-ID (Client-ID)"><input value={clientId} onChange={(e) => setClientId(e.target.value.trim())} /></Field>
            <Field label="Verzeichnis-ID (Heim-Tenant)"><input value={tenantId} onChange={(e) => setTenantId(e.target.value.trim())} /></Field>
          </div>
          <button className="primary" disabled={!clientId || !tenantId || busy} onClick={testApp}>{busy ? <Spinner label="Wird geprüft …" /> : 'Speichern und testen'}</button>
          {test && (
            <div style={{ marginTop: 12 }}>
              <Info tone={test.ok && test.missing.length === 0 ? 'ok' : 'warn'}>{test.message}</Info>
              {test.missing.length > 0 && <p>Fehlende Berechtigungen: {test.missing.map((m) => <code key={m}>{m} </code>)}</p>}
              {test.ok && <button className="primary" onClick={() => setStep(4)}>Weiter</button>}
            </div>
          )}
        </Card>
      )}

      {step === 4 && state && (
        <Card title="Abschluss">
          <ul>
            <li>Organisation: <strong>{state.organizationName}</strong></li>
            <li>Adresse: <code>{state.publicBaseUrl}</code></li>
            <li>Erster Owner: <strong>{state.bootstrapOwnerUpn}</strong></li>
            <li>App: <code>{state.clientId}</code> im Tenant <code>{state.homeTenantId}</code></li>
          </ul>
          <Info tone="warn">Nach dem Abschluss ist das Setup-Token ungültig. Weitere Änderungen nehmen Owner in der Plattform vor.</Info>
          <button className="primary" disabled={busy} onClick={complete}>Einrichtung abschließen</button>
        </Card>
      )}
    </div>
  );
}
