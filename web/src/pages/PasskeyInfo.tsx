import { useEffect, useRef, useState, type ReactNode } from 'react';
import { Link, useLocation, useNavigate } from 'react-router';
import { Icon, type IconName } from '../icons';
import { ThemeToggle } from '../theme';
import { LanguageToggle } from '../language';
import '../passkey.css';
import { tr } from '../i18n';

// Öffentliche Passkey-Infoseite (/passkey) mit zwei Ansichten: Endbenutzer (Standard, mit Anleitung) und Techniker (?ansicht=techniker).
// Ohne Anmeldung erreichbar, damit der PIN-Brief darauf verlinken kann. ?t=<Tenant-Id> wählt das Erscheinungsbild des Tenants.

interface PublicBrand { companyName?: string | null; accentColor?: string | null; contact?: string | null; hasLogo: boolean }

function useBrand(tenant: string | null) {
  const [brand, setBrand] = useState<PublicBrand | null>(null);
  useEffect(() => {
    const q = tenant ? '?t=' + encodeURIComponent(tenant) : '';
    void fetch('/api/public/branding' + q).then((r) => (r.ok ? r.json() : null)).then(setBrand).catch(() => setBrand(null));
  }, [tenant]);
  return brand;
}

/** Abschnitte blenden beim Scrollen weich ein. */
function useReveal(key: string) {
  const ref = useRef<HTMLDivElement>(null);
  useEffect(() => {
    const root = ref.current;
    if (!root) return;
    const items = Array.from(root.querySelectorAll('.pk-reveal'));
    if (!('IntersectionObserver' in window)) { items.forEach((el) => el.classList.add('in')); return; }
    const io = new IntersectionObserver((entries) => {
      for (const e of entries) if (e.isIntersecting) { e.target.classList.add('in'); io.unobserve(e.target); }
    }, { rootMargin: '0px 0px -8% 0px' });
    items.forEach((el) => io.observe(el));
    return () => io.disconnect();
  }, [key]);
  return ref;
}

/** Spielt Schritte automatisch nacheinander ab; pausiert, wenn nicht sichtbar oder „weniger Bewegung“ gewünscht ist. */
function useStepper(count: number, ms: number) {
  const reduced = typeof window !== 'undefined' && window.matchMedia?.('(prefers-reduced-motion: reduce)').matches;
  const [step, setStep] = useState(reduced ? count - 1 : 0);
  const [playing, setPlaying] = useState(!reduced);
  const [visible, setVisible] = useState(false);
  const ref = useRef<HTMLDivElement>(null);
  useEffect(() => {
    const el = ref.current;
    if (!el || !('IntersectionObserver' in window)) { setVisible(true); return; }
    const io = new IntersectionObserver(([e]) => setVisible(e.isIntersecting), { threshold: 0.35 });
    io.observe(el);
    return () => io.disconnect();
  }, []);
  useEffect(() => {
    if (!playing || !visible) return;
    const t = setTimeout(() => setStep((s) => (s + 1) % count), step === count - 1 ? ms * 1.6 : ms);
    return () => clearTimeout(t);
  }, [playing, visible, step, count, ms]);
  const go = (s: number) => { setStep(s); setPlaying(false); };
  return { ref, step, playing, setPlaying, go };
}

type View = 'endbenutzer' | 'techniker';

/**
 * embedded: angemeldet im App-Rahmen (Menü links, gewähltes Design), ohne eigene Kopfzeile.
 * Öffentlich (PIN-Brief-Link, ohne Anmeldung) bleibt die Seite eigenständig.
 */
export default function PasskeyInfoPage({ authenticated, embedded = false }: { authenticated: boolean; embedded?: boolean }) {
  const { pathname, search } = useLocation();
  const navigate = useNavigate();
  // Früher zwei Seiten: /passkey/anleitung landet jetzt in der Endbenutzer-Ansicht.
  useEffect(() => { if (pathname !== '/passkey') navigate('/passkey' + search, { replace: true }); }, [pathname, search, navigate]);
  const params = new URLSearchParams(search);
  const tenant = params.get('t');
  const view: View = params.get('ansicht') === 'techniker' ? 'techniker' : 'endbenutzer';
  const brand = useBrand(tenant);
  const ref = useReveal(view);
  const tech = view === 'techniker';

  const switchTo = (v: View) => {
    const q = new URLSearchParams(search);
    if (v === 'techniker') q.set('ansicht', 'techniker'); else q.delete('ansicht');
    const s = q.toString();
    navigate('/passkey' + (s ? '?' + s : ''), { replace: true });
  };

  useEffect(() => {
    document.title = (tech ? tr("Passkeys für Techniker") : tr("Was ist ein Passkey?")) + (brand?.companyName ? ' · ' + brand.companyName : '');
  }, [tech, brand?.companyName]);
  // Sprungmarken wie /passkey#verleihen (z. B. aus dem PIN-Brief) öffnen direkt den Abschnitt.
  useEffect(() => {
    const id = window.location.hash.slice(1);
    const target = id ? document.getElementById(decodeURIComponent(id)) : null;
    if (target) setTimeout(() => target.scrollIntoView({ block: 'start' }), 60); else window.scrollTo(0, 0);
  }, [view]);
  const [copied, setCopied] = useState(false);
  const publicUrl = window.location.origin + '/passkey' + (tech ? '?ansicht=techniker' : '');
  const copyLink = () => { void navigator.clipboard?.writeText(publicUrl).then(() => { setCopied(true); setTimeout(() => setCopied(false), 1800); }); };

  const keep = tenant ? '?t=' + encodeURIComponent(tenant) : '';
  const style = brand?.accentColor && /^#[0-9a-f]{6}$/i.test(brand.accentColor) ? ({ '--pk-brand': brand.accentColor } as React.CSSProperties) : undefined;

  const tabs = (
    <div className="pk-tabs" role="tablist" aria-label={tr("Ansicht")}>
      <button type="button" role="tab" aria-selected={!tech} className={tech ? '' : 'active'} onClick={() => switchTo('endbenutzer')}><Icon name="user" size={15} />{tr("Endbenutzer")}</button>
      <button type="button" role="tab" aria-selected={tech} className={tech ? 'active' : ''} onClick={() => switchTo('techniker')}><Icon name="settings" size={15} />{tr("Techniker")}</button>
    </div>
  );

  if (embedded) return (
    <div className="pk pk-embedded" ref={ref}>
      <header className="page-header pk-embedded-head">
        <div>
          <h1>{tr("Passkeys erklärt")}</h1>
          <p className="muted">{tr("Dieselbe Seite, die Endbenutzer über den Link im PIN-Brief sehen. Die Techniker-Ansicht erklärt Kryptografie und Entra-Anbindung.")}</p>
        </div>
        <div className="actions">
          {tabs}
          <button type="button" onClick={copyLink} title={publicUrl}><Icon name={copied ? 'check' : 'copy'} size={16} />{copied ? tr("Kopiert") : tr("Öffentlichen Link kopieren")}</button>
        </div>
      </header>
      <main className="pk-main" key={view}>
        {tech ? <Technical onSwitch={() => switchTo('endbenutzer')} /> : <EndUser brand={brand} onSwitch={() => switchTo('techniker')} />}
      </main>
    </div>
  );

  return (
    <div className={'pk' + (style ? ' branded' : '')} style={style} ref={ref}>
      <header className="pk-top">
        <div className="pk-top-inner">
          <div className="pk-brand">
            {brand?.hasLogo
              ? <img src={'/api/public/branding/logo' + keep} alt={brand.companyName ?? tr("Logo")} />
              : <KeyArt size={34} />}
            <span>{brand?.companyName || tr("YubiKey & Passkey")}</span>
          </div>
          {tabs}
          <div className="pk-top-actions">
            <ThemeToggle />
            <LanguageToggle saveToAccount={authenticated} />
            {authenticated && <Link className="btn small" to="/">{tr("Zur Plattform")}</Link>}
          </div>
        </div>
      </header>
      <main className="pk-main" key={view}>
        {tech ? <Technical onSwitch={() => switchTo('endbenutzer')} /> : <EndUser brand={brand} onSwitch={() => switchTo('techniker')} />}
      </main>
      <footer className="pk-foot">
        {brand?.contact && <p>{tr("Fragen zu Ihrem YubiKey? Ihre IT hilft weiter:")} <strong>{brand.contact}</strong></p>}
        <p className="muted small">{tr("Passkeys beruhen auf dem offenen Standard FIDO2/WebAuthn. YubiKey ist eine Marke von Yubico.")}</p>
      </footer>
    </div>
  );
}


