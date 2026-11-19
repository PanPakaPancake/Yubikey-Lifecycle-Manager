import { useEffect, useRef, useState } from 'react';
import { api, ApiError } from '../api';
import { Badge, Card, download, ErrorBox, Field, Spinner, useAction, useApi } from '../ui';
import { useToast } from '../feedback';
import { Icon } from '../icons';

interface BrandingValues { companyName?: string | null; accentColor?: string | null; contact?: string | null; footer?: string | null; hasLogo: boolean; updatedAt?: string | null }
interface BrandingResponse {
  partner: BrandingValues;
  tenant: BrandingValues | null;
  effective: { companyName?: string | null; accentColor: string; contact?: string | null; footer?: string | null; hasLogo: boolean };
}

const DEFAULT_ACCENT = '#1f5fbf';
type Form = { companyName: string; accentColor: string; contact: string; footer: string };
const toForm = (v?: BrandingValues | null): Form => ({ companyName: v?.companyName ?? '', accentColor: v?.accentColor ?? '', contact: v?.contact ?? '', footer: v?.footer ?? '' });

/**
 * Whitelabeling der PIN-Briefe, Berichte und Exporte.
 * Ohne tenantId: Vorgabe des Partners. Mit tenantId: leere Felder übernehmen den Wert des Partners.
 */
export function BrandingCard({ tenantId, editable }: { tenantId?: string; editable: boolean }) {
  const qs = tenantId ? '?tenantId=' + tenantId : '';
  const { data, error, reload } = useApi<BrandingResponse>('/api/branding' + qs, [tenantId]);
  const own = tenantId ? data?.tenant : data?.partner;
  const [form, setForm] = useState<Form>(toForm());
  const [logoVersion, setLogoVersion] = useState(() => Date.now());
  const fileRef = useRef<HTMLInputElement>(null);
  const { busy, error: actError, run, setError } = useAction();
  const toast = useToast();
  useEffect(() => { if (data) setForm(toForm(own)); }, [data]); // eslint-disable-line react-hooks/exhaustive-deps

  if (!data) return <Card title="Erscheinungsbild der Dokumente" className="mt"><ErrorBox error={error} />{!error && <Spinner />}</Card>;

  const partner = data.partner;
  const inherit = (v: string, p?: string | null) => (tenantId && !v.trim() ? p ?? '' : v.trim());
  const preview = {
    company: inherit(form.companyName, partner.companyName) || 'Ihre Organisation',
    accent: /^#[0-9a-f]{6}$/i.test(inherit(form.accentColor, partner.accentColor)) ? inherit(form.accentColor, partner.accentColor) : DEFAULT_ACCENT,
    contact: inherit(form.contact, partner.contact),
    footer: inherit(form.footer, partner.footer),
    hasLogo: own?.hasLogo || (!!tenantId && partner.hasLogo),
  };
  const dirty = JSON.stringify(form) !== JSON.stringify(toForm(own));
  const set = (k: keyof Form) => (e: { target: { value: string } }) => setForm({ ...form, [k]: e.target.value });

  const save = () => run(async () => {
    await api.put('/api/branding' + qs, {
      companyName: form.companyName || null, accentColor: form.accentColor || null, contact: form.contact || null, footer: form.footer || null,
    });
    toast.ok('Erscheinungsbild gespeichert');
    reload();
  });
  const upload = (file: File) => run(async () => {
    const res = await fetch('/api/branding/logo' + qs, { method: 'PUT', credentials: 'same-origin', headers: { 'X-YLM-CSRF': '1', 'Content-Type': file.type || 'application/octet-stream' }, body: file });
    if (!res.ok) {
      const j = await res.json().catch(() => ({}));
      throw new ApiError(res.status, j.code ?? 'error', j.message ?? `Fehler ${res.status}`);
    }
    setLogoVersion(Date.now());
    toast.ok('Logo hochgeladen');
    reload();
  });
  const removeLogo = () => run(async () => {
    await api.del('/api/branding/logo' + qs);
    setLogoVersion(Date.now());
    reload();
  });
  const inheritedHint = (p?: string | null) => tenantId ? (p ? <>Leer lassen übernimmt den Partner-Wert „{p}“.</> : 'Leer lassen übernimmt die Vorgabe des Partners.') : undefined;

  return (
    <Card
      title={<span id="branding">Erscheinungsbild der Dokumente</span>}
      className="mt"
      actions={<button onClick={() => download('/api/branding/preview.pdf' + qs)}><Icon name="download" size={15} />Muster-PIN-Brief</button>}
    >
      <p className="muted small" style={{ marginTop: 0 }}>
        {tenantId
          ? 'Gilt für PIN-Briefe, Berichte und Exporte dieses Tenants. Was Sie hier nicht ausfüllen, kommt aus den globalen Einstellungen.'
          : 'Firmenname, Farbe, Logo und Kontakt erscheinen auf PIN-Briefen, Berichten und PDF-Exporten. Jeder Tenant kann einzelne Werte überschreiben.'}
      </p>
      <div className="branding-grid">
        <div>
          <Field label="Firmenname" hint={inheritedHint(partner.companyName)}>
            <input value={form.companyName} onChange={set('companyName')} disabled={!editable} maxLength={120} placeholder={tenantId ? partner.companyName ?? '' : 'z. B. Muster IT GmbH'} />
          </Field>
          <Field label="Akzentfarbe" hint={inheritedHint(partner.accentColor)}>
            <div className="color-field">
              <input type="color" value={preview.accent} onChange={set('accentColor')} disabled={!editable} aria-label="Farbe wählen" />
              <input value={form.accentColor} onChange={set('accentColor')} disabled={!editable} placeholder={tenantId ? partner.accentColor ?? DEFAULT_ACCENT : DEFAULT_ACCENT} maxLength={7} className="mono" />
              {form.accentColor && editable && <button type="button" className="link" onClick={() => setForm({ ...form, accentColor: '' })}>{tenantId ? 'Partner-Farbe' : 'Standard'}</button>}
            </div>
          </Field>
          <Field label="Kontakt für Rückfragen" hint={inheritedHint(partner.contact) ?? 'Steht im PIN-Brief beim Hinweis auf gesperrte Keys und in der Fußzeile.'}>
            <input value={form.contact} onChange={set('contact')} disabled={!editable} maxLength={200} placeholder="z. B. IT-Service · 0800 123 456 · it@firma.de" />
          </Field>
          <Field label="Fußzeile" hint={inheritedHint(partner.footer) ?? 'Ersetzt im PIN-Brief den Hinweis „Erstellt mit dem Yubikey Lifecycle Manager“.'}>
            <input value={form.footer} onChange={set('footer')} disabled={!editable} maxLength={200} placeholder="z. B. Muster IT GmbH · Musterstraße 1 · 12345 Musterstadt" />
          </Field>
          <Field label="Logo" hint="PNG oder JPEG, höchstens 1 MB. Am besten quer und mit transparentem Hintergrund.">
            <div className="logo-field">
              <div className="logo-box">
                {preview.hasLogo
                  ? <img src={`/api/branding/logo${qs}${qs ? '&' : '?'}effective=true&v=${logoVersion}`} alt="Logo" />
                  : <span className="muted small">Kein Logo</span>}
              </div>
              {editable && <>
                <input ref={fileRef} type="file" accept="image/png,image/jpeg" hidden onChange={(e) => { const f = e.target.files?.[0]; if (f) void upload(f); e.target.value = ''; }} />
                <button type="button" disabled={busy} onClick={() => fileRef.current?.click()}>{own?.hasLogo ? 'Ersetzen' : 'Hochladen'}</button>
                {own?.hasLogo && <button type="button" disabled={busy} onClick={removeLogo}>Entfernen</button>}
              </>}
              {tenantId && !own?.hasLogo && partner.hasLogo && <Badge>vom Partner</Badge>}
            </div>
          </Field>
          <ErrorBox error={actError} />
          {editable && (
            <div className="actions-row">
              <button className="primary" disabled={busy || !dirty} onClick={save}>Speichern</button>
              {dirty && <button type="button" onClick={() => { setForm(toForm(own)); setError(undefined); }}>Verwerfen</button>}
            </div>
          )}
        </div>
        <LetterPreview {...preview} logoUrl={preview.hasLogo ? `/api/branding/logo${qs}${qs ? '&' : '?'}effective=true&v=${logoVersion}` : undefined} />
      </div>
    </Card>
  );
}

