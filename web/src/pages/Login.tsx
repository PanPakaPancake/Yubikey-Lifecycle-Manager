import { useState } from 'react';
import { api } from '../api';
import { ErrorBox, Field } from '../ui';

const errorTexts: Record<string, string> = {
  not_registered: 'Ihr Konto ist für diese Plattform nicht freigeschaltet oder deaktiviert. Bitte wenden Sie sich an einen Owner.',
  not_phishing_resistant: 'Bitte melden Sie sich mit Ihrem YubiKey (Passkey) an. Andere Anmeldemethoden sind nicht zulässig.',
  wrong_tenant: 'Die Anmeldung ist nur mit Konten aus dem Heim-Tenant der Installation möglich.',
  invalid_token: 'Die Anmeldung konnte nicht geprüft werden.',
  remote_failure: 'Die Anmeldung bei Microsoft ist fehlgeschlagen.',
  denied: 'Die Anmeldung wurde abgelehnt.',
};

export default function LoginPage({ setupComplete }: { setupComplete: boolean }) {
    const params = new URLSearchParams(window.location.search);
  const returnUrl = params.get('returnUrl') ?? '/';
  const err = params.get('error');
  const [devUpn, setDevUpn] = useState('admin@contoso.dev');
  const [devError, setDevError] = useState<string>();
  const [devAvailable, setDevAvailable] = useState(true);

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
        <p className="muted">Melden Sie sich mit Ihrem Microsoft-Konto an. Die Anmeldung erfordert Ihren YubiKey.</p>
        <a className="btn primary" style={{ width: '100%', justifyContent: 'center' }} href={'/auth/login?returnUrl=' + encodeURIComponent(returnUrl)}>
          Mit Microsoft anmelden
        </a>
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
