import { useEffect, useState } from 'react';
import { api } from '../api';
import { ErrorBox, Field, useAction } from '../ui';
import { BrandMark, HeroKey } from '../brand';
import { Icon } from '../icons';
import { getPasskey, passkeyError, passkeysSupported } from '../webauthn';

const errorTexts: Record<string, string> = {
  not_registered: 'Ihr Konto ist für diese Plattform nicht freigeschaltet oder deaktiviert. Bitte wenden Sie sich an einen Owner.',
  not_phishing_resistant: 'Bitte melden Sie sich mit Ihrem YubiKey (Passkey) an. Andere Anmeldemethoden sind nicht zulässig.',
  wrong_tenant: 'Die Anmeldung ist nur mit Konten aus dem Heim-Tenant der Installation möglich.',
  invalid_token: 'Die Anmeldung konnte nicht geprüft werden.',
  remote_failure: 'Die Anmeldung bei Microsoft ist fehlgeschlagen.',
  denied: 'Die Anmeldung wurde abgelehnt.',
  sso_disabled: 'Die Anmeldung mit Microsoft ist abgeschaltet. Bitte mit Passkey anmelden.',
  sso_not_configured: 'Für die Anmeldung mit Microsoft ist keine Entra-App eingetragen. Bitte den Setup-Assistenten abschließen oder mit Passkey anmelden.',
};

type Methods = { entraSso: boolean; passkey: boolean; emergency: boolean };

export default function LoginPage({ setupComplete }: { setupComplete: boolean }) {
    const params = new URLSearchParams(window.location.search);
  const returnUrl = params.get('returnUrl') ?? '/';
  const err = params.get('error');
  const [devUpn, setDevUpn] = useState('admin@contoso.dev');
  const [devError, setDevError] = useState<string>();
  const [devAvailable, setDevAvailable] = useState(true);
  const [methods, setMethods] = useState<Methods>({ entraSso: true, passkey: false, emergency: false });
  useEffect(() => { void api.get<Methods>('/auth/methods').then(setMethods).catch(() => {}); }, []);
  const passkey = useAction();
  const emergency = useAction();
  const [em, setEm] = useState({ upn: '', password: '', code: '' });

  const passkeyLogin = () => passkey.run(async () => {
    const { requestId, options } = await api.post<{ requestId: string; options: Record<string, unknown> }>('/auth/passkey/options');
    let response;
    try { response = await getPasskey(options); } catch (e) { throw new Error(passkeyError(e)); }
    await api.post('/auth/passkey/login', { requestId, response });
    window.location.href = returnUrl;
  });
  const emergencyLogin = () => emergency.run(async () => {
    await api.post('/auth/emergency', em);
    window.location.href = returnUrl;
  });

  const devLogin = async () => {
    try {
      await api.post('/auth/dev-login', { upn: devUpn });
      window.location.href = returnUrl;
    } catch (e) {
      if (e instanceof Error && 'status' in e && (e as { status: number }).status === 404) setDevAvailable(false);
      setDevError(e instanceof Error ? e.message : String(e));
    }
  };

  return (
    <div className="login-page">
      <aside className="login-hero">
        <div className="brand"><BrandMark size={38} /><div>Yubikey Lifecycle Manager</div></div>
        <div>
          <HeroKey />
          <h2>Jeder Key an der richtigen Stelle.</h2>
          <p>Ausgeben, registrieren, sperren und nachverfolgen, für alle Ihre Entra-Tenants an einem Ort.</p>
          <ul>
            <li><Icon name="shield" />Anmeldung nur phishing-resistent per YubiKey</li>
            <li><Icon name="users" />Auf einen Blick: wer hat welchen Key</li>
            <li><Icon name="scroll" />Lückenloses Audit-Log</li>
          </ul>
        </div>
        <div className="small">Selbst gehostet · Ihre Daten bleiben bei Ihnen</div>
      </aside>
      <div className="login-side">
      <div className="login-card card">
        <div className="brand compact"><BrandMark /><div>Yubikey Lifecycle Manager</div></div>
        <h1>Anmelden</h1>
        {!setupComplete && <div className="alert warn">Die Einrichtung ist noch nicht abgeschlossen. <a href="/setup">Zur Einrichtung</a></div>}
        {err && <ErrorBox error={errorTexts[err] ?? decodeURIComponent(err)} />}
        <p className="muted">Melden Sie sich mit Ihrem YubiKey an. <a href="/passkey">Was ist ein Passkey?</a></p>
        <div className="login-buttons">
          {methods.entraSso && (
            <a className="btn primary" href={'/auth/login?returnUrl=' + encodeURIComponent(returnUrl)}>Mit Microsoft anmelden</a>
          )}
          {(methods.passkey || !methods.entraSso) && passkeysSupported() && (
            <button className={methods.entraSso ? '' : 'primary'} disabled={passkey.busy} onClick={passkeyLogin}>
              {passkey.busy ? 'Schlüssel berühren …' : 'Mit Passkey anmelden'}
            </button>
          )}
        </div>
        {methods.entraSso && (methods.passkey) && <p className="small muted">Microsoft für Konten aus dem Entra-Tenant, Passkey für lokale Konten der Plattform.</p>}
        <ErrorBox error={passkey.error} />
        {methods.emergency && (
          <details style={{ marginTop: 18 }}>
            <summary className="muted small">Notfallzugang (Passwort und Code)</summary>
            <form onSubmit={(e) => { e.preventDefault(); emergencyLogin(); }}>
              <Field label="Anmeldename"><input autoComplete="username" value={em.upn} onChange={(e) => setEm({ ...em, upn: e.target.value })} /></Field>
              <Field label="Passwort"><input type="password" autoComplete="current-password" value={em.password} onChange={(e) => setEm({ ...em, password: e.target.value })} /></Field>
              <Field label="Code aus der Authenticator-App"><input inputMode="numeric" autoComplete="one-time-code" maxLength={7} value={em.code} onChange={(e) => setEm({ ...em, code: e.target.value })} /></Field>
              <button type="submit" className="danger" disabled={emergency.busy || !em.upn || !em.password || em.code.length < 6}>Notfall-Anmeldung</button>
              <ErrorBox error={emergency.error} />
            </form>
          </details>
        )}
        {(import.meta.env.DEV || window.location.hostname === 'localhost') && devAvailable && (
          <details style={{ marginTop: 18 }}>
            <summary className="muted small">Entwicklungs-Anmeldung</summary>
            <Field label="UPN" hint="Nur verfügbar, wenn der Server mit AuthMode=Development läuft.">
              <input value={devUpn} onChange={(e) => setDevUpn(e.target.value)} />
            </Field>
            <button onClick={devLogin}>Anmelden (Entwicklung)</button>
            <ErrorBox error={devError} />
          </details>
        )}
      </div>
      </div>
    </div>
  );
}
