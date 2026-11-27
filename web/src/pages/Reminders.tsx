import { useState } from 'react';
import { Link } from 'react-router';
import { anyReminder, api, fmtDate, statusLabels, usageLabels, type KeyStatus, type KeyUsage } from '../api';
import { useCan } from '../auth';
import { CountUp, Empty, ErrorBox, Field, Info, Modal, PageHeader, Spinner, useAction, useApi } from '../ui';
import { useToast } from '../feedback';
import { useTenant } from '../tenant';
import { Icon, type IconName } from '../icons';

export type ReminderKind = 'UnusedKey' | 'NoSpareKey' | 'Leaver' | 'RetiredWithPasskey';

export interface ReminderItem {
  kind: ReminderKind; tenantId: string; tenantName: string; subject: string;
  keyId?: string | null; serialNumber?: number | null; model?: string | null; keyStatus?: KeyStatus | null; usage?: KeyUsage | null;
  entraUserId?: string | null; userPrincipalName?: string | null; userDisplayName?: string | null;
  since?: string | null; detail: string; firstSeenAt?: string | null;
  dismissed: boolean; dismissedByName?: string | null; dismissedAt?: string | null; dismissNote?: string | null; todoId?: string | null;
}
export interface ReminderTenant { tenantId: string; tenantName: string; unusedKeys: boolean; unusedKeyDays: number; noSpareKey: boolean; leavers: boolean; retiredWithPasskey: boolean; createTodos: boolean }
export interface ReminderOverview { tenants: ReminderTenant[]; counts: { kind: ReminderKind; count: number }[]; items: ReminderItem[]; keysWithoutUsageDate: number }

/** Texte je Art, auch für den Handlungsbedarf auf der Übersicht. */
export const reminderKinds: { kind: ReminderKind; id: string; icon: IconName; title: string; action: (n: number) => string; what: string; todo: string; setting: keyof ReminderTenant }[] = [
  { kind: 'Leaver', id: 'ausgeschieden', icon: 'user', title: 'Ausgeschiedene Personen mit aktivem Key', setting: 'leavers',
    action: (n) => `${n} aktive(r) Key(s) von ausgeschiedenen Personen`,
    what: 'Das Konto ist in Entra deaktiviert oder gelöscht, der Key gilt aber noch als ausgegeben.',
    todo: 'Key zurückholen, dann zurücksetzen und ins Lager legen oder ausmustern.' },
  { kind: 'RetiredWithPasskey', id: 'gesperrt-mit-passkey', icon: 'shield', title: 'Gesperrte oder ausgemusterte Keys mit Passkey in Entra', setting: 'retiredWithPasskey',
    action: (n) => `${n} gesperrte(r) oder ausgemusterte(r) Key(s) mit Passkey in Entra`,
    what: 'Bei uns gesperrt bzw. ausgemustert, in Entra ist der Passkey aber noch angemeldet und funktioniert.',
    todo: 'Am Key „Sperren“ erneut ausführen oder den Passkey in Entra löschen.' },
  { kind: 'UnusedKey', id: 'ungenutzt', icon: 'history', title: 'Keys lange nicht benutzt', setting: 'unusedKeys',
    action: (n) => `${n} Key(s) lange nicht benutzt`,
    what: 'Laut Entra hat sich mit diesem Passkey länger als die eingestellte Zeit niemand angemeldet.',
    todo: 'Nachfragen, ob der Key noch gebraucht wird. Liegt er in der Schublade, zurückholen; ist er verloren, sperren.' },
  { kind: 'NoSpareKey', id: 'ohne-ersatz', icon: 'keyPlus', title: 'Personen ohne Ersatz-Key', setting: 'noSpareKey',
    action: (n) => `${n} Person(en) ohne Ersatz-Key`,
    what: 'Die Person hat genau einen persönlichen Key. Geht er verloren, kommt sie nur noch mit einem befristeten Zugriffspass (TAP) hinein.',
    todo: 'Einen zweiten Key registrieren und getrennt aufbewahren lassen.' },
];

/**
 * Optionale Lebenszyklus-Erinnerungen der gewählten Tenants: eine Liste je Art mit Person, Key und Bezugsdatum.
 * Einzelne Fälle lassen sich ausblenden (z. B. „Key liegt bewusst im Tresor“), auch wieder einblenden.
 */
