import { Fragment, useState } from 'react';
import { useSearchParams } from 'react-router';
import { api, fmtDate, type Tenant } from '../api';
import { Badge, Card, Empty, ErrorBox, Field, Info, Modal, PageHeader, Spinner, useAction, useApi } from '../ui';
import { useConfirm, useToast } from '../feedback';
import { ScopeEditor } from './Platform';
import { tr } from '../i18n';

type Tab = 'webhooks' | 'alerts' | 'mail';

/** Partnerebene → Benachrichtigungen: Webhooks, Mail-Alarme (je mit Ereignisauswahl) und Mail-Berichte. */
export default function IntegrationsPage() {
  const [params, setParams] = useSearchParams();
  const tab = (params.get('tab') as Tab) || 'webhooks';
  const set = (t: Tab) => setParams({ tab: t }, { replace: true });
  return (
    <>
      <PageHeader title={tr("Benachrichtigungen")} subtitle={tr("Partnerebene: festlegen, welche Ereignisse per Webhook oder Mail gemeldet werden, und regelmäßige Berichte verschicken.")} />
      <div className="tabs" role="tablist">
        <button className={tab === 'webhooks' ? 'selected' : ''} onClick={() => set('webhooks')}>{tr("Webhooks")}</button>
        <button className={tab === 'alerts' ? 'selected' : ''} onClick={() => set('alerts')}>{tr("Mail-Alarme")}</button>
        <button className={tab === 'mail' ? 'selected' : ''} onClick={() => set('mail')}>{tr("Mail-Berichte")}</button>
      </div>
      {tab === 'webhooks' && <Webhooks key="webhook" channel="Webhook" />}
      {tab === 'alerts' && <Webhooks key="mail" channel="Mail" />}
      {tab === 'mail' && <MailReports />}
    </>
  );
}

// ---------------- Webhooks ----------------

type Channel = 'Webhook' | 'Mail';
interface WebhookRow { id: string; name: string; channel: Channel; url: string; recipients: string[]; events: string[]; allTenants: boolean; tenantIds: string[]; isActive: boolean; createdAt: string; lastDeliveryAt?: string | null; lastDeliverySucceeded?: boolean | null; lastError?: string | null }
interface EventType { type: string; group: string; label: string }
interface Delivery { id: string; eventType: string; status: 'Pending' | 'Delivered' | 'Failed'; attempts: number; lastStatusCode?: number | null; lastError?: string | null; createdAt: string; deliveredAt?: string | null; nextAttemptAt: string; payload: string }

function useActiveTenants() {
  const { data } = useApi<Tenant[]>('/api/tenants');
  return (data ?? []).filter((t) => t.status === 'Active');
}

