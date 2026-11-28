import { Link } from 'react-router';
import { fmtDate } from '../api';
import { changelog, kindLabels, parseChangelog, type Release, type UpdateSummary } from '../changelog';
import { Icon } from '../icons';
import { Card, PageHeader, useApi } from '../ui';
import { tr } from '../i18n';

/** Versionsverlauf: was sich je Version geändert hat, und ob eine neuere Version bereitsteht. */
export default function VersionsPage() {
  const { data: s } = useApi<UpdateSummary>('/api/updates/summary');
  const current = s?.currentVersion;
  // Notizen der neuen Version kommen von GitHub (Abschnitt aus changelog.md, ältere Releases mit automatischen Notizen).
  const pending = s?.updateAvailable && s.latestVersion
    ? parseChangelog(s.notes ?? '').find((r) => r.version === s.latestVersion || r.version === '') ?? null
    : null;

  return (
    <div className="versions">
      <PageHeader title={tr("Versionsverlauf")} subtitle={current ? <>{tr("Installiert ist Version")} <strong>{current}</strong>.</> : tr("Was sich mit jeder Version geändert hat.")} />

      {s && <UpdateState s={s} pending={pending} />}

      <ol className="ver-timeline">
        {changelog.map((r) => <ReleaseItem key={r.version} r={r} installed={r.version === current} />)}
      </ol>
    </div>
  );
}

function UpdateState({ s, pending }: { s: UpdateSummary; pending: Release | null }) {
  if (s.updateAvailable) return (
    <Card className="ver-update">
      <div className="ver-update-head">
        <span className="ver-update-ico" aria-hidden><Icon name="sparkle" size={22} /></span>
        <div>
          <h2>{tr("Version {latestVersion} ist verfügbar", { latestVersion: s.latestVersion })}</h2>
          <p className="muted">{tr("Installiert ist {currentVersion}", { currentVersion: s.currentVersion })}{s.publishedAt && <>{tr(", die neue Version erschien am {n}", { n: fmtDate(s.publishedAt, false) })}</>}.</p>
        </div>
        <div className="ver-update-actions">
          {s.canApply
            ? <Link className="btn primary" to="/platform#updates"><Icon name="download" size={16} />{tr("Zum Update")}</Link>
            : <span className="muted small">{tr("Das Update startet ein Administrator unter „Globale Einstellungen“.")}</span>}
          {s.releaseUrl && <a className="btn ghost" href={s.releaseUrl} target="_blank" rel="noreferrer">{tr("Auf GitHub")}</a>}
        </div>
      </div>
      {pending && <Changes r={pending} />}
    </Card>
  );
  return (
    <div className={'ver-state' + (s.error ? ' ver-state-warn' : '')}>
      <Icon name={s.error ? 'alert' : 'check'} size={16} />
      {s.error ? <span>{tr("Ob es eine neuere Version gibt, ließ sich nicht prüfen: {error}", { error: s.error })}</span>
        : <span>{tr("Sie verwenden die neueste Version. Zuletzt geprüft {checkedAt}.", { checkedAt: fmtDate(s.checkedAt) })}</span>}
    </div>
  );
}

function ReleaseItem({ r, installed }: { r: Release; installed: boolean }) {
  return (
    <li className={'ver-item' + (installed ? ' ver-installed' : '')}>
      <span className="ver-dot" aria-hidden />
      <div className="ver-card">
        <div className="ver-title">
          <h3>{tr("Version {version}", { version: r.version })}</h3>
          {r.date && <span className="muted small">{fmtDate(r.date, false)}</span>}
          {installed && <span className="badge tone-ok">{tr("Installiert")}</span>}
        </div>
        <Changes r={r} />
      </div>
    </li>
  );
}

function Changes({ r }: { r: Release }) {
  return (
    <ul className="ver-changes">
      {r.changes.map((c, i) => (
        <li key={i}><span className={'ver-kind ver-kind-' + c.kind}>{kindLabels[c.kind]}</span><span>{c.text}</span></li>
      ))}
    </ul>
  );
}
