import { useEffect, useMemo, useState } from 'react';
import { api, fmtDate } from '../api';
import { Badge, Card, download, ErrorBox, Field, Info, Spinner, useAction, useApi } from '../ui';
import { useConfirm } from '../feedback';

type Mode = 'Https' | 'Proxy' | 'Local';
type Tls = 'Internal' | 'Own' | 'LetsEncrypt' | 'LetsEncryptDns';
type Dns = { token?: string | null; keyId?: string | null; secret?: string | null; region?: string | null; azureTenant?: string | null; azureSubscription?: string | null; azureResourceGroup?: string | null; netcupCustomer?: string | null };
type View = {
  mode: Mode; publicUrl?: string | null; domain?: string | null; aliases?: string | null; httpsPort: number; httpPort: number; httpsBind?: string | null;
  tls: Tls; acmeEmail?: string | null; dnsProvider?: string | null; dns: Dns; secretsSet: string[]; ownCertificatePresent: boolean; bindAddress?: string | null; port: number;
};
type CertInfo = { subject: string; issuer: string; notBefore: string; notAfter: string; dnsNames: string[]; thumbprint: string };
type Provider = { id: string; name: string; required: (keyof Dns)[] };
type HostingInfo = {
  available: boolean; reason?: string | null; current: View; certificate?: CertInfo | null; dnsProviders: Provider[];
  entra: { clientId?: string | null; homeTenantId?: string | null; redirectUris: string[] }; localPasskeys: number; agents: number;
};

const modeLabels: Record<Mode, string> = { Https: 'HTTPS (mitgeliefert)', Proxy: 'Eigener Reverse Proxy / ZTNA', Local: 'Nur auf diesem PC' };
/** Erklärung je Betriebsart: wer HTTPS macht, wann sie passt. */
const modeHelp: Record<Mode, { what: string; when: string }> = {
  Https: {
    what: 'Die Plattform kümmert sich selbst um HTTPS: Der mitgelieferte Webserver (Caddy) nimmt die Verbindungen an, verschlüsselt sie und besorgt bzw. erneuert das Zertifikat.',
    when: 'Passt fast immer, auf dem eigenen PC (https://localhost) wie auf einem Server im Firmennetz. Empfohlen.',
  },
  Proxy: {
    what: 'Vor der Plattform steht schon etwas, das HTTPS übernimmt, z. B. nginx, Traefik, IIS, ein Cloudflare-Tunnel oder ein ZTNA-Connector (Entra Private Access, Zscaler). Das Zertifikat liegt dort; die Plattform spricht intern nur http und bekommt die Anfragen weitergeleitet.',
    when: 'Nur wählen, wenn ein solcher Proxy bereits eingerichtet ist. Ohne ihn ist die Plattform danach nicht erreichbar.',
  },
  Local: {
    what: 'Kein HTTPS: Die Plattform läuft unverschlüsselt unter http://localhost und ist nur auf diesem PC erreichbar.',
    when: 'Nur zum Ausprobieren. Besser ist „HTTPS (mitgeliefert)“ mit dem Namen localhost.',
  },
};
/** Erklärung je Zertifikatsart. */
const tlsHelp: Record<Tls, string> = {
  Internal: 'Caddy stellt das Zertifikat selbst aus und erneuert es automatisch. Damit Browser und Agent nicht warnen, muss das Stammzertifikat einmal auf den Admin-PCs als vertrauenswürdig eingetragen werden (GPO/Intune, auf dem eigenen PC per Installationsskript). Ideal für localhost und interne Server.',
  Own: 'Sie laden ein vorhandenes Zertifikat hoch, z. B. ein Wildcard-Zertifikat (*.firma.de) oder eines aus der Firmen-PKI. Vor Ablauf tauschen Sie es hier oder automatisch per API.',
  LetsEncrypt: 'Kostenloses, überall vertrautes Zertifikat, automatisch erneuert. Voraussetzung: Der Name zeigt öffentlich auf diesen Server und Port 80 ist aus dem Internet erreichbar.',
  LetsEncryptDns: 'Wie Let\'s Encrypt, aber die Prüfung läuft über einen DNS-Eintrag statt über Port 80. Funktioniert deshalb auch für interne Server und andere Ports. Braucht ein API-Token Ihres DNS-Anbieters.',
};

