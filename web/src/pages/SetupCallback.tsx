import { useEffect, useState } from 'react';
import { api } from '../api';
import { Info, Spinner } from '../ui';
import { tr } from '../i18n';

/**
 * Rücksprung der interaktiven Microsoft-Anmeldung im Setup-Assistenten, wenn die Plattform auf http://localhost läuft.
 * Microsoft leitet auf http://localhost:<port>/?code=…&state=… weiter; diese Seite reicht beides an den Server und
 * der Assistent im ursprünglichen Fenster geht von selbst weiter.
 */
export default function SetupCallbackPage() {
  const [result, setResult] = useState<{ ok: boolean; message: string }>();
  useEffect(() => {
    void api.post<{ status: string; message?: string }>('/api/setup/signin/complete', { url: window.location.href })
      .then((r) => setResult(r.status === 'signed_in'
        ? { ok: true, message: tr("Angemeldet. Der Einrichtungsassistent im anderen Fenster geht jetzt weiter.") }
        : { ok: false, message: r.message ?? tr("Die Anmeldung ist fehlgeschlagen.") }))
      .catch((e) => setResult({ ok: false, message: e instanceof Error ? e.message : String(e) }));
    // Code nicht in der Adresszeile oder im Verlauf stehen lassen.
    window.history.replaceState(null, '', '/');
  }, []);
  useEffect(() => {
    if (result?.ok) setTimeout(() => window.close(), 1500);
  }, [result]);
  return (
    <div className="center-page">
      <div className="login-card card">
        <h1>{tr("Microsoft-Anmeldung")}</h1>
        {!result && <Spinner label={tr("Anmeldung wird abgeschlossen …")} />}
        {result && <Info tone={result.ok ? 'ok' : 'error'}>{result.message}</Info>}
        {result?.ok && <p className="muted">{tr("Dieses Fenster kann geschlossen werden.")}</p>}
      </div>
    </div>
  );
}
