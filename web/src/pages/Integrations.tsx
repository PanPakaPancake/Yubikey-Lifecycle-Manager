import { Fragment, useState } from 'react';
import { useSearchParams } from 'react-router';
import { api, fmtDate, type Tenant } from '../api';
import { Badge, Card, Empty, ErrorBox, Field, Info, Modal, PageHeader, Spinner, useAction, useApi } from '../ui';
import { useConfirm, useToast } from '../feedback';
import { ScopeEditor } from './Platform';

type Tab = 'webhooks' | 'mail';

/** Partnerebene → Integrationen (V2): Webhooks und Mail-Berichte. */
export default function IntegrationsPage() {
  const [params, setParams] = useSearchParams();
  const tab = (params.get('tab') as Tab) || 'webhooks';
  const set = (t: Tab) => setParams({ tab: t }, { replace: true });
  return (
    <>
      <PageHeader title="Integrationen" subtitle="Partnerebene: Ereignisse und Berichte an andere Systeme geben. Die Plattform sendet nur, sie nimmt hier nichts entgegen." />
      <div className="tabs" role="tablist">
        <button className={tab === 'webhooks' ? 'selected' : ''} onClick={() => set('webhooks')}>Webhooks</button>
        <button className={tab === 'mail' ? 'selected' : ''} onClick={() => set('mail')}>Mail-Berichte</button>
      </div>
      {tab === 'webhooks' && <Webhooks />}
      {tab === 'mail' && <MailReports />}
    </>
  );
}

// ---------------- Webhooks ----------------

interface WebhookRow { id: string; name: string; url: string; events: string[]; allTenants: boolean; tenantIds: string[]; isActive: boolean; createdAt: string; lastDeliveryAt?: string | null; lastDeliverySucceeded?: boolean | null; lastError?: string | null }
interface EventType { type: string; group: string; label: string }
interface Delivery { id: string; eventType: string; status: 'Pending' | 'Delivered' | 'Failed'; attempts: number; lastStatusCode?: number | null; lastError?: string | null; createdAt: string; deliveredAt?: string | null; nextAttemptAt: string; payload: string }

function useActiveTenants() {
  const { data } = useApi<Tenant[]>('/api/tenants');
  return (data ?? []).filter((t) => t.status === 'Active');
}

