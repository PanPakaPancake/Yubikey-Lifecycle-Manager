import { useMemo, useState, type ReactNode } from 'react';
import { createPortal } from 'react-dom';
import { Link, useSearchParams } from 'react-router';
import { api, fmtDate } from '../api';
import { useCan } from '../auth';
import { useConfirm, useToast } from '../feedback';
import { Icon, type IconName } from '../icons';
import { ChooseTenant, useTenant } from '../tenant';
import { Badge, CountUp, Donut, download, Empty, ErrorBox, LiveSearchInput, MenuButton, Modal, PageHeader, Spinner, useAction, useApi } from '../ui';
import '../passkey.css';
import '../rollout.css';

// Passkey-Rollout: Wer hat schon einen Passkey, wer nur klassische MFA, wer gar nichts – und direkt handeln (TAP, Methoden entfernen).

export type Stage = 'NoMfa' | 'ClassicMfa' | 'OtherPasskey' | 'SecurityKey';
export interface RolloutUser {
  id: string; upn: string; displayName?: string | null; department?: string | null; userType?: string | null; accountEnabled: boolean; isAdmin: boolean;
  methods: string[]; stage: Stage; defaultMfaMethod?: string | null; lastSignIn?: string | null; inTarget: boolean; platformKeys: number;
}
interface MethodInfo { name: string; label: string; strength: 'None' | 'Mfa' | 'PhishingResistant'; isPasskey: boolean }
export interface RolloutData { generatedAt: string; fromReport: boolean; hasSignInData: boolean; targetDefined: boolean; methods: MethodInfo[]; users: RolloutUser[] }
interface LiveMethod { id: string; kind: string; reportName: string; label: string; strength: MethodInfo['strength']; isPasskey: boolean; displayName?: string | null; detail?: string | null; createdDateTime?: string | null; removable: boolean }
interface TapPolicy { enabled: boolean; defaultLifetimeInMinutes: number; minimumLifetimeInMinutes: number; maximumLifetimeInMinutes: number; defaultUsableOnce: boolean; isUsableOnceEnforced: boolean; targetCount: number }
interface TapResult { userId: string; upn: string; displayName?: string | null; pass?: string | null; startDateTime?: string | null; lifetimeInMinutes?: number | null; isUsableOnce?: boolean | null; error?: string | null }
interface RemovalResult { userId: string; upn: string; removed: number; removedLabels: string[]; skipped?: string | null; error?: string | null }

/** Reihenfolge vom Anfang bis zum Ziel. Farben passen zu den Status-Tönen der Plattform. */
export const stages: { key: Stage; label: string; short: string; text: string; icon: IconName; tone: 'error' | 'warn' | 'info' | 'ok'; color: string }[] = [
  { key: 'NoMfa', label: 'Keine MFA', short: 'Keine MFA', text: 'Nur Kennwort. Höchste Priorität: am besten direkt mit TAP und Key starten.', icon: 'alert', tone: 'error', color: 'var(--error)' },
  { key: 'ClassicMfa', label: 'Nur klassische MFA', short: 'Klassisch', text: 'Authenticator, SMS oder OTP. Lässt sich abfangen (Phishing).', icon: 'phone', tone: 'warn', color: 'var(--warn)' },
  { key: 'OtherPasskey', label: 'Anderer Passkey', short: 'Anderer Passkey', text: 'Windows Hello oder Passkey im Authenticator, aber kein Sicherheitsschlüssel.', icon: 'monitor', tone: 'info', color: 'var(--accent-2)' },
  { key: 'SecurityKey', label: 'Sicherheitsschlüssel', short: 'Ziel erreicht', text: 'Passkey auf einem YubiKey bzw. FIDO2-Key. Ziel des Rollouts.', icon: 'key', tone: 'ok', color: 'var(--ok)' },
];
const stageOf = (k: Stage) => stages.find((s) => s.key === k)!;

/** Methodentypen, die sich in der Sammelaktion entfernen lassen (Graph-Typ → Anzeige). */
const removableKinds: { kind: string; label: string; reportNames: string[] }[] = [
  { kind: 'phone', label: 'SMS / Anruf', reportNames: ['mobilePhone', 'alternateMobilePhone', 'officePhone'] },
  { kind: 'microsoftAuthenticator', label: 'Authenticator-App', reportNames: ['microsoftAuthenticatorPush', 'microsoftAuthenticatorPasswordless'] },
  { kind: 'softwareOath', label: 'Software-OTP', reportNames: ['softwareOneTimePasscode'] },
  { kind: 'email', label: 'E-Mail (SSPR)', reportNames: ['email'] },
  { kind: 'temporaryAccessPass', label: 'Befristeter Zugriffspass', reportNames: ['temporaryAccessPass'] },
];

const collator = new Intl.Collator('de', { sensitivity: 'base', numeric: true });
const PageSize = 150;

export default function RolloutPage() {
  const { tenant } = useTenant();
  if (!tenant) return <><PageHeader title="Passkey-Rollout" subtitle="Wer hat schon einen Passkey, wer nur klassische MFA, wer noch gar nichts." /><ChooseTenant purpose="Für den Rollout-Überblick" /></>;
  return <Rollout key={tenant.id} tenantId={tenant.id} tenantName={tenant.displayName} />;
}

