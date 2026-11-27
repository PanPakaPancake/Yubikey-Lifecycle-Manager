import { useState } from 'react';
import { Link } from 'react-router';
import { fmtDate, statusLabels, usageLabels, type KeyStatus, type KeyUsage } from '../api';
import { useCan } from '../auth';
import { Badge, Card, CountUp, download, Empty, ErrorBox, Info, MenuButton, PageHeader, Spinner, useApi } from '../ui';
import { useTenant } from '../tenant';
import { Icon } from '../icons';

export interface AdvisoryKey {
  keyId: string; tenantId?: string | null; tenantName: string; serialNumber?: number | null; model?: string | null; series: string; firmwareVersion?: string | null;
  status: KeyStatus; usage: KeyUsage; custodian?: string | null; people: { userPrincipalName: string; displayName?: string | null }[];
}
export interface AdvisoryEntry {
  id: string; title: string; severity: string; url: string; published: string; affectsFido: boolean; applications: string;
  summary: string; recommendation: string; fixedIn: string; affected: string[]; keys: number; people: number;
  byStatus: Record<string, number>; items: AdvisoryKey[];
}
export interface AdvisoryOverview { generatedAt: string; keysChecked: number; keysWithoutFirmware: number; affectedKeys: number; affectedPeople: number; advisories: AdvisoryEntry[] }

const severityTone = (s: string) => (s === 'hoch' ? 'error' : s === 'mittel' ? 'warn' : 'neutral') as 'error' | 'warn' | 'neutral';

/** Filterwerte für das Inventar (/keys?advisory=…), auch für die Auswahl in der Werkzeugleiste. */
export const advisoryFilterLabel = (v: string) => (v === 'fido' ? 'Hinweis betrifft Passkeys' : v === 'any' ? 'Irgendein Sicherheitshinweis' : v);

/**
 * Welche Keys sind von welchem Yubico-Sicherheitshinweis betroffen? Je Hinweis: Einstufung, Empfehlung,
 * betroffene Firmware-Bereiche und die Liste der Keys mit Personen. Gewählter Tenant oder alle (plus Partnerlager).
 */
export default function AdvisoriesPage() {
  const { tenantId, tenant } = useTenant();
  const can = useCan();
  const q = tenantId ? '?tenantId=' + tenantId : '';
  const { data, error } = useApi<AdvisoryOverview>('/api/advisories' + q, [tenantId]);
  const [showAll, setShowAll] = useState(false);
  const exp = (format: 'csv' | 'xlsx', advisory?: string) => {
    const qs = new URLSearchParams();
    if (tenantId) qs.set('tenantId', tenantId);
    if (advisory) qs.set('advisory', advisory);
    download(`/api/exports/advisories.${format}?` + qs.toString());
  };
  const affected = data?.advisories.filter((a) => a.keys > 0) ?? [];
  const clean = data?.advisories.filter((a) => a.keys === 0) ?? [];
  const passkeyHits = affected.filter((a) => a.affectsFido);

  return (
    <>
      <PageHeader
        title={tenant ? 'Sicherheitshinweise ' + tenant.displayName : 'Sicherheitshinweise aller Tenants'}
        subtitle={<>Welche Keys von einem <a href="https://www.yubico.com/support/security-advisories/" target="_blank" rel="noreferrer">Yubico-Sicherheitshinweis</a> betroffen sind, mit Personen und Empfehlung. Ausgemusterte Keys zählen nicht mit.</>}
        actions={can('ReadReports') && data && affected.length > 0 && (
          <MenuButton label="Exportieren" items={[
            { label: 'Alle betroffenen Keys (Excel)', hint: 'Eine Zeile pro Hinweis und Key, mit Personen', icon: 'shield', onClick: () => exp('xlsx') },
            { label: 'Alle betroffenen Keys (CSV)', hint: 'Für andere Programme (Semikolon, UTF-8)', icon: 'download', onClick: () => exp('csv') },
          ]} />
        )}
      />
      <ErrorBox error={error} />
      {!data && !error && <Spinner label="Wird geladen …" />}
      {data && <>
        <div className="grid cols-3 adv-kpis">
          <div className="card kpi adv-kpi-keys">
            <span className="kpi-ico" aria-hidden><Icon name="shield" size={20} /></span>
            <div className="label">Keys mit Hinweis zu Passkeys</div>
            <div className={'value' + (data.affectedKeys ? ' tone-text-warn' : '')}><CountUp value={data.affectedKeys} /></div>
            <div className="sub">von {data.keysChecked} geprüften Keys{data.affectedKeys > 0 && <> · <Link to="/keys?advisory=fido">im Inventar zeigen</Link></>}</div>
          </div>
          <div className="card kpi adv-kpi-people">
            <span className="kpi-ico" aria-hidden><Icon name="users" size={20} /></span>
            <div className="label">Betroffene Personen</div>
            <div className="value"><CountUp value={data.affectedPeople} /></div>
            <div className="sub">mit Konto auf einem dieser Keys</div>
          </div>
          <div className="card kpi adv-kpi-nofw">
            <span className="kpi-ico" aria-hidden><Icon name="scan" size={20} /></span>
            <div className="label">Ohne Firmware-Angabe</div>
            <div className="value"><CountUp value={data.keysWithoutFirmware} /></div>
            <div className="sub">{data.keysWithoutFirmware > 0 ? <>nicht prüfbar · <Link to="/keys/identify">nacherfassen</Link></> : 'alle Keys prüfbar'}</div>
          </div>
        </div>

        {passkeyHits.length === 0 && affected.length === 0 && (
          <Info tone="ok">Kein Key in diesem Bereich ist von einem bekannten Yubico-Sicherheitshinweis betroffen.</Info>
        )}

        <div className="adv-list">
          {affected.map((a) => <AdvisoryCard key={a.id} a={a} allTenants={!tenantId} onExport={can('ReadReports') ? (f) => exp(f, a.id) : undefined} />)}
        </div>

        {clean.length > 0 && (
          <Card title="Ohne betroffene Keys" className="adv-clean" actions={<button className="link" onClick={() => setShowAll((v) => !v)}>{showAll ? 'Weniger' : 'Details'}</button>}>
            <ul className="adv-clean-list">
              {clean.map((a) => (
                <li key={a.id}>
                  <span style={{ color: "var(--ok)" }}><Icon name="check" size={15} /></span>
                  <a href={a.url} target="_blank" rel="noreferrer" className="mono">{a.id}</a> <span>{a.title}</span>
                  {showAll && <div className="small muted">{a.affected.join(' · ')}</div>}
                </li>
              ))}
            </ul>
          </Card>
        )}
        <p className="small muted" style={{ marginTop: 16 }}>
          Stand {fmtDate(data.generatedAt)}. Berücksichtigt sind Hinweise zur Firmware der Keys; Hinweise zu Software wie YubiKey Manager betreffen den einzelnen Key nicht.
          Die Firmware eines YubiKeys lässt sich nicht aktualisieren, behoben wird ein Hinweis also durch Tausch gegen einen neueren Key.
        </p>
      </>}
    </>
  );
}

