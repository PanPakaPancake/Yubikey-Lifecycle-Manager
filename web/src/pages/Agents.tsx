import { useState } from 'react';
import { api, fmtDate, isNewer, type AgentInfo, type AgentPackageInfo } from '../api';
import { Badge, Card, Empty, ErrorBox, Info, PageHeader, Spinner, useAction, useApi, usePolling } from '../ui';

export default function AgentsPage() {
  const { data, error, reload } = useApi<AgentInfo[]>('/api/agents');
  usePolling(reload, 3000);
  const { data: pkg } = useApi<AgentPackageInfo>('/api/agent-package');
  const [code, setCode] = useState<{ code: string; expiresAt: string }>();
  const { busy, error: actError, run } = useAction();
  const newCode = () => run(async () => setCode(await api.post('/api/agents/pairing-codes')));
  const revoke = (id: string) => run(async () => { await api.del('/api/agents/' + id); reload(); });
  const [updateResult, setUpdateResult] = useState<Record<string, string>>({});
  const updateNow = (id: string) => run(async () => {
    const r = await api.post<{ message: string }>(`/api/agents/${id}/update`);
    setUpdateResult((m) => ({ ...m, [id]: r.message }));
    reload();
  });

  return (
    <>
      <PageHeader title="Agent" subtitle="Der Agent auf Ihrem Windows-PC spricht mit dem eingesteckten YubiKey." />
      <ErrorBox error={error ?? actError} />
      <div className="grid cols-2">
        <Card title="Agent installieren und koppeln">
          <ol>
            <li>Installieren Sie <strong>YubikeyLifecycleManager-Agent.msi</strong> (einmalig, mit Adminrechten). Der Agent läuft danach als Windows-Dienst; das Tray-Symbol startet bei der Anmeldung. Neue Versionen installiert der Agent danach selbst (abschaltbar im Tray unter <strong>Einstellungen</strong>).</li>
            <li>Klicken Sie im Tray-Symbol auf <strong>„Agent koppeln“</strong> und geben Sie die Adresse dieser Plattform und den Kopplungscode ein.</li>
          </ol>
          {pkg?.available && pkg.downloadUrl
            ? <p><a className="btn" href={pkg.downloadUrl} download>Agent herunterladen{pkg.version ? ` (Version ${pkg.version})` : ''}</a>{pkg.size ? <span className="small muted"> {(pkg.size / 1024 / 1024).toFixed(1)} MB</span> : null}</p>
            : pkg && <p className="small muted">Für diese Installation ist noch kein Agent-Paket veröffentlicht. Es kommt mit dem nächsten Release.</p>}
          <p className="small muted">Plattform-Adresse: <code>{window.location.origin}</code></p>
          {code ? (
            <div>
              <div className="pairing-code">{code.code}</div>
              <p className="small muted">Gültig bis {fmtDate(code.expiresAt)}, nur einmal verwendbar.</p>
            </div>
          ) : null}
          <button className="primary" disabled={busy} onClick={newCode}>{code ? 'Neuen Code erzeugen' : 'Kopplungscode erzeugen'}</button>
        </Card>
        <Card title="Meine Agents">
          {!data && <Spinner />}
          {data && data.length === 0 && <Empty>Noch kein Agent gekoppelt.</Empty>}
          {data?.map((a) => (
            <div key={a.id} className="check-row">
              <span className={'dot ' + (a.online ? 'Ok' : 'Warning')} />
              <div style={{ flex: 1 }}>
                <strong>{a.machineName}</strong> {a.online ? <Badge tone="ok">online</Badge> : <Badge>offline</Badge>} {a.busy && <Badge tone="warn">{a.busy}</Badge>}
                <div className="small muted">Version {a.agentVersion ?? '?'} {pkg?.version && isNewer(pkg.version, a.agentVersion) && <Badge tone="warn">{a.autoUpdate === false ? `Version ${pkg.version} verfügbar` : `Update auf ${pkg.version} folgt automatisch`}</Badge>}
                  {a.autoUpdate === false && <> <Badge>Auto-Update aus</Badge></>} · gekoppelt {fmtDate(a.createdAt, false)} · zuletzt gesehen {fmtDate(a.lastSeenAt)}</div>
                {a.updateError && <div className="small" style={{ color: 'var(--danger, #b42318)' }}>Letztes Update fehlgeschlagen: {a.updateError}</div>}
                {updateResult[a.id] && <div className="small">{updateResult[a.id]}</div>}
                {a.key && <div className="small">Eingesteckt: {a.key.model} <span className="mono">{a.key.serialNumber}</span>, Firmware <span className="mono">{a.key.firmwareVersion}</span></div>}
              </div>
              {a.online && a.autoUpdate != null && pkg?.version && isNewer(pkg.version, a.agentVersion) && (
                <button className="link" disabled={busy} onClick={() => updateNow(a.id)}>Jetzt aktualisieren</button>
              )}
              <button className="link" onClick={() => revoke(a.id)}>Entkoppeln</button>
            </div>
          ))}
          <Info>Ein Agent gehört immer zu Ihrem Konto. Andere Admins koppeln ihren eigenen PC.</Info>
        </Card>
      </div>
    </>
  );
}
