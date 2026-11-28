import { useEffect, useState } from 'react';
import { api } from './api';
import { ErrorBox, useAction } from './ui';
import { Icon } from './icons';
import { tr } from './i18n';

/** Demo-Modus (docker-compose.demo.yml): Server mit erfundenen Beispieldaten, ohne Entra-Anbindung. */
let demoStatus: Promise<boolean> | undefined;
export function useDemoMode() {
  const [demo, setDemo] = useState(false);
  useEffect(() => {
    demoStatus ??= fetch('/api/setup/status').then((r) => r.json()).then((s) => !!s.demo).catch(() => false);
    void demoStatus.then(setDemo);
  }, []);
  return demo;
}

/** Hinweisleiste über jeder Seite, damit niemand die Demo mit einer echten Installation verwechselt. */
export function DemoBanner() {
  const demo = useDemoMode();
  if (!demo) return null;
  return (
    <div className="demo-banner" role="note">
      <Icon name="info" size={16} />
      <span><strong>{tr("Demo")}</strong> {tr("mit erfundenen Beispieldaten. Keine Verbindung zu Microsoft Entra, es werden keine Mails verschickt.")}</span>
    </div>
  );
}

type DemoAccount = { role: string; displayName: string; upn: string; description: string };

/** Anmeldung in der Demo: Rolle wählen, ohne Passwort. */
export function DemoLogin({ returnUrl }: { returnUrl: string }) {
  const [accounts, setAccounts] = useState<DemoAccount[]>([]);
  const login = useAction();
  useEffect(() => { void api.get<DemoAccount[]>('/auth/demo').then(setAccounts).catch(() => {}); }, []);
  const signIn = (role: string) => login.run(async () => {
    await api.post('/auth/demo-login', { role });
    window.location.href = returnUrl;
  });
  return (
    <div className="demo-login">
      <div className="alert info"><strong>{tr("Demo-Modus:")}</strong> {tr("Diese Installation läuft mit erfundenen Beispieldaten. Wählen Sie eine Rolle, um sich ohne Passwort anzumelden.")}</div>
      <div className="demo-accounts">
        {accounts.map((a) => (
          <button key={a.role} type="button" className={a.role === 'Owner' ? 'primary' : ''} disabled={login.busy} onClick={() => signIn(a.role)}>
            <strong>{a.role}</strong>
            <span className="small">{a.description}</span>
          </button>
        ))}
      </div>
      <ErrorBox error={login.error} />
    </div>
  );
}
