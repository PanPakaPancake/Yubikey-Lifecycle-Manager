import { useEffect, useState } from 'react';
import { api } from '../api';
import { ErrorBox, Field, useAction } from '../ui';
import { getPasskey, passkeyError, passkeysSupported } from '../webauthn';

const errorTexts: Record<string, string> = {
  not_registered: 'Ihr Konto ist für diese Plattform nicht freigeschaltet oder deaktiviert. Bitte wenden Sie sich an einen Owner.',
  not_phishing_resistant: 'Bitte melden Sie sich mit Ihrem YubiKey (Passkey) an. Andere Anmeldemethoden sind nicht zulässig.',
  wrong_tenant: 'Die Anmeldung ist nur mit Konten aus dem Heim-Tenant der Installation möglich.',
  invalid_token: 'Die Anmeldung konnte nicht geprüft werden.',
  remote_failure: 'Die Anmeldung bei Microsoft ist fehlgeschlagen.',
  denied: 'Die Anmeldung wurde abgelehnt.',
  sso_disabled: 'Die Anmeldung mit Microsoft ist abgeschaltet. Bitte mit Passkey anmelden.',
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
    <div className="center-page">
      <div className="login-card card">
        <div className="brand">
          <svg width="36" height="36" viewBox="0 0 32 32" aria-hidden><rect x="21" y="12" width="9" height="8" fill="#b8bec7" /><rect x="2" y="8" width="22" height="16" rx="6" fill="#2b2f36" /><circle cx="11" cy="16" r="5" fill="#d4a52a" /></svg>
          <div>Yubikey Lifecycle Manager</div>
        </div>
        {!setupComplete && <div className="alert warn">Die Einrichtung ist noch nicht abgeschlossen. <a href="/setup">Zur Einrichtung</a></div>}
        {err && <ErrorBox error={errorTexts[err] ?? decodeURIComponent(err)} />}
        <p className="muted">Melden Sie sich mit Ihrem YubiKey an.</p>
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
  );
}