/* ---------- Bausteine ---------- */

function Hero({ eyebrow, title, children, art }: { eyebrow: string; title: string; children: ReactNode; art: ReactNode }) {
  return (
    <section className="pk-hero">
      <div>
        <div className="pk-eyebrow">{eyebrow}</div>
        <h1>{title}</h1>
        <p className="pk-lead">{children}</p>
      </div>
      <div className="pk-hero-art" aria-hidden>{art}</div>
    </section>
  );
}

function Toc({ items }: { items: { id: string; label: string; icon: IconName }[] }) {
  return (
    <nav className="pk-toc pk-reveal" aria-label={tr("Inhalt")}>
      {items.map((it, i) => (
        <a key={it.id} href={'#' + it.id} onClick={(e) => { e.preventDefault(); document.getElementById(it.id)?.scrollIntoView({ behavior: 'smooth', block: 'start' }); }}>
          <span className="pk-num">{i + 1}</span><Icon name={it.icon} size={16} /><span>{it.label}</span>
        </a>
      ))}
    </nav>
  );
}

function Section({ id, n, title, children }: { id: string; n: number; title: string; children: ReactNode }) {
  return (
    <section id={id} className="pk-section pk-reveal">
      <h2><span className="pk-num big">{n}</span>{title}</h2>
      {children}
    </section>
  );
}

function Note({ kind = 'info', title, children }: { kind?: 'info' | 'ok' | 'warn'; title: string; children: ReactNode }) {
  return (
    <div className={'pk-note ' + kind}>
      <Icon name={kind === 'warn' ? 'alert' : kind === 'ok' ? 'check' : 'info'} size={20} />
      <div><strong>{title}</strong><div>{children}</div></div>
    </div>
  );
}

/** Stilisierter YubiKey: dunkler Körper, goldene Berührungsfläche, USB-Stecker. */
export function KeyArt({ size = 120, glow = false }: { size?: number; glow?: boolean }) {
  return (
    <svg className={'pk-keyart' + (glow ? ' glow' : '')} width={size} height={size * 0.42} viewBox="0 0 100 42" aria-hidden>
      <g>
        <rect x="70" y="11" width="28" height="20" rx="2" fill="#b8bec7" />
        <rect x="74" y="15" width="6" height="4" fill="#8a919c" /><rect x="86" y="15" width="6" height="4" fill="#8a919c" />
        <rect x="2" y="2" width="72" height="38" rx="15" fill="#2b2f36" />
        <circle cx="13" cy="21" r="4" fill="none" stroke="#555b66" strokeWidth="1.5" />
        <circle className="pk-disc-glow" cx="40" cy="21" r="12" fill="none" stroke="var(--gold)" strokeWidth="2" />
        <circle className="pk-disc" cx="40" cy="21" r="9" fill="var(--gold)" />
        <path d="M36 18.5 40 16l4 2.5v4.5L40 26l-4-3Z" fill="none" stroke="#9c7a1c" strokeWidth="1.2" />
      </g>
    </svg>
  );
}

function Cloud() {
  return (
    <svg width="64" height="44" viewBox="0 0 64 44" aria-hidden>
      <path d="M18 40h30a12 12 0 0 0 1-24A16 16 0 0 0 19 13 13.5 13.5 0 0 0 18 40Z" fill="var(--pk-accent-soft)" stroke="var(--pk-accent)" strokeWidth="2" />
      <g transform="translate(25 19)"><rect width="6" height="6" fill="#f25022" /><rect x="7" width="6" height="6" fill="#7fba00" /><rect y="7" width="6" height="6" fill="#00a4ef" /><rect x="7" y="7" width="6" height="6" fill="#ffb900" /></g>
    </svg>
  );
}

function Laptop({ children }: { children?: ReactNode }) {
  return (
    <div className="pk-laptop">
      <div className="pk-screen">{children}</div>
      <div className="pk-laptop-base" />
    </div>
  );
}

/* ---------- Animation: Anmeldung mit Passkey ---------- */

const flowSteps = [
  { title: tr("Anmelden"), text: tr("Sie öffnen die Microsoft-Anmeldung und wählen „Sicherheitsschlüssel“."), token: tr("Anmeldung, bitte") },
  { title: tr("Aufgabe"), text: tr("Microsoft Entra ID schickt eine zufällige Aufgabe. Ihr Browser reicht sie samt Adresse der Seite an den YubiKey weiter."), token: tr("Aufgabe 7f3a…") },
  { title: tr("PIN und Berührung"), text: tr("Sie geben Ihre PIN ein und berühren die goldene Fläche. Damit ist klar: Sie sind es, und Sie sitzen wirklich vor dem Gerät."), token: '' },
  { title: tr("Unterschrift"), text: tr("Der YubiKey unterschreibt die Aufgabe mit seinem geheimen Schlüssel. Der geheime Schlüssel selbst bleibt im Key."), token: tr("Unterschrift ✓") },
  { title: tr("Geprüft"), text: tr("Microsoft prüft die Unterschrift mit dem öffentlichen Prüfschlüssel. Passt sie, sind Sie angemeldet."), token: '' },
];

type FlowStep = { title: string; text: string; token: string };

