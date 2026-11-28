import { useEffect, useState } from 'react';
import { api, fmtDate } from '../api';
import { useAuth } from '../auth';
import { Card, ErrorBox, useAction, useApi } from '../ui';
import { useConfirm } from '../feedback';
import { language, locale, tr } from '../i18n';

type Status = { remaining: number; total: number; createdAt?: string | null };

const escape = (s: string) => s.replace(/[&<>"']/g, (c) => `&#${c.charCodeAt(0)};`);

/** Druckblatt in einem eigenen Fenster, damit nur die Codes auf dem Papier landen. */
function printCodes(codes: string[], upn: string, org?: string) {
  const w = window.open('', '_blank', 'width=720,height=900');
  if (!w) return false;
  const rows = codes.map((c, i) => `<li><span>${i + 1}.</span><code>${escape(c)}</code><span class="box"></span></li>`).join('');
  w.document.write(`<!doctype html><html lang="${language()}"><head><meta charset="utf-8"><title>${escape(tr("Notfallcodes"))}</title><style>
    body{font-family:system-ui,sans-serif;color:#111;margin:40px;max-width:640px}
    h1{font-size:22px;margin:0 0 4px} p{font-size:13px;line-height:1.5;color:#333}
    ol{list-style:none;padding:0;margin:24px 0;display:grid;grid-template-columns:1fr 1fr;gap:10px 24px}
    li{display:flex;align-items:center;gap:10px;font-size:13px}
    code{font:600 17px ui-monospace,Consolas,monospace;letter-spacing:1px}
    .box{width:14px;height:14px;border:1.5px solid #555;border-radius:3px;margin-left:auto}
    .meta{border-top:1px solid #ccc;padding-top:10px;font-size:12px;color:#555}
  </style></head><body>
    <h1>${escape(tr("Notfallcodes"))} ${org ? '· ' + escape(org) : ''}</h1>
    <p>${escape(tr("YubiKey Lifecycle Manager · Owner-Konto"))} <strong>${escape(upn)}</strong></p>
    <p>${escape(tr("Jeder Code gilt genau einmal und meldet Sie ohne Microsoft oder Passkey an (Anmeldeseite → „Mit Notfallcode anmelden“). Verbrauchte Codes abhaken. Bitte sicher verwahren, z. B. im Tresor, und nicht digital ablegen."))}</p>
    <ol>${rows}</ol>
    <p class="meta">${escape(tr("Erzeugt am {date}. Neue Codes machen dieses Blatt ungültig.", { date: new Date().toLocaleString(locale()) }))}</p>
  </body></html>`);
  w.document.close();
  // Ohne Inline-Skript im Druckfenster: Die Content-Security-Policy erlaubt nur Skripte der Plattform.
  w.onafterprint = () => w.close();
  w.focus();
  w.print();
  return true;
}

/** Notfallcodes des eigenen Owner-Kontos: Stand, neu erzeugen, einmalig anzeigen und ausdrucken. */
export default function RecoveryCodesCard() {
  const { me, reload } = useAuth();
  const { data, reload: reloadStatus } = useApi<Status>('/api/me/recovery-codes');
  const [codes, setCodes] = useState<string[]>();
  const [printBlocked, setPrintBlocked] = useState(false);
  const { busy, error, run } = useAction();
  const confirm = useConfirm();
  useEffect(() => {
    if (window.location.hash === '#notfallcodes') document.getElementById('notfallcodes')?.scrollIntoView({ behavior: 'smooth' });
  }, []);

  const create = async () => {
    if (data && data.total > 0 && !(await confirm({
      title: tr("Neue Notfallcodes erzeugen?"),
      message: tr("Alle bisherigen Codes werden sofort ungültig, auch ausgedruckte."),
      confirmLabel: tr("Neu erzeugen"), danger: true,
    }))) return;
    await run(async () => {
      const r = await api.post<{ codes: string[] }>('/api/me/recovery-codes');
      setCodes(r.codes);
      reloadStatus();
      reload();
    });
  };

  return (
    <div id="notfallcodes">
      <Card title={tr("Notfallcodes")} actions={!codes && <button className={data?.remaining ? '' : 'primary'} disabled={busy} onClick={create}>{data?.total ? tr("Neue Codes erzeugen") : tr("Codes erzeugen")}</button>}>
        <p className="muted">
          {tr("Einmal gültige Codes für den Notfall: Damit melden Sie sich an, wenn Microsoft oder Ihr Passkey nicht funktioniert. Ausdrucken und sicher verwahren. Jede Nutzung steht im Audit-Log und kann als Mail-Alarm gemeldet werden.")}
        </p>
        {data && !codes && (
          <p>{data.total === 0
            ? <strong>{tr("Noch keine Codes erzeugt.")}</strong>
            : <>{tr("Noch")} <strong>{tr("{remaining} von {total}", { remaining: data.remaining, total: data.total })}</strong> {tr("Codes gültig")}{data.createdAt ? tr(", erzeugt am {createdAt}", { createdAt: fmtDate(data.createdAt) }) : ''}.</>}</p>
        )}
        {codes && (
          <>
            <div className="alert warn">{tr("Diese Codes werden nur jetzt angezeigt. Bitte ausdrucken oder notieren, bevor Sie die Seite verlassen.")}</div>
            <ol className="recovery-codes" style={{ display: 'grid', gridTemplateColumns: 'repeat(auto-fill, minmax(190px, 1fr))', gap: '8px 20px', padding: 0, listStyle: 'none', margin: '16px 0' }}>
              {codes.map((c) => <li key={c}><code style={{ fontSize: 16, letterSpacing: 1 }}>{c}</code></li>)}
            </ol>
            <div className="row" style={{ display: 'flex', gap: 8, flexWrap: 'wrap' }}>
              <button className="primary" onClick={() => setPrintBlocked(!printCodes(codes, me.user?.upn ?? '', me.organizationName))}>{tr("Drucken")}</button>
              <button onClick={() => void navigator.clipboard?.writeText(codes.join('\n'))}>{tr("Kopieren")}</button>
              <button className="ghost" onClick={() => setCodes(undefined)}>{tr("Fertig, Codes ausblenden")}</button>
            </div>
            {printBlocked && <p className="small muted">{tr("Das Druckfenster wurde vom Browser blockiert. Bitte Pop-ups für diese Seite erlauben.")}</p>}
          </>
        )}
        <ErrorBox error={error} />
      </Card>
    </div>
  );
}
