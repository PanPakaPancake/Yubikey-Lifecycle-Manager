import { useState } from 'react';
import { useAuth } from '../auth';
import { useConfirm } from '../feedback';
import { Info, Modal } from '../ui';

/**
 * Notfallblatt für den versiegelten Umschlag zum Breakglass-Key.
 *
 * Wichtig (Vorgabe): Die Eingaben werden nirgends gespeichert. Sie leben nur im React-State dieses Dialogs,
 * gehen nie an das Backend, nicht in localStorage/sessionStorage und nicht in die URL. Beim Schließen sind sie weg.
 * Die Felder sind so markiert, dass Browser und Passwort-Manager nichts übernehmen oder vorschlagen.
 */

export interface SheetAccount { userPrincipalName: string; userDisplayName?: string | null }

export interface SheetContext {
  tenantName?: string | null;
  serialNumber?: number | null;
  custodian?: string | null;
  accounts: SheetAccount[];
}

interface Row { id: number; description: string; username: string; password: string; mfa: string }

const MIN_PRINT_ROWS = 6;
const mfaSuggestions = ['Dieser YubiKey (FIDO2)', 'TOTP-App', 'Hardware-Token', 'SMS', 'keine'];

// Felder sollen weder im Browser-Verlauf noch im Passwort-Manager landen.
const noStore = {
  autoComplete: 'off', autoCorrect: 'off', autoCapitalize: 'off', spellCheck: false,
  'data-lpignore': 'true', 'data-1p-ignore': 'true', 'data-bwignore': 'true', 'data-form-type': 'other',
} as const;