function LoginFlow({ steps, tech = false }: { steps: FlowStep[]; tech?: boolean }) {
  const { ref, step, playing, setPlaying, go } = useStepper(steps.length, tech ? 4200 : 2800);
  // Position des „Umschlags“: 0 = YubiKey, 50 = Laptop, 100 = Microsoft.
  const pos = [55, 8, 8, 92, 92][step];
  const from = [8, 92, 8, 8, 92][step];
  return (
    <div className="pk-flow" ref={ref}>
      <div className="pk-flow-stage" data-step={step}>
        <div className="pk-actor key">
          <div className={'pk-actor-art' + (step === 2 ? ' active' : '')}><KeyArt size={96} glow={step === 2} /></div>
          <span>{tr("Ihr YubiKey")}</span>
          <small>{tech ? tr("privater Schlüssel, Secure Element") : tr("geheimer Schlüssel")}</small>
        </div>
        <div className="pk-actor laptop">
          <Laptop>
            {step < 2 && <div className="pk-mini-login"><b>{tr("Anmelden")}</b><i /><em><KeyArt size={22} /> {tr("Sicherheitsschlüssel")}</em></div>}
            {step === 2 && <div className="pk-mini-login"><b>{tr("PIN")}</b><span className="pk-dots"><i /><i /><i /><i /><i /><i /></span><em className="touch">{tr("Key berühren …")}</em></div>}
            {step === 3 && <div className="pk-mini-login"><b>{tr("Bitte warten …")}</b><span className="pk-spin" /></div>}
            {step === 4 && <div className="pk-mini-login ok"><span className="pk-check"><Icon name="check" size={26} /></span><b>{tr("Angemeldet")}</b></div>}
          </Laptop>
          <span>{tech ? tr("Browser (WebAuthn)") : tr("Ihr Browser")}</span>
          <small>{tr("login.microsoft.com")}</small>
        </div>
        <div className="pk-actor cloud">
          <div className={'pk-actor-art' + (step === 4 ? ' active' : '')}><Cloud /></div>
          <span>{tr("Microsoft Entra ID")}</span>
          <small>{tech ? tr("Relying Party, Public Key") : tr("öffentlicher Prüfschlüssel")}</small>
        </div>
        <div className="pk-track" aria-hidden><div className="pk-track-line" /></div>
        {steps[step].token && (
          <div key={step} className="pk-token" style={{ ['--from' as string]: from + '%', ['--to' as string]: pos + '%' }}>
            {steps[step].token}
          </div>
        )}
      </div>
      <ol className="pk-flow-steps">
        {steps.map((s, i) => (
          <li key={s.title} className={i === step ? 'active' : i < step ? 'done' : ''}>
            <button type="button" onClick={() => go(i)} aria-current={i === step}>
              <span className="pk-num">{i < step ? <Icon name="check" size={13} /> : i + 1}</span>{s.title}
            </button>
          </li>
        ))}
      </ol>
      <div className="pk-flow-caption" aria-live="polite" key={'c' + step}>{steps[step].text}</div>
      <button type="button" className="ghost small pk-play" onClick={() => setPlaying(!playing)}>
        {playing ? tr("❚❚ Anhalten") : tr("▶ Abspielen")}
      </button>
    </div>
  );
}

/* ---------- Animation: echte und gefälschte Seite ---------- */

function PhishingDemo() {
  const { ref, step, playing, setPlaying, go } = useStepper(2, 3600);
  const fake = step === 1;
  return (
    <div className={'pk-phish' + (fake ? ' fake' : '')} ref={ref}>
      <div className="pk-phish-switch" role="tablist">
        <button type="button" role="tab" aria-selected={!fake} className={!fake ? 'active' : ''} onClick={() => go(0)}>{tr("Echte Seite")}</button>
        <button type="button" role="tab" aria-selected={fake} className={fake ? 'active' : ''} onClick={() => go(1)}>{tr("Gefälschte Seite")}</button>
        <button type="button" className="ghost small" onClick={() => setPlaying(!playing)} aria-label={playing ? tr("Anhalten") : tr("Abspielen")}>{playing ? '❚❚' : '▶'}</button>
      </div>
      <div className="pk-phish-stage" key={step}>
        <div className="pk-browser">
          <div className="pk-urlbar"><Icon name={fake ? 'alert' : 'shield'} size={14} /><span>{fake ? 'https://login-micros0ft-sicherheit.com' : 'https://login.microsoft.com'}</span></div>
          <div className="pk-browser-body">
            <div className="pk-ms"><Cloud /><b>{tr("Anmelden")}</b></div>
            <div className="pk-ask">{tr("„Bitte unterschreiben für")} <code>{fake ? 'login-micros0ft-sicherheit.com' : 'login.microsoft.com'}</code>“</div>
          </div>
        </div>
        <div className="pk-phish-arrow" aria-hidden><span /></div>
        <div className="pk-phish-key">
          <KeyArt size={90} glow={!fake} />
          <div className={'pk-verdict ' + (fake ? 'no' : 'yes')}>
            {fake
              ? <><Icon name="x" size={18} />{tr("Kein Passkey für diese Adresse. Der Key antwortet nicht.")}</>
              : <><Icon name="check" size={18} />{tr("Passkey für login.microsoft.com gefunden. Unterschrift wird erstellt.")}</>}
          </div>
        </div>
      </div>
      <p className="muted small">{tr("Die Adresse prüft nicht der Mensch, sondern der Browser. Eine täuschend echte Kopie der Seite bekommt deshalb nichts, womit sie sich anmelden könnte.")}</p>
    </div>
  );
}

/* ---------- Animation: Schlüsselpaar ---------- */

function KeyPair() {
  return (
    <div className="pk-pair pk-reveal">
      <div className="pk-pair-side">
        <div className="pk-pair-icon secret"><Icon name="key" size={28} /></div>
        <strong>{tr("Geheimer Schlüssel")}</strong>
        <span>{tr("liegt fest im YubiKey, lässt sich nicht auslesen und nicht kopieren")}</span>
      </div>
      <div className="pk-pair-link" aria-hidden><span /><span /><span /></div>
      <div className="pk-pair-side">
        <div className="pk-pair-icon public"><Icon name="shield" size={28} /></div>
        <strong>{tr("Öffentlicher Prüfschlüssel")}</strong>
        <span>{tr("liegt bei Microsoft Entra ID, kann nur prüfen, nicht unterschreiben")}</span>
      </div>
    </div>
  );
}

/* ---------- Ansicht Endbenutzer ---------- */

