import { useEffect, useRef, useState } from 'react';
import { api, ApiError } from '../api';
import { Badge, Card, download, ErrorBox, Field, Spinner, useAction, useApi } from '../ui';
import { useToast } from '../feedback';
import { Icon } from '../icons';
import { logoColors } from '../logoColors';
import { tr } from '../i18n';

interface BrandingValues { companyName?: string | null; accentColor?: string | null; contact?: string | null; footer?: string | null; language?: string | null; hasLogo: boolean; updatedAt?: string | null }
interface BrandingResponse {
  partner: BrandingValues;
  tenant: BrandingValues | null;
  effective: { companyName?: string | null; accentColor: string; contact?: string | null; footer?: string | null; hasLogo: boolean };
}

const DEFAULT_ACCENT = '#1f5fbf';
type Form = { companyName: string; accentColor: string; contact: string; footer: string; language: string };
const toForm = (v?: BrandingValues | null): Form => ({ companyName: v?.companyName ?? '', accentColor: v?.accentColor ?? '', contact: v?.contact ?? '', footer: v?.footer ?? '', language: v?.language ?? '' });

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
  const [suggestions, setSuggestions] = useState<string[] | null>(null);
  useEffect(() => { if (data) setForm(toForm(own)); }, [data]); // eslint-disable-line react-hooks/exhaustive-deps

  if (!data) return <Card title={tr("Erscheinungsbild der Dokumente")} className="mt"><ErrorBox error={error} />{!error && <Spinner />}</Card>;

  const partner = data.partner;
  const inherit = (v: string, p?: string | null) => (tenantId && !v.trim() ? p ?? '' : v.trim());
  const preview = {
    company: inherit(form.companyName, partner.companyName) || tr("Ihre Organisation"),
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
      language: form.language || null,
    });
    toast.ok(tr("Erscheinungsbild gespeichert"));
    reload();
  });
  const upload = (file: File) => run(async () => {
    const res = await fetch('/api/branding/logo' + qs, { method: 'PUT', credentials: 'same-origin', headers: { 'X-YLM-CSRF': '1', 'Content-Type': file.type || 'application/octet-stream' }, body: file });
    if (!res.ok) {
      const j = await res.json().catch(() => ({}));
      throw new ApiError(res.status, j.code ?? 'error', j.message ?? tr("Fehler {status}", { status: res.status }));
    }
    setLogoVersion(Date.now());
    toast.ok(tr("Logo hochgeladen"));
    reload();
    setSuggestions(null);
  });
  const removeLogo = () => run(async () => {
    await api.del('/api/branding/logo' + qs);
    setLogoVersion(Date.now());
    reload();
  });
  const logoUrl = `/api/branding/logo${qs}${qs ? '&' : '?'}effective=true&v=${logoVersion}`;
  const suggestFromLogo = () => run(async () => {
    const colors = await logoColors(logoUrl);
    if (colors.length === 0) throw new Error(tr("Im Logo ist keine deutliche Farbe zu finden (nur Weiß, Schwarz oder transparent)."));
    setSuggestions(colors);
    if (colors.length === 1) setForm((f) => ({ ...f, accentColor: colors[0] }));
  });
  const inheritedHint = (p?: string | null) => tenantId ? (p ? <>{tr("Leer lassen übernimmt den Partner-Wert „{p}“.", { p })}</> : tr("Leer lassen übernimmt die Vorgabe des Partners.")) : undefined;

  return (
    <Card
      title={<span id="branding">{tr("Erscheinungsbild der Dokumente")}</span>}
      className="mt"
      actions={<button onClick={() => download('/api/branding/preview.pdf' + qs)}><Icon name="download" size={15} />{tr("Muster-PIN-Brief")}</button>}
    >
      <p className="muted small" style={{ marginTop: 0 }}>
        {tenantId
          ? tr("Gilt für PIN-Briefe, Berichte und Exporte dieses Tenants. Was Sie hier nicht ausfüllen, kommt aus den globalen Einstellungen.")
          : tr("Firmenname, Farbe, Logo und Kontakt erscheinen auf PIN-Briefen, Berichten und PDF-Exporten. Jeder Tenant kann einzelne Werte überschreiben.")}
      </p>
      <div className="branding-grid">
        <div>
          <Field label={tr("Firmenname")} hint={inheritedHint(partner.companyName)}>
            <input value={form.companyName} onChange={set('companyName')} disabled={!editable} maxLength={120} placeholder={tenantId ? partner.companyName ?? '' : tr("z. B. Muster IT GmbH")} />
          </Field>
          <Field label={tr("Akzentfarbe")} hint={inheritedHint(partner.accentColor)}>
            <div className="color-field">
              <input type="color" value={preview.accent} onChange={set('accentColor')} disabled={!editable} aria-label={tr("Farbe wählen")} />
              <input value={form.accentColor} onChange={set('accentColor')} disabled={!editable} placeholder={tenantId ? partner.accentColor ?? DEFAULT_ACCENT : DEFAULT_ACCENT} maxLength={7} className="mono" />
              {form.accentColor && editable && <button type="button" className="link" onClick={() => setForm({ ...form, accentColor: '' })}>{tenantId ? tr("Partner-Farbe") : tr("Standard")}</button>}
              {editable && preview.hasLogo && <button type="button" className="ghost small" disabled={busy} onClick={suggestFromLogo}><Icon name="sparkle" size={14} />{tr("Farbe aus Logo")}</button>}
            </div>
            {suggestions && suggestions.length > 1 && (
              <div className="color-suggestions" role="group" aria-label={tr("Farben aus dem Logo")}>
                <span className="muted small">{tr("Aus dem Logo:")}</span>
                {suggestions.map((c) => (
                  <button key={c} type="button" className={'swatch' + (form.accentColor.toLowerCase() === c ? ' selected' : '')} style={{ ['--sw' as string]: c }}
                    title={c} aria-label={tr("Farbe {c} übernehmen", { c })} onClick={() => setForm({ ...form, accentColor: c })}>
                    <span className="swatch-dot" /><span className="mono small">{c}</span>
                  </button>
                ))}
              </div>
            )}
          </Field>
          <Field label={tr("Kontakt für Rückfragen")} hint={inheritedHint(partner.contact) ?? tr("Steht im PIN-Brief beim Hinweis auf gesperrte Keys und in der Fußzeile.")}>
            <input value={form.contact} onChange={set('contact')} disabled={!editable} maxLength={200} placeholder={tr("z. B. IT-Service · 0800 123 456 · it@firma.de")} />
          </Field>
          <Field label={tr("Fußzeile")} hint={inheritedHint(partner.footer) ?? tr("Ersetzt im PIN-Brief den Hinweis „Erstellt mit dem Yubikey Lifecycle Manager“.")}>
            <input value={form.footer} onChange={set('footer')} disabled={!editable} maxLength={200} placeholder={tr("z. B. Muster IT GmbH · Musterstraße 1 · 12345 Musterstadt")} />
          </Field>
          <Field label={tenantId ? tr("Sprache der PIN-Briefe") : tr("Sprache der PIN-Briefe, Mails und Webhooks")}
            hint={tenantId
              ? tr("Leer übernimmt die Vorgabe des Partners ({language}).", { language: languageName(partner.language) })
              : tr("Gilt für alle Tenants, die nichts anderes einstellen. Mail-Berichte, Mail-Alarme und Webhooks erscheinen immer in dieser Sprache, unabhängig von der Sprache der Oberfläche.")}>
            <select value={form.language} onChange={set('language')} disabled={!editable}>
              <option value="">{tenantId ? tr("Wie beim Partner") : tr("Deutsch (Vorgabe)")}</option>
              <option value="de">Deutsch</option>
              <option value="en">English</option>
            </select>
          </Field>
          <Field label={tr("Logo")} hint={tr("PNG oder JPEG, höchstens 1 MB. Am besten quer und mit transparentem Hintergrund.")}>
            <div className="logo-field">
              <div className="logo-box">
                {preview.hasLogo
                  ? <img src={logoUrl} alt={tr("Logo")} />
                  : <span className="muted small">{tr("Kein Logo")}</span>}
              </div>
              {editable && <>
                <input ref={fileRef} type="file" accept="image/png,image/jpeg" hidden onChange={(e) => { const f = e.target.files?.[0]; if (f) void upload(f); e.target.value = ''; }} />
                <button type="button" disabled={busy} onClick={() => fileRef.current?.click()}>{own?.hasLogo ? tr("Ersetzen") : tr("Hochladen")}</button>
                {own?.hasLogo && <button type="button" disabled={busy} onClick={removeLogo}>{tr("Entfernen")}</button>}
              </>}
              {tenantId && !own?.hasLogo && partner.hasLogo && <Badge>{tr("vom Partner")}</Badge>}
            </div>
          </Field>
          <ErrorBox error={actError} />
          {editable && (
            <div className="actions-row">
              <button className="primary" disabled={busy || !dirty} onClick={save}>{tr("Speichern")}</button>
              {dirty && <button type="button" onClick={() => { setForm(toForm(own)); setError(undefined); }}>{tr("Verwerfen")}</button>}
            </div>
          )}
        </div>
        <LetterPreview {...preview} logoUrl={preview.hasLogo ? logoUrl : undefined} />
      </div>
    </Card>
  );
}