function Webhooks() {
  const { data, error, reload } = useApi<WebhookRow[]>('/api/integrations/webhooks');
  const { data: events } = useApi<EventType[]>('/api/integrations/webhooks/events');
  const [editing, setEditing] = useState<WebhookRow | 'new' | null>(null);
  const [secret, setSecret] = useState<string>();
  const [log, setLog] = useState<WebhookRow | null>(null);
  const toast = useToast();
  const confirm = useConfirm();
  const test = async (w: WebhookRow) => {
    try {
      const r = await api.post<{ success: boolean; statusCode?: number; error?: string }>(`/api/integrations/webhooks/${w.id}/test`);
      if (r.success) toast.ok(`Testereignis zugestellt (HTTP ${r.statusCode}).`); else toast.error(`Zustellung fehlgeschlagen: ${r.error ?? 'HTTP ' + r.statusCode}`);
      reload();
    } catch (e) { toast.error((e as Error).message); }
  };
  const rotate = async (w: WebhookRow) => {
    if (!await confirm({ title: 'Neues Geheimnis erzeugen?', message: 'Das bisherige Geheimnis gilt sofort nicht mehr. Der Empfänger muss das neue eintragen.', confirmLabel: 'Erzeugen' })) return;
    const r = await api.post<{ secret: string }>(`/api/integrations/webhooks/${w.id}/secret`);
    setSecret(r.secret);
  };
  const remove = async (w: WebhookRow) => {
    if (!await confirm({ title: 'Webhook löschen?', message: `„${w.name}“ bekommt keine Ereignisse mehr.`, confirmLabel: 'Löschen', danger: true })) return;
    await api.del(`/api/integrations/webhooks/${w.id}`);
    reload();
  };
  return (
    <>
      <Card title="Webhooks" actions={<button className="primary" onClick={() => setEditing('new')}>Webhook anlegen</button>}>
        <p className="small muted" style={{ marginTop: 0 }}>
          Bei Ereignissen (Key registriert, gesperrt, ToDo erledigt …) schickt die Plattform ein JSON per <code>POST</code>. Jede Zustellung ist signiert:
          <code> X-YLM-Signature: sha256=HMAC(Geheimnis, X-YLM-Timestamp + "." + Body)</code>. Fehlgeschlagene Zustellungen werden nach 1 und 5 Minuten, 30 Minuten, 2 und 6 Stunden wiederholt.
        </p>
        {secret && <Info tone="ok">Geheimnis, nur jetzt sichtbar: <code style={{ wordBreak: 'break-all' }}>{secret}</code> <button className="link" onClick={() => void navigator.clipboard?.writeText(secret)}>Kopieren</button></Info>}
        <ErrorBox error={error} />
        {!data && <Spinner />}
        {data && data.length === 0 && <Empty>Noch kein Webhook.</Empty>}
        {data && data.length > 0 && (
          <table>
            <thead><tr><th>Name</th><th>Ereignisse</th><th>Letzte Zustellung</th><th /></tr></thead>
            <tbody>
              {data.map((w) => (
                <tr key={w.id} style={{ opacity: w.isActive ? 1 : 0.6 }}>
                  <td><strong>{w.name}</strong>{!w.isActive && <> <Badge>pausiert</Badge></>}<div className="small muted mono" style={{ wordBreak: 'break-all' }}>{w.url}</div></td>
                  <td className="small">{w.events.length === 0 ? 'Alle' : `${w.events.length} ausgewählt`}<div className="muted">{w.allTenants ? 'alle Tenants' : `${w.tenantIds.length} Tenant(s)`}</div></td>
                  <td className="small">{w.lastDeliveryAt ? <>{w.lastDeliverySucceeded ? <Badge tone="ok">ok</Badge> : <Badge tone="error">Fehler</Badge>} {fmtDate(w.lastDeliveryAt)}{w.lastError && <div className="muted">{w.lastError}</div>}</> : '–'}</td>
                  <td className="actions">
                    <button className="small" onClick={() => test(w)}>Testen</button>
                    <button className="small" onClick={() => setLog(w)}>Protokoll</button>
                    <button className="small" onClick={() => setEditing(w)}>Bearbeiten</button>
                    <button className="small ghost" onClick={() => rotate(w)}>Neues Geheimnis</button>
                    <button className="small danger" onClick={() => remove(w)}>Löschen</button>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </Card>
      {editing && <WebhookModal hook={editing === 'new' ? undefined : editing} events={events ?? []} onClose={() => setEditing(null)} onSaved={(s) => { setEditing(null); if (s) setSecret(s); reload(); }} />}
      {log && <DeliveryLog hook={log} onClose={() => setLog(null)} />}
    </>
  );
}

function WebhookModal({ hook, events, onClose, onSaved }: { hook?: WebhookRow; events: EventType[]; onClose: () => void; onSaved: (secret?: string) => void }) {
  const tenants = useActiveTenants();
  const [name, setName] = useState(hook?.name ?? '');
  const [url, setUrl] = useState(hook?.url ?? 'https://');
  const [all, setAll] = useState(hook ? hook.events.length === 0 : true);
  const [selected, setSelected] = useState<string[]>(hook?.events ?? []);
  const [allTenants, setAllTenants] = useState(hook?.allTenants ?? true);
  const [tenantIds, setTenantIds] = useState<string[]>(hook?.tenantIds ?? []);
  const [active, setActive] = useState(hook?.isActive ?? true);
  const { busy, error, run } = useAction();
  const groups = [...new Set(events.map((e) => e.group))];
  const save = () => run(async () => {
    const body = { name, url, events: all ? [] : selected, allTenants, tenantIds: allTenants ? [] : tenantIds, isActive: active };
    if (hook) { await api.put(`/api/integrations/webhooks/${hook.id}`, body); onSaved(); }
    else { const r = await api.post<{ secret: string }>('/api/integrations/webhooks', body); onSaved(r.secret); }
  });
  return (
    <Modal title={hook ? `Webhook „${hook.name}“` : 'Webhook anlegen'} onClose={onClose}
      footer={<><button onClick={onClose}>Abbrechen</button><button className="primary" disabled={busy || !name.trim() || !url.trim() || (!all && selected.length === 0)} onClick={save}>Speichern</button></>}>
      <Field label="Name"><input value={name} onChange={(e) => setName(e.target.value)} placeholder="z. B. Ticketsystem" /></Field>
      <Field label="URL" hint="Empfänger, möglichst mit https://"><input value={url} onChange={(e) => setUrl(e.target.value)} /></Field>
      <div className="field-group">
        <span className="field-label">Ereignisse</span>
        <label className="check"><input type="radio" checked={all} onChange={() => setAll(true)} /> Alle Ereignisse, auch künftige</label>
        <label className="check"><input type="radio" checked={!all} onChange={() => setAll(false)} /> Nur ausgewählte</label>
        {!all && groups.map((g) => (
          <div key={g} style={{ paddingLeft: 26 }}>
            <div className="small muted" style={{ marginTop: 6 }}>{g}</div>
            {events.filter((e) => e.group === g).map((e) => (
              <label key={e.type} className="check">
                <input type="checkbox" checked={selected.includes(e.type)} onChange={(x) => setSelected((s) => x.target.checked ? [...s, e.type] : s.filter((v) => v !== e.type))} />
                {e.label} <code className="small muted">{e.type}</code>
              </label>
            ))}
          </div>
        ))}
      </div>
      <ScopeEditor allTenants={allTenants} setAllTenants={setAllTenants} tenantIds={tenantIds} setTenantIds={setTenantIds} tenants={tenants} canAll />
      <label className="check"><input type="checkbox" checked={active} onChange={(e) => setActive(e.target.checked)} /> Aktiv</label>
      <ErrorBox error={error} />
    </Modal>
  );
}

function DeliveryLog({ hook, onClose }: { hook: WebhookRow; onClose: () => void }) {
  const { data, reload } = useApi<Delivery[]>(`/api/integrations/webhooks/${hook.id}/deliveries`);
  const [open, setOpen] = useState<string>();
  const toast = useToast();
  const retry = async (d: Delivery) => {
    const r = await api.post<{ success: boolean; error?: string }>(`/api/integrations/webhooks/deliveries/${d.id}/retry`);
    if (r.success) toast.ok('Zugestellt.'); else toast.error('Erneut fehlgeschlagen: ' + (r.error ?? ''));
    reload();
  };
  const tone = { Pending: 'warn', Delivered: 'ok', Failed: 'error' } as const;
  return (
    <Modal title={`Protokoll „${hook.name}“`} onClose={onClose} footer={<button onClick={onClose}>Schließen</button>}>
      {!data && <Spinner />}
      {data && data.length === 0 && <Empty>Noch keine Zustellungen.</Empty>}
      {data && data.length > 0 && (
        <table>
          <thead><tr><th>Ereignis</th><th>Status</th><th>Zeit</th><th /></tr></thead>
          <tbody>
            {data.map((d) => (
              <Fragment key={d.id}>
                <tr>
                  <td><button className="link" onClick={() => setOpen(open === d.id ? undefined : d.id)}><code>{d.eventType}</code></button></td>
                  <td><Badge tone={tone[d.status]}>{d.status === 'Pending' ? 'wird wiederholt' : d.status === 'Delivered' ? 'zugestellt' : 'fehlgeschlagen'}</Badge>
                    <div className="small muted">{d.attempts} Versuch(e){d.lastStatusCode ? ` · HTTP ${d.lastStatusCode}` : ''}{d.lastError ? ` · ${d.lastError}` : ''}</div></td>
                  <td className="small">{fmtDate(d.createdAt)}{d.status === 'Pending' && d.attempts > 0 && <div className="muted">nächster Versuch {fmtDate(d.nextAttemptAt)}</div>}</td>
                  <td>{d.status !== 'Delivered' && <button className="small" onClick={() => retry(d)}>Jetzt senden</button>}</td>
                </tr>
                {open === d.id && <tr><td colSpan={4}><pre className="small" style={{ whiteSpace: 'pre-wrap', wordBreak: 'break-all' }}>{JSON.stringify(JSON.parse(d.payload), null, 2)}</pre></td></tr>}
              </Fragment>
            ))}
          </tbody>
        </table>
      )}
    </Modal>
  );
}

// ---------------- Mail-Berichte ----------------

interface Smtp { host?: string | null; port: number; security: 'None' | 'StartTls' | 'SslOnConnect'; username?: string | null; hasPassword: boolean; fromAddress?: string | null; fromName?: string | null; isConfigured: boolean }
interface Schedule {
  id: string; name: string; recipients: string[]; allTenants: boolean; tenantIds: string[]; frequency: 'Daily' | 'Weekly' | 'Monthly'; dayOfWeek: number; dayOfMonth: number; hour: number;
  timeZone: string; includeCsv: boolean; isActive: boolean; nextRunAt?: string | null; lastSentAt?: string | null; lastSucceeded?: boolean | null; lastError?: string | null; description: string;
}

function MailReports() {
  const { data: smtp, reload: reloadSmtp } = useApi<Smtp>('/api/integrations/smtp');
  const { data, error, reload } = useApi<Schedule[]>('/api/integrations/reports');
  const [editing, setEditing] = useState<Schedule | 'new' | null>(null);
  const toast = useToast();
  const confirm = useConfirm();
  const send = async (s: Schedule) => {
    try { await api.post(`/api/integrations/reports/${s.id}/send`); toast.ok(`„${s.name}“ verschickt.`); } catch (e) { toast.error((e as Error).message); }
    reload();
  };
  const remove = async (s: Schedule) => {
    if (!await confirm({ title: 'Bericht löschen?', message: `„${s.name}“ wird nicht mehr verschickt.`, confirmLabel: 'Löschen', danger: true })) return;
    await api.del(`/api/integrations/reports/${s.id}`);
    reload();
  };
  return (
    <>
      {smtp ? <SmtpCard smtp={smtp} onSaved={reloadSmtp} /> : <Spinner />}
      <Card title="Geplante Berichte" className="mt" actions={<button className="primary" onClick={() => setEditing('new')}>Bericht anlegen</button>}>
        <p className="small muted" style={{ marginTop: 0 }}>Der Bericht enthält die wichtigsten Kennzahlen in der Mail und das Dashboard als PDF, auf Wunsch auch die Key-Liste als CSV.</p>
        <ErrorBox error={error} />
        {data && data.length === 0 && <Empty>Noch kein Bericht geplant.</Empty>}
        {data && data.length > 0 && (
          <table>
            <thead><tr><th>Name</th><th>Wann</th><th>Empfänger</th><th>Zuletzt</th><th /></tr></thead>
            <tbody>
              {data.map((s) => (
                <tr key={s.id} style={{ opacity: s.isActive ? 1 : 0.6 }}>
                  <td><strong>{s.name}</strong>{!s.isActive && <> <Badge>pausiert</Badge></>}<div className="small muted">{s.allTenants ? 'Alle Tenants' : `${s.tenantIds.length} Tenant(s)`}{s.includeCsv ? ' · mit CSV' : ''}</div></td>
                  <td className="small">{s.description}{s.nextRunAt && <div className="muted">nächster Versand {fmtDate(s.nextRunAt)}</div>}</td>
                  <td className="small">{s.recipients.join(', ')}</td>
                  <td className="small">{s.lastSentAt ? <>{s.lastSucceeded ? <Badge tone="ok">ok</Badge> : <Badge tone="error">Fehler</Badge>} {fmtDate(s.lastSentAt)}{s.lastError && <div className="muted">{s.lastError}</div>}</> : '–'}</td>
                  <td className="actions">
                    <button className="small" disabled={!smtp?.isConfigured} onClick={() => send(s)}>Jetzt senden</button>
                    <button className="small" onClick={() => setEditing(s)}>Bearbeiten</button>
                    <button className="small danger" onClick={() => remove(s)}>Löschen</button>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </Card>
      {editing && <ScheduleModal schedule={editing === 'new' ? undefined : editing} onClose={() => setEditing(null)} onSaved={() => { setEditing(null); reload(); }} />}
    </>
  );
}

function SmtpCard({ smtp, onSaved }: { smtp: Smtp; onSaved: () => void }) {
  const [host, setHost] = useState(smtp.host ?? '');
  const [port, setPort] = useState(smtp.port);
  const [security, setSecurity] = useState(smtp.security);
  const [username, setUsername] = useState(smtp.username ?? '');
  const [password, setPassword] = useState('');
  const [fromAddress, setFromAddress] = useState(smtp.fromAddress ?? '');
  const [fromName, setFromName] = useState(smtp.fromName ?? '');
  const [testTo, setTestTo] = useState('');
  const { busy, error, run } = useAction();
  const toast = useToast();
  const save = () => run(async () => {
    await api.put('/api/integrations/smtp', { host, port, security, username, password: password || null, clearPassword: false, fromAddress, fromName });
    setPassword('');
    toast.ok('Mailserver gespeichert.');
    onSaved();
  });
  const test = () => run(async () => { await api.post('/api/integrations/smtp/test', { to: testTo }); toast.ok(`Testmail an ${testTo} verschickt.`); });
  return (
    <Card title="Mailserver (SMTP)" actions={smtp.isConfigured ? <Badge tone="ok">eingerichtet</Badge> : <Badge tone="warn">nicht eingerichtet</Badge>}>
      <div className="grid cols-2">
        <Field label="Server"><input value={host} onChange={(e) => setHost(e.target.value)} placeholder="smtp.example.com" /></Field>
        <div className="grid cols-2">
          <Field label="Port"><input type="number" value={port} onChange={(e) => setPort(Number(e.target.value))} /></Field>
          <Field label="Verschlüsselung">
            <select value={security} onChange={(e) => { const v = e.target.value as Smtp['security']; setSecurity(v); setPort(v === 'SslOnConnect' ? 465 : v === 'StartTls' ? 587 : 25); }}>
              <option value="StartTls">STARTTLS</option><option value="SslOnConnect">SSL/TLS</option><option value="None">keine</option>
            </select>
          </Field>
        </div>
        <Field label="Benutzername" hint="Leer lassen, wenn der Server ohne Anmeldung annimmt"><input value={username} onChange={(e) => setUsername(e.target.value)} autoComplete="off" /></Field>
        <Field label="Passwort" hint={smtp.hasPassword ? 'Gespeichert. Leer lassen, um es zu behalten.' : undefined}><input type="password" value={password} onChange={(e) => setPassword(e.target.value)} autoComplete="new-password" /></Field>
        <Field label="Absenderadresse"><input value={fromAddress} onChange={(e) => setFromAddress(e.target.value)} placeholder="yubikeys@example.com" /></Field>
        <Field label="Absendername"><input value={fromName} onChange={(e) => setFromName(e.target.value)} placeholder="Yubikey Lifecycle Manager" /></Field>
      </div>
      <div className="actions">
        <button className="primary" disabled={busy} onClick={save}>Speichern</button>
        <input value={testTo} onChange={(e) => setTestTo(e.target.value)} placeholder="Testmail an …" aria-label="Testmail an" style={{ maxWidth: 260 }} />
        <button disabled={busy || !smtp.isConfigured || !testTo.includes('@')} onClick={test}>Testmail senden</button>
      </div>
      <ErrorBox error={error} />
    </Card>
  );
}

const weekdays = ['Sonntag', 'Montag', 'Dienstag', 'Mittwoch', 'Donnerstag', 'Freitag', 'Samstag'];

function ScheduleModal({ schedule, onClose, onSaved }: { schedule?: Schedule; onClose: () => void; onSaved: () => void }) {
  const tenants = useActiveTenants();
  const [name, setName] = useState(schedule?.name ?? 'Wochenbericht YubiKeys');
  const [recipients, setRecipients] = useState(schedule?.recipients.join(', ') ?? '');
  const [allTenants, setAllTenants] = useState(schedule?.allTenants ?? true);
  const [tenantIds, setTenantIds] = useState<string[]>(schedule?.tenantIds ?? []);
  const [frequency, setFrequency] = useState(schedule?.frequency ?? 'Weekly');
  const [dayOfWeek, setDayOfWeek] = useState(schedule?.dayOfWeek ?? 1);
  const [dayOfMonth, setDayOfMonth] = useState(schedule?.dayOfMonth ?? 1);
  const [hour, setHour] = useState(schedule?.hour ?? 7);
  const [includeCsv, setIncludeCsv] = useState(schedule?.includeCsv ?? false);
  const [active, setActive] = useState(schedule?.isActive ?? true);
  const { busy, error, run } = useAction();
  const save = () => run(async () => {
    const body = { name, recipients: [recipients], allTenants, tenantIds: allTenants ? [] : tenantIds, frequency, dayOfWeek, dayOfMonth, hour, timeZone: schedule?.timeZone ?? 'Europe/Berlin', includeCsv, isActive: active };
    if (schedule) await api.put(`/api/integrations/reports/${schedule.id}`, body); else await api.post('/api/integrations/reports', body);
    onSaved();
  });
  return (
    <Modal title={schedule ? `Bericht „${schedule.name}“` : 'Bericht anlegen'} onClose={onClose}
      footer={<><button onClick={onClose}>Abbrechen</button><button className="primary" disabled={busy || !name.trim() || !recipients.includes('@')} onClick={save}>Speichern</button></>}>
      <Field label="Name"><input value={name} onChange={(e) => setName(e.target.value)} /></Field>
      <Field label="Empfänger" hint="Mehrere mit Komma oder Semikolon trennen"><textarea rows={2} value={recipients} onChange={(e) => setRecipients(e.target.value)} /></Field>
      <div className="grid cols-2">
        <Field label="Wie oft">
          <select value={frequency} onChange={(e) => setFrequency(e.target.value as Schedule['frequency'])}>
            <option value="Daily">täglich</option><option value="Weekly">wöchentlich</option><option value="Monthly">monatlich</option>
          </select>
        </Field>
        {frequency === 'Weekly' && <Field label="Tag"><select value={dayOfWeek} onChange={(e) => setDayOfWeek(Number(e.target.value))}>{weekdays.map((d, i) => <option key={d} value={i}>{d}</option>)}</select></Field>}
        {frequency === 'Monthly' && <Field label="Tag im Monat"><select value={dayOfMonth} onChange={(e) => setDayOfMonth(Number(e.target.value))}>{Array.from({ length: 28 }, (_, i) => <option key={i} value={i + 1}>{i + 1}.</option>)}</select></Field>}
        <Field label="Uhrzeit" hint="Deutsche Zeit"><select value={hour} onChange={(e) => setHour(Number(e.target.value))}>{Array.from({ length: 24 }, (_, i) => <option key={i} value={i}>{i}:00 Uhr</option>)}</select></Field>
      </div>
      <ScopeEditor allTenants={allTenants} setAllTenants={setAllTenants} tenantIds={tenantIds} setTenantIds={setTenantIds} tenants={tenants} canAll />
      <label className="check"><input type="checkbox" checked={includeCsv} onChange={(e) => setIncludeCsv(e.target.checked)} /> Key-Liste als CSV anhängen</label>
      <label className="check"><input type="checkbox" checked={active} onChange={(e) => setActive(e.target.checked)} /> Aktiv</label>
      <ErrorBox error={error} />
    </Modal>
  );
}