function EndUser({ brand, onSwitch }: { brand: PublicBrand | null; onSwitch: () => void }) {
  const toc = [
    { id: 'was', label: tr("Was ist ein Passkey?"), icon: 'info' as const },
    { id: 'ablauf', label: tr("So läuft die Anmeldung ab"), icon: 'monitor' as const },
    { id: 'sicher', label: tr("Warum das sicherer ist"), icon: 'shield' as const },
    { id: 'key', label: tr("Ihr YubiKey"), icon: 'key' as const },
    { id: 'anmelden', label: tr("Am PC anmelden"), icon: 'monitor' as const },
    { id: 'erste', label: tr("Erste Anmeldung und neue PIN"), icon: 'user' as const },
    { id: 'handy', label: tr("Am Handy anmelden"), icon: 'share' as const },
    { id: 'pin', label: tr("PIN selbst ändern"), icon: 'settings' as const },
    { id: 'regeln', label: tr("Sicher aufbewahren"), icon: 'shield' as const },
    { id: 'verleihen', label: tr("Warum man den Key nicht verleiht"), icon: 'users' as const },
    { id: 'hilfe', label: tr("Es klappt nicht"), icon: 'alert' as const },
  ];
  return (
    <>
      <Hero eyebrow={tr("Einfach erklärt")} title={tr("Was ist ein Passkey?")} art={<div className="pk-float"><KeyArt size={220} glow /></div>}>
        {tr("Ein Passkey ersetzt das Passwort. Statt etwas einzutippen, das man sich merken muss und das gestohlen werden kann, bestätigen Sie die Anmeldung mit Ihrem YubiKey: einstecken, PIN eingeben, goldene Fläche berühren.")}
      </Hero>
      <Toc items={toc} />

      <Section id="was" n={1} title={tr("Was ist ein Passkey?")}>
        <p>
          {tr("Stellen Sie sich ein Siegel vor: Nur Sie besitzen den Siegelstempel, aber jeder kann am Abdruck erkennen, dass er echt ist. Ein Passkey besteht genau so aus zwei zusammengehörenden Teilen.")}
        </p>
        <KeyPair />
        <Note title={tr("Kurz gesagt")}>{tr("Der geheime Teil steckt fest in Ihrem YubiKey und funktioniert nur zusammen mit Ihrer PIN und nur auf der echten Microsoft-Anmeldeseite.")}</Note>
      </Section>

      <Section id="ablauf" n={2} title={tr("So läuft die Anmeldung ab")}>
        <p>{tr("Die Animation zeigt, was bei jeder Anmeldung passiert. Sie können auch auf einen Schritt klicken.")}</p>
        <LoginFlow steps={flowSteps} />
        <Note title={tr("Gut zu wissen")}>{tr("Die Aufgabe ist bei jeder Anmeldung neu. Eine abgefangene Unterschrift nützt deshalb niemandem.")}</Note>
      </Section>

      <Section id="sicher" n={3} title={tr("Warum das sicherer ist als ein Passwort")}>
        <p>{tr("Ein Passwort ist ein Geheimnis, das Sie bei jeder Anmeldung herausgeben. Genau das macht es angreifbar.")}</p>
        <div className="pk-cards four">
          <Card icon="alert" title={tr("Phishing")}>{tr("Eine gefälschte Seite sieht aus wie die echte. Wer dort sein Passwort eintippt, hat es verschenkt.")}</Card>
          <Card icon="box" title={tr("Datenlecks")}>{tr("Wird ein Anbieter gehackt, landen Passwörter in Listen, die im Internet gehandelt werden.")}</Card>
          <Card icon="layers" title={tr("Mehrfachnutzung")}>{tr("Ein Passwort für viele Dienste: Fällt eines, fallen alle.")}</Card>
          <Card icon="key" title={tr("Zu einfach")}>{tr("Gute Passwörter sind lang und zufällig. Merken kann sie sich kaum jemand.")}</Card>
        </div>
        <Note kind="ok" title={tr("Mit Passkey")}>{tr("Es gibt nichts mehr, das Sie eintippen, merken oder verraten könnten. Ihr YubiKey beweist, dass Sie es sind, ohne ein Geheimnis herauszugeben.")}</Note>
        <h3>{tr("Gefälschte Seiten gehen leer aus")}</h3>
        <p>{tr("Ihr Passkey gehört fest zur Adresse")} <code>{tr("login.microsoft.com")}</code>{tr(". Auf einer gefälschten Seite bleibt der YubiKey einfach stumm.")}</p>
        <PhishingDemo />
      </Section>

      <Section id="key" n={4} title={tr("Ihr YubiKey")}>
        <div className="pk-anatomy">
          <div className="pk-anatomy-art"><KeyArt size={260} /></div>
          <ul>
            <li><span className="pk-dot gold" /><span><strong>{tr("Goldene Fläche:")}</strong> {tr("zum Berühren. Sie blinkt, wenn der Key auf Sie wartet.")}</span></li>
            <li><span className="pk-dot metal" /><span><strong>{tr("Stecker:")}</strong> {tr("USB-A oder USB-C, je nach Modell. Passt nicht? Ein Adapter hilft.")}</span></li>
            <li><span className="pk-dot ring" /><span><strong>{tr("Öse:")}</strong> {tr("für den Schlüsselbund. Dort ist der Key am besten aufgehoben.")}</span></li>
            <li><span className="pk-dot nfc" /><span><strong>{tr("NFC")}</strong> {tr("(Modelle mit „NFC“ im Namen): auch kontaktlos am Handy nutzbar.")}</span></li>
          </ul>
        </div>
      </Section>

      <Section id="anmelden" n={5} title={tr("Am PC anmelden")}>
        <div className="pk-steps">
          <Step n={1} title={tr("Key einstecken")} art={<PlugArt />}>{tr("Stecken Sie den YubiKey in einen freien USB-Anschluss. Es passiert noch nichts, das ist richtig so.")}</Step>
          <Step n={2} title={tr("Anmeldeoption wählen")} art={<OptionsArt />}>
            {tr("Geben Sie bei der Microsoft-Anmeldung Ihre E-Mail-Adresse ein. Falls nach einem Passwort gefragt wird, wählen Sie „Andere Anmeldemöglichkeiten“ und dann „Gesicht, Fingerabdruck, PIN oder Sicherheitsschlüssel“.")}
          </Step>
          <Step n={3} title={tr("PIN eingeben")} art={<PinArt />}>{tr("Windows oder der Browser fragt nach der PIN des Sicherheitsschlüssels. Geben Sie Ihre PIN ein und bestätigen Sie.")}</Step>
          <Step n={4} title={tr("Goldene Fläche berühren")} art={<TouchArt />}>{tr("Sobald der Key blinkt, berühren Sie kurz die goldene Fläche. Fertig, Sie sind angemeldet.")}</Step>
        </div>
        <Note kind="ok" title={tr("Beim nächsten Mal")}>{tr("Microsoft merkt sich, dass Sie einen Sicherheitsschlüssel nutzen, und bietet ihn direkt an.")}</Note>
      </Section>

      <Section id="erste" n={6} title={tr("Erste Anmeldung und neue PIN")}>
        <p>{tr("Ihre erste PIN steht im PIN-Brief, den Sie mit dem Key erhalten haben.")}</p>
        <ol className="pk-list numbered">
          <li>{tr("Melden Sie sich wie oben beschrieben an und geben Sie die PIN aus dem Brief ein.")}</li>
          <li>{tr("Steht im Brief, dass ein PIN-Wechsel nötig ist, fordert Windows Sie direkt auf, eine")} <strong>{tr("neue PIN")}</strong> {tr("zu wählen. Wie lang sie mindestens sein muss, zeigt Windows an.")}</li>
          <li>{tr("Geben Sie die neue PIN zweimal ein und berühren Sie den Key, wenn er blinkt.")}</li>
          <li>{tr("Vernichten Sie danach den PIN-Brief. Ihre neue PIN kennen nur Sie.")}</li>
        </ol>
        <Note kind="warn" title={tr("Die IT fragt nie nach Ihrer PIN")}>{tr("Niemand braucht Ihre PIN, auch nicht die IT. Wer danach fragt, will Sie täuschen.")}</Note>
      </Section>

      <Section id="handy" n={7} title={tr("Am Handy anmelden")}>
        <div className="pk-split">
          <div>
            <p>{tr("In Apps wie Outlook oder Teams auf dem Handy funktioniert der YubiKey ebenfalls.")}</p>
            <ul className="pk-list">
              <li><strong>{tr("Mit NFC:")}</strong> {tr("Wählen Sie „Sicherheitsschlüssel“, geben Sie die PIN ein und halten Sie den Key an die Rückseite des Handys, bis die Anmeldung durch ist.")}</li>
              <li><strong>{tr("Mit USB-C:")}</strong> {tr("Key einstecken, PIN eingeben, goldene Fläche berühren, genau wie am PC.")}</li>
            </ul>
            <p className="muted small">{tr("Wo die NFC-Stelle liegt, hängt vom Handy ab. Meist ist sie oben in der Mitte der Rückseite.")}</p>
          </div>
          <PhoneArt />
        </div>
      </Section>

      <Section id="pin" n={8} title={tr("PIN selbst ändern")}>
        <p>{tr("Sie können Ihre PIN jederzeit ändern, zum Beispiel wenn jemand zugesehen hat.")}</p>
        <div className="pk-path">
          {[tr("Einstellungen"), tr("Konten"), tr("Anmeldeoptionen"), tr("Sicherheitsschlüssel"), tr("Verwalten"), tr("Sicherheitsschlüssel-PIN „Ändern“")].map((s, i) => (
            <span key={s} style={{ animationDelay: i * 90 + 'ms' }}>{s}</span>
          ))}
        </div>
        <p className="muted small">{tr("Windows fragt nach der alten PIN, zweimal nach der neuen und bittet Sie, den Key zu berühren.")}</p>
      </Section>

      <Section id="regeln" n={9} title={tr("Sicher aufbewahren")}>
        <div className="pk-cards two">
          <div className="pk-card good">
            <strong><Icon name="check" size={16} /> {tr("So ist es gut")}</strong>
            <ul><li>{tr("Am Schlüsselbund, den Sie ohnehin dabeihaben")}</li><li>{tr("PIN nur im Kopf")}</li><li>{tr("Verlust sofort der IT melden")}</li></ul>
          </div>
          <div className="pk-card bad">
            <strong><Icon name="x" size={16} /> {tr("Lieber nicht")}</strong>
            <ul><li>{tr("PIN auf einen Zettel am Key schreiben")}</li><li>{tr("Key dauerhaft im Firmen-PC stecken lassen")}</li><li>{tr("Key an Kollegen verleihen (")}<a href="#verleihen" onClick={(e) => { e.preventDefault(); document.getElementById('verleihen')?.scrollIntoView({ behavior: 'smooth', block: 'start' }); }}>{tr("warum?")}</a>)</li></ul>
          </div>
        </div>
      </Section>

      <Section id="verleihen" n={10} title={tr("Warum Sie Ihren Key nicht verleihen")}>
        <p>
          {tr("„Kannst du mir kurz deinen Key leihen?“ Das ist schnell gesagt und gut gemeint. Trotzdem: Ihr YubiKey ist wie Ihr Haustürschlüssel. Den geben Sie auch guten Freunden nicht einfach so, selbst wenn Sie ihnen vertrauen.")}
        </p>
        <KeyCompare />
        <h3>{tr("Was beim Ausleihen wirklich passiert")}</h3>
        <p>{tr("Für Microsoft gibt es nur eine Wahrheit: Wer mit Ihrem Key und Ihrer PIN kommt, ist Sie. Alles, was der Kollege tut, steht unter Ihrem Namen.")}</p>
        <LendLog />
        <div className="pk-cards four">
          <Card icon="user" title={tr("Der Key ist Ihre Identität")}>{tr("Er öffnet nicht ein Programm, sondern Ihr ganzes Konto: E-Mails, Dateien, Teams und alles, was daran hängt.")}</Card>
          <Card icon="history" title={tr("Alles unter Ihrem Namen")}>{tr("Jede Anmeldung und jede Aktion wird protokolliert. Ob Sie es waren oder jemand mit Ihrem Key, sieht dort niemand.")}</Card>
          <Card icon="key" title={tr("Die PIN schützt dann nicht")}>{tr("Die PIN hält Fremde ab, die den Key finden. Wer sie von Ihnen bekommt, ist für den Key kein Fremder mehr.")}</Card>
          <Card icon="alert" title={tr("Verliehen, vergessen, verloren")}>{tr("Der Key bleibt irgendwo liegen, geht verloren oder die PIN spricht sich herum. Passiert so etwas, melden Sie es sofort der IT.")}</Card>
        </div>
        <Note kind="ok" title={tr("Jemand muss Sie vertreten?")}>
          {tr("Dafür gibt es saubere Wege, ohne dass jemand Ihren Key braucht: Ihre IT kann der Vertretung Zugriff auf Postfach oder Ordner geben, einen eigenen Key einrichten oder für besondere Fälle einen gemeinsamen Key mit klarer Verantwortung ausgeben. Fragen Sie einfach bei der IT nach")}{brand?.contact ? <>: <strong>{brand.contact}</strong></> : '.'}
        </Note>
      </Section>

      <Section id="hilfe" n={11} title={tr("Es klappt nicht")}>
        <div className="pk-faq">
          {[
            [tr("Der Key blinkt nicht."), tr("Ziehen Sie den Key ab und stecken Sie ihn erneut ein, gern in einen anderen Anschluss. Bei USB-Hubs und Dockingstationen hilft oft ein Anschluss direkt am Gerät.")],
            [tr("Es wird nur nach einem Passwort gefragt."), tr("Klicken Sie auf „Andere Anmeldemöglichkeiten“ bzw. „Anmeldeoptionen“ und wählen Sie „Gesicht, Fingerabdruck, PIN oder Sicherheitsschlüssel“.")],
            [tr("Windows bietet Windows Hello statt des Keys an."), tr("Wählen Sie im Fenster „Weitere Optionen“ und dann „Sicherheitsschlüssel“.")],
            [tr("Die PIN wird nicht angenommen."), tr("Achten Sie auf Groß- und Kleinschreibung, falls Ihre PIN Buchstaben enthält. Nach 8 Fehlversuchen sperrt sich der Key, dann hilft nur die IT.")],
            [tr("Das Fenster ist einfach verschwunden."), tr("Für die Berührung bleiben etwa 30 Sekunden. Starten Sie die Anmeldung einfach noch einmal.")],
            [tr("Ich habe meinen Key verloren."), tr("Melden Sie es sofort der IT. Der Key wird gesperrt, und Sie bekommen einen neuen. Ein Finder kann ohne Ihre PIN nichts damit anfangen.")],
            [tr("Ich habe meine PIN vergessen."), tr("Die PIN lässt sich nicht auslesen. Die IT setzt den Key zurück und richtet ihn neu für Sie ein.")],
          ].map(([q, a]) => (
            <details key={q}>
              <summary>{q}</summary>
              <p>{a}</p>
            </details>
          ))}
        </div>
        {brand?.contact && <Note title={tr("Weiterhin Probleme?")}>{tr("Ihre IT hilft Ihnen gern weiter:")} <strong>{brand.contact}</strong></Note>}
      </Section>

      <div className="pk-cta pk-reveal">
        <div><strong>{tr("Mehr Details?")}</strong><span className="muted">{tr("Die Techniker-Ansicht erklärt Kryptografie, Phishing-Resistenz und die Einbindung in Entra ID.")}</span></div>
        <button type="button" className="primary" onClick={onSwitch}><Icon name="settings" size={16} />{tr("Zur Techniker-Ansicht")}</button>
      </div>
    </>
  );
}