function Rollout({ tenantId, tenantName }: { tenantId: string; tenantName: string }) {
  const can = useCan();
  const toast = useToast();
  const [params, setParams] = useSearchParams();
  const [refreshing, setRefreshing] = useState(false);
  const { data, error, loading, setData, reload } = useApi<RolloutData>(`/api/tenants/${tenantId}/rollout`);
  const tap = useApi<TapPolicy>(`/api/tenants/${tenantId}/rollout/tap-policy`);

  const stageFilter = (params.get('stage') ?? '').split(',').filter(Boolean) as Stage[];
  const method = params.get('method') ?? '';
  const q = params.get('q') ?? '';
  const onlyTarget = params.get('target') === '1';
  const guests = params.get('guests') === '1';
  const disabled = params.get('disabled') === '1';
  const set = (k: string, v: string) => {
    const next = new URLSearchParams(params);
    if (v) next.set(k, v); else next.delete(k);
    setParams(next, { replace: true });
    setLimit(PageSize);
  };

  const [selected, setSelected] = useState<Set<string>>(new Set());
  const [limit, setLimit] = useState(PageSize);
  const [openUser, setOpenUser] = useState<RolloutUser | null>(null);
  const [dialog, setDialog] = useState<'tap' | 'remove' | null>(null);
  const [tapTargets, setTapTargets] = useState<RolloutUser[]>([]);

  // Grundmenge: ohne Gäste/Deaktivierte (sofern nicht eingeschaltet), optional nur Zielgruppe.
  const base = useMemo(() => (data?.users ?? []).filter((u) =>
    (guests || u.userType?.toLowerCase() !== 'guest') && (disabled || u.accountEnabled) && (!onlyTarget || u.inTarget)), [data, guests, disabled, onlyTarget]);
  const counts = useMemo(() => {
    const c: Record<Stage, number> = { NoMfa: 0, ClassicMfa: 0, OtherPasskey: 0, SecurityKey: 0 };
    base.forEach((u) => { c[u.stage]++; });
    return c;
  }, [base]);
  const methodCounts = useMemo(() => {
    const m = new Map<string, number>();
    base.forEach((u) => new Set(u.methods.map((x) => labelOf(data, x))).forEach((l) => m.set(l, (m.get(l) ?? 0) + 1)));
    return [...m.entries()].filter(([l]) => l !== 'Kennwort').sort((a, b) => b[1] - a[1]);
  }, [base, data]);
  const filtered = useMemo(() => {
    const terms = q.toLocaleLowerCase('de').split(/\s+/).filter(Boolean);
    return base.filter((u) =>
      (stageFilter.length === 0 || stageFilter.includes(u.stage))
      && (!method || u.methods.some((m) => labelOf(data, m) === method))
      && terms.every((t) => [u.displayName, u.upn, u.department].some((v) => v?.toLocaleLowerCase('de').includes(t))))
      .sort((a, b) => collator.compare(a.displayName ?? a.upn, b.displayName ?? b.upn));
  }, [base, stageFilter, method, q, data]);

  const total = base.length;
  const goal = total ? Math.round((counts.SecurityKey / total) * 100) : 0;
  const phishingResistant = total ? Math.round(((counts.SecurityKey + counts.OtherPasskey) / total) * 100) : 0;
  const selectedUsers = (data?.users ?? []).filter((u) => selected.has(u.id));
  const allVisibleSelected = filtered.length > 0 && filtered.slice(0, limit).every((u) => selected.has(u.id));

  const refresh = async () => {
    setRefreshing(true);
    try {
      setData(await api.get<RolloutData>(`/api/tenants/${tenantId}/rollout?refresh=true`));
      toast.ok('Daten aus Entra neu geladen.');
    } catch (e) {
      toast.error(e instanceof Error ? e.message : String(e));
    } finally {
      setRefreshing(false);
    }
  };
  const toggleStage = (s: Stage) => set('stage', stageFilter.includes(s) ? stageFilter.filter((x) => x !== s).join(',') : [...stageFilter, s].join(','));
  const toggle = (id: string) => setSelected((cur) => { const n = new Set(cur); if (n.has(id)) n.delete(id); else n.add(id); return n; });
  const exportUrl = (format: 'csv' | 'xlsx') => {
    const qs = new URLSearchParams();
    if (stageFilter.length) qs.set('stages', stageFilter.join(','));
    if (guests) qs.set('guests', 'true');
    if (disabled) qs.set('disabled', 'true');
    if (onlyTarget) qs.set('target', 'true');
    return `/api/tenants/${tenantId}/rollout/export.${format}?` + qs.toString();
  };
  /** Nach einer Aktion: Methoden der betroffenen Benutzer im lokalen Stand nachziehen (Server hat seinen Stand schon aktualisiert). */
  const patchUsers = (changes: Record<string, string[]>) => setData((d) => d && {
    ...d, users: d.users.map((u) => (changes[u.id] ? { ...u, methods: changes[u.id], stage: stageFor(d, changes[u.id]) } : u)),
  });

  return (
    <div className="ro">
      <PageHeader title="Passkey-Rollout"
        subtitle={data ? <>Stand {fmtDate(data.generatedAt)} · {data.fromReport ? 'aus dem Entra-Registrierungsbericht' : 'pro Benutzer abgefragt (ohne Entra ID P1)'}</> : undefined}
        actions={<>
          <button type="button" onClick={refresh} disabled={refreshing || loading}><Icon name="refresh" size={16} className={refreshing ? 'spin' : undefined} />Aktualisieren</button>
          {can('ReadReports') && <MenuButton label="Exportieren" items={[
            { label: 'Liste (Excel)', hint: 'Mit den aktuellen Filtern (Stand, Zielgruppe)', icon: 'users', onClick: () => download(exportUrl('xlsx')) },
            { label: 'Liste (CSV)', hint: 'Für andere Programme', icon: 'download', onClick: () => download(exportUrl('csv')) },
          ]} />}
        </>} />
      <ErrorBox error={error} />
      {!data && loading && <div className="ro-loading"><Spinner label="Daten werden aus Entra geladen … bei großen Tenants ohne Entra ID P1 kann das etwas dauern." /></div>}
      {data && (
        <>
          <section className="ro-hero">
            <div>
              <div className="pk-eyebrow">{tenantName}</div>
              <h2 className="ro-title">{goal >= 100 ? 'Alle haben einen Sicherheitsschlüssel.' : <><CountUp value={counts.SecurityKey} /> von {total} Personen sind am Ziel.</>}</h2>
              <p className="ro-lead">
                {phishingResistant}&nbsp;% melden sich bereits phishing-resistent an. <strong>{counts.NoMfa}</strong> haben noch gar keine MFA,
                {' '}<strong>{counts.ClassicMfa}</strong> nur klassische MFA wie SMS oder Authenticator-Push.
              </p>
              <div className="ro-journey" role="img" aria-label={stages.map((s) => `${s.label}: ${counts[s.key]}`).join(', ')}>
                {stages.map((s, i) => counts[s.key] > 0 && (
                  <span key={s.key} style={{ flexGrow: counts[s.key], background: s.color, animationDelay: i * 90 + 'ms' }} title={`${s.label}: ${counts[s.key]}`} />
                ))}
              </div>
              <div className="ro-toggles">
                {data.targetDefined && <Toggle on={onlyTarget} onChange={(v) => set('target', v ? '1' : '')}>Nur Zielgruppe</Toggle>}
                <Toggle on={guests} onChange={(v) => set('guests', v ? '1' : '')}>Gäste</Toggle>
                <Toggle on={disabled} onChange={(v) => set('disabled', v ? '1' : '')}>Deaktivierte Konten</Toggle>
              </div>
            </div>
            <div className="ro-hero-art">
              <Donut size={168} thickness={18} parts={stages.map((s) => ({ key: s.key, value: counts[s.key], color: s.color, label: s.label }))}
                center={<><strong className="ro-goal"><CountUp value={goal + ' %'} /></strong><span className="small muted">am Ziel</span></>} />
            </div>
          </section>

          <div className="ro-stages">
            {stages.map((s, i) => (
              <button key={s.key} type="button" className={'ro-stage tone-' + s.tone + (stageFilter.includes(s.key) ? ' on' : '')} onClick={() => toggleStage(s.key)} aria-pressed={stageFilter.includes(s.key)}>
                <span className="ro-stage-top"><span className="pk-num">{i + 1}</span><Icon name={s.icon} size={18} /><span className="ro-stage-label">{s.label}</span></span>
                <span className="ro-stage-value"><CountUp value={counts[s.key]} /><small>{total ? Math.round((counts[s.key] / total) * 100) : 0}&nbsp;%</small></span>
                <span className="ro-stage-bar"><i style={{ width: (total ? (counts[s.key] / total) * 100 : 0) + '%' }} /></span>
                <span className="ro-stage-text">{s.text}</span>
              </button>
            ))}
          </div>

          {tap.data && !tap.data.enabled && (can('IssueTemporaryAccessPass')) && (
            <div className="pk-note ro-note"><Icon name="ticket" size={20} />
              <div>
                <strong>Befristeter Zugriffspass (TAP) ist im Tenant ausgeschaltet.</strong> Ohne TAP können sich Personen ohne MFA nicht selbst einen Passkey einrichten, und hier lassen sich keine TAPs erzeugen.
                <div className="ro-note-actions">
                  {can('ManageTenants') && <EnableTap tenantId={tenantId} onDone={(p) => tap.setData(p)} />}
                  <span className="small muted">Manuell: Entra Admin Center → Schutz → Authentifizierungsmethoden → Richtlinien → Befristeter Zugriffspass → Aktivieren.</span>
                </div>
              </div>
            </div>
          )}

          <div className="ro-grid">
            <section className="card ro-methods">
              <div className="card-head"><h2>Andere Methoden im Einsatz</h2>{method && <button type="button" className="link" onClick={() => set('method', '')}>Filter aufheben</button>}</div>
              {methodCounts.length === 0 ? <Empty>Außer dem Kennwort ist nichts registriert.</Empty> : (
                <ul className="ro-method-list">
                  {methodCounts.map(([label, n]) => (
                    <li key={label}>
                      <button type="button" className={method === label ? 'on' : ''} onClick={() => set('method', method === label ? '' : label)}>
                        <span className="ro-method-name">{label}</span>
                        <span className="ro-method-bar"><i style={{ width: (n / Math.max(1, total)) * 100 + '%' }} /></span>
                        <span className="num">{n}</span>
                      </button>
                    </li>
                  ))}
                </ul>
              )}
              <p className="small muted">Ein Klick filtert die Liste, z. B. alle mit SMS. Danach lassen sich die Methoden gesammelt entfernen.</p>
            </section>
            <section className="card ro-howto">
              <div className="card-head"><h2>So geht der Rollout</h2></div>
              <ol className="ro-steps">
                <li><span className="pk-num">1</span><div><strong>Ohne MFA zuerst</strong><span>Filter „Keine MFA“, Personen auswählen, TAP erzeugen und mit dem Key übergeben.</span></div></li>
                <li><span className="pk-num">2</span><div><strong>Keys registrieren</strong><span>Über <Link to="/register">Registrieren</Link> on Behalf oder die Person richtet ihn mit dem TAP selbst ein.</span></div></li>
                <li><span className="pk-num">3</span><div><strong>Altlasten entfernen</strong><span>Wer einen Key hat, braucht kein SMS mehr: Methode filtern, auswählen, entfernen.</span></div></li>
              </ol>
            </section>
          </div>

          <section className="card ro-list">
            <div className="toolbar">
              <LiveSearchInput value={q} onSearch={(v) => set('q', v)} placeholder="Name, UPN oder Abteilung" />
              {(stageFilter.length > 0 || method) && (
                <div className="chips" style={{ margin: 0 }}>
                  {stageFilter.map((s) => <span key={s} className="chip">{stageOf(s).label}<button type="button" className="chip-x" aria-label="Filter entfernen" onClick={() => toggleStage(s)}>×</button></span>)}
                  {method && <span className="chip">{method}<button type="button" className="chip-x" aria-label="Filter entfernen" onClick={() => set('method', '')}>×</button></span>}
                </div>
              )}
              <span className="muted small" style={{ marginLeft: 'auto' }}>{filtered.length} von {total} Personen</span>
            </div>
            {filtered.length === 0 ? <Empty>Keine Person passt zu den Filtern.</Empty> : (
              <div className="table-wrap">
                <table className="ro-table">
                  <thead>
                    <tr>
                      <th className="ro-check"><input type="checkbox" aria-label="Alle sichtbaren auswählen" checked={allVisibleSelected}
                        onChange={() => setSelected((cur) => { const n = new Set(cur); filtered.slice(0, limit).forEach((u) => (allVisibleSelected ? n.delete(u.id) : n.add(u.id))); return n; })} /></th>
                      <th>Person</th><th>Stand</th><th>Methoden</th><th className="hide-sm">Keys</th>{data.hasSignInData && <th className="hide-sm">Letzte Anmeldung</th>}
                    </tr>
                  </thead>
                  <tbody>
                    {filtered.slice(0, limit).map((u) => (
                      <tr key={u.id} className={'clickable' + (selected.has(u.id) ? ' selected-row' : '')} onClick={() => setOpenUser(u)}>
                        <td className="ro-check" onClick={(e) => e.stopPropagation()}><input type="checkbox" aria-label={`${u.displayName ?? u.upn} auswählen`} checked={selected.has(u.id)} onChange={() => toggle(u.id)} /></td>
                        <td>
                          <div className="ro-person"><strong>{u.displayName ?? u.upn}</strong>
                            {u.isAdmin && <Badge tone="info">Admin</Badge>}{!u.accountEnabled && <Badge>Deaktiviert</Badge>}{u.userType?.toLowerCase() === 'guest' && <Badge>Gast</Badge>}
                          </div>
                          <div className="small muted">{u.upn}{u.department ? ' · ' + u.department : ''}</div>
                        </td>
                        <td><StageBadge stage={u.stage} /></td>
                        <td><MethodChips data={data} methods={u.methods} /></td>
                        <td className="hide-sm">{u.platformKeys > 0 ? <Badge tone="ok">{u.platformKeys}</Badge> : <span className="muted">–</span>}</td>
                        {data.hasSignInData && <td className="hide-sm small muted">{u.lastSignIn ? fmtDate(u.lastSignIn, false) : 'nie'}</td>}
                      </tr>
                    ))}
                  </tbody>
                </table>
                {filtered.length > limit && <div className="ro-more"><button type="button" onClick={() => setLimit(limit + PageSize)}>Weitere {Math.min(PageSize, filtered.length - limit)} anzeigen</button></div>}
              </div>
            )}
          </section>

          {/* Overlays außerhalb der animierten Seite, sonst gelten „fixed“-Positionen relativ zur Seite statt zum Fenster. */}
          {createPortal(<div className="ro-layer">
          {selected.size > 0 && (
            <div className="ro-selbar" role="region" aria-label="Auswahl">
              <span><strong>{selected.size}</strong> ausgewählt</span>
              {can('IssueTemporaryAccessPass') && <button type="button" className="primary" onClick={() => { setTapTargets(selectedUsers); setDialog('tap'); }}><Icon name="ticket" size={16} />TAP erzeugen</button>}
              {can('RemoveAuthMethods') && <button type="button" onClick={() => setDialog('remove')}><Icon name="trash" size={16} />Methoden entfernen</button>}
              <button type="button" className="ghost" onClick={() => setSelected(new Set())}>Auswahl aufheben</button>
            </div>
          )}

          {openUser && <UserDialog tenantId={tenantId} user={openUser} data={data} onClose={() => setOpenUser(null)}
            onChanged={(methods) => patchUsers({ [openUser.id]: methods })}
            onTap={() => { setTapTargets([openUser]); setDialog('tap'); }} />}
          {dialog === 'tap' && <TapDialog tenantId={tenantId} users={tapTargets} policy={tap.data} onClose={() => setDialog(null)}
            onDone={(r) => patchUsers(Object.fromEntries(r.filter((x) => x.pass).map((x) => {
              const u = data.users.find((y) => y.id === x.userId);
              return [x.userId, [...new Set([...(u?.methods ?? []), 'temporaryAccessPass'])]];
            })))} />}
          {dialog === 'remove' && <RemoveDialog tenantId={tenantId} users={selectedUsers} data={data} onClose={() => setDialog(null)}
            onDone={() => { void reload(); }} />}
          </div>, document.body)}
        </>
      )}
    </div>
  );
}