export default function RemindersPage() {
  const { tenantId, tenant } = useTenant();
  const can = useCan();
  const [showDismissed, setShowDismissed] = useState(false);
  const qs = new URLSearchParams();
  if (tenantId) qs.set('tenantId', tenantId);
  if (showDismissed) qs.set('includeDismissed', 'true');
  const { data, error, reload } = useApi<ReminderOverview>('/api/reminders?' + qs.toString(), [tenantId, showDismissed]);
  const [dismiss, setDismiss] = useState<ReminderItem | null>(null);
  const { run } = useAction();
  const toast = useToast();
  const active = data?.tenants.filter((t) => anyReminder(t)) ?? [];
  const editable = can('ManageInventory');

  const restore = (i: ReminderItem) => run(async () => {
    await api.post('/api/reminders/restore', { tenantId: i.tenantId, kind: i.kind, subject: i.subject });
    toast.ok('Wieder eingeblendet.');
    await reload();
  });

  return (
    <>
      <PageHeader
        title={tenant ? 'Erinnerungen ' + tenant.displayName : 'Erinnerungen aller Tenants'}
        subtitle={<>Optionale Hinweise zum Lebenszyklus der Keys. Welche gelten, steht je Tenant unter <Link to="/tenant#erinnerungen">Tenant-Einstellungen</Link>.</>}
        actions={data && active.length > 0 && (
          <label className="check" style={{ margin: 0 }}>
            <input type="checkbox" checked={showDismissed} onChange={(e) => setShowDismissed(e.target.checked)} />
            <span>Ausgeblendete zeigen</span>
          </label>
        )}
      />
      <ErrorBox error={error} />
      {!data && !error && <Spinner label="Wird geladen …" />}
      {data && active.length === 0 && (
        <Empty>
          Für {tenant ? 'diesen Tenant' : 'keinen Tenant'} sind Erinnerungen eingeschaltet. Sie sind optional und lassen sich in den{' '}
          {tenant ? <Link to="/tenant#erinnerungen">Tenant-Einstellungen</Link> : 'Tenant-Einstellungen'} einzeln einschalten: Keys lange nicht benutzt,
          Personen ohne Ersatz-Key, ausgeschiedene Personen mit aktivem Key und gesperrte Keys, deren Passkey in Entra noch angemeldet ist.
        </Empty>
      )}
      {data && active.length > 0 && <>
        <div className="grid cols-4 rem-kpis">
          {reminderKinds.map((k) => {
            const on = active.some((t) => t[k.setting]);
            const n = data.counts.find((c) => c.kind === k.kind)?.count ?? 0;
            return (
              <a key={k.kind} href={'#' + k.id} className={'card kpi kpi-link' + (on ? '' : ' rem-off')}>
                <span className="kpi-ico" aria-hidden><Icon name={k.icon} size={20} /></span>
                <div className="label">{k.title}</div>
                <div className={'value' + (on && n ? ' tone-text-warn' : '')}>{on ? <CountUp value={n} /> : '–'}</div>
                <div className="sub">{on ? (n ? 'offen' : 'nichts offen') : 'ausgeschaltet'}</div>
              </a>
            );
          })}
        </div>
        {reminderKinds.filter((k) => active.some((t) => t[k.setting])).map((k) => (
          <ReminderSection key={k.kind} k={k} items={data.items.filter((i) => i.kind === k.kind)} allTenants={!tenantId} editable={editable}
            note={k.kind === 'UnusedKey' ? unusedNote(active, data.keysWithoutUsageDate) : undefined}
            onDismiss={setDismiss} onRestore={restore} />
        ))}
      </>}
      {dismiss && <DismissModal item={dismiss} onClose={() => setDismiss(null)} onDone={async () => { setDismiss(null); toast.ok('Ausgeblendet.'); await reload(); }} />}
    </>
  );
}

function unusedNote(tenants: ReminderTenant[], withoutDate: number) {
  const days = [...new Set(tenants.filter((t) => t.unusedKeys).map((t) => t.unusedKeyDays))];
  const limit = days.length === 1 ? `Schwelle ${days[0]} Tage.` : `Schwelle je Tenant ${days.join(' / ')} Tage.`;
  return withoutDate > 0 ? `${limit} Für ${withoutDate} Key(s) liefert Entra (noch) kein Nutzungsdatum; sie werden nicht gemeldet.` : limit;
}

