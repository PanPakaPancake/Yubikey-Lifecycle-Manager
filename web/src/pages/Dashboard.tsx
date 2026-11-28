import { Link } from 'react-router';
import { fmtDate, statusLabels, type KeyStatus } from '../api';
import { useAuth, useCan } from '../auth';
import { KeyArt } from './PasskeyInfo';
import type { ReactNode } from 'react';
import { Badge, Card, CountUp, Donut, download, Empty, ErrorBox, MenuButton, PageHeader, Spinner, useApi } from '../ui';
import { Icon, type IconName } from '../icons';
import { useTenant } from '../tenant';
import type { CertificateStatus } from './Platform';
import type { UpdateSummary } from '../changelog';
import { locale, tr } from '../i18n';
import { reminderKinds, type ReminderKind } from './Reminders';

interface DashboardData {
  generatedAt: string; byStatus: Record<string, number>; inStock: number; total: number; needsDetails: number;
  targetUsers?: number; usersWithKey: number; coveragePercent?: number; targetAllUsers?: boolean;
  issuedPerMonth: { month: string; count: number }[];
  firmware: { firmware: string; count: number; advisories: string[]; vulnerable: boolean }[];
  vulnerableKeys: number; forcePinChangeUnsupported: number;
  advisories?: { id: string; title: string; severity: string; affectsFido: boolean; keys: number }[]; openTodos: number; pendingApprovals: number;
  /** Nur wenn ein Tenant Erinnerungen eingeschaltet hat. */
  reminders?: { kind: ReminderKind; count: number }[] | null;
  tenants: { id: string; displayName: string; lastSyncAt?: string; lastSyncSucceeded?: boolean; targetUserCount?: number; active: number; inStock: number; usersWithKey: number; checksFailed: number }[];
}

const monthLabel = (m: string) => new Date(m + '-01').toLocaleDateString(locale(), { month: 'short', year: '2-digit' });