function labelOf(data: RolloutData | undefined, name: string) {
  return data?.methods.find((m) => m.name.toLowerCase() === name.toLowerCase())?.label ?? name;
}

/** Gleiche Einstufung wie im Backend (AuthMethodCatalog.StageOf). */
function stageFor(data: RolloutData, methods: string[]): Stage {
  const infos = methods.map((m) => data.methods.find((x) => x.name.toLowerCase() === m.toLowerCase()));
  if (methods.some((m) => ['passkeydevicebound', 'fido2'].includes(m.toLowerCase()))) return 'SecurityKey';
  if (infos.some((i) => i?.strength === 'PhishingResistant')) return 'OtherPasskey';
  if (infos.some((i) => i?.strength === 'Mfa')) return 'ClassicMfa';
  return 'NoMfa';
}

function Toggle({ on, onChange, children }: { on: boolean; onChange: (v: boolean) => void; children: ReactNode }) {
  return (
    <label className={'ro-toggle' + (on ? ' on' : '')}>
      <input type="checkbox" checked={on} onChange={(e) => onChange(e.target.checked)} />
      <span className="ro-switch" aria-hidden />{children}
    </label>
  );
}

export function StageBadge({ stage }: { stage: Stage }) {
  const s = stageOf(stage);
  return <span className={'badge ro-badge tone-' + s.tone}><Icon name={s.icon} size={13} />{s.label}</span>;
}