/* ---------- Grafik: Haustürschlüssel und YubiKey ---------- */

function HouseKeyArt({ size = 120 }: { size?: number }) {
  return (
    <svg className="pk-housekey" width={size} height={size * 0.42} viewBox="0 0 100 42" aria-hidden>
      <circle cx="18" cy="21" r="15" fill="#c9a24a" />
      <circle cx="18" cy="21" r="5.5" fill="var(--surface)" />
      <path d="M32 17h62v8h-6v6h-5v-6h-4v8h-5v-8h-4v5h-5v-5H32Z" fill="#c9a24a" />
      <path d="M38 20h52" stroke="#a7842f" strokeWidth="1.4" />
    </svg>
  );
}

function KeyCompare() {
  return (
    <div className="pk-compare pk-reveal">
      <div className="pk-compare-side">
        <div className="pk-compare-art"><HouseKeyArt size={150} /></div>
        <div className="pk-compare-arrow" aria-hidden><span /></div>
        <div className="pk-compare-target"><Icon name="home" size={26} /><span>{tr("Ihre Wohnung")}</span></div>
        <p>{tr("Wer ihn hat, kommt rein, auch wenn Sie nicht da sind. Deshalb hat ihn nicht jeder.")}</p>
      </div>
      <div className="pk-compare-eq" aria-hidden>=</div>
      <div className="pk-compare-side">
        <div className="pk-compare-art"><KeyArt size={150} /></div>
        <div className="pk-compare-arrow" aria-hidden><span /></div>
        <div className="pk-compare-target"><Icon name="user" size={26} /><span>{tr("Ihr Firmenkonto")}</span></div>
        <p>{tr("Wer ihn und Ihre PIN hat, ist für Microsoft Sie. Mit allen Ihren Rechten.")}</p>
      </div>
    </div>
  );
}