/** Verkleinerte Vorschau des PIN-Briefs; folgt den Eingaben sofort. */
function LetterPreview({ company, accent, contact, footer, logoUrl }: { company: string; accent: string; contact: string; footer: string; logoUrl?: string }) {
  return (
    <figure className="letter-preview" style={{ ['--brand' as string]: accent }} aria-label="Vorschau PIN-Brief">
      <div className="lp-page">
        <div className="lp-bar" />
        <div className="lp-head">
          <span className="lp-org">{company}</span>
          {logoUrl && <img src={logoUrl} alt="" />}
        </div>
        <div className="lp-title">Ihr YubiKey und Ihre PIN</div>
        <div className="lp-sub">Persönlich für Erika Mustermann</div>
        <div className="lp-pin">
          <span className="lp-pin-label">Ihre persönliche PIN</span>
          <div className="lp-digits">{'482913'.split('').map((d, i) => <span key={i} style={{ animationDelay: i * 70 + 'ms' }}>{d}</span>)}</div>
        </div>
        <div className="lp-steps">{[1, 2, 3, 4].map((n) => <span key={n}><b>{n}</b></span>)}</div>
        <div className="lp-lines"><i /><i /><i style={{ width: '70%' }} /></div>
        {contact && <div className="lp-contact">Fragen: {contact}</div>}
        <div className="lp-foot">{footer || 'Erstellt mit dem Yubikey Lifecycle Manager.'}</div>
      </div>
      <figcaption className="small muted">Vorschau, der echte Brief ist A4.</figcaption>
    </figure>
  );
}
