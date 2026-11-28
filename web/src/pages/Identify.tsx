import { useEffect, useState } from 'react';
import { Link, useSearchParams } from 'react-router';
import { api, ApiError, fmtDate, type AgentInfo, type YubiKey } from '../api';
import { Badge, Card, Empty, ErrorBox, Field, Info, PageHeader, Spinner, useAction, useApi, usePolling } from '../ui';
import { useToast } from '../feedback';
import { ChooseTenant, useTenant } from '../tenant';
import { tr } from '../i18n';

/**
 * Nacherfassen (V1.1): Keys, die Nutzer selbst registriert haben, kennt die Plattform nur aus Entra, ohne Seriennummer und Firmware.
 * Key einstecken, Eintrag wählen, der Agent liest den Key aus und ergänzt das Inventar.
 */
export default function IdentifyPage() {
  const [params] = useSearchParams();
  const { tenantId, tenant } = useTenant();
  const toast = useToast();
  const { data, error, loading, reload } = useApi<{ total: number; items: YubiKey[] }>(tenantId ? `/api/keys?tenantId=${tenantId}&needsDetails=true&pageSize=500` : null, [tenantId]);
  const { data: agents, reload: reloadAgents } = useApi<AgentInfo[]>('/api/agents');
  usePolling(reloadAgents, 2000);
  const online = agents?.filter((a) => a.online) ?? [];
  const [agentId, setAgentId] = useState('');
  const agent = online.find((a) => a.id === agentId) ?? online[0];
  const [keyId, setKeyId] = useState(params.get('keyId') ?? '');
  const [filter, setFilter] = useState('');
  const [custodianNeeded, setCustodianNeeded] = useState<string>();
  const [custodian, setCustodian] = useState('');
  const [mismatch, setMismatch] = useState<string>();
  const { busy, error: actionError, run, setError } = useAction();
  useEffect(() => { if (agent && agent.id !== agentId) setAgentId(agent.id); }, [agent, agentId]);
  const waitingFor = agent && !agent.key ? agent.id : undefined;
  useEffect(() => { if (waitingFor) void api.post(`/api/agents/${waitingFor}/request-key`, {}).catch(() => undefined); }, [waitingFor]);

  const items = (data?.items ?? []).filter((k) => {
    const t = filter.trim().toLowerCase();
    return !t || k.registrations.some((r) => r.userPrincipalName.toLowerCase().includes(t) || (r.userDisplayName ?? '').toLowerCase().includes(t));
  });
  const selected = data?.items.find((k) => k.id === keyId);

  const identify = (ignoreModelMismatch = false) => run(async () => {
    setCustodianNeeded(undefined);
    setMismatch(undefined);
    try {
      const r = await api.post<{ key: YubiKey; merged: boolean }>(`/api/keys/${keyId}/identify`, { agentId: agent!.id, custodian: custodian || null, ignoreModelMismatch });
      toast.ok(<>{tr("Key {serialNumber} nacherfasst", { serialNumber: r.key.serialNumber })}{r.merged ? tr(" und mit dem vorhandenen Lager-Eintrag zusammengeführt") : ''}. <Link to={'/keys/' + r.key.id}>{tr("Ansehen")}</Link></>);
      setKeyId('');
      setCustodian('');
      reload();
    } catch (e) {
      if (e instanceof ApiError && e.code === 'custodian_required') { setCustodianNeeded(e.message); return; }
      if (e instanceof ApiError && e.code === 'model_mismatch') { setMismatch(e.message); return; }
      throw e;
    }
  });

  return (
    <>
      <PageHeader title={tr("Nacherfassen")} subtitle={tr("Selbst registrierte Keys: Seriennummer und Firmware per Einstecken ergänzen.")} actions={<Link className="btn" to="/keys?needsDetails=1">{tr("Im Inventar zeigen")}</Link>} />
      {!tenantId && <ChooseTenant purpose={tr("Zum Nacherfassen")} />}
      <ErrorBox error={error} />
      {tenantId && (
        <div className="grid cols-2">
          <Card title={tr("1. Eintrag wählen{n}", { n: data ? tr(" ({total} offen)", { total: data.total }) : '' })}>
            <Field label={tr("Person suchen")}><input type="search" value={filter} onChange={(e) => setFilter(e.target.value)} placeholder={tr("Name oder UPN")} /></Field>
            {loading && !data && <Spinner />}
            {data && data.total === 0 && <Empty>{tr("In {displayName} gibt es keine Keys ohne Seriennummer.", { displayName: tenant?.displayName })}</Empty>}
            <div className="pick" style={{ maxHeight: 460, overflow: 'auto' }}>
              {items.map((k) => {
                const r = k.registrations.find((x) => !x.removedAt) ?? k.registrations[0];
                return (
                  <button key={k.id} className={k.id === keyId ? 'selected' : ''} onClick={() => { setKeyId(k.id); setError(undefined); setCustodianNeeded(undefined); setMismatch(undefined); }}>
                    <span><strong>{r?.userDisplayName ?? r?.userPrincipalName ?? tr("Unbekannt")}</strong><br /><span className="small muted">{r?.userPrincipalName} · „{r?.displayName ?? '–'}“ · {k.model ?? tr("Modell unbekannt")}</span></span>
                    <span className="small muted">{fmtDate(r?.registeredAt, false)}</span>
                  </button>
                );
              })}
            </div>
          </Card>
          <Card title={tr("2. Key einstecken und zuordnen")}>
            {agents && online.length === 0 && <Info tone="warn">{tr("Auf Ihrem PC ist kein Agent verbunden.")} <Link to="/agents">{tr("Agent koppeln")}</Link></Info>}
            {online.length > 1 && (
              <Field label={tr("Agent")}><select value={agent?.id} onChange={(e) => setAgentId(e.target.value)}>{online.map((a) => <option key={a.id} value={a.id}>{a.machineName}</option>)}</select></Field>
            )}
            {agent && (agent.key ? (
              <div className="keycard">
                <div className="keyglyph"><span /></div>
                <div>
                  <strong>{agent.key.model ?? 'YubiKey'}</strong>
                  <div>{tr("Seriennummer")} <span className="mono">{agent.key.serialNumber ?? '–'}</span> {tr("· Firmware")} <span className="mono">{agent.key.firmwareVersion}</span></div>
                </div>
              </div>
            ) : (
              <div className="waiting-key"><Spinner /><div><strong>{tr("Bitte den Key der Person an {machineName} einstecken.", { machineName: agent.machineName })}</strong><div className="small muted">{tr("Die PIN wird nicht gebraucht, der Key wird nur ausgelesen.")}</div></div></div>
            ))}
            {selected && (
              <p style={{ marginTop: 12 }}>
                {tr("Zuordnen zu")} <strong>{selected.registrations.map((r) => r.userPrincipalName).join(', ')}</strong>
                {selected.model && <> {tr("· Entra meldet")} <Badge>{selected.model}</Badge></>}
              </p>
            )}
            <Info>{tr("Bitte nur den Key einstecken, der zu diesem Eintrag gehört. Die Plattform kann ohne PIN nicht prüfen, welcher Passkey auf dem Key liegt; sie vergleicht nur das Modell.")}</Info>
            {mismatch && (
              <div className="alert warn">{mismatch}
                <div className="actions" style={{ marginTop: 8 }}><button className="small" disabled={busy} onClick={() => identify(true)}>{tr("Trotzdem zuordnen")}</button></div>
              </div>
            )}
            {custodianNeeded && (
              <div className="alert warn">{custodianNeeded}
                <Field label={tr("Verwahrer *")}><input value={custodian} onChange={(e) => setCustodian(e.target.value)} /></Field>
              </div>
            )}
            <ErrorBox error={actionError} />
            <div className="actions" style={{ marginTop: 12 }}>
              <button className="primary" disabled={!selected || !agent?.key || busy || (!!custodianNeeded && !custodian.trim())} onClick={() => identify(false)}>{tr("Nacherfassen")}</button>
            </div>
          </Card>
        </div>
      )}
    </>
  );
}