const lendLog = [
  ['08:52', 'haben sich angemeldet', false],
  ['09:14', tr("haben das Angebot an Kunde Meier verschickt"), true],
  ['09:31', tr("haben den Ordner „Projekt Nord“ gelöscht"), true],
  ['09:40', 'haben sich abgemeldet', false],
] as const;

function LendLog() {
  return (
    <div className="pk-lendlog pk-reveal" aria-label={tr("Beispiel aus einem Protokoll")}>
      <div className="pk-lendlog-head"><Icon name="history" size={16} /><b>{tr("Protokoll")}</b><span className="muted small">{tr("so sieht es die IT")}</span></div>
      {lendLog.map(([t, what, colleague], i) => (
        <div key={t} className={'pk-lendlog-row' + (colleague ? ' lent' : '')} style={{ animationDelay: i * 260 + 'ms' }}>
          <span className="pk-lendlog-time">{t}</span>
          <span><strong>{tr("Sie")}</strong> {what}</span>
          {colleague && <span className="pk-lendlog-tag">{tr("eigentlich: Ihr Kollege")}</span>}
        </div>
      ))}
    </div>
  );
}

/* ---------- Ansicht Techniker ---------- */

const techFlowSteps = [
  { title: tr("Anfrage"), text: tr("Der Browser ruft navigator.credentials.get() auf. Entra ID liefert dafür die Optionen: RP-ID login.microsoft.com, erlaubte Credential-IDs und userVerification = required."), token: tr("GET assertion options") },
  { title: tr("Challenge"), text: tr("Entra ID erzeugt eine zufällige, einmalige Challenge. Der Browser bildet clientDataJSON (Challenge, Origin, Typ) und schickt über CTAP2 den rpIdHash und den Hash von clientDataJSON an den YubiKey."), token: tr("challenge · rpIdHash") },
  { title: tr("UV und UP"), text: tr("Der YubiKey prüft die PIN selbst (User Verification, PIN/UV-Auth-Protokoll von CTAP2) und verlangt eine Berührung (User Presence). Die PIN wird am PC eingegeben und verschlüsselt an den Key übergeben, zu Entra ID gelangt sie nie."), token: '' },
  { title: tr("Assertion"), text: tr("Der Key signiert authenticatorData (rpIdHash, Flags UP/UV, Zähler) zusammen mit dem Hash von clientDataJSON mit dem privaten Schlüssel (ES256, P-256). Zurück gehen Signatur, authenticatorData und clientDataJSON."), token: tr("signature (ES256)") },
  { title: tr("Verifikation"), text: tr("Entra ID prüft Challenge, Origin, rpIdHash, UV-Flag und die Signatur mit dem hinterlegten Public Key. Passt alles, wird ein Token mit der Methode „phishing-resistent“ ausgestellt."), token: '' },
];

