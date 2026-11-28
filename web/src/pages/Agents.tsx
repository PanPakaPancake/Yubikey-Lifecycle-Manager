import { useState } from 'react';
import { api, fmtDate, isNewer, type AgentInfo, type AgentPackageInfo } from '../api';
import { Badge, Card, Empty, ErrorBox, Info, PageHeader, Spinner, useAction, useApi, usePolling } from '../ui';
import { tr } from '../i18n';

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
      <PageHeader title={tr("Agent")} subtitle={tr("Der Agent auf Ihrem Windows-PC spricht mit dem eingesteckten YubiKey.")} />
      <ErrorBox error={error ?? actError} />
      <div className="grid cols-2">
        <Card title={tr("Agent installieren und koppeln")}>
          <ol>
            <li>{tr("Installieren Sie")} <strong>{tr("YubikeyLifecycleManager-Agent.msi")}</strong> {tr("(einmalig, mit Adminrechten). Der Agent läuft danach als Windows-Dienst; das Tray-Symbol startet bei der Anmeldung. Neue Versionen installiert der Agent danach selbst (abschaltbar im Tray unter")} <strong>{tr("Einstellungen")}</strong>).</li>
            <li>{tr("Klicken Sie mit der rechten Maustaste auf das Tray-Symbol, wählen Sie")} <strong>{tr("„Koppeln …“")}</strong> {tr("und geben Sie die Plattform-Adresse (siehe unten, genau so wie im Browser) und den Kopplungscode ein.")}</li>
          </ol>
          {pkg?.available && pkg.downloadUrl
            ? <p><a className="btn" href={pkg.downloadUrl} download>{tr("Agent herunterladen{n}", { n: pkg.version ? tr(" (Version {version})", { version: pkg.version }) : '' })}</a>{pkg.size ? <span className="small muted"> {tr("{n} MB", { n: (pkg.size / 1024 / 1024).toFixed(1) })}</span> : null}</p>
            : pkg && <p className="small muted">{tr("Für diese Installation ist noch kein Agent-Paket veröffentlicht. Es kommt mit dem nächsten Release.")}</p>}
          <p className="small muted">{tr("Plattform-Adresse:")} <code>{window.location.origin}</code></p>
          {code ? (
            <div>
              <div className="pairing-code">{code.code}</div>
              <p className="small muted">{tr("Gültig bis {expiresAt}, nur einmal verwendbar.", { expiresAt: fmtDate(code.expiresAt) })}</p>
            </div>
          ) : null}
          <button className="primary" disabled={busy} onClick={newCode}>{code ? tr("Neuen Code erzeugen") : tr("Kopplungscode erzeugen")}</button>
        </Card>
        <Card title={tr("Meine Agents")}>
          {!data && <Spinner />}
          {data && data.length === 0 && <Empty>{tr("Noch kein Agent gekoppelt.")}</Empty>}
          {data?.map((a) => (
            <div key={a.id} className="check-row">
              <span className={'dot ' + (a.online ? 'Ok' : 'Warning')} />
              <div style={{ flex: 1 }}>
                <strong>{a.machineName}</strong> {a.online ? <Badge tone="ok">{tr("online")}</Badge> : <Badge>{tr("offline")}</Badge>} {a.busy && <Badge tone="warn">{a.busy}</Badge>}
                <div className="small muted">{tr("Version {n}", { n: a.agentVersion ?? '?' })} {pkg?.version && isNewer(pkg.version, a.agentVersion) && <Badge tone="warn">{a.autoUpdate === false ? tr("Version {version} verfügbar", { version: pkg.version }) : tr("Update auf {version} folgt automatisch", { version: pkg.version })}</Badge>}
                  {a.autoUpdate === false && <> <Badge>{tr("Auto-Update aus")}</Badge></>} {tr("· gekoppelt {n} · zuletzt gesehen {lastSeenAt}", { n: fmtDate(a.createdAt, false), lastSeenAt: fmtDate(a.lastSeenAt) })}</div>
                {a.updateError && <div className="small" style={{ color: 'var(--danger, #b42318)' }}>{tr("Letztes Update fehlgeschlagen: {updateError}", { updateError: a.updateError })}</div>}
                {updateResult[a.id] && <div className="small">{updateResult[a.id]}</div>}
                {a.key && <div className="small">{tr("Eingesteckt: {model}", { model: a.key.model })} <span className="mono">{a.key.serialNumber}</span>{tr(", Firmware")} <span className="mono">{a.key.firmwareVersion}</span></div>}
              </div>
              {a.online && a.autoUpdate != null && pkg?.version && isNewer(pkg.version, a.agentVersion) && (
                <button className="link" disabled={busy} onClick={() => updateNow(a.id)}>{tr("Jetzt aktualisieren")}</button>
              )}
              <button className="link" onClick={() => revoke(a.id)}>{tr("Entkoppeln")}</button>
            </div>
          ))}
          <Info>{tr("Ein Agent gehört immer zu Ihrem Konto. Andere Admins koppeln ihren eigenen PC.")}</Info>
        </Card>
      </div>
    </>
  );
}
