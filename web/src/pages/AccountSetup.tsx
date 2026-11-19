import { useEffect, useState } from 'react';
import QRCode from 'qrcode';
import { api } from '../api';
import { ErrorBox, Field, Info, Spinner, useAction } from '../ui';
import { createPasskey, passkeyError, passkeysSupported } from '../webauthn';

type SetupInfo = { upn: string; displayName: string; loginKind: 'Local' | 'Emergency'; setupTokenExpiresAt: string };

/** Einmal-Link für lokale Konten: Passkey registrieren bzw. Passwort + TOTP für ein Notfallkonto festlegen. */
export default function AccountSetupPage() {
  const token = window.location.hash.slice(1);
  const [info, setInfo] = useState<SetupInfo>();
  const [loadError, setLoadError] = useState<string>();
  useEffect(() => {
    api.post<SetupInfo>('/auth/account-setup', { token }).then(setInfo).catch((e: Error) => setLoadError(e.message));
  }, [token]);

  return (
    <div className="center-page">
      <div className="login-card card" style={{ maxWidth: 520 }}>
        <div className="brand">
          <svg width="36" height="36" viewBox="0 0 32 32" aria-hidden><rect x="21" y="12" width="9" height="8" fill="#b8bec7" /><rect className="brand-body" x="2" y="8" width="22" height="16" rx="6" /><circle cx="11" cy="16" r="5" fill="#d4a52a" /></svg>
          <div>Konto einrichten</div>
        </div>
        {!token && <ErrorBox error="Im Link fehlt der Einrichtungscode. Bitte den vollständigen Link aus der Nachricht öffnen." />}
        <ErrorBox error={loadError} />
        {token && !info && !loadError && <Spinner />}
        {info && <p>Hallo {info.displayName}. Sie richten den Zugang für <strong>{info.upn}</strong> ein.</p>}
        {info?.loginKind === 'Local' && <PasskeySetup token={token} />}
        {info?.loginKind === 'Emergency' && <EmergencySetup token={token} upn={info.upn} />}
      </div>
    </div>
  );
}

function PasskeySetup({ token }: { token: string }) {
  const [name, setName] = useState('YubiKey');
  const { busy, error, run } = useAction();
  const register = () => run(async () => {
    const { requestId, options } = await api.post<{ requestId: string; options: Record<string, unknown> }>('/auth/account-setup/passkey/options', { token });
    let response;
    try { response = await createPasskey(options); } catch (e) { throw new Error(passkeyError(e)); }
    await api.post('/auth/account-setup/passkey', { token, requestId, response, name });
    window.location.href = '/';
  });
  if (!passkeysSupported()) return <ErrorBox error="Dieser Browser unterstützt keine Passkeys." />;
  return (
    <>
      <p className="muted">Stecken Sie Ihren YubiKey ein. Sie werden nach einer PIN gefragt (falls noch keine gesetzt ist, legen Sie jetzt eine fest) und berühren dann den Schlüssel.</p>
      <Field label="Name des Schlüssels" hint="Hilft später, mehrere Schlüssel auseinanderzuhalten."><input value={name} onChange={(e) => setName(e.target.value)} /></Field>
      <button className="primary" disabled={busy} onClick={register}>{busy ? 'Schlüssel berühren …' : 'Passkey einrichten'}</button>
      <ErrorBox error={error} />
    </>
  );
}

function EmergencySetup({ token, upn }: { token: string; upn: string }) {
  const [totp, setTotp] = useState<{ secret: string; uri: string; qr: string }>();
  const [password, setPassword] = useState('');
  const [repeat, setRepeat] = useState('');
  const [code, setCode] = useState('');
  const start = useAction();
  const finish = useAction();
  const begin = () => start.run(async () => {
    const r = await api.post<{ secret: string; uri: string }>('/auth/account-setup/totp', { token });
    setTotp({ ...r, qr: await QRCode.toDataURL(r.uri, { margin: 1, width: 220 }) });
  });
  const complete = () => finish.run(async () => {
    await api.post('/auth/account-setup/emergency', { token, password, code });
    window.location.href = '/';
  });
  return (
    <>
      <Info tone="warn">Notfallkonten sind für den Fall gedacht, dass Entra oder die Passkey-Anmeldung nicht funktioniert. Bewahren Sie Passwort und Authenticator sicher und getrennt auf.</Info>
      {!totp && <>
        <p className="muted">Schritt 1: Authenticator-App (z. B. Microsoft Authenticator) mit dem Konto verbinden.</p>
        <button className="primary" disabled={start.busy} onClick={begin}>QR-Code anzeigen</button>
        <ErrorBox error={start.error} />
      </>}
      {totp && <>
        <p className="muted">Schritt 1: QR-Code mit der Authenticator-App scannen.</p>
        <div style={{ display: 'flex', gap: 16, alignItems: 'center', flexWrap: 'wrap' }}>
          <img src={totp.qr} width={180} height={180} alt="QR-Code für die Authenticator-App" />
          <div className="small">Oder manuell eingeben:<div className="mono" style={{ wordBreak: 'break-all' }}>{totp.secret}</div></div>
        </div>
        <p className="muted">Schritt 2: Passwort festlegen (mindestens 14 Zeichen) und den aktuellen Code aus der App eingeben.</p>
        <form onSubmit={(e) => { e.preventDefault(); complete(); }}>
          <input type="text" autoComplete="username" value={upn} readOnly hidden />
          <div className="grid cols-2">
            <Field label="Passwort"><input type="password" autoComplete="new-password" value={password} onChange={(e) => setPassword(e.target.value)} /></Field>
            <Field label="Passwort wiederholen"><input type="password" autoComplete="new-password" value={repeat} onChange={(e) => setRepeat(e.target.value)} /></Field>
          </div>
          <Field label="Code aus der App"><input inputMode="numeric" autoComplete="one-time-code" maxLength={7} value={code} onChange={(e) => setCode(e.target.value)} /></Field>
          {repeat && password !== repeat && <Info tone="warn">Die Passwörter stimmen nicht überein.</Info>}
          <button type="submit" className="primary" disabled={finish.busy || password.length < 14 || password !== repeat || code.length < 6}>Notfallkonto einrichten</button>
          <ErrorBox error={finish.error} />
        </form>
      </>}
    </>
  );
}