function ReminderSection({ k, items, allTenants, editable, note, onDismiss, onRestore }: {
  k: (typeof reminderKinds)[number]; items: ReminderItem[]; allTenants: boolean; editable: boolean; note?: string;
  onDismiss: (i: ReminderItem) => void; onRestore: (i: ReminderItem) => void;
}) {
  const open = items.filter((i) => !i.dismissed).length;
  return (
    <section className="card rem-card" id={k.id}>
      <div className="rem-head">
        <span className={'ai-icon tone-' + (open ? 'warn' : 'ok')}><Icon name={open ? k.icon : 'check'} size={16} /></span>
        <div>
          <h2>{k.title} <span className="count">{open}</span></h2>
          <p className="small muted">{k.what} <strong>Was tun:</strong> {k.todo}{note && <> {note}</>}</p>
        </div>
      </div>
      {items.length === 0 ? <Empty>Nichts offen.</Empty> : (
        <div className="table-wrap">
          <table>
            <thead><tr><th>Person</th><th>Key</th><th>{k.kind === 'UnusedKey' ? 'Zuletzt benutzt' : k.kind === 'RetiredWithPasskey' ? 'Status seit' : k.kind === 'Leaver' ? 'Bezug' : 'Registriert'}</th><th>Hinweis</th>{allTenants && <th>Tenant</th>}<th /></tr></thead>
            <tbody>
              {items.map((i) => (
                <tr key={i.tenantId + i.kind + i.subject} className={i.dismissed ? 'rem-dismissed' : undefined}>
                  <td>{i.userDisplayName ?? i.userPrincipalName ?? '–'}{i.userPrincipalName && i.userDisplayName && <div className="small muted">{i.userPrincipalName}</div>}</td>
                  <td>
                    {i.keyId ? <Link className="mono" to={'/keys/' + i.keyId}>{i.serialNumber ?? 'ohne Seriennummer'}</Link> : '–'}
                    <div className="small muted">{[i.model, i.keyStatus && i.keyStatus !== 'Active' ? statusLabels[i.keyStatus] : null, i.usage && i.usage !== 'Personal' ? usageLabels[i.usage] : null].filter(Boolean).join(' · ')}</div>
                  </td>
                  <td className="small">{i.since ? fmtDate(i.since) : '–'}</td>
                  <td>
                    {i.detail}
                    {i.todoId && <div className="small"><Link to="/todos">ToDo angelegt</Link></div>}
                    {i.dismissed && <div className="small muted">Ausgeblendet von {i.dismissedByName ?? '–'} am {fmtDate(i.dismissedAt)}{i.dismissNote ? ': ' + i.dismissNote : ''}</div>}
                  </td>
                  {allTenants && <td>{i.tenantName}</td>}
                  <td className="actions">
                    {k.kind === 'NoSpareKey' && !i.dismissed && <Link className="btn small" to="/register"><Icon name="keyPlus" size={14} />Registrieren</Link>}
                    {editable && (i.dismissed
                      ? <button type="button" className="small" onClick={() => onRestore(i)}>Einblenden</button>
                      : <button type="button" className="small ghost" onClick={() => onDismiss(i)}>Ausblenden</button>)}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </section>
  );
}

function DismissModal({ item, onClose, onDone }: { item: ReminderItem; onClose: () => void; onDone: () => void }) {
  const [note, setNote] = useState('');
  const { busy, error, run } = useAction();
  const save = () => run(async () => {
    await api.post('/api/reminders/dismiss', { tenantId: item.tenantId, kind: item.kind, subject: item.subject, note: note || null });
    onDone();
  });
  return (
    <Modal title="Erinnerung ausblenden" onClose={onClose}
      footer={<><button type="button" onClick={onClose}>Abbrechen</button><button type="button" className="primary" disabled={busy} onClick={save}>Ausblenden</button></>}>
      <p>
        {item.userDisplayName ?? item.userPrincipalName}{item.serialNumber ? <>, Key <span className="mono">{item.serialNumber}</span></> : null}: {item.detail}.
        Die Erinnerung erscheint dann nicht mehr unter „Handlungsbedarf“. Der Eintrag im Audit-Log bleibt.
      </p>
      <Field label="Grund (optional)">
        <input value={note} maxLength={300} placeholder="z. B. Key liegt bewusst im Tresor" onChange={(e) => setNote(e.target.value)} />
      </Field>
      <Info>Erledigt sich der Fall (z. B. zweiter Key registriert) und tritt später wieder auf, wird er neu gemeldet.</Info>
      <ErrorBox error={error} />
    </Modal>
  );
}