const tlsLabels: Record<Tls, string> = {
  Internal: 'Interne Zertifizierungsstelle (selbstsigniert)',
  Own: 'Eigenes Zertifikat (z. B. Wildcard, Firmen-PKI)',
  LetsEncrypt: "Let's Encrypt (Server aus dem Internet erreichbar)",
  LetsEncryptDns: "Let's Encrypt per DNS (auch intern, beliebige Ports)",
};
const dnsLabels: Record<keyof Dns, string> = {
  token: 'API-Token', keyId: 'Schlüssel-ID / Client-ID / API-Key', secret: 'Geheimnis / Passwort', region: 'Region',
  azureTenant: 'Tenant-ID', azureSubscription: 'Subscription-ID', azureResourceGroup: 'Ressourcengruppe der DNS-Zone', netcupCustomer: 'Kundennummer',
};
const secretFields: (keyof Dns)[] = ['token', 'secret'];

function origin(url?: string | null) {
  try { return url ? new URL(url).origin : undefined; } catch { return undefined; }
}

/** Neue Adresse wie im Server berechnet (nur für die Anzeige und den Entra-Schritt). */
function targetUrl(f: View): string | undefined {
  const host = (f.domain ?? '').trim().toLowerCase().replace(/^https:\/\//, '').replace(/\/+$/, '');
  if (f.mode === 'Https') return host ? `https://${host}${f.httpsPort && f.httpsPort !== 443 ? ':' + f.httpsPort : ''}` : undefined;
  if (f.mode === 'Local') return `http://localhost:${f.port || 8080}`;
  return origin(f.publicUrl);
}

function readFile(file: File) {
  return new Promise<string>((resolve, reject) => {
    const r = new FileReader();
    r.onload = () => resolve(String(r.result));
    r.onerror = () => reject(r.error);
    r.readAsText(file);
  });
}

export function HostingCard() {
  const { data, error, reload } = useApi<HostingInfo>('/api/platform/hosting');
  const [editing, setEditing] = useState(false);
  const [applied, setApplied] = useState<string>();
  const cur = data?.current;

  return (
    <div id="hosting" className="anchor-target">
      <Card title="Adresse und Zertifikat" actions={data?.available && !editing && !applied && <button onClick={() => setEditing(true)}>Ändern</button>}>
        <ErrorBox error={error} />
        {!data && !error && <Spinner />}
        {data && cur && <>
          <dl className="props">
            <dt>Adresse</dt><dd>{cur.publicUrl ? <a href={cur.publicUrl} className="mono">{cur.publicUrl}</a> : '–'}
              {cur.publicUrl?.startsWith('http://') && <> <Badge tone="warn">ohne HTTPS</Badge></>}</dd>
            <dt>Betriebsart</dt><dd>{modeLabels[cur.mode]}</dd>
            {cur.mode === 'Https' && <>
              <dt>Weitere Namen</dt><dd>{cur.aliases || <span className="muted">keine</span>}</dd>
              <dt>Ports</dt><dd className="mono">HTTPS {cur.httpsPort} · HTTP {cur.httpPort}{cur.httpsBind ? ` · nur ${cur.httpsBind}` : ''}</dd>
              <dt>Zertifikat</dt><dd>{tlsLabels[cur.tls]}{cur.tls === 'LetsEncryptDns' && cur.dnsProvider && <> · {data.dnsProviders.find((p) => p.id === cur.dnsProvider)?.name ?? cur.dnsProvider}</>}
                {cur.tls === 'Internal' && <div className="small"><button className="link" onClick={() => download('/api/platform/hosting/root-ca')}>Stammzertifikat herunterladen</button> <span className="muted">{cur.domain === 'localhost'
                  ? <>und eintragen: Installationsskript erneut ausführen (trägt es per UAC ein) oder als Administrator <code>certutil -addstore Root ylm-root-ca.crt</code>. Danach vertrauen Browser und Agent https://localhost.</>
                  : <>und per GPO/Intune als vertrauenswürdige Stammzertifizierungsstelle auf den Admin-PCs verteilen, dann warnt der Browser nicht mehr.</>}</span></div>}</dd>
              {data.certificate && <>
                <dt>Gültig bis</dt><dd>{fmtDate(data.certificate.notAfter)} <CertDays notAfter={data.certificate.notAfter} /></dd>
                <dt>Ausgestellt für</dt><dd className="small">{data.certificate.dnsNames.join(', ') || data.certificate.subject}</dd>
                <dt>Aussteller</dt><dd className="small">{data.certificate.issuer}</dd>
              </>}
            </>}
            {cur.mode !== 'Https' && <><dt>Server-Port</dt><dd className="mono">{cur.bindAddress}:{cur.port}</dd></>}
          </dl>
          {cur.mode === 'Local' && <Info tone="warn">Die Plattform läuft ohne HTTPS. Empfohlen ist „HTTPS (mitgeliefert)“, auf dem eigenen PC mit dem Namen <code>localhost</code> und der internen Zertifizierungsstelle.</Info>}
          {cur.mode === 'Https' && cur.tls === 'Own' && <p className="small muted">Ein neues Zertifikat lässt sich hier über „Ändern“ hochladen oder automatisch per API (<code>PUT /api/v1/certificate</code>, API-Schlüssel mit dem Recht „HTTPS-Zertifikat tauschen“).</p>}
          {!data.available && <Info>{data.reason}</Info>}
          {applied && <Applied url={applied} onDone={() => { setApplied(undefined); reload(); }} />}
          {editing && <HostingForm info={data} onCancel={() => setEditing(false)} onApplied={(url) => { setEditing(false); setApplied(url); }} />}
        </>}
      </Card>
    </div>
  );
}

function CertDays({ notAfter }: { notAfter: string }) {
  const days = Math.floor((new Date(notAfter).getTime() - Date.now()) / 86_400_000);
  return days < 0 ? <Badge tone="error">abgelaufen</Badge> : days <= 14 ? <Badge tone="warn">noch {days} Tage</Badge> : <span className="small muted">noch {days} Tage</span>;
}

function HostingForm({ info, onCancel, onApplied }: { info: HostingInfo; onCancel: () => void; onApplied: (url: string) => void }) {
  const cur = info.current;
  const [f, setF] = useState<View>(() => ({
    ...cur,
    // Ohne HTTPS: HTTPS als Vorschlag, lokal mit localhost und interner Zertifizierungsstelle.
    ...(cur.mode === 'Local' ? { mode: 'Https' as Mode, domain: 'localhost', tls: 'Internal' as Tls } : {}),
    domain: cur.domain ?? (cur.mode === 'Local' ? 'localhost' : ''),
    dns: { ...cur.dns },
  }));
  const [keepOld, setKeepOld] = useState(true);
  const [cert, setCert] = useState<string>();
  const [key, setKey] = useState<string>();
  const [entraDone, setEntraDone] = useState(false);
  const { busy, error, run } = useAction();
  const confirm = useConfirm();
  const set = (patch: Partial<View>) => setF((x) => ({ ...x, ...patch }));
  const setDns = (k: keyof Dns, v: string) => setF((x) => ({ ...x, dns: { ...x.dns, [k]: v } }));

  const newUrl = targetUrl(f);
  const addressChanged = !!newUrl && origin(newUrl) !== origin(cur.publicUrl);
  const hostChanged = !!newUrl && (() => { try { return new URL(newUrl).hostname !== new URL(cur.publicUrl ?? '').hostname; } catch { return true; } })();
  const needsEntra = addressChanged && !!info.entra.clientId;
  const provider = info.dnsProviders.find((p) => p.id === f.dnsProvider);
  const oldDomain = cur.mode === 'Https' ? cur.domain : undefined;
  const domainChanged = f.mode === 'Https' && !!oldDomain && oldDomain !== f.domain?.trim().toLowerCase();

  const save = async () => {
    if (!(await confirm({
      title: 'Einstellungen übernehmen?',
      message: `Die Plattform startet dabei neu und ist etwa eine Minute nicht erreichbar.${addressChanged ? ` Danach erreichen Sie sie unter ${newUrl}.` : ''} Ist etwas falsch, stellt der Updater den vorigen Stand wieder her.`,
      confirmLabel: 'Übernehmen',
    }))) return;
    await run(async () => {
      const r = await api.put<{ publicUrl: string }>('/api/platform/hosting', {
        entraConfirmed: entraDone,
        settings: {
          mode: f.mode, domain: f.domain, aliases: f.aliases ?? '', keepOldAddress: keepOld, httpsPort: f.httpsPort, httpPort: f.httpPort,
          tls: f.tls, acmeEmail: f.acmeEmail, dnsProvider: f.dnsProvider, dns: f.dns, certificatePem: cert, keyPem: key,
          publicUrl: f.publicUrl, bindAddress: f.bindAddress, port: f.port,
        },
      });
      onApplied(r.publicUrl);
    });
  };

  return (
    <div className="mt">
      <p className="small muted" style={{ marginTop: 0 }}>Wer kümmert sich um HTTPS? Nichts ändert sich, bevor Sie auf „Übernehmen“ klicken.</p>
      <div role="radiogroup" aria-label="Betriebsart" style={{ marginBottom: 12 }}>
        {(Object.keys(modeLabels) as Mode[]).map((m) => (
          <label key={m} className={`choice${f.mode === m ? ' selected' : ''}`}>
            <input type="radio" name="hosting-mode" checked={f.mode === m} onChange={() => set({ mode: m })} />
            <span>
              <strong>{modeLabels[m]}</strong>{cur.mode === m && <> <Badge tone="ok">aktuell</Badge></>}
              <div className="small">{modeHelp[m].what}</div>
              <div className="small muted">{modeHelp[m].when}</div>
            </span>
          </label>
        ))}
      </div>

      {f.mode === 'Https' && <>
        <p className="small muted">Für den eigenen PC: DNS-Name <code>localhost</code> und Zertifikat „Interne Zertifizierungsstelle“.</p>
        <div className="grid-2">
          <Field label="DNS-Name" hint="Unter diesem Namen erreichen Browser und Agents die Plattform, z. B. ylm.firma.de.">
            <input value={f.domain ?? ''} onChange={(e) => set({ domain: e.target.value.trim() })} placeholder="ylm.firma.de" />
          </Field>
          <Field label="Weitere Namen (optional)" hint="Mit Leerzeichen getrennt. Die Plattform antwortet auch darauf.">
            <input value={f.aliases ?? ''} onChange={(e) => set({ aliases: e.target.value })} placeholder="ylm.firma.intern" />
          </Field>
          <Field label="HTTPS-Port" hint="Standard 443. Andere Ports stehen dann in der Adresse.">
            <input type="number" min={1} max={65535} value={f.httpsPort} onChange={(e) => set({ httpsPort: Number(e.target.value) })} />
          </Field>
          <Field label="HTTP-Port" hint="Standard 80 (leitet auf HTTPS um, Let's Encrypt ohne DNS braucht ihn).">
            <input type="number" min={1} max={65535} value={f.httpPort} onChange={(e) => set({ httpPort: Number(e.target.value) })} />
          </Field>
        </div>
        {domainChanged && (
          <label className="check"><input type="checkbox" checked={keepOld} onChange={(e) => setKeepOld(e.target.checked)} />
            Alte Adresse <code>{oldDomain}</code> weiter beantworten (empfohlen: Agents ziehen darüber automatisch um, Lesezeichen funktionieren weiter)</label>
        )}

        <div className="field-label" style={{ marginTop: 12, marginBottom: 6 }}>Zertifikat</div>
        <div role="radiogroup" aria-label="Zertifikat">
          {(Object.keys(tlsLabels) as Tls[]).map((t) => (
            <label key={t} className={`choice${f.tls === t ? ' selected' : ''}`}>
              <input type="radio" name="hosting-tls" checked={f.tls === t} onChange={() => set({ tls: t })} />
              <span><strong>{tlsLabels[t]}</strong>{cur.mode === 'Https' && cur.tls === t && <> <Badge tone="ok">aktuell</Badge></>}
                <div className="small muted">{tlsHelp[t]}</div></span>
            </label>
          ))}
        </div>
        {f.tls === 'Internal' && f.domain === 'localhost' && <p className="small muted">Auf dem eigenen PC trägt das Installationsskript das Stammzertifikat beim nächsten Lauf selbst ein. Bis dahin verbindet sich der Agent weiter über die alte Adresse.</p>}
        {(f.tls === 'LetsEncrypt' || f.tls === 'LetsEncryptDns') && (
          <Field label="E-Mail für Let's Encrypt (optional)" hint="Für Hinweise zum Ablauf, falls die automatische Erneuerung klemmt.">
            <input type="email" value={f.acmeEmail ?? ''} onChange={(e) => set({ acmeEmail: e.target.value })} placeholder="it@firma.de" />
          </Field>
        )}
        {f.tls === 'LetsEncrypt' && <Info>Der Name muss öffentlich auf diesen Server zeigen und Port 80 aus dem Internet erreichbar sein. Für interne Server oder andere Ports: „Let's Encrypt per DNS“.</Info>}
        {f.tls === 'LetsEncryptDns' && <>
          <Field label="DNS-Anbieter" hint="Caddy legt für die Prüfung kurz einen TXT-Eintrag _acme-challenge an. Das Token braucht nur Rechte auf diese Zone.">
            <select value={f.dnsProvider ?? ''} onChange={(e) => set({ dnsProvider: e.target.value })}>
              <option value="">Bitte wählen …</option>
              {info.dnsProviders.map((p) => <option key={p.id} value={p.id}>{p.name}</option>)}
            </select>
          </Field>
          {provider && <div className="grid-2">
            {provider.required.map((k) => {
              const stored = secretFields.includes(k) && cur.dnsProvider === provider.id && cur.secretsSet.includes(k);
              return (
                <Field key={k} label={dnsLabels[k]} hint={stored ? 'Gespeichert. Leer lassen, um es zu behalten.' : undefined}>
                  <input type={secretFields.includes(k) ? 'password' : 'text'} autoComplete="off" value={f.dns[k] ?? ''} onChange={(e) => setDns(k, e.target.value)}
                    placeholder={stored ? '••••••••' : k === 'region' ? 'eu-central-1' : ''} />
                </Field>
              );
            })}
          </div>}
        </>}
        {f.tls === 'Own' && <>
          <p className="small muted">Zertifikat als PEM (mit Zwischenzertifikaten) und privater Schlüssel als PEM ohne Passwort. Wildcard-Zertifikate (*.firma.de) gehen auch. Die Plattform prüft, ob beide zusammenpassen und für den DNS-Namen gelten.
            {cur.ownCertificatePresent && ' Ohne neue Dateien bleibt das vorhandene Zertifikat.'}</p>
          <div className="grid-2">
            <Field label="Zertifikat (.crt, .pem)"><input type="file" accept=".crt,.pem,.cer" onChange={async (e) => setCert(e.target.files?.[0] ? await readFile(e.target.files[0]) : undefined)} /></Field>
            <Field label="Privater Schlüssel (.key, .pem)"><input type="file" accept=".key,.pem" onChange={async (e) => setKey(e.target.files?.[0] ? await readFile(e.target.files[0]) : undefined)} /></Field>
          </div>
          {cert && !cert.includes('BEGIN CERTIFICATE') && <Info tone="error">Die Zertifikatsdatei ist kein PEM. Aus einer .pfx z. B. mit <code>openssl pkcs12 -in ylm.pfx -nokeys -out ylm.crt</code> und <code>-nocerts -nodes -out ylm.key</code>.</Info>}
        </>}
      </>}

      {f.mode === 'Proxy' && <>
        <div className="grid-2">
          <Field label="Adresse für Benutzer" hint="Die https-Adresse, unter der der Proxy die Plattform veröffentlicht. Diese Adresse öffnen Browser und Agents.">
            <input value={f.publicUrl ?? ''} onChange={(e) => set({ publicUrl: e.target.value.trim() })} placeholder="https://ylm.firma.de" />
          </Field>
          <Field label="Lauscht auf (IP)" hint="Wo die Plattform Anfragen vom Proxy annimmt: 127.0.0.1, wenn der Proxy auf diesem Server läuft, sonst die interne IP dieses Servers (der Proxy muss sie erreichen).">
            <input value={f.bindAddress ?? ''} onChange={(e) => set({ bindAddress: e.target.value.trim() })} placeholder="127.0.0.1" />
          </Field>
          <Field label="Port" hint="An diesen Port leitet der Proxy weiter (Standard 8080)."><input type="number" min={1} max={65535} value={f.port} onChange={(e) => set({ port: Number(e.target.value) })} /></Field>
        </div>
      </>}

      {f.mode === 'Local' && <>
        <Field label="Port"><input type="number" min={1} max={65535} value={f.port} onChange={(e) => set({ port: Number(e.target.value) })} /></Field>
      </>}

      {newUrl && <p>Neue Adresse: <strong className="mono">{newUrl}</strong>{!addressChanged && <span className="muted small"> (unverändert)</span>}</p>}
      {addressChanged && f.mode === 'Proxy' && cur.mode === 'Https' && <Info tone="warn">Der mitgelieferte Caddy wird beendet. Richten Sie den Proxy vorher ein, sonst ist die Plattform danach nicht erreichbar.</Info>}
      {hostChanged && info.localPasskeys > 0 && <Info tone="warn">Passkeys lokaler Konten gelten nur für den bisherigen Namen ({info.localPasskeys} vorhanden). Die Konten melden sich danach mit Passwort + TOTP oder einem Notfallcode an und richten einen neuen Passkey ein. Entra-Anmeldung ist nicht betroffen.</Info>}
      {addressChanged && info.agents > 0 && <Info>{f.mode === 'Https' && keepOld
        ? `Die ${info.agents} Agents ziehen automatisch zur neuen Adresse um, solange die alte erreichbar bleibt.`
        : `Die ${info.agents} Agents ziehen automatisch um, wenn sie die neue Adresse erreichen und die alte bis dahin noch antwortet. Sonst einmal neu koppeln.`}</Info>}
      {needsEntra && <EntraStep newUrl={newUrl!} done={entraDone} onDone={() => setEntraDone(true)} info={info} />}

      <ErrorBox error={error} />
      <div className="actions mt">
        <button className="primary" disabled={busy || (needsEntra && !entraDone) || !newUrl} onClick={save}>{busy ? <Spinner label="Wird geprüft …" /> : 'Übernehmen'}</button>
        <button className="ghost" onClick={onCancel}>Abbrechen</button>
      </div>
    </div>
  );
}

/** Neue Redirect-URIs in der App-Registrierung, bevor die Adresse wechselt (sonst klappt die Entra-Anmeldung nicht). */
function EntraStep({ newUrl, done, onDone, info }: { newUrl: string; done: boolean; onDone: () => void; info: HostingInfo }) {
  const { busy, error, run } = useAction();
  const [signIn, setSignIn] = useState<{ url: string }>();
  const [pasted, setPasted] = useState('');
  const [manual, setManual] = useState(false);
  const uris = useMemo(() => [newUrl + '/signin-oidc', newUrl + '/api/tenants/consent-callback'], [newUrl]);
  useEffect(() => { setSignIn(undefined); setPasted(''); }, [newUrl]);
  const portal = info.entra.clientId ? `https://entra.microsoft.com/#view/Microsoft_AAD_RegisteredApps/ApplicationMenuBlade/~/Authentication/appId/${info.entra.clientId}` : undefined;

  const start = () => run(async () => {
    const r = await api.post<{ url: string }>('/api/platform/hosting/entra/start', { publicUrl: newUrl });
    setSignIn(r);
    window.open(r.url, 'ylm-signin', 'width=520,height=720');
  });
  const complete = () => run(async () => {
    await api.post('/api/platform/hosting/entra/complete', { publicUrl: newUrl, url: pasted });
    onDone();
  });

  if (done) return <Info tone="ok">Die neue Adresse ist in Entra eingetragen.</Info>;
  return (
    <Info tone="warn">
      <strong>Erst die neue Adresse in Entra eintragen.</strong> Sonst lehnt Microsoft die Anmeldung danach ab. Die alten Adressen bleiben eingetragen.
      <ul className="small">{uris.map((u) => <li key={u} className="mono">{u}</li>)}</ul>
      {!signIn && <div className="toolbar">
        <button className="primary" disabled={busy} onClick={start}>Mit Microsoft anmelden und eintragen</button>
        <button className="link" onClick={() => setManual((m) => !m)}>Selbst eintragen</button>
      </div>}
      {signIn && <>
        <p className="small">Im geöffneten Fenster mit einem Admin des Heim-Tenants anmelden (Anwendungsadministrator genügt). Microsoft leitet danach auf <code>http://localhost/?code=…</code> weiter; der Browser zeigt „Seite nicht erreichbar“. Bitte die komplette Adresse aus der Adresszeile hier einfügen:
          {' '}<a href={signIn.url} target="ylm-signin" rel="noreferrer">Fenster erneut öffnen</a></p>
        <Field label="Adresse aus der Adresszeile"><input value={pasted} onChange={(e) => setPasted(e.target.value.trim())} placeholder="http://localhost/?code=…&state=…" /></Field>
        <button className="primary" disabled={busy || !pasted.includes('code=')} onClick={complete}>{busy ? <Spinner label="Wird eingetragen …" /> : 'Eintragen'}</button>
      </>}
      {manual && <p className="small">Im Entra Admin Center unter App-Registrierungen, Plattform-App, „Authentifizierung“ beide Adressen als Umleitungs-URI (Web) hinzufügen{portal && <> (<a href={portal} target="_blank" rel="noreferrer">direkt öffnen</a>)</>}.
        {' '}<button className="link" onClick={onDone}>Habe ich eingetragen</button></p>}
      <ErrorBox error={error} />
    </Info>
  );
}

/** Nach dem Übernehmen: Updater-Protokoll verfolgen, danach zur (neuen) Adresse. */
function Applied({ url, onDone }: { url: string; onDone: () => void }) {
  const [log, setLog] = useState<string[]>([]);
  const [state, setState] = useState<'running' | 'done' | 'failed' | 'restarting'>('running');
  const sameOrigin = origin(url) === window.location.origin;
  useEffect(() => {
    if (state === 'done' || state === 'failed') return;
    const t = setInterval(async () => {
      try {
        const s = await api.get<{ status?: { state: string; version?: string; log?: string[] } }>('/api/platform/updates/status');
        if (s.status?.version !== 'hosting') return;
        if (s.status.log) setLog(s.status.log);
        if (s.status.state === 'done' || s.status.state === 'failed') setState(s.status.state);
      } catch {
        setState('restarting');
      }
    }, 2500);
    return () => clearInterval(t);
  }, [state]);
  return <>
    {state === 'running' && <Info><Spinner label="Wird übernommen: Konfiguration wird geprüft, Container starten neu …" /></Info>}
    {state === 'restarting' && <Info><Spinner label="Die Plattform startet neu …" />{!sameOrigin && <> Neue Adresse: <a href={url}>{url}</a></>}</Info>}
    {state === 'done' && <Info tone="ok">Übernommen. {sameOrigin ? <button className="link" onClick={onDone}>Neu laden</button> : <>Die Plattform ist jetzt unter <a href={url}>{url}</a> erreichbar.</>}</Info>}
    {state === 'failed' && <Info tone="error">Das hat nicht geklappt; der vorige Stand ist wiederhergestellt. Details im Protokoll. <button className="link" onClick={onDone}>Schließen</button></Info>}
    {log.length > 0 && <details open={state === 'failed'}><summary className="small muted">Protokoll</summary><pre className="script" style={{ maxHeight: 220, overflow: 'auto' }}>{log.join('\n')}</pre></details>}
  </>;
}