function Technical({ onSwitch }: { onSwitch: () => void }) {
  const toc = [
    { id: 'secret', label: tr("Das Problem: geteilte Geheimnisse"), icon: 'alert' as const },
    { id: 'krypto', label: tr("Public-Key-Kryptografie"), icon: 'key' as const },
    { id: 'ablauf', label: tr("Challenge-Response im Detail"), icon: 'monitor' as const },
    { id: 'origin', label: tr("Origin-Bindung"), icon: 'shield' as const },
    { id: 'server', label: tr("Kein Geheimnis auf dem Server"), icon: 'box' as const },
    { id: 'uv', label: tr("User Verification und Presence"), icon: 'user' as const },
    { id: 'attest', label: tr("Attestierung und AAGUID"), icon: 'scan' as const },
    { id: 'bindung', label: tr("Gerätegebunden oder synchronisiert"), icon: 'layers' as const },
    { id: 'entra', label: tr("Einbindung in Entra ID"), icon: 'building' as const },
    { id: 'vergleich', label: tr("MFA-Methoden im Vergleich"), icon: 'list' as const },
    { id: 'grenzen', label: tr("Grenzen und Restrisiken"), icon: 'info' as const },
  ];
  return (
    <>
      <Hero eyebrow={tr("Für Techniker")} title={tr("Warum Passkeys so sicher sind")} art={<div className="pk-float"><KeyArt size={220} glow /></div>}>
        {tr("FIDO2-Sicherheitsschlüssel wie der YubiKey gelten als phishing-resistente MFA. Hier steht, warum: asymmetrische Kryptografie, Bindung an die Origin, Prüfung im Authenticator und ein Schlüssel, der das Gerät nie verlässt.")}
      </Hero>
      <Toc items={toc} />

      <Section id="secret" n={1} title={tr("Das Problem: geteilte Geheimnisse")}>
        <p>
          {tr("Passwort, SMS-Code und TOTP haben eines gemeinsam: Ein Geheimnis wird von Mensch zu Server übertragen. Wer sich dazwischen setzt, bekommt es. Adversary-in-the-Middle-Proxys (etwa Evilginx) reichen sogar Einmalcodes und Push-Bestätigungen in Echtzeit durch und erbeuten das Session-Cookie.")}
        </p>
        <div className="pk-cards four">
          <Card icon="alert" title={tr("Phishing und AiTM")}>{tr("Der Mensch prüft die Adresse, und Menschen lassen sich täuschen. Einmalcodes helfen gegen Echtzeit-Proxys nicht.")}</Card>
          <Card icon="box" title={tr("Datenbank-Leaks")}>{tr("Passwort-Hashes lassen sich offline knacken, TOTP-Seeds liegen im Klartext beim Server.")}</Card>
          <Card icon="layers" title={tr("Credential Stuffing")}>{tr("Wiederverwendete Passwörter aus fremden Leaks funktionieren auch bei Ihnen.")}</Card>
          <Card icon="user" title={tr("MFA-Fatigue")}>{tr("Push-Anfragen werden irgendwann genervt bestätigt.")}</Card>
        </div>
      </Section>

      <Section id="krypto" n={2} title={tr("Public-Key-Kryptografie statt Geheimnis")}>
        <p>
          {tr("Bei der Registrierung erzeugt der YubiKey")} <strong>{tr("für genau dieses Konto und diese Relying Party")}</strong> {tr("ein neues Schlüsselpaar (in Entra ID ES256 auf der Kurve P-256). Der private Schlüssel entsteht im Secure Element und ist nicht exportierbar. Entra ID speichert nur Credential-ID und Public Key.")}
        </p>
        <KeyPair />
        <ul className="pk-list">
          <li><strong>{tr("Ein Schlüssel pro Dienst:")}</strong> {tr("Jede Relying Party bekommt ein eigenes Paar. Konten lassen sich über Dienste hinweg nicht verknüpfen.")}</li>
          <li><strong>{tr("Discoverable Credentials:")}</strong> {tr("Der Key kann das Credential samt Benutzer-Handle speichern, dadurch geht die Anmeldung auch ohne Benutzernamen.")}</li>
          <li><strong>{tr("Begrenzter Speicher:")}</strong> {tr("Ein YubiKey 5 speichert je nach Firmware 25 (bis 5.6) bzw. 100 (ab 5.7) Discoverable Credentials.")}</li>
        </ul>
      </Section>

      <Section id="ablauf" n={3} title={tr("Challenge-Response im Detail")}>
        <p>{tr("Die Anmeldung ist ein Challenge-Response-Verfahren nach WebAuthn (Browser ↔ Server) und CTAP2 (Browser ↔ Authenticator).")}</p>
        <LoginFlow steps={techFlowSteps} tech />
        <Note title={tr("Replay ausgeschlossen")}>{tr("Die Challenge ist zufällig und nur einmal gültig, die Signatur deckt sie mit ab. Eine mitgeschnittene Assertion lässt sich nicht wiederverwenden.")}</Note>
      </Section>

      <Section id="origin" n={4} title={tr("Origin-Bindung: der eigentliche Phishing-Schutz")}>
        <p>
          {tr("Nicht der Benutzer entscheidet, ob eine Seite echt ist, sondern der Browser. Er prüft, ob die angefragte RP-ID zur Domain der Seite passt, und schreibt die tatsächliche Origin in")} <code>{tr("clientDataJSON")}</code>{tr(". Der Authenticator findet Credentials nur über den")} <code>{tr("rpIdHash")}</code>{tr(", und der Server prüft Origin und rpIdHash in der Signatur.")}
        </p>
        <PhishingDemo />
        <div className="pk-cards">
          <Card icon="x" title={tr("Fremde Domain")}>{tr("Für")} <code>{tr("login-micros0ft.com")}</code> {tr("existiert kein Credential, der Key antwortet nicht.")}</Card>
          <Card icon="shield" title={tr("AiTM-Proxy")}>{tr("Selbst wenn ein Proxy die Anfrage weiterreicht: Die signierte Origin ist die des Proxys, Entra ID lehnt ab.")}</Card>
          <Card icon="check" title={tr("Kein Ermessen")}>{tr("Es gibt keinen Code, den der Benutzer abtippen und dabei falsch einsetzen könnte.")}</Card>
        </div>
      </Section>

      <Section id="server" n={5} title={tr("Kein Geheimnis auf dem Server")}>
        <div className="pk-split">
          <div>
            <p>{tr("Entra ID kennt nur Public Keys. Ein Leak der Verzeichnisdaten liefert nichts, was sich knacken oder für eine Anmeldung nutzen ließe.")}</p>
            <p>{tr("Zusätzlich führt der Authenticator einen Signaturzähler. Springt er unerwartet zurück, kann das auf ein geklontes Credential hindeuten.")}</p>
          </div>
          <div className="pk-vault pk-reveal" aria-hidden>
            <div className="pk-vault-row"><Icon name="shield" size={18} /><span>{tr("Public Key")}</span><em>{tr("nutzlos für Angreifer")}</em></div>
            <div className="pk-vault-row"><Icon name="list" size={18} /><span>{tr("Credential-ID, AAGUID")}</span><em>{tr("Metadaten")}</em></div>
            <div className="pk-vault-row strike"><Icon name="key" size={18} /><span>{tr("Passwort-Hash, OTP-Seed")}</span><em>{tr("gibt es nicht")}</em></div>
          </div>
        </div>
      </Section>

      <Section id="uv" n={6} title={tr("User Verification und User Presence")}>
        <div className="pk-cards">
          <Card icon="user" title={tr("UV: die PIN")}>
            {tr("Die FIDO2-PIN prüft der Key selbst (CTAP2 PIN/UV-Auth-Protokoll). Nach 8 Fehlversuchen ist sie gesperrt, dann hilft nur ein Reset, der alle Credentials löscht. Nach 3 Fehlversuchen in Folge muss der Key neu eingesteckt werden.")}
          </Card>
          <Card icon="check" title={tr("UP: die Berührung")}>
            {tr("Die kapazitive Fläche bestätigt physische Anwesenheit. Schadsoftware kann den Key nicht unbemerkt aus der Ferne benutzen.")}
          </Card>
          <Card icon="settings" title={tr("PIN-Richtlinien")}>
            {tr("Ab Firmware 5.7 lassen sich eine Mindestlänge und ein erzwungener PIN-Wechsel auf dem Key setzen. Die Plattform nutzt das für zufällige Start-PINs.")}
          </Card>
        </div>
        <Note title={tr("Zwei Faktoren in einem Gerät")}>{tr("Besitz (der Key mit privatem Schlüssel) und Wissen (die PIN) werden lokal kombiniert. Entra ID sieht das UV-Flag in der signierten authenticatorData.")}</Note>
      </Section>

      <Section id="attest" n={7} title={tr("Attestierung und AAGUID")}>
        <p>
          {tr("Bei der Registrierung kann der Key eine")} <strong>{tr("Attestierung")}</strong> {tr("liefern: eine Signatur mit einem Herstellerzertifikat, die belegt, dass das Credential in echter YubiKey-Hardware erzeugt wurde. Die")} <strong>{tr("AAGUID")}</strong> {tr("identifiziert dabei das Modell und lässt sich gegen den FIDO Metadata Service prüfen.")}
        </p>
        <ul className="pk-list">
          <li>{tr("In Entra ID kann die FIDO2-Richtlinie Attestierung erzwingen und nur bestimmte AAGUIDs zulassen (Key Restrictions).")}</li>
          <li>{tr("Diese Plattform liest Seriennummer, Firmware und AAGUID über den lokalen Agent und warnt, wenn ein Key nicht zu den Vorgaben des Tenants passt.")}</li>
          <li>{tr("Yubico-Sicherheitshinweise (etwa zu bestimmten Firmware-Ständen) lassen sich so pro Key zuordnen.")}</li>
        </ul>
      </Section>

      <Section id="bindung" n={8} title={tr("Gerätegebunden oder synchronisiert")}>
        <div className="pk-table-wrap">
          <table className="pk-table">
            <thead><tr><th>{tr("Merkmal")}</th><th>{tr("Synchronisierter Passkey")}</th><th>{tr("Sicherheitsschlüssel (YubiKey)")}</th></tr></thead>
            <tbody>
              {[
                [tr("Wo liegt der private Schlüssel?"), tr("Im Passwortmanager bzw. Cloud-Konto, auf mehrere Geräte kopiert"), tr("Im Secure Element, nicht exportierbar")],
                [tr("Hängt an einem Cloud-Konto?"), tr("Ja, dessen Sicherheit zählt mit"), tr("Nein")],
                [tr("Attestierung möglich?"), tr("In der Regel nicht"), tr("Ja, mit AAGUID-Einschränkung")],
                [tr("Verlust des Geräts"), tr("Passkey bleibt über die Cloud verfügbar"), tr("Credential ist weg, Ersatzkey nötig")],
              ].map(([q, a, b]) => (
                <tr key={q}><td>{q}</td><td>{a}</td><td className="good"><Icon name="check" size={14} />{b}</td></tr>
              ))}
            </tbody>
          </table>
        </div>
        <p className="muted small">{tr("Für Administratoren und besonders schützenswerte Konten empfiehlt Microsoft gerätegebundene Passkeys.")}</p>
      </Section>

      <Section id="entra" n={9} title={tr("Einbindung in Entra ID")}>
        <div className="pk-cards">
          <Card icon="sliders" title={tr("Methodenrichtlinie")}>{tr("Passkey (FIDO2) in den Authentifizierungsmethoden aktivieren, Zielgruppen festlegen, optional Attestierung und Key Restrictions.")}</Card>
          <Card icon="shield" title={tr("Authentifizierungsstärke")}>{tr("Conditional Access mit der Stärke „Phishing-resistente MFA“ erzwingt FIDO2, Windows Hello for Business oder zertifikatbasierte Anmeldung.")}</Card>
          <Card icon="keyPlus" title={tr("Registrierung im Auftrag")}>{tr("Diese Plattform registriert Keys über Microsoft Graph (fido2Methods) im Namen der Benutzer, setzt die PIN und dokumentiert alles im Audit-Log.")}</Card>
        </div>
        <Note title={tr("Fehlende Einstellungen")}>{tr("Die Tenant-Prüfung der Plattform zeigt fehlende FIDO2-Einstellungen an und behebt sie auf Wunsch direkt.")}</Note>
      </Section>

      <Section id="vergleich" n={10} title={tr("MFA-Methoden im Vergleich")}>
        <div className="pk-table-wrap">
          <table className="pk-table cmp">
            <thead><tr><th>{tr("Eigenschaft")}</th><th>{tr("Passwort")}</th><th>{tr("SMS / TOTP")}</th><th>{tr("Push-App")}</th><th>{tr("FIDO2-Key")}</th></tr></thead>
            <tbody>
              {([
                [tr("Phishing-resistent"), 0, 0, 0, 1],
                [tr("Schutz vor AiTM-Proxy"), 0, 0, 0, 1],
                [tr("Kein Geheimnis beim Server"), 0, 0, 1, 1],
                [tr("Replay-sicher"), 0, 1, 1, 1],
                [tr("Nicht kopierbar"), 0, 0, 1, 1],
                [tr("Keine MFA-Fatigue"), 1, 1, 0, 1],
              ] as [string, number, number, number, number][]).map(([q, ...v]) => (
                <tr key={q}><td>{q}</td>{v.map((x, i) => <td key={i} className={x ? 'good' : 'bad'}><Icon name={x ? 'check' : 'x'} size={14} />{x ? tr("ja") : tr("nein")}</td>)}</tr>
              ))}
            </tbody>
          </table>
        </div>
        <p className="muted small">{tr("Push mit Nummernabgleich mildert MFA-Fatigue, bleibt aber für AiTM-Proxys anfällig.")}</p>
      </Section>

      <Section id="grenzen" n={11} title={tr("Grenzen und Restrisiken")}>
        <ul className="pk-list">
          <li><strong>{tr("Token-Diebstahl nach der Anmeldung:")}</strong> {tr("FIDO2 schützt den Anmeldevorgang, nicht das ausgestellte Session-Token. Schadsoftware auf dem Gerät kann Cookies stehlen. Dagegen helfen Token Protection, konforme Geräte und kurze Sitzungen.")}</li>
          <li><strong>{tr("Schwächere Methoden parallel:")}</strong> {tr("Solange Passwort oder SMS als Alternative erlaubt sind, wählt ein Angreifer diese. Erst Conditional Access mit phishing-resistenter Stärke schließt die Lücke.")}</li>
          <li><strong>{tr("Wiederherstellung:")}</strong> {tr("Verlorene Keys sind nicht wiederherstellbar. Einen Prozess mit Temporary Access Pass und Ersatzkey einplanen, für kritische Konten einen zweiten Key.")}</li>
          <li><strong>{tr("Legacy-Protokolle:")}</strong> {tr("Basic Auth (IMAP, POP, SMTP AUTH) kann kein FIDO2 und gehört abgeschaltet.")}</li>
          <li><strong>{tr("Breakglass:")}</strong> {tr("Notfallkonten ebenfalls mit FIDO2 absichern, aber von Conditional Access ausnehmen und überwachen.")}</li>
        </ul>
      </Section>

      <div className="pk-cta pk-reveal">
        <div><strong>{tr("Für den Kunden erklärt")}</strong><span className="muted">{tr("Die Endbenutzer-Ansicht zeigt das Ganze einfach und mit Schritt-für-Schritt-Anleitung.")}</span></div>
        <button type="button" className="primary" onClick={onSwitch}><Icon name="user" size={16} />{tr("Zur Endbenutzer-Ansicht")}</button>
      </div>
    </>
  );
}