const escape = (s: string) => s.replace(/[&<>"']/g, (c) => `&#${c.charCodeAt(0)};`);

let nextId = 1;
const emptyRow = (patch: Partial<Row> = {}): Row => ({ id: nextId++, description: '', username: '', password: '', mfa: '', ...patch });

/** Druckt in einem eigenen Fenster (wie die Notfallcodes), damit nur das Blatt auf dem Papier landet. */
function printSheet(ctx: SheetContext, rows: Row[], org?: string) {
  const w = window.open('', '_blank', 'width=900,height=1000');
  if (!w) return false;
  const filled = rows.filter((r) => r.description || r.username || r.password || r.mfa);
  const padded = [...filled, ...Array.from({ length: Math.max(0, MIN_PRINT_ROWS - filled.length) }, () => emptyRow())];
  const body = padded.map((r, i) => `<tr><td class="n">${i + 1}</td><td>${escape(r.description)}</td><td class="m">${escape(r.username)}</td><td class="m pw">${escape(r.password)}</td><td>${escape(r.mfa)}</td></tr>`).join('');
  const accounts = ctx.accounts.map((a) => escape(a.userDisplayName ? `${a.userDisplayName} (${a.userPrincipalName})` : a.userPrincipalName)).join('<br>');
  w.document.write(`<!doctype html><html lang="de"><head><meta charset="utf-8"><title>Notfallblatt Breakglass</title><style>
    @page{size:A4 landscape;margin:14mm}
    body{font-family:system-ui,sans-serif;color:#111;margin:0}
    @media screen{body{margin:28px}}
    h1{font-size:20px;margin:0 0 2px} .sub{font-size:12px;color:#444;margin:0 0 14px}
    .warn{border:1.5px solid #111;padding:6px 10px;font-size:12px;font-weight:600;margin-bottom:14px}
    .meta{display:grid;grid-template-columns:repeat(4,1fr);gap:8px 18px;font-size:12px;margin-bottom:14px}
    .meta b{display:block;font-size:10px;text-transform:uppercase;letter-spacing:.5px;color:#555;font-weight:600}
    table{width:100%;border-collapse:collapse;font-size:12.5px}
    th{text-align:left;font-size:10px;text-transform:uppercase;letter-spacing:.5px;color:#555;border-bottom:1.5px solid #111;padding:6px}
    td{border-bottom:1px solid #999;padding:0 6px;height:30px;vertical-align:middle;word-break:break-all}
    td.n{width:22px;color:#777} td.m{font-family:ui-monospace,Consolas,monospace} td.pw{font-size:14px;letter-spacing:.5px}
    .seal{display:grid;grid-template-columns:repeat(3,1fr);gap:28px;margin-top:26px;font-size:11px;color:#444}
    .seal div{border-top:1px solid #111;padding-top:4px}
    .foot{margin-top:18px;font-size:10.5px;color:#666}
  </style></head><body>
    <h1>Notfallblatt Breakglass${org ? ' · ' + escape(org) : ''}</h1>
    <p class="sub">Gehört zusammen mit dem YubiKey in den versiegelten Umschlag. Nur im Notfall öffnen.</p>
    <div class="warn">VERTRAULICH: Dieses Blatt enthält Zugangsdaten. Nicht kopieren, nicht scannen, nicht fotografieren.</div>
    <div class="meta">
      <div><b>Tenant</b>${escape(ctx.tenantName ?? '–')}</div>
      <div><b>YubiKey Seriennummer</b>${ctx.serialNumber ?? '–'}</div>
      <div><b>Verwahrer</b>${escape(ctx.custodian ?? '–')}</div>
      <div><b>Konten auf dem Key</b>${accounts || '–'}</div>
    </div>
    <table><thead><tr><th></th><th>Beschreibung</th><th>Benutzername</th><th>Passwort</th><th>MFA-Methode</th></tr></thead><tbody>${body}</tbody></table>
    <div class="seal"><div>Umschlag verschlossen am</div><div>Verschlossen von</div><div>Zeuge (Vier-Augen)</div></div>
    <p class="foot">Erstellt am ${escape(new Date().toLocaleString('de-DE'))} mit dem YubiKey Lifecycle Manager. Die Angaben wurden nicht gespeichert und sind nur auf diesem Blatt vorhanden.</p>
  </body></html>`);
  w.document.close();
  // Ohne Inline-Skript im Druckfenster (Content-Security-Policy); nach dem Drucken schließt es sich wieder.
  w.onafterprint = () => w.close();
  w.focus();
  w.print();
  return true;
}

export function BreakglassSheetModal({ context, onClose }: { context: SheetContext; onClose: () => void }) {
  const { me } = useAuth();
  const confirm = useConfirm();
  const [rows, setRows] = useState<Row[]>(() => [
    ...context.accounts.map((a) => emptyRow({ description: 'Entra Breakglass-Konto', username: a.userPrincipalName, mfa: mfaSuggestions[0] })),
    emptyRow(),
  ]);
  const [masked, setMasked] = useState(false);
  const [blocked, setBlocked] = useState(false);
  const hasSecrets = rows.some((r) => r.password || r.username || r.description);

  const set = (id: number, patch: Partial<Row>) => setRows((rs) => rs.map((r) => (r.id === id ? { ...r, ...patch } : r)));
  const close = async () => {
    if (hasSecrets && !(await confirm({ title: 'Notfallblatt schließen?', message: 'Die Eingaben werden nicht gespeichert und sind danach weg.', confirmLabel: 'Schließen' }))) return;
    setRows([]);
    onClose();
  };

  return (
    <Modal wide title="Notfallblatt für den Umschlag" onClose={close} footer={<>
      <button className="ghost" onClick={() => setRows([emptyRow()])}>Alles leeren</button>
      <button onClick={close}>Schließen</button>
      <button className="primary" onClick={() => setBlocked(!printSheet(context, rows, me.organizationName))}>Drucken</button>
    </>}>
      <Info tone="warn">
        Weitere Notfallkonten (z. B. Amazon, Domain-Registrar, Bank) eintragen und ausdrucken. <strong>Nichts davon wird gespeichert</strong>,
        weder hier noch auf dem Server. Bitte direkt auf Papier drucken, nicht „Als PDF speichern“.
      </Info>
      {blocked && <Info tone="warn">Das Druckfenster wurde blockiert. Bitte Pop-ups für diese Seite erlauben und erneut drucken.</Info>}
      <p className="small muted">
        {context.tenantName ?? ''}{context.serialNumber ? ` · Key ${context.serialNumber}` : ''}{context.custodian ? ` · Verwahrer ${context.custodian}` : ''}
      </p>
      <datalist id="bg-mfa">{mfaSuggestions.map((m) => <option key={m} value={m} />)}</datalist>
      <table className={'bg-sheet-table' + (masked ? ' masked' : '')}>
        <thead><tr><th>Beschreibung</th><th>Benutzername</th><th>Passwort</th><th>MFA-Methode</th><th /></tr></thead>
        <tbody>
          {rows.map((r) => (
            <tr key={r.id}>
              <td><input {...noStore} aria-label="Beschreibung" placeholder="z. B. Amazon Business Root" value={r.description} onChange={(e) => set(r.id, { description: e.target.value })} /></td>
              <td><input {...noStore} aria-label="Benutzername" value={r.username} onChange={(e) => set(r.id, { username: e.target.value })} /></td>
              <td><input {...noStore} className="secret" aria-label="Passwort" value={r.password} onChange={(e) => set(r.id, { password: e.target.value })} /></td>
              <td><input {...noStore} list="bg-mfa" aria-label="MFA-Methode" placeholder="falls vorhanden" value={r.mfa} onChange={(e) => set(r.id, { mfa: e.target.value })} /></td>
              <td className="row-actions"><button className="small ghost" aria-label="Zeile entfernen" onClick={() => setRows((rs) => rs.filter((x) => x.id !== r.id))}>×</button></td>
            </tr>
          ))}
        </tbody>
      </table>
      <div className="actions" style={{ marginTop: 10 }}>
        <button className="small" onClick={() => setRows((rs) => [...rs, emptyRow()])}>Zeile hinzufügen</button>
        <label className="small"><input type="checkbox" checked={masked} onChange={(e) => setMasked(e.target.checked)} /> Passwörter am Bildschirm verdecken</label>
      </div>
      <p className="small muted">Leere Zeilen werden auf dem Blatt bis {MIN_PRINT_ROWS} Zeilen aufgefüllt, damit Sie von Hand ergänzen können.</p>
    </Modal>
  );
}