/** Gewählter Tenant aus dem Umschalter oder (ohne Auswahl) alle Tenants. */
export default function DashboardPage() {
  const ctx = useTenant();
  const tenantId = ctx.tenantId;
  const can = useCan();
  const q = tenantId ? '?tenantId=' + tenantId : '';
  const { data, error, loading } = useApi<DashboardData>('/api/dashboard' + q, [tenantId]);
  const { data: cert } = useApi<CertificateStatus>(can('ManageSettings') ? '/api/platform/certificate' : null);
  const { data: update } = useApi<UpdateSummary>('/api/updates/summary');
  // Kennzahlen führen ins passend gefilterte Inventar (auf Partnerebene: über alle Tenants).
  const keysLink = (status: KeyStatus) => '/keys?status=' + status;
  const maxMonth = Math.max(1, ...(data?.issuedPerMonth.map((m) => m.count) ?? [1]));

  return (
    <>
      <PageHeader
        title={ctx.tenant ? tr("Übersicht {displayName}", { displayName: ctx.tenant.displayName }) : tr("Übersicht aller Tenants")}
        subtitle={data ? tr("Stand {generatedAt}", { generatedAt: fmtDate(data.generatedAt) }) : undefined}
        actions={can('ReadReports') && (
          <MenuButton label={tr("Exportieren")} items={[
            { label: tr("Bericht (PDF)"), hint: tr("Kennzahlen, Ausgaben pro Monat, Firmware"), icon: 'scroll', onClick: () => download('/api/exports/dashboard.pdf' + q) },
            { label: tr("Wer hat welchen Key (Excel)"), hint: tr("Person, Key, Verwahrer"), icon: 'users', onClick: () => download('/api/exports/assignments.xlsx' + q) },
            { label: tr("Alle Keys (CSV)"), hint: tr("Inventar mit Status, Tags und Zusatzfeldern"), icon: 'key', onClick: () => download('/api/exports/keys.csv' + q) },
            { label: tr("Registrierungen (CSV)"), hint: tr("Verlauf inkl. entfernter Registrierungen"), icon: 'list', onClick: () => download('/api/exports/registrations.csv' + q) },
          ]} />
        )}
      />
      <ErrorBox error={error} />
      {!data && loading && <Spinner label={tr("Wird geladen …")} />}
      {data && (
        <>
          <div className="dash">
          <DashHero data={data} cert={cert} update={update} />
          <ActionItems data={data} keysLink={keysLink} cert={cert} update={update} />

          <div className="grid cols-4 dash-kpis">
            <Kpi icon="users" className="kpi-coverage" label={tr("Abdeckung Zielgruppe")} value={data.coveragePercent != null ? data.coveragePercent.toFixed(0) + ' %' : '–'}
              sub={data.targetUsers != null ? tr("{usersWithKey} von {targetUsers} Personen mit Key", { usersWithKey: data.usersWithKey, targetUsers: data.targetUsers }) : tr("{usersWithKey} Personen mit Key, Zielgruppe noch nicht ermittelt", { usersWithKey: data.usersWithKey })} to="/keys?view=people"
              footer={data.targetAllUsers && <span className="kpi-foot">{tr("Zielgruppe: alle aktiven Konten ·")} <Link to={tenantId ? '/tenant' : '/tenants'}>{tr("Zielgruppe festlegen")}</Link></span>}
              aside={data.coveragePercent != null && <Donut size={64} thickness={9} parts={[
                { key: 'with', value: Math.min(100, data.coveragePercent), color: 'var(--ok)', label: tr("Mit Key") },
                { key: 'without', value: Math.max(0, 100 - data.coveragePercent), color: 'transparent', label: tr("Ohne Key") },
              ]} />} />
            <Kpi icon="key" className="kpi-active" label={tr("Keys aktiv")} value={data.byStatus.Active ?? 0} sub={tr("{total} Keys gesamt", { total: data.total })} to={keysLink('Active')} />
            <Kpi icon="box" className="kpi-stock" label={tr("Im Lager")} value={data.inStock} sub={tr("{n} gesperrt, {n2} ausgemustert", { n: data.byStatus.Locked ?? 0, n2: data.byStatus.Retired ?? 0 })} to={keysLink('InStock')} />
            <Kpi icon="shield" className="kpi-firmware" label={tr("Keys mit Sicherheitshinweis")} value={data.vulnerableKeys} tone={data.vulnerableKeys ? 'warn' : undefined}
              sub={advisorySub(data)} to="/sicherheitshinweise" />
          </div>

          <div className="grid cols-2 dash-charts" style={{ marginTop: 16 }}>
            <Card title={tr("Ausgegebene Keys pro Monat")} className="card-months">
              <div className="bars">
                {data.issuedPerMonth.map((m) => (
                  <div className="bar" key={m.month} title={`${monthLabel(m.month)}: ${m.count}`}>
                    <span className="n">{m.count || ''}</span>
                    <span className="fill" style={{ height: (m.count / maxMonth) * 100 + '%' }} />
                    <span className="m">{monthLabel(m.month)}</span>
                  </div>
                ))}
              </div>
            </Card>
            <Card title={tr("Keys nach Status")} className="card-status">
              <div className="status-chart">
                <Donut parts={(Object.keys(statusLabels) as KeyStatus[]).map((s) => ({ key: s, value: data.byStatus[s] ?? 0, color: statusColor[s], label: statusLabels[s] }))}
                  center={<><strong>{data.total}</strong><span className="small muted">{tr("Keys")}</span></>} />
                <ul className="status-legend">
                  {(Object.keys(statusLabels) as KeyStatus[]).map((s) => (
                    <li key={s}><Link to={keysLink(s)}><i style={{ background: statusColor[s] }} />{statusLabels[s]}<span className="num">{data.byStatus[s] ?? 0}</span></Link></li>
                  ))}
                </ul>
              </div>
            </Card>
          </div>

          <div className="grid cols-2 dash-tables" style={{ marginTop: 16 }}>
            <Card title={<span id="firmware">{tr("Firmware-Stände")}</span>} className="card-firmware">
              {data.firmware.length === 0 ? <Empty>{tr("Noch keine Firmware-Daten.")}</Empty> : (
                <table>
                  <thead><tr><th>{tr("Firmware")}</th><th>{tr("Keys")}</th><th>{tr("Hinweise")}</th></tr></thead>
                  <tbody>
                    {data.firmware.map((f) => (
                      <tr key={f.firmware}>
                        <td className="mono">{f.firmware}</td>
                        <td>{f.count}</td>
                        <td>{f.advisories.map((a) => <Link key={a} to={'/sicherheitshinweise#' + a}><Badge tone={data.advisories?.find((x) => x.id === a)?.affectsFido ? 'warn' : 'neutral'}>{a}</Badge></Link>)}</td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              )}
            </Card>
            <Card title={tr("Tenants")} className="card-tenants">
              {data.tenants.length === 0 ? <Empty>{tr("Noch kein Tenant angebunden.")} <Link to="/tenants">{tr("Tenant hinzufügen")}</Link></Empty> : (
                <table>
                  <thead><tr><th>{tr("Tenant")}</th><th>{tr("Aktiv")}</th><th>{tr("Lager")}</th><th>{tr("Abdeckung")}</th><th>{tr("Sync")}</th></tr></thead>
                  <tbody>
                    {data.tenants.map((t) => (
                      <tr key={t.id}>
                        <td><Link to={'/tenants/' + t.id}>{t.displayName}</Link> {t.checksFailed > 0 && <Badge tone="error">{tr("{checksFailed} Prüfung(en)", { checksFailed: t.checksFailed })}</Badge>}</td>
                        <td>{t.active}</td>
                        <td>{t.inStock}</td>
                        <td>{t.targetUserCount ? Math.round((t.usersWithKey / t.targetUserCount) * 100) + ' %' : '–'}</td>
                        <td>{t.lastSyncSucceeded === false ? <Badge tone="error">{tr("Fehler")}</Badge> : <span className="small muted">{fmtDate(t.lastSyncAt)}</span>}</td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              )}
            </Card>
          </div>
          </div>
        </>
      )}
    </>
  );
}

/**
 * Einstieg im Stil der Passkey-Seite: Begrüßung, Lage in einem Satz und Abkürzungen.
 * Gestaltung in bento.css.
 */
function DashHero({ data, cert, update }: { data: DashboardData; cert?: CertificateStatus; update?: UpdateSummary }) {
  const { me } = useAuth();
  const h = new Date().getHours();
  const hello = h < 11 ? tr("Guten Morgen") : h < 18 ? tr("Guten Tag") : tr("Guten Abend");
  const first = me.user?.displayName?.split(/\s+/)[0];
  const todo = data.tenants.reduce((n, t) => n + (t.checksFailed > 0 ? 1 : 0) + (t.lastSyncSucceeded === false ? 1 : 0), 0)
    + (cert && cert.state !== 'ok' && cert.state !== 'missing' ? 1 : 0) + (data.byStatus.Locked ? 1 : 0) + (data.vulnerableKeys ? 1 : 0) + (data.pendingApprovals ? 1 : 0) + (data.openTodos ? 1 : 0) + (data.needsDetails ? 1 : 0) + (update?.updateAvailable && update.canApply ? 1 : 0)
    + (data.reminders?.filter((r) => r.count > 0).length ?? 0);
  return (
    <section className="dash-hero">
      <div className="dash-hero-text">
        <span className="dash-eyebrow">{hello}{first ? ', ' + first : ''}</span>
        <p className="dash-hero-title">
          {data.coveragePercent != null ? <><b>{data.coveragePercent.toFixed(0)} %</b> {tr("der Zielgruppe haben einen YubiKey.")}</> : <><b>{data.byStatus.Active ?? 0}</b> {tr("Keys sind aktiv.")}</>}
        </p>
        <p className="dash-hero-sub">{todo ? (todo === 1 ? tr("1 Punkt braucht heute Ihre Aufmerksamkeit.") : tr("{todo} Punkte brauchen heute Ihre Aufmerksamkeit.", { todo })) : tr("Alles in Ordnung, kein Handlungsbedarf.")} {tr("{inStock} Keys liegen bereit.", { inStock: data.inStock })}</p>
        <div className="dash-hero-meter"><span style={{ width: Math.min(100, data.coveragePercent ?? 0) + '%' }} /></div>
        <div className="dash-hero-links">
          <Link to="/register" className="hero-chip"><Icon name="keyPlus" size={15} />{tr("Key registrieren")}</Link>
          <Link to="/passkey" className="hero-chip"><Icon name="info" size={15} />{tr("Passkeys erklärt")}</Link>
          {update && (update.updateAvailable
            ? <Link to="/versionen" className="hero-chip hero-chip-update" title={tr("Installiert: {currentVersion}", { currentVersion: update.currentVersion })}><Icon name="sparkle" size={15} />{tr("Update auf {latestVersion} verfügbar", { latestVersion: update.latestVersion })}</Link>
            : <Link to="/versionen" className="hero-chip hero-chip-version" title={tr("Versionsverlauf")}><Icon name="history" size={15} />{tr("Version {currentVersion}", { currentVersion: update.currentVersion })}{!update.error && tr(" · aktuell")}</Link>)}
        </div>
      </div>
      <div className="dash-hero-art"><span className="pk-float"><KeyArt size={190} glow /></span></div>
    </section>
  );
}

/** Unterzeile der Kachel: der Hinweis mit den meisten Keys, sonst PIN-Wechsel-Fähigkeit. */
function advisorySub(data: DashboardData) {
  const top = [...(data.advisories ?? [])].filter((a) => a.affectsFido).sort((a, b) => b.keys - a.keys)[0];
  const pin = tr("{forcePinChangeUnsupported} ohne erzwingbaren PIN-Wechsel", { forcePinChangeUnsupported: data.forcePinChangeUnsupported });
  return top ? tr("am häufigsten {id} ({keys}) · {pin}", { id: top.id, keys: top.keys, pin }) : tr("keine bekannten Hinweise · {pin}", { pin });
}

const statusColor: Record<KeyStatus, string> = { Active: 'var(--ok)', InStock: 'var(--accent)', Locked: 'var(--error)', Retired: 'var(--muted)' };

function Kpi({ label, value, sub, to, tone, children, aside, icon, className = '', footer }: { label: string; value: ReactNode; sub: string; to?: string; tone?: 'warn'; children?: ReactNode; aside?: ReactNode; icon?: IconName; className?: string; footer?: ReactNode }) {
  const text = <>
    {icon && <span className="kpi-ico" aria-hidden><Icon name={icon} size={20} /></span>}
    <div className="label">{label}</div>
    <div className={'value' + (tone ? ' tone-text-' + tone : '')}><CountUp value={value} /></div>
    <div className="sub">{sub}</div>
    {children}
  </>;
  const body = aside ? <div className="kpi-gauge"><div style={{ flex: 1, minWidth: 0 }}>{text}</div>{aside}</div> : text;
  if (!to) return <div className={'card kpi ' + className}>{body}{footer}</div>;
  // A footer holds its own link, so the tile itself cannot be one link: link the body and keep the footer beside it.
  if (footer) return <div className={'card kpi kpi-has-foot ' + className}><Link className="kpi-body-link" to={to}>{body}</Link>{footer}</div>;
  if (to.startsWith('#')) return <a className={'card kpi kpi-link ' + className} href={to}>{body}</a>;
  return <Link className={'card kpi kpi-link ' + className} to={to}>{body}</Link>;
}

/** „Handlungsbedarf“: alles, was ein Admin heute anfassen sollte, mit direktem Link. */
function ActionItems({ data, keysLink, cert, update }: { data: DashboardData; keysLink: (s: KeyStatus) => string; cert?: CertificateStatus; update?: UpdateSummary }) {
  const items: { tone: 'error' | 'warn' | 'info'; text: ReactNode; to: string; cta: string }[] = [];
  if (cert && cert.state !== 'ok' && cert.state !== 'missing') items.push({
    tone: cert.state === 'warning' ? 'warn' : 'error',
    text: cert.state === 'expired'
      ? <>{tr("Das App-Zertifikat ist abgelaufen. Die Plattform kann sich bei keinem Tenant mehr anmelden.")}</>
      : <>{tr("Das App-Zertifikat läuft in {daysLeft} Tag(en) ab", { daysLeft: cert.daysLeft })}{cert.lastError ? tr(", die automatische Erneuerung ist fehlgeschlagen") : cert.nextThumbprint ? tr(", ein neues wartet auf Entra") : ''}.</>,
    to: '/platform#certificate', cta: tr("Zertifikat"),
  });
  for (const t of data.tenants) {
    if (t.checksFailed > 0) items.push({ tone: 'error', text: <><strong>{t.displayName}</strong>{tr(": {checksFailed} FIDO2-Prüfung(en) fehlgeschlagen", { checksFailed: t.checksFailed })}</>, to: '/tenants/' + t.id, cta: tr("Beheben") });
    if (t.lastSyncSucceeded === false) items.push({ tone: 'error', text: <><strong>{t.displayName}</strong>{tr(": letzte Synchronisation fehlgeschlagen")}</>, to: '/tenants/' + t.id, cta: tr("Ansehen") });
  }
  if ((data.byStatus.Locked ?? 0) > 0) items.push({ tone: 'warn', text: <>{tr("{Locked} gesperrte(r) Key(s)", { Locked: data.byStatus.Locked })}</>, to: keysLink('Locked'), cta: tr("Im Inventar") });
  if (data.vulnerableKeys > 0) items.push({ tone: 'warn', text: <>{tr("{vulnerableKeys} Key(s) von einem Yubico-Sicherheitshinweis zu Passkeys betroffen", { vulnerableKeys: data.vulnerableKeys })}</>, to: '/sicherheitshinweise', cta: tr("Betroffene Keys") });
  if (data.pendingApprovals > 0) items.push({ tone: 'warn', text: <>{tr("{pendingApprovals} Breakglass-Antrag/Anträge warten auf Freigabe", { pendingApprovals: data.pendingApprovals })}</>, to: '/approvals', cta: tr("Freigaben") });
  // Optionale Lebenszyklus-Erinnerungen, je Art eine Zeile.
  for (const k of reminderKinds) {
    const n = data.reminders?.find((r) => r.kind === k.kind)?.count ?? 0;
    if (n > 0) items.push({ tone: k.kind === 'Leaver' || k.kind === 'RetiredWithPasskey' ? 'warn' : 'info', text: <>{k.action(n)}</>, to: '/erinnerungen#' + k.id, cta: tr("Erinnerungen") });
  }
  if (data.openTodos > 0) items.push({ tone: 'info', text: <>{tr("{openTodos} offene(s) ToDo(s): Personen, die einen Key bekommen sollen", { openTodos: data.openTodos })}</>, to: '/todos', cta: tr("ToDo-Liste") });
  if (update?.updateAvailable && update.canApply) items.push({ tone: 'info', text: <>{tr("Version {latestVersion} ist verfügbar (installiert: {currentVersion})", { latestVersion: update.latestVersion, currentVersion: update.currentVersion })}</>, to: '/versionen', cta: tr("Was ist neu") });
  if (data.needsDetails > 0) items.push({ tone: 'info', text: <>{tr("{needsDetails} selbst registrierte Key(s) ohne Seriennummer/Firmware", { needsDetails: data.needsDetails })}</>, to: '/keys/identify', cta: tr("Nacherfassen") });

  if (items.length === 0) return (
    <div className="action-items all-good"><span className="ai-icon tone-ok"><Icon name="check" size={16} /></span>{tr("Kein Handlungsbedarf. Alle Prüfungen und Synchronisationen sind in Ordnung.")}</div>
  );
  return (
    <section className="action-items" aria-label={tr("Handlungsbedarf")}>
      <h2>{tr("Handlungsbedarf")} <span className="count">{items.length}</span></h2>
      <ul>
        {items.map((i, n) => (
          <li key={n}>
            <span className={'ai-icon tone-' + i.tone}><Icon name={i.tone === 'info' ? 'info' : 'alert'} size={15} /></span>
            <span className="ai-text">{i.text}</span>
            {i.to.startsWith('#') ? <a href={i.to}>{i.cta}</a> : <Link to={i.to}>{i.cta}</Link>}
          </li>
        ))}
      </ul>
    </section>
  );
}