function Card({ icon, title, children }: { icon: IconName; title: string; children: ReactNode }) {
  return (
    <div className="pk-card">
      <div className="pk-card-icon"><Icon name={icon} size={20} /></div>
      <strong>{title}</strong>
      <p>{children}</p>
    </div>
  );
}

/* ---------- Seite 2: So geht's ---------- */

function Step({ n, title, art, children }: { n: number; title: string; art: ReactNode; children: ReactNode }) {
  return (
    <div className="pk-step pk-reveal">
      <div className="pk-step-art" aria-hidden>{art}</div>
      <div className="pk-step-body">
        <div className="pk-step-title"><span className="pk-num">{n}</span>{title}</div>
        <div>{children}</div>
      </div>
    </div>
  );
}

function PlugArt() {
  return (
    <div className="pk-plug">
      <div className="pk-port"><span /></div>
      <div className="pk-plug-key"><KeyArt size={110} /></div>
    </div>
  );
}

function OptionsArt() {
  return (
    <div className="pk-mock">
      <div className="pk-mock-head"><Cloud /><b>{tr("Anmelden")}</b></div>
      <div className="pk-mock-field">{tr("name@firma.de")}</div>
      <div className="pk-mock-btn hl"><KeyArt size={26} />{tr("Gesicht, Fingerabdruck, PIN oder Sicherheitsschlüssel")}</div>
    </div>
  );
}

function PinArt() {
  return (
    <div className="pk-mock">
      <div className="pk-mock-head"><Icon name="shield" size={20} /><b>{tr("Windows-Sicherheit")}</b></div>
      <div className="small">{tr("Geben Sie die PIN Ihres Sicherheitsschlüssels ein.")}</div>
      <div className="pk-mock-field pin"><span className="pk-dots typing"><i /><i /><i /><i /><i /><i /></span></div>
      <div className="pk-mock-ok">{tr("OK")}</div>
    </div>
  );
}

function TouchArt() {
  return (
    <div className="pk-touch">
      <KeyArt size={140} glow />
      <div className="pk-finger" />
    </div>
  );
}

function PhoneArt() {
  return (
    <div className="pk-phone">
      <div className="pk-phone-body"><div className="pk-nfc-waves"><span /><span /><span /></div><small>{tr("Key an die Rückseite halten")}</small></div>
      <div className="pk-phone-key"><KeyArt size={84} /></div>
    </div>
  );
}