function MethodChips({ data, methods }: { data: RolloutData; methods: string[] }) {
  const labels = [...new Set(methods.filter((m) => m.toLowerCase() !== 'password').map((m) => labelOf(data, m)))];
  if (labels.length === 0) return <span className="muted small">nur Kennwort</span>;
  return (
    <span className="ro-chips">
      {labels.map((l) => {
        const info = data.methods.find((m) => m.label === l);
        return <span key={l} className={'ro-chip s-' + (info?.strength ?? 'None')}>{l}</span>;
      })}
    </span>
  );
}

function EnableTap({ tenantId, onDone }: { tenantId: string; onDone: (p: TapPolicy) => void }) {
  const { busy, error, run } = useAction();
  const toast = useToast();
  return (
    <>
      <button type="button" className="primary small" disabled={busy} onClick={() => run(async () => {
        onDone(await api.post<TapPolicy>(`/api/tenants/${tenantId}/rollout/tap-policy/enable`));
        toast.ok('Befristeter Zugriffspass ist jetzt aktiviert.');
      })}>{busy ? 'Wird aktiviert …' : 'Jetzt aktivieren'}</button>
      <ErrorBox error={error} />
    </>
  );
}

/** Detail einer Person: Methoden live aus Entra, einzeln entfernen, TAP, Key registrieren. */
function UserDialog({ tenantId, user, data, onClose, onChanged, onTap }: { tenantId: string; user: RolloutUser; data: RolloutData; onClose: () => void; onChanged: (methods: string[]) => void; onTap: () => void }) {
  const can = useCan();
  const confirm = useConfirm();
  const toast = useToast();
  const { data: methods, error, loading, setData } = useApi<LiveMethod[]>(`/api/tenants/${tenantId}/rollout/users/${encodeURIComponent(user.id)}/methods`);
  const [busyId, setBusyId] = useState<string | null>(null);
  const live = methods ?? [];
  const stage = methods ? stageFor(data, live.map((m) => m.reportName)) : user.stage;

  const remove = async (m: LiveMethod) => {
    const rest = live.filter((x) => x.id !== m.id);
    const lastMfa = !rest.some((x) => x.strength !== 'None');
    const ok = await confirm({
      title: `${m.label} entfernen?`,
      message: <>Die Methode wird bei <strong>{user.displayName ?? user.upn}</strong> in Entra gelöscht.{lastMfa && <><br /><br /><strong>Achtung:</strong> Danach hat die Person keine MFA-Methode mehr und kann sich ggf. nicht mehr anmelden.</>}</>,
      confirmLabel: 'Entfernen', danger: true,
    });
    if (!ok) return;
    setBusyId(m.id);
    try {
      const next = await api.del<LiveMethod[]>(`/api/tenants/${tenantId}/rollout/users/${encodeURIComponent(user.id)}/methods/${m.kind}/${encodeURIComponent(m.id)}`);
      setData(next);
      onChanged([...new Set(next.map((x) => x.reportName))]);
      toast.ok(`${m.label} entfernt.`);
    } catch (e) {
      toast.error(e instanceof Error ? e.message : String(e));
    } finally {
      setBusyId(null);
    }
  };

  return (
    <Modal title={user.displayName ?? user.upn} onClose={onClose} footer={<>
      {can('RegisterKeys') && <Link className="btn" to={`/register?tenantId=${tenantId}&q=${encodeURIComponent(user.upn)}`}><Icon name="keyPlus" size={16} />Key registrieren</Link>}
      {can('IssueTemporaryAccessPass') && <button type="button" className="primary" onClick={onTap}><Icon name="ticket" size={16} />TAP erzeugen</button>}
    </>}>
      <div className="ro-user-head">
        <span className="avatar" aria-hidden>{(user.displayName ?? user.upn).split(/\s+/).map((p) => p[0]).slice(0, 2).join('').toUpperCase()}</span>
        <div>
          <div>{user.upn}</div>
          <div className="small muted">{[user.department, user.isAdmin ? 'Administrator' : null, user.platformKeys ? `${user.platformKeys} Key(s) im Inventar` : null].filter(Boolean).join(' · ') || ' '}</div>
        </div>
        <StageBadge stage={stage} />
      </div>
      <ErrorBox error={error} />
      {loading && !methods && <Spinner label="Methoden werden aus Entra geladen …" />}
      {methods && (
        <ul className="ro-live">
          {live.map((m) => (
            <li key={m.id} className={'s-' + m.strength}>
              <span className="ro-live-icon"><Icon name={m.isPasskey ? 'key' : m.kind === 'phone' ? 'phone' : m.kind === 'temporaryAccessPass' ? 'ticket' : m.strength === 'PhishingResistant' ? 'monitor' : m.kind === 'password' ? 'scroll' : 'shield'} size={18} /></span>
              <div>
                <strong>{m.label}</strong>
                <div className="small muted">{[m.displayName, m.detail, m.createdDateTime ? 'seit ' + fmtDate(m.createdDateTime, false) : null].filter(Boolean).join(' · ') || ' '}</div>
              </div>
              {m.removable && can('RemoveAuthMethods')
                ? <button type="button" className="ghost small danger" disabled={busyId === m.id} onClick={() => remove(m)}><Icon name="trash" size={15} />{busyId === m.id ? '…' : 'Entfernen'}</button>
                : m.kind === 'fido2' ? <span className="small muted" title="Passkeys auf Sicherheitsschlüsseln werden über das Inventar gesperrt">über Inventar</span> : <span />}
            </li>
          ))}
        </ul>
      )}
      <p className="small muted">Microsoft aktualisiert den Registrierungsbericht mit etwas Verzögerung. Diese Liste kommt live aus Entra.</p>
    </Modal>
  );
}