function AdvisoryCard({ a, allTenants, onExport }: { a: AdvisoryEntry; allTenants: boolean; onExport?: (f: 'csv' | 'xlsx') => void }) {
  // Aufgeklappt nur bei Hinweisen zu Passkeys mit Einstufung mittel oder hoch, die übrigen auf Klick.
  const [open, setOpen] = useState(a.affectsFido && (a.severity === 'mittel' || a.severity === 'hoch'));
  const statuses = (['Active', 'InStock', 'Locked'] as KeyStatus[]).filter((s) => a.byStatus[s]);
  return (
    <section className={'card adv-card' + (a.affectsFido ? ' adv-fido' : '')} id={a.id}>
      <div className="adv-head">
        <div className="adv-title">
          <div className="adv-badges">
            <a className="mono adv-id" href={a.url} target="_blank" rel="noreferrer">{a.id}</a>
            <Badge tone={severityTone(a.severity)}>Einstufung {a.severity}</Badge>
            {a.affectsFido ? <Badge tone="warn">betrifft Passkeys</Badge> : <Badge>nicht Passkeys: {a.applications}</Badge>}
            <span className="small muted">veröffentlicht {new Date(a.published).toLocaleDateString('de-DE')}</span>
          </div>
          <h2>{a.title}</h2>
        </div>
        <div className="adv-count">
          <strong><CountUp value={a.keys} /></strong><span>{a.keys === 1 ? 'Key' : 'Keys'}</span>
          <span className="small muted">{a.people} {a.people === 1 ? 'Person' : 'Personen'}</span>
        </div>
      </div>
      <p>{a.summary}</p>
      <div className="adv-reco"><Icon name="info" size={16} /><div><strong>Empfehlung:</strong> {a.recommendation} <span className="muted">Behoben ab Firmware {a.fixedIn}.</span></div></div>
      <div className="adv-meta small muted">Betroffen: {a.affected.join(' · ')}</div>
      <div className="adv-actions">
        <button type="button" onClick={() => setOpen((v) => !v)} aria-expanded={open}>{open ? 'Liste ausblenden' : `${a.keys} Keys anzeigen`}</button>
        <Link className="btn" to={'/keys?advisory=' + a.id}><Icon name="key" size={15} />Im Inventar filtern</Link>
        {onExport && <MenuButton label="Exportieren" items={[
          { label: 'Excel', hint: 'Betroffene Keys mit Personen', icon: 'shield', onClick: () => onExport('xlsx') },
          { label: 'CSV', hint: 'Semikolon, UTF-8', icon: 'download', onClick: () => onExport('csv') },
        ]} />}
        <span className="small muted">{statuses.map((s) => `${a.byStatus[s]} ${statusLabels[s].toLowerCase()}`).join(', ')}</span>
      </div>
      {open && (a.items.length === 0 ? <Empty>Keine Keys.</Empty> : (
        <div className="table-wrap adv-table">
          <table>
            <thead><tr><th>Seriennummer</th><th>Modell</th><th>Firmware</th><th>Status</th><th>Person / Verwahrer</th>{allTenants && <th>Tenant</th>}</tr></thead>
            <tbody>
              {a.items.map((k) => (
                <tr key={k.keyId}>
                  <td><Link className="mono" to={'/keys/' + k.keyId}>{k.serialNumber ?? '—'}</Link></td>
                  <td>{k.model ?? <span className="muted">{k.series}</span>}</td>
                  <td className="mono">{k.firmwareVersion}</td>
                  <td>{statusLabels[k.status]}</td>
                  <td>
                    {k.usage !== 'Personal' && <div><Badge tone={k.usage === 'Breakglass' ? 'error' : 'info'}>{usageLabels[k.usage]}</Badge> <span className="small muted">{k.custodian ? 'bei ' + k.custodian : 'Verwahrer fehlt'}</span></div>}
                    {k.people.length === 0 ? (k.usage === 'Personal' ? <span className="muted">–</span> : null) : k.people.map((p) => <div key={p.userPrincipalName}>{p.displayName ?? p.userPrincipalName}<div className="small muted">{p.userPrincipalName}</div></div>)}
                  </td>
                  {allTenants && <td>{k.tenantId ? k.tenantName : <Badge tone="warn">Partnerlager</Badge>}</td>}
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      ))}
    </section>
  );
}