/** Verkleinerte Vorschau des PIN-Briefs; folgt den Eingaben sofort. */
const languageName = (l?: string | null) => (l === 'en' ? 'English' : 'Deutsch');

function LetterPreview({ company, accent, contact, footer, logoUrl }: { company: string; accent: string; contact: string; footer: string; logoUrl?: string }) {
  return (
    <figure className="letter-preview" style={{ ['--brand' as string]: accent }} aria-label={tr("Vorschau PIN-Brief")}>
      <div className="lp-page">
        <div className="lp-head">
          <span className="lp-org">{company}</span>
          {logoUrl && <img src={logoUrl} alt="" />}
        </div>
        <div className="lp-title">{tr("Ihr YubiKey und Ihre PIN")}</div>
        <div className="lp-sub">{tr("Persönlich für Erika Mustermann")}</div>
        <div className="lp-bar" />
        <div className="lp-pin">
          <span className="lp-pin-label">{tr("Ihre persönliche PIN")}</span>
          <div className="lp-digits">{'482913'.split('').map((d, i) => <span key={i} style={{ animationDelay: i * 70 + 'ms' }}>{d}</span>)}</div>
        </div>
        <div className="lp-steps">{[1, 2, 3, 4].map((n) => <span key={n}><b>{n}</b></span>)}</div>
        <div className="lp-lines"><i /><i /><i style={{ width: '70%' }} /></div>
        {contact && <div className="lp-contact">{tr("Fragen: {contact}", { contact })}</div>}
        <div className="lp-foot">{footer || tr("Erstellt mit dem Yubikey Lifecycle Manager.")}</div>
      </div>
      <figcaption className="small muted">{tr("Vorschau, der echte Brief ist A4.")}</figcaption>
    </figure>
  );
}