const lifetimes = [60, 120, 240, 480, 1440, 2880, 10080, 43200];
const lifetimeLabel = (m: number) => (m < 60 ? `${m} Minuten` : m < 1440 ? `${m / 60} Stunde${m === 60 ? '' : 'n'}` : `${m / 1440} Tag${m === 1440 ? '' : 'e'}`);

function TapDialog({ tenantId, users, policy, onClose, onDone }: { tenantId: string; users: RolloutUser[]; policy?: TapPolicy; onClose: () => void; onDone: (r: TapResult[]) => void }) {
  const toast = useToast();
  const { busy, error, run } = useAction();
  const min = policy?.minimumLifetimeInMinutes ?? 60;
  const max = policy?.maximumLifetimeInMinutes ?? 480;
  const options = [...new Set([...lifetimes, policy?.defaultLifetimeInMinutes ?? 60])].filter((m) => m >= min && m <= max).sort((a, b) => a - b);
  const [lifetime, setLifetime] = useState(policy?.defaultLifetimeInMinutes ?? 60);
  const [once, setOnce] = useState(policy?.defaultUsableOnce ?? false);
  const [startNow, setStartNow] = useState(true);
  const [start, setStart] = useState(() => new Date(Date.now() + 3600_000).toISOString().slice(0, 16));
  const [replace, setReplace] = useState(true);
  const [results, setResults] = useState<TapResult[] | null>(null);

  const submit = () => run(async () => {
    const r = await api.post<TapResult[]>(`/api/tenants/${tenantId}/rollout/taps`, {
      userIds: users.map((u) => u.id), lifetimeInMinutes: lifetime, isUsableOnce: policy?.isUsableOnceEnforced ? true : once,
      startDateTime: startNow ? null : new Date(start).toISOString(), replaceExisting: replace,
    });
    setResults(r);
    onDone(r);
    const ok = r.filter((x) => x.pass).length;
    if (ok) toast.ok(`${ok} TAP${ok === 1 ? '' : 's'} erzeugt.`);
  });

  const ok = results?.filter((r) => r.pass) ?? [];
  const copyAll = async () => {
    await navigator.clipboard.writeText(ok.map((r) => `${r.displayName ?? r.upn}\t${r.upn}\t${r.pass}`).join('\n'));
    toast.ok('In die Zwischenablage kopiert.');
  };
  const saveCsv = () => {
    const esc = (v: unknown) => `"${String(v ?? '').replace(/"/g, '""')}"`;
    const rows = [['Name', 'UPN', 'TAP', 'Gültig ab', 'Gültigkeit (Minuten)', 'Einmalig'], ...ok.map((r) => [r.displayName, r.upn, r.pass, r.startDateTime ? new Date(r.startDateTime).toLocaleString('de-DE') : '', r.lifetimeInMinutes, r.isUsableOnce ? 'ja' : 'nein'])];
    const blob = new Blob(['﻿' + rows.map((r) => r.map(esc).join(';')).join('\r\n')], { type: 'text/csv;charset=utf-8' });
    const a = document.createElement('a');
    a.href = URL.createObjectURL(blob);
    a.download = `tap-${new Date().toISOString().slice(0, 10)}.csv`;
    a.click();
    URL.revokeObjectURL(a.href);
  };

  if (results) {
    return (
      <Modal title="Befristete Zugriffspässe" onClose={onClose} footer={<>
        {ok.length > 1 && <button type="button" onClick={saveCsv}><Icon name="download" size={16} />Als CSV speichern</button>}
        {ok.length > 0 && <button type="button" onClick={copyAll}><Icon name="copy" size={16} />Alle kopieren</button>}
        <button type="button" className="primary" onClick={onClose}>Fertig</button>
      </>}>
        {ok.length > 0 && <div className="pk-note"><Icon name="alert" size={18} /><div><strong>Die Codes werden nur jetzt angezeigt.</strong> Die Plattform speichert sie nicht. Bitte direkt weitergeben oder sicher ablegen.</div></div>}
        <ul className="ro-taps">
          {results.map((r) => (
            <li key={r.userId} className={r.pass ? '' : 'failed'}>
              <div><strong>{r.displayName ?? r.upn}</strong><div className="small muted">{r.upn}</div></div>
              {r.pass ? (
                <>
                  <code className="ro-tap-code">{r.pass}</code>
                  <span className="small muted">{r.startDateTime ? 'ab ' + fmtDate(r.startDateTime) : 'sofort'} · {lifetimeLabel(r.lifetimeInMinutes ?? 60)}{r.isUsableOnce ? ' · einmalig' : ''}</span>
                  <button type="button" className="icon-btn" aria-label="Kopieren" onClick={async () => { await navigator.clipboard.writeText(r.pass!); toast.ok('Kopiert.'); }}><Icon name="copy" size={16} /></button>
                </>
              ) : <span className="ro-tap-error">{r.error}</span>}
            </li>
          ))}
        </ul>
      </Modal>
    );
  }

  return (
    <Modal title={users.length === 1 ? 'TAP erzeugen' : `TAP für ${users.length} Personen erzeugen`} onClose={onClose} footer={<>
      <button type="button" onClick={onClose}>Abbrechen</button>
      <button type="button" className="primary" disabled={busy || users.length === 0 || users.length > 100} onClick={submit}>{busy ? 'Wird erzeugt …' : 'Erzeugen'}</button>
    </>}>
      <p className="muted">Mit dem befristeten Zugriffspass melden sich die Personen ohne Kennwort an und richten ihren Passkey ein. {users.length > 100 && <strong>Höchstens 100 Personen auf einmal.</strong>}</p>
      <div className="ro-who">{users.slice(0, 8).map((u) => <span key={u.id} className="chip" style={{ paddingRight: 10 }}>{u.displayName ?? u.upn}</span>)}{users.length > 8 && <span className="muted small">und {users.length - 8} weitere</span>}</div>
      {policy && !policy.enabled && <div className="alert warn">Die Methode ist im Tenant ausgeschaltet; das Erzeugen schlägt fehl, bis sie aktiviert ist.</div>}
      <div className="ro-form">
        <label className="field"><span className="field-label">Gültigkeit</span>
          <select value={lifetime} onChange={(e) => setLifetime(Number(e.target.value))}>{options.map((m) => <option key={m} value={m}>{lifetimeLabel(m)}</option>)}</select>
          <span className="field-hint">Laut Tenant-Richtlinie {lifetimeLabel(min)} bis {lifetimeLabel(max)}.</span>
        </label>
        <div className="field"><span className="field-label">Beginn</span>
          <div className="segmented"><button type="button" className={startNow ? 'on' : ''} onClick={() => setStartNow(true)}>Sofort</button><button type="button" className={!startNow ? 'on' : ''} onClick={() => setStartNow(false)}>Später</button></div>
          {!startNow && <input type="datetime-local" value={start} onChange={(e) => setStart(e.target.value)} style={{ marginTop: 8 }} />}
        </div>
      </div>
      <label className="check"><input type="checkbox" checked={policy?.isUsableOnceEnforced ? true : once} disabled={policy?.isUsableOnceEnforced} onChange={(e) => setOnce(e.target.checked)} />
        <span>Nur einmal verwendbar{policy?.isUsableOnceEnforced && <span className="muted small"> (vom Tenant vorgegeben)</span>}</span></label>
      <label className="check"><input type="checkbox" checked={replace} onChange={(e) => setReplace(e.target.checked)} /><span>Vorhandenen TAP ersetzen <span className="muted small">(Entra erlaubt nur einen pro Person)</span></span></label>
      <ErrorBox error={error} />
    </Modal>
  );
}