function Webhooks({ channel }: { channel: Channel }) {
  const mail = channel === 'Mail';
  const { data: all, error, reload } = useApi<WebhookRow[]>('/api/integrations/webhooks');
  const data = all?.filter((w) => (w.channel ?? 'Webhook') === channel);
  const { data: events } = useApi<EventType[]>('/api/integrations/webhooks/events');
  const [editing, setEditing] = useState<WebhookRow | 'new' | null>(null);
  const [secret, setSecret] = useState<string>();
  const [log, setLog] = useState<WebhookRow | null>(null);
  const toast = useToast();
  const confirm = useConfirm();
  const test = async (w: WebhookRow) => {
    try {
      const r = await api.post<{ success: boolean; statusCode?: number; error?: string }>(`/api/integrations/webhooks/${w.id}/test`);
      if (r.success) toast.ok(mail ? tr("Testmail an {n} verschickt.", { n: w.recipients.join(', ') }) : tr("Testereignis zugestellt (HTTP {statusCode}).", { statusCode: r.statusCode })); else toast.error(tr("Zustellung fehlgeschlagen: {n}", { n: r.error ?? 'HTTP ' + r.statusCode }));
      reload();
    } catch (e) { toast.error((e as Error).message); }
  };
  const rotate = async (w: WebhookRow) => {
    if (!await confirm({ title: tr("Neues Geheimnis erzeugen?"), message: tr("Das bisherige Geheimnis gilt sofort nicht mehr. Der Empfänger muss das neue eintragen."), confirmLabel: tr("Erzeugen") })) return;
    const r = await api.post<{ secret: string }>(`/api/integrations/webhooks/${w.id}/secret`);
    setSecret(r.secret);
  };
  const remove = async (w: WebhookRow) => {
    if (!await confirm({ title: mail ? tr("Mail-Alarm löschen?") : tr("Webhook löschen?"), message: tr("„{name}“ bekommt keine Ereignisse mehr.", { name: w.name }), confirmLabel: tr("Löschen"), danger: true })) return;
    await api.del(`/api/integrations/webhooks/${w.id}`);
    reload();
  };
  return (
    <>
      <Card title={mail ? tr("Mail-Alarme") : tr("Webhooks")} actions={<button className="primary" onClick={() => setEditing('new')}>{mail ? tr("Mail-Alarm anlegen") : tr("Webhook anlegen")}</button>}>
        {mail ? <p className="small muted" style={{ marginTop: 0 }}>
          {tr("Bei ausgewählten Ereignissen (z. B. Key gesperrt, Sync fehlgeschlagen, Freigabe beantragt) geht sofort eine Mail an die Empfänger. Versand über den Mailserver aus dem Reiter „Mail-Berichte“; fehlgeschlagene Mails werden wie Webhooks wiederholt.")}
        </p> : <p className="small muted" style={{ marginTop: 0 }}>
          {tr("Bei Ereignissen (Key registriert, gesperrt, ToDo erledigt …) schickt die Plattform ein JSON per")} <code>{tr("POST")}</code>{tr(". Jede Zustellung ist signiert:")}
          <code> {tr("X-YLM-Signature: sha256=HMAC(Geheimnis, X-YLM-Timestamp + \".\" + Body)")}</code>{tr(". Fehlgeschlagene Zustellungen werden nach 1 und 5 Minuten, 30 Minuten, 2 und 6 Stunden wiederholt.")}
        </p>}
        {secret && <Info tone="ok">{tr("Geheimnis, nur jetzt sichtbar:")} <code style={{ wordBreak: 'break-all' }}>{secret}</code> <button className="link" onClick={() => void navigator.clipboard?.writeText(secret)}>{tr("Kopieren")}</button></Info>}
        <ErrorBox error={error} />
        {!data && <Spinner />}
        {data && data.length === 0 && <Empty>{mail ? tr("Noch kein Mail-Alarm.") : tr("Noch kein Webhook.")}</Empty>}
        {data && data.length > 0 && (
          <table>
            <thead><tr><th>{tr("Name")}</th><th>{tr("Ereignisse")}</th><th>{tr("Letzte Zustellung")}</th><th /></tr></thead>
            <tbody>
              {data.map((w) => (
                <tr key={w.id} style={{ opacity: w.isActive ? 1 : 0.6 }}>
                  <td><strong>{w.name}</strong>{!w.isActive && <> <Badge>{tr("pausiert")}</Badge></>}<div className="small muted mono" style={{ wordBreak: 'break-all' }}>{mail ? w.recipients.join(', ') : w.url}</div></td>
                  <td className="small">{w.events.length === 0 ? tr("Alle") : tr("{length} ausgewählt", { length: w.events.length })}<div className="muted">{w.allTenants ? tr("alle Tenants") : tr("{length} Tenant(s)", { length: w.tenantIds.length })}</div></td>
                  <td className="small">{w.lastDeliveryAt ? <>{w.lastDeliverySucceeded ? <Badge tone="ok">{tr("ok")}</Badge> : <Badge tone="error">{tr("Fehler")}</Badge>} {fmtDate(w.lastDeliveryAt)}{w.lastError && <div className="muted">{w.lastError}</div>}</> : '–'}</td>
                  <td className="actions">
                    <button className="small" onClick={() => test(w)}>{tr("Testen")}</button>
                    <button className="small" onClick={() => setLog(w)}>{tr("Protokoll")}</button>
                    <button className="small" onClick={() => setEditing(w)}>{tr("Bearbeiten")}</button>
                    {!mail && <button className="small ghost" onClick={() => rotate(w)}>{tr("Neues Geheimnis")}</button>}
                    <button className="small danger" onClick={() => remove(w)}>{tr("Löschen")}</button>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </Card>
      {editing && <WebhookModal channel={channel} hook={editing === 'new' ? undefined : editing} events={events ?? []} onClose={() => setEditing(null)} onSaved={(s) => { setEditing(null); if (s) setSecret(s); reload(); }} />}
      {log && <DeliveryLog hook={log} onClose={() => setLog(null)} />}
    </>
  );
}

function WebhookModal({ channel, hook, events, onClose, onSaved }: { channel: Channel; hook?: WebhookRow; events: EventType[]; onClose: () => void; onSaved: (secret?: string) => void }) {
  const mail = channel === 'Mail';
  const tenants = useActiveTenants();
  const [name, setName] = useState(hook?.name ?? '');
  const [url, setUrl] = useState(hook?.url ?? 'https://');
  const [recipients, setRecipients] = useState((hook?.recipients ?? []).join(', '));
  const [all, setAll] = useState(hook ? hook.events.length === 0 : !mail);
  const [selected, setSelected] = useState<string[]>(hook?.events ?? []);
  const [allTenants, setAllTenants] = useState(hook?.allTenants ?? true);
  const [tenantIds, setTenantIds] = useState<string[]>(hook?.tenantIds ?? []);
  const [active, setActive] = useState(hook?.isActive ?? true);
  const { busy, error, run } = useAction();
  const groups = [...new Set(events.map((e) => e.group))];
  const save = () => run(async () => {
    const body = { name, channel, url: mail ? null : url, recipients: mail ? [recipients] : null, events: all ? [] : selected, allTenants, tenantIds: allTenants ? [] : tenantIds, isActive: active };
    if (hook) { await api.put(`/api/integrations/webhooks/${hook.id}`, body); onSaved(); }
    else { const r = await api.post<{ secret?: string | null }>('/api/integrations/webhooks', body); onSaved(r.secret ?? undefined); }
  });
  return (
    <Modal title={hook ? `${mail ? tr("Mail-Alarm") : 'Webhook'} „${hook.name}“` : mail ? tr("Mail-Alarm anlegen") : tr("Webhook anlegen")} onClose={onClose}
      footer={<><button onClick={onClose}>{tr("Abbrechen")}</button><button className="primary" disabled={busy || !name.trim() || (mail ? !recipients.includes('@') : !url.trim()) || (!all && selected.length === 0)} onClick={save}>{tr("Speichern")}</button></>}>
      <Field label={tr("Name")}><input value={name} onChange={(e) => setName(e.target.value)} placeholder={mail ? tr("z. B. Sicherheitsereignisse an IT-Leitung") : tr("z. B. Ticketsystem")} /></Field>
      {mail
        ? <Field label={tr("Empfänger")} hint={tr("Mehrere Adressen mit Komma trennen")}><input value={recipients} onChange={(e) => setRecipients(e.target.value)} placeholder="it@firma.de, leitung@firma.de" /></Field>
        : <Field label="URL" hint={tr("Empfänger, möglichst mit https://")}><input value={url} onChange={(e) => setUrl(e.target.value)} /></Field>}
      <div className="field-group">
        <span className="field-label">{tr("Ereignisse")}</span>
        <label className="check"><input type="radio" checked={all} onChange={() => setAll(true)} /> {tr("Alle Ereignisse, auch künftige")}</label>
        <label className="check"><input type="radio" checked={!all} onChange={() => setAll(false)} /> {tr("Nur ausgewählte")}</label>
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
      <label className="check"><input type="checkbox" checked={active} onChange={(e) => setActive(e.target.checked)} /> {tr("Aktiv")}</label>
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
    if (r.success) toast.ok(tr("Zugestellt.")); else toast.error(tr("Erneut fehlgeschlagen: {n}", { n: (r.error ?? '') }));
    reload();
  };
  const tone = { Pending: 'warn', Delivered: 'ok', Failed: 'error' } as const;
  return (
    <Modal title={tr("Protokoll „{name}“", { name: hook.name })} onClose={onClose} footer={<button onClick={onClose}>{tr("Schließen")}</button>}>
      {!data && <Spinner />}
      {data && data.length === 0 && <Empty>{tr("Noch keine Zustellungen.")}</Empty>}
      {data && data.length > 0 && (
        <table>
          <thead><tr><th>{tr("Ereignis")}</th><th>{tr("Status")}</th><th>{tr("Zeit")}</th><th /></tr></thead>
          <tbody>
            {data.map((d) => (
              <Fragment key={d.id}>
                <tr>
                  <td><button className="link" onClick={() => setOpen(open === d.id ? undefined : d.id)}><code>{d.eventType}</code></button></td>
                  <td><Badge tone={tone[d.status]}>{d.status === 'Pending' ? tr("wird wiederholt") : d.status === 'Delivered' ? 'zugestellt' : 'fehlgeschlagen'}</Badge>
                    <div className="small muted">{tr("{attempts} Versuch(e)", { attempts: d.attempts })}{d.lastStatusCode ? tr(" · HTTP {lastStatusCode}", { lastStatusCode: d.lastStatusCode }) : ''}{d.lastError ? ` · ${d.lastError}` : ''}</div></td>
                  <td className="small">{fmtDate(d.createdAt)}{d.status === 'Pending' && d.attempts > 0 && <div className="muted">{tr("nächster Versuch {nextAttemptAt}", { nextAttemptAt: fmtDate(d.nextAttemptAt) })}</div>}</td>
                  <td>{d.status !== 'Delivered' && <button className="small" onClick={() => retry(d)}>{tr("Jetzt senden")}</button>}</td>
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
    try { await api.post(`/api/integrations/reports/${s.id}/send`); toast.ok(tr("„{name}“ verschickt.", { name: s.name })); } catch (e) { toast.error((e as Error).message); }
    reload();
  };
  const remove = async (s: Schedule) => {
    if (!await confirm({ title: tr("Bericht löschen?"), message: tr("„{name}“ wird nicht mehr verschickt.", { name: s.name }), confirmLabel: tr("Löschen"), danger: true })) return;
    await api.del(`/api/integrations/reports/${s.id}`);
    reload();
  };
  return (
    <>
      {smtp ? <SmtpCard smtp={smtp} onSaved={reloadSmtp} /> : <Spinner />}
      <Card title={tr("Geplante Berichte")} className="mt" actions={<button className="primary" onClick={() => setEditing('new')}>{tr("Bericht anlegen")}</button>}>
        <p className="small muted" style={{ marginTop: 0 }}>{tr("Der Bericht enthält die wichtigsten Kennzahlen in der Mail und das Dashboard als PDF, auf Wunsch auch die Key-Liste als CSV.")}</p>
        <ErrorBox error={error} />
        {data && data.length === 0 && <Empty>{tr("Noch kein Bericht geplant.")}</Empty>}
        {data && data.length > 0 && (
          <table>
            <thead><tr><th>{tr("Name")}</th><th>{tr("Wann")}</th><th>{tr("Empfänger")}</th><th>{tr("Zuletzt")}</th><th /></tr></thead>
            <tbody>
              {data.map((s) => (
                <tr key={s.id} style={{ opacity: s.isActive ? 1 : 0.6 }}>
                  <td><strong>{s.name}</strong>{!s.isActive && <> <Badge>{tr("pausiert")}</Badge></>}<div className="small muted">{s.allTenants ? tr("Alle Tenants") : tr("{length} Tenant(s)", { length: s.tenantIds.length })}{s.includeCsv ? tr(" · mit CSV") : ''}</div></td>
                  <td className="small">{s.description}{s.nextRunAt && <div className="muted">{tr("nächster Versand {nextRunAt}", { nextRunAt: fmtDate(s.nextRunAt) })}</div>}</td>
                  <td className="small">{s.recipients.join(', ')}</td>
                  <td className="small">{s.lastSentAt ? <>{s.lastSucceeded ? <Badge tone="ok">{tr("ok")}</Badge> : <Badge tone="error">{tr("Fehler")}</Badge>} {fmtDate(s.lastSentAt)}{s.lastError && <div className="muted">{s.lastError}</div>}</> : '–'}</td>
                  <td className="actions">
                    <button className="small" disabled={!smtp?.isConfigured} onClick={() => send(s)}>{tr("Jetzt senden")}</button>
                    <button className="small" onClick={() => setEditing(s)}>{tr("Bearbeiten")}</button>
                    <button className="small danger" onClick={() => remove(s)}>{tr("Löschen")}</button>
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
    toast.ok(tr("Mailserver gespeichert."));
    onSaved();
  });
  const test = () => run(async () => { await api.post('/api/integrations/smtp/test', { to: testTo }); toast.ok(tr("Testmail an {testTo} verschickt.", { testTo })); });
  return (
    <Card title={tr("Mailserver (SMTP)")} actions={smtp.isConfigured ? <Badge tone="ok">{tr("eingerichtet")}</Badge> : <Badge tone="warn">{tr("nicht eingerichtet")}</Badge>}>
      <div className="grid cols-2">
        <Field label={tr("Server")}><input value={host} onChange={(e) => setHost(e.target.value)} placeholder="smtp.example.com" /></Field>
        <div className="grid cols-2">
          <Field label={tr("Port")}><input type="number" value={port} onChange={(e) => setPort(Number(e.target.value))} /></Field>
          <Field label={tr("Verschlüsselung")}>
            <select value={security} onChange={(e) => { const v = e.target.value as Smtp['security']; setSecurity(v); setPort(v === 'SslOnConnect' ? 465 : v === 'StartTls' ? 587 : 25); }}>
              <option value="StartTls">{tr("STARTTLS")}</option><option value="SslOnConnect">{tr("SSL/TLS")}</option><option value="None">{tr("keine")}</option>
            </select>
          </Field>
        </div>
        <Field label={tr("Benutzername")} hint={tr("Leer lassen, wenn der Server ohne Anmeldung annimmt")}><input value={username} onChange={(e) => setUsername(e.target.value)} autoComplete="off" /></Field>
        <Field label={tr("Passwort")} hint={smtp.hasPassword ? tr("Gespeichert. Leer lassen, um es zu behalten.") : undefined}><input type="password" value={password} onChange={(e) => setPassword(e.target.value)} autoComplete="new-password" /></Field>
        <Field label={tr("Absenderadresse")}><input value={fromAddress} onChange={(e) => setFromAddress(e.target.value)} placeholder="yubikeys@example.com" /></Field>
        <Field label={tr("Absendername")}><input value={fromName} onChange={(e) => setFromName(e.target.value)} placeholder={tr("Yubikey Lifecycle Manager")} /></Field>
      </div>
      <div className="actions">
        <button className="primary" disabled={busy} onClick={save}>{tr("Speichern")}</button>
        <input value={testTo} onChange={(e) => setTestTo(e.target.value)} placeholder={tr("Testmail an …")} aria-label={tr("Testmail an")} style={{ maxWidth: 260 }} />
        <button disabled={busy || !smtp.isConfigured || !testTo.includes('@')} onClick={test}>{tr("Testmail senden")}</button>
      </div>
      <ErrorBox error={error} />
    </Card>
  );
}

const weekdays = [tr("Sonntag"), tr("Montag"), tr("Dienstag"), tr("Mittwoch"), tr("Donnerstag"), tr("Freitag"), tr("Samstag")];

function ScheduleModal({ schedule, onClose, onSaved }: { schedule?: Schedule; onClose: () => void; onSaved: () => void }) {
  const tenants = useActiveTenants();
  const [name, setName] = useState(schedule?.name ?? tr("Wochenbericht YubiKeys"));
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
    <Modal title={schedule ? tr("Bericht „{name}“", { name: schedule.name }) : tr("Bericht anlegen")} onClose={onClose}
      footer={<><button onClick={onClose}>{tr("Abbrechen")}</button><button className="primary" disabled={busy || !name.trim() || !recipients.includes('@')} onClick={save}>{tr("Speichern")}</button></>}>
      <Field label={tr("Name")}><input value={name} onChange={(e) => setName(e.target.value)} /></Field>
      <Field label={tr("Empfänger")} hint={tr("Mehrere mit Komma oder Semikolon trennen")}><textarea rows={2} value={recipients} onChange={(e) => setRecipients(e.target.value)} /></Field>
      <div className="grid cols-2">
        <Field label={tr("Wie oft")}>
          <select value={frequency} onChange={(e) => setFrequency(e.target.value as Schedule['frequency'])}>
            <option value="Daily">{tr("täglich")}</option><option value="Weekly">{tr("wöchentlich")}</option><option value="Monthly">{tr("monatlich")}</option>
          </select>
        </Field>
        {frequency === 'Weekly' && <Field label={tr("Tag")}><select value={dayOfWeek} onChange={(e) => setDayOfWeek(Number(e.target.value))}>{weekdays.map((d, i) => <option key={d} value={i}>{d}</option>)}</select></Field>}
        {frequency === 'Monthly' && <Field label={tr("Tag im Monat")}><select value={dayOfMonth} onChange={(e) => setDayOfMonth(Number(e.target.value))}>{Array.from({ length: 28 }, (_, i) => <option key={i} value={i + 1}>{i + 1}.</option>)}</select></Field>}
        <Field label={tr("Uhrzeit")} hint={tr("Deutsche Zeit")}><select value={hour} onChange={(e) => setHour(Number(e.target.value))}>{Array.from({ length: 24 }, (_, i) => <option key={i} value={i}>{tr("{i}:00 Uhr", { i })}</option>)}</select></Field>
      </div>
      <ScopeEditor allTenants={allTenants} setAllTenants={setAllTenants} tenantIds={tenantIds} setTenantIds={setTenantIds} tenants={tenants} canAll />
      <label className="check"><input type="checkbox" checked={includeCsv} onChange={(e) => setIncludeCsv(e.target.checked)} /> {tr("Key-Liste als CSV anhängen")}</label>
      <label className="check"><input type="checkbox" checked={active} onChange={(e) => setActive(e.target.checked)} /> {tr("Aktiv")}</label>
      <ErrorBox error={error} />
    </Modal>
  );
}