function RemoveDialog({ tenantId, users, data, onClose, onDone }: { tenantId: string; users: RolloutUser[]; data: RolloutData; onClose: () => void; onDone: () => void }) {
  const toast = useToast();
  const { busy, error, run } = useAction();
  const present = removableKinds.filter((k) => users.some((u) => u.methods.some((m) => k.reportNames.includes(m))));
  const [kinds, setKinds] = useState<string[]>(present.filter((k) => k.kind === 'phone').map((k) => k.kind));
  const [keep, setKeep] = useState(true);
  const [typed, setTyped] = useState('');
  const [results, setResults] = useState<RemovalResult[] | null>(null);
  const affected = users.filter((u) => u.methods.some((m) => removableKinds.some((k) => kinds.includes(k.kind) && k.reportNames.includes(m))));
  const unsafe = affected.filter((u) => u.stage !== 'SecurityKey' && u.stage !== 'OtherPasskey').length;
  const effective = keep ? affected.length - unsafe : affected.length;
  const canSubmit = kinds.length > 0 && effective > 0 && (keep || typed.trim().toUpperCase() === 'ENTFERNEN');

  const submit = () => run(async () => {
    const r = await api.post<RemovalResult[]>(`/api/tenants/${tenantId}/rollout/remove-methods`, { userIds: affected.map((u) => u.id), kinds, keepPhishingResistantOnly: keep });
    setResults(r);
    onDone();
    const n = r.reduce((s, x) => s + x.removed, 0);
    toast.ok(`${n} Methode${n === 1 ? '' : 'n'} entfernt.`);
  });

  if (results) {
    return (
      <Modal title="Methoden entfernt" onClose={onClose} footer={<button type="button" className="primary" onClick={onClose}>Fertig</button>}>
        <ul className="ro-taps">
          {results.map((r) => (
            <li key={r.userId} className={r.error ? 'failed' : ''}>
              <div><strong>{data.users.find((u) => u.id === r.userId)?.displayName ?? r.upn}</strong><div className="small muted">{r.upn}</div></div>
              {r.error ? <span className="ro-tap-error">{r.error}</span>
                : r.removed > 0 ? <span><Badge tone="ok">{r.removed} entfernt</Badge> <span className="small muted">{r.removedLabels.join(', ')}</span></span>
                : <span className="small muted">{r.skipped}</span>}
            </li>
          ))}
        </ul>
      </Modal>
    );
  }

  return (
    <Modal title="Andere Methoden entfernen" onClose={onClose} footer={<>
      <button type="button" onClick={onClose}>Abbrechen</button>
      <button type="button" className="primary danger-fill" disabled={busy || !canSubmit} onClick={submit}>{busy ? 'Wird entfernt …' : `Bei ${effective} Person${effective === 1 ? '' : 'en'} entfernen`}</button>
    </>}>
      <p className="muted">Welche Methoden sollen bei den {users.length} ausgewählten Personen in Entra gelöscht werden? Kennwort und Passkeys bleiben immer erhalten.</p>
      {present.length === 0 ? <Empty>Keine der ausgewählten Personen hat eine entfernbare Methode.</Empty> : (
        <div className="ro-kinds">
          {present.map((k) => {
            const n = users.filter((u) => u.methods.some((m) => k.reportNames.includes(m))).length;
            const on = kinds.includes(k.kind);
            return (
              <label key={k.kind} className={'ro-kind' + (on ? ' on' : '')}>
                <input type="checkbox" checked={on} onChange={() => setKinds(on ? kinds.filter((x) => x !== k.kind) : [...kinds, k.kind])} />
                <span>{k.label}</span><span className="num">{n}</span>
              </label>
            );
          })}
        </div>
      )}
      <label className="check"><input type="checkbox" checked={keep} onChange={(e) => setKeep(e.target.checked)} />
        <span><strong>Nur bei Personen, die danach noch einen Passkey haben</strong><br /><span className="small muted">Schützt davor, jemanden ohne MFA zurückzulassen. {keep && unsafe > 0 && `${unsafe} Person${unsafe === 1 ? '' : 'en'} werden dadurch übersprungen.`}</span></span></label>
      {!keep && (
        <div className="alert error">
          <strong>Ohne Schutz</strong> können Personen danach ohne MFA dastehen{unsafe > 0 && ` (hier ${unsafe})`}. Zum Bestätigen <code>ENTFERNEN</code> eingeben:
          <input value={typed} onChange={(e) => setTyped(e.target.value)} placeholder="ENTFERNEN" style={{ marginTop: 8, width: '100%' }} aria-label="Bestätigung" />
        </div>
      )}
      <ErrorBox error={error} />
    </Modal>
  );
}
