import { useEffect, useRef, useState, type ReactNode } from 'react';
import { Link, useLocation } from 'react-router';
import { Icon, type IconName } from '../icons';
import { ThemeToggle } from '../theme';
import '../passkey.css';

// Öffentliche Infoseiten für Endnutzer: „Passkey verstehen“ (/passkey) und „So geht's“ (/passkey/anleitung).
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
function useReveal() {
  const ref = useRef<HTMLDivElement>(null);
  const { pathname } = useLocation();
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
  }, [pathname]);
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

export default function PasskeyInfoPage({ authenticated }: { authenticated: boolean }) {
  const { pathname, search } = useLocation();
  const tenant = new URLSearchParams(search).get('t');
  const brand = useBrand(tenant);
  const guide = pathname.startsWith('/passkey/anleitung');
  const ref = useReveal();
  const keep = tenant ? '?t=' + encodeURIComponent(tenant) : '';

  useEffect(() => {
    document.title = (guide ? "So geht's mit dem YubiKey" : 'Passkey verstehen') + (brand?.companyName ? ' · ' + brand.companyName : '');
    window.scrollTo(0, 0);
  }, [guide, brand?.companyName]);

  const style = brand?.accentColor && /^#[0-9a-f]{6}$/i.test(brand.accentColor) ? ({ '--pk-brand': brand.accentColor } as React.CSSProperties) : undefined;

  return (
    <div className={'pk' + (style ? ' branded' : '')} style={style} ref={ref}>
      <header className="pk-top">
        <div className="pk-top-inner">
          <div className="pk-brand">
            {brand?.hasLogo
              ? <img src={'/api/public/branding/logo' + keep} alt={brand.companyName ?? 'Logo'} />
              : <KeyArt size={34} />}
            <span>{brand?.companyName || 'YubiKey & Passkey'}</span>
          </div>
          <nav className="pk-tabs" aria-label="Infoseiten">
            <Link to={'/passkey' + keep} className={guide ? '' : 'active'}>Passkey verstehen</Link>
            <Link to={'/passkey/anleitung' + keep} className={guide ? 'active' : ''}>So geht's</Link>
          </nav>
          <div className="pk-top-actions">
            <ThemeToggle />
            {authenticated && <Link className="btn small" to="/">Zur Plattform</Link>}
          </div>
        </div>
      </header>
      <main className="pk-main" key={pathname}>
        {guide ? <Guide brand={brand} keep={keep} /> : <Knowledge keep={keep} />}
      </main>
      <footer className="pk-foot">
        {brand?.contact && <p>Fragen zu Ihrem YubiKey? Ihre IT hilft weiter: <strong>{brand.contact}</strong></p>}
        <p className="muted small">Passkeys beruhen auf dem offenen Standard FIDO2/WebAuthn. YubiKey ist eine Marke von Yubico.</p>
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
    <nav className="pk-toc pk-reveal" aria-label="Inhalt">
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
  { title: 'Anmelden', text: 'Sie öffnen die Microsoft-Anmeldung und wählen „Sicherheitsschlüssel“.', token: 'Anmeldung, bitte' },
  { title: 'Aufgabe', text: 'Microsoft Entra ID schickt eine zufällige Aufgabe. Ihr Browser reicht sie samt Adresse der Seite an den YubiKey weiter.', token: 'Aufgabe 7f3a…' },
  { title: 'PIN und Berührung', text: 'Sie geben Ihre PIN ein und berühren die goldene Fläche. Damit ist klar: Sie sind es, und Sie sitzen wirklich vor dem Gerät.', token: '' },
  { title: 'Unterschrift', text: 'Der YubiKey unterschreibt die Aufgabe mit seinem geheimen Schlüssel. Der geheime Schlüssel selbst bleibt im Key.', token: 'Unterschrift ✓' },
  { title: 'Geprüft', text: 'Microsoft prüft die Unterschrift mit dem öffentlichen Prüfschlüssel. Passt sie, sind Sie angemeldet.', token: '' },
];

function LoginFlow() {
  const { ref, step, playing, setPlaying, go } = useStepper(flowSteps.length, 2800);
  // Position des „Umschlags“: 0 = YubiKey, 50 = Laptop, 100 = Microsoft.
  const pos = [55, 8, 8, 92, 92][step];
  const from = [8, 92, 8, 8, 92][step];
  return (
    <div className="pk-flow" ref={ref}>
      <div className="pk-flow-stage" data-step={step}>
        <div className="pk-actor key">
          <div className={'pk-actor-art' + (step === 2 ? ' active' : '')}><KeyArt size={96} glow={step === 2} /></div>
          <span>Ihr YubiKey</span>
          <small>geheimer Schlüssel</small>
        </div>
        <div className="pk-actor laptop">
          <Laptop>
            {step < 2 && <div className="pk-mini-login"><b>Anmelden</b><i /><em><KeyArt size={22} /> Sicherheitsschlüssel</em></div>}
            {step === 2 && <div className="pk-mini-login"><b>PIN</b><span className="pk-dots"><i /><i /><i /><i /><i /><i /></span><em className="touch">Key berühren …</em></div>}
            {step === 3 && <div className="pk-mini-login"><b>Bitte warten …</b><span className="pk-spin" /></div>}
            {step === 4 && <div className="pk-mini-login ok"><span className="pk-check"><Icon name="check" size={26} /></span><b>Angemeldet</b></div>}
          </Laptop>
          <span>Ihr Browser</span>
          <small>login.microsoft.com</small>
        </div>
        <div className="pk-actor cloud">
          <div className={'pk-actor-art' + (step === 4 ? ' active' : '')}><Cloud /></div>
          <span>Microsoft Entra ID</span>
          <small>öffentlicher Prüfschlüssel</small>
        </div>
        <div className="pk-track" aria-hidden><div className="pk-track-line" /></div>
        {flowSteps[step].token && (
          <div key={step} className="pk-token" style={{ ['--from' as string]: from + '%', ['--to' as string]: pos + '%' }}>
            {flowSteps[step].token}
          </div>
        )}
      </div>
      <ol className="pk-flow-steps">
        {flowSteps.map((s, i) => (
          <li key={s.title} className={i === step ? 'active' : i < step ? 'done' : ''}>
            <button type="button" onClick={() => go(i)} aria-current={i === step}>
              <span className="pk-num">{i < step ? <Icon name="check" size={13} /> : i + 1}</span>{s.title}
            </button>
          </li>
        ))}
      </ol>
      <div className="pk-flow-caption" aria-live="polite" key={'c' + step}>{flowSteps[step].text}</div>
      <button type="button" className="ghost small pk-play" onClick={() => setPlaying(!playing)}>
        {playing ? '❚❚ Anhalten' : '▶ Abspielen'}
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
        <button type="button" role="tab" aria-selected={!fake} className={!fake ? 'active' : ''} onClick={() => go(0)}>Echte Seite</button>
        <button type="button" role="tab" aria-selected={fake} className={fake ? 'active' : ''} onClick={() => go(1)}>Gefälschte Seite</button>
        <button type="button" className="ghost small" onClick={() => setPlaying(!playing)} aria-label={playing ? 'Anhalten' : 'Abspielen'}>{playing ? '❚❚' : '▶'}</button>
      </div>
      <div className="pk-phish-stage" key={step}>
        <div className="pk-browser">
          <div className="pk-urlbar"><Icon name={fake ? 'alert' : 'shield'} size={14} /><span>{fake ? 'https://login-micros0ft-sicherheit.com' : 'https://login.microsoft.com'}</span></div>
          <div className="pk-browser-body">
            <div className="pk-ms"><Cloud /><b>Anmelden</b></div>
            <div className="pk-ask">„Bitte unterschreiben für <code>{fake ? 'login-micros0ft-sicherheit.com' : 'login.microsoft.com'}</code>“</div>
          </div>
        </div>
        <div className="pk-phish-arrow" aria-hidden><span /></div>
        <div className="pk-phish-key">
          <KeyArt size={90} glow={!fake} />
          <div className={'pk-verdict ' + (fake ? 'no' : 'yes')}>
            {fake
              ? <><Icon name="x" size={18} />Kein Passkey für diese Adresse. Der Key antwortet nicht.</>
              : <><Icon name="check" size={18} />Passkey für login.microsoft.com gefunden. Unterschrift wird erstellt.</>}
          </div>
        </div>
      </div>
      <p className="muted small">Die Adresse prüft nicht der Mensch, sondern der Browser. Eine täuschend echte Kopie der Seite bekommt deshalb nichts, womit sie sich anmelden könnte.</p>
    </div>
  );
}

/* ---------- Animation: Schlüsselpaar ---------- */

function KeyPair() {
  return (
    <div className="pk-pair pk-reveal">
      <div className="pk-pair-side">
        <div className="pk-pair-icon secret"><Icon name="key" size={28} /></div>
        <strong>Geheimer Schlüssel</strong>
        <span>liegt fest im YubiKey, lässt sich nicht auslesen und nicht kopieren</span>
      </div>
      <div className="pk-pair-link" aria-hidden><span /><span /><span /></div>
      <div className="pk-pair-side">
        <div className="pk-pair-icon public"><Icon name="shield" size={28} /></div>
        <strong>Öffentlicher Prüfschlüssel</strong>
        <span>liegt bei Microsoft Entra ID, kann nur prüfen, nicht unterschreiben</span>
      </div>
    </div>
  );
}

/* ---------- Seite 1: Passkey verstehen ---------- */

function Knowledge({ keep }: { keep: string }) {
  const toc = [
    { id: 'problem', label: 'Das Problem mit Passwörtern', icon: 'alert' as const },
    { id: 'prinzip', label: 'So funktioniert ein Passkey', icon: 'key' as const },
    { id: 'phishing', label: 'Gefälschte Seiten gehen leer aus', icon: 'shield' as const },
    { id: 'hack', label: 'Was bei einem Hack passiert', icon: 'box' as const },
    { id: 'pin', label: 'PIN und Berührung', icon: 'user' as const },
    { id: 'vergleich', label: 'Passwort und Passkey im Vergleich', icon: 'list' as const },
    { id: 'standard', label: 'Wer steckt dahinter?', icon: 'globe' as const },
    { id: 'grenzen', label: 'Ehrlich gesagt: die Nachteile', icon: 'info' as const },
  ];
  return (
    <>
      <Hero eyebrow="Einfach erklärt" title="Was ist ein Passkey?" art={<div className="pk-float"><KeyArt size={220} glow /></div>}>
        Ein Passkey ersetzt das Passwort. Statt etwas einzutippen, das man sich merken muss und das gestohlen werden kann,
        bestätigen Sie die Anmeldung mit Ihrem YubiKey: einstecken, PIN eingeben, goldene Fläche berühren.
        Hier erfahren Sie, warum das so viel sicherer ist.
      </Hero>
      <Toc items={toc} />

      <Section id="problem" n={1} title="Das Problem mit Passwörtern">
        <p>Ein Passwort ist ein Geheimnis, das Sie bei jeder Anmeldung herausgeben. Genau das macht es angreifbar.</p>
        <div className="pk-cards four">
          <Card icon="alert" title="Phishing">Eine gefälschte Seite sieht aus wie die echte. Wer dort sein Passwort eintippt, hat es verschenkt.</Card>
          <Card icon="box" title="Datenlecks">Wird ein Anbieter gehackt, landen Passwörter in Listen, die im Internet gehandelt werden.</Card>
          <Card icon="layers" title="Mehrfachnutzung">Ein Passwort für viele Dienste: Fällt eines, fallen alle.</Card>
          <Card icon="key" title="Zu einfach">Gute Passwörter sind lang und zufällig. Merken kann sie sich kaum jemand.</Card>
        </div>
        <Note kind="ok" title="Mit Passkey">Es gibt nichts mehr, das Sie eintippen, merken oder verraten könnten. Ihr YubiKey beweist, dass Sie es sind, ohne ein Geheimnis herauszugeben.</Note>
      </Section>

      <Section id="prinzip" n={2} title="So funktioniert ein Passkey">
        <p>
          Stellen Sie sich ein Siegel vor: Nur Sie besitzen den Siegelstempel, aber jeder kann am Abdruck erkennen, dass er echt ist.
          Ein Passkey besteht genau so aus zwei zusammengehörenden Teilen, einem <strong>Schlüsselpaar</strong>.
        </p>
        <KeyPair />
        <p>Bei jeder Anmeldung passiert dann Folgendes. Die Animation zeigt es Schritt für Schritt, Sie können auch auf einen Schritt klicken.</p>
        <LoginFlow />
        <Note title="Gut zu wissen">Die Aufgabe ist bei jeder Anmeldung neu. Eine abgefangene Unterschrift nützt deshalb niemandem, sie passt nur ein einziges Mal.</Note>
      </Section>

      <Section id="phishing" n={3} title="Warum gefälschte Seiten leer ausgehen">
        <p>
          Jeder Passkey ist fest an die Adresse gebunden, für die er erstellt wurde, bei Ihnen <code>login.microsoft.com</code>.
          Der Browser teilt dem YubiKey bei jeder Anfrage mit, auf welcher Seite Sie gerade sind. Stimmt die Adresse nicht, bleibt der Key stumm.
        </p>
        <PhishingDemo />
      </Section>

      <Section id="hack" n={4} title="Was bei einem Hack passiert">
        <div className="pk-split">
          <div>
            <p>
              Bei Microsoft liegt nur der öffentliche Prüfschlüssel. Mit ihm kann man Unterschriften prüfen, aber keine erstellen.
              Selbst wenn Angreifer diese Daten erbeuten, können sie sich damit nirgends anmelden.
            </p>
            <p>Es gibt also keine Passwortliste, die gestohlen und ausprobiert werden könnte.</p>
          </div>
          <div className="pk-vault pk-reveal" aria-hidden>
            <div className="pk-vault-row"><Icon name="shield" size={18} /><span>Prüfschlüssel</span><em>nutzlos für Angreifer</em></div>
            <div className="pk-vault-row"><Icon name="shield" size={18} /><span>Prüfschlüssel</span><em>nutzlos für Angreifer</em></div>
            <div className="pk-vault-row strike"><Icon name="key" size={18} /><span>Passwörter</span><em>gibt es nicht</em></div>
          </div>
        </div>
      </Section>

      <Section id="pin" n={5} title="PIN und Berührung: zwei Beweise in einem">
        <div className="pk-cards">
          <Card icon="user" title="Die PIN beweist: Sie sind es">
            Die PIN wird nur vom YubiKey selbst geprüft und verlässt ihn nie. Sie ist kein Passwort, denn ohne den Key ist sie wertlos.
            Nach 8 falschen Versuchen sperrt sich der Key.
          </Card>
          <Card icon="check" title="Die Berührung beweist: Sie sind da">
            Die goldene Fläche reagiert nur auf einen echten Finger. Ein Angreifer aus der Ferne kann den Key also nicht heimlich benutzen.
          </Card>
          <Card icon="key" title="Der Key beweist: dieses Gerät">
            Der geheime Schlüssel ist im Chip eingeschlossen. Man kann ihn nicht kopieren, auch nicht mit Zugriff auf Ihren PC.
          </Card>
        </div>
        <Note title="Darum reicht eine kurze PIN">Wer Ihre PIN kennt, braucht trotzdem Ihren Key, und wer Ihren Key findet, kennt Ihre PIN nicht. Erst beides zusammen öffnet die Tür.</Note>
      </Section>

      <Section id="vergleich" n={6} title="Passwort und Passkey im Vergleich">
        <div className="pk-table-wrap">
          <table className="pk-table">
            <thead><tr><th>Frage</th><th>Passwort</th><th>Passkey (YubiKey)</th></tr></thead>
            <tbody>
              {[
                ['Muss ich mir etwas merken?', 'Ja, für jeden Dienst ein anderes', 'Nur eine kurze PIN'],
                ['Funktioniert eine gefälschte Seite?', 'Ja, wenn man darauf hereinfällt', 'Nein, der Key antwortet nicht'],
                ['Was nützt ein Datenleck beim Anbieter?', 'Passwörter können geknackt werden', 'Nichts, dort liegt nur der Prüfschlüssel'],
                ['Kann jemand aus der Ferne mitlesen?', 'Ja, beim Eintippen oder per Schadsoftware', 'Nein, es wird kein Geheimnis übertragen'],
                ['Kann man es kopieren?', 'Ja, beliebig oft', 'Nein, der Schlüssel steckt im Chip'],
                ['Wie schnell ist die Anmeldung?', 'Tippen, vertippen, zurücksetzen', 'Einstecken, PIN, berühren'],
              ].map(([q, a, b]) => (
                <tr key={q}><td>{q}</td><td className="bad"><Icon name="x" size={14} />{a}</td><td className="good"><Icon name="check" size={14} />{b}</td></tr>
              ))}
            </tbody>
          </table>
        </div>
      </Section>

      <Section id="standard" n={7} title="Wer steckt dahinter?">
        <p>
          Passkeys sind kein Sonderweg eines Herstellers, sondern ein offener Standard namens <strong>FIDO2</strong> mit dem Browser-Teil <strong>WebAuthn</strong>.
          Entwickelt wurde er von der FIDO-Allianz, zu der unter anderem Microsoft, Google, Apple und Yubico gehören.
        </p>
        <div className="pk-cards">
          <Card icon="key" title="Passkey im Sicherheitsschlüssel">So wie Ihr YubiKey: Der Passkey ist an genau dieses Gerät gebunden und verlässt es nie. Das ist die stärkste Form.</Card>
          <Card icon="monitor" title="Passkey im Handy oder PC">Passkeys können auch in Windows Hello, im Handy oder im Passwortmanager liegen und werden dort oft über die Cloud synchronisiert.</Card>
          <Card icon="shield" title="Phishing-resistent">Microsoft stuft Sicherheitsschlüssel als „phishing-resistent“ ein, die höchste Stufe der Anmeldesicherheit in Entra ID.</Card>
        </div>
      </Section>

      <Section id="grenzen" n={8} title="Ehrlich gesagt: die Nachteile">
        <ul className="pk-list">
          <li><strong>Der Key muss dabei sein.</strong> Ohne YubiKey keine Anmeldung. Behandeln Sie ihn wie Ihren Haustürschlüssel.</li>
          <li><strong>Verloren ist verloren.</strong> Ein verlorener Key lässt sich nicht wiederherstellen. Die IT sperrt ihn und gibt Ihnen einen neuen, Ihre Daten bleiben dabei sicher.</li>
          <li><strong>Ein Anschluss wird gebraucht.</strong> Der Key braucht einen passenden USB-Anschluss oder NFC am Handy. Für andere Anschlüsse gibt es Adapter.</li>
          <li><strong>Es ist ungewohnt.</strong> Die ersten Anmeldungen fühlen sich anders an. Nach wenigen Tagen geht es schneller als jedes Passwort.</li>
        </ul>
      </Section>

      <div className="pk-cta pk-reveal">
        <div><strong>Bereit?</strong><span className="muted">Die Schritt-für-Schritt-Anleitung zeigt, wie Sie sich mit Ihrem YubiKey anmelden.</span></div>
        <Link className="btn primary" to={'/passkey/anleitung' + keep}>Zur Anleitung</Link>
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
      <div className="pk-mock-head"><Cloud /><b>Anmelden</b></div>
      <div className="pk-mock-field">name@firma.de</div>
      <div className="pk-mock-btn hl"><KeyArt size={26} />Gesicht, Fingerabdruck, PIN oder Sicherheitsschlüssel</div>
    </div>
  );
}

function PinArt() {
  return (
    <div className="pk-mock">
      <div className="pk-mock-head"><Icon name="shield" size={20} /><b>Windows-Sicherheit</b></div>
      <div className="small">Geben Sie die PIN Ihres Sicherheitsschlüssels ein.</div>
      <div className="pk-mock-field pin"><span className="pk-dots typing"><i /><i /><i /><i /><i /><i /></span></div>
      <div className="pk-mock-ok">OK</div>
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
      <div className="pk-phone-body"><div className="pk-nfc-waves"><span /><span /><span /></div><small>Key an die Rückseite halten</small></div>
      <div className="pk-phone-key"><KeyArt size={84} /></div>
    </div>
  );
}

function Guide({ brand, keep }: { brand: PublicBrand | null; keep: string }) {
  const toc = [
    { id: 'kurz', label: 'Was ist ein Passkey?', icon: 'info' as const },
    { id: 'key', label: 'Ihr YubiKey', icon: 'key' as const },
    { id: 'anmelden', label: 'Am PC anmelden', icon: 'monitor' as const },
    { id: 'erste', label: 'Erste Anmeldung und neue PIN', icon: 'user' as const },
    { id: 'handy', label: 'Am Handy anmelden', icon: 'share' as const },
    { id: 'pin', label: 'PIN selbst ändern', icon: 'settings' as const },
    { id: 'regeln', label: 'Sicher aufbewahren', icon: 'shield' as const },
    { id: 'hilfe', label: 'Es klappt nicht', icon: 'alert' as const },
  ];
  return (
    <>
      <Hero eyebrow="Schritt für Schritt" title="So geht's mit dem YubiKey" art={<div className="pk-hero-touch"><TouchArt /></div>}>
        Ihr Konto ist durch einen Passkey auf Ihrem YubiKey geschützt. Statt eines Passworts stecken Sie den Key ein,
        geben Ihre PIN ein und berühren die goldene Fläche. Diese Anleitung zeigt alle Schritte.
      </Hero>
      <Toc items={toc} />

      <Section id="kurz" n={1} title="Was ist ein Passkey?">
        <p>
          Ein Passkey ist ein digitaler Schlüssel, der fest in Ihrem YubiKey steckt. Er funktioniert nur zusammen mit Ihrer PIN und nur auf der echten
          Microsoft-Anmeldeseite. Phishing-Mails und gefälschte Seiten laufen damit ins Leere.
        </p>
        <Note title="Mehr erfahren">Wie das genau funktioniert, zeigt die Seite <Link to={'/passkey' + keep}>Passkey verstehen</Link> mit animierten Beispielen.</Note>
      </Section>

      <Section id="key" n={2} title="Ihr YubiKey">
        <div className="pk-anatomy">
          <div className="pk-anatomy-art"><KeyArt size={260} /></div>
          <ul>
            <li><span className="pk-dot gold" /><span><strong>Goldene Fläche:</strong> zum Berühren. Sie blinkt, wenn der Key auf Sie wartet.</span></li>
            <li><span className="pk-dot metal" /><span><strong>Stecker:</strong> USB-A oder USB-C, je nach Modell. Passt nicht? Ein Adapter hilft.</span></li>
            <li><span className="pk-dot ring" /><span><strong>Öse:</strong> für den Schlüsselbund. Dort ist der Key am besten aufgehoben.</span></li>
            <li><span className="pk-dot nfc" /><span><strong>NFC</strong> (Modelle mit „NFC“ im Namen): auch kontaktlos am Handy nutzbar.</span></li>
          </ul>
        </div>
      </Section>

      <Section id="anmelden" n={3} title="Am PC anmelden">
        <div className="pk-steps">
          <Step n={1} title="Key einstecken" art={<PlugArt />}>Stecken Sie den YubiKey in einen freien USB-Anschluss. Es passiert noch nichts, das ist richtig so.</Step>
          <Step n={2} title="Anmeldeoption wählen" art={<OptionsArt />}>
            Geben Sie bei der Microsoft-Anmeldung Ihre E-Mail-Adresse ein. Falls nach einem Passwort gefragt wird, wählen Sie
            „Andere Anmeldemöglichkeiten“ und dann „Gesicht, Fingerabdruck, PIN oder Sicherheitsschlüssel“.
          </Step>
          <Step n={3} title="PIN eingeben" art={<PinArt />}>Windows oder der Browser fragt nach der PIN des Sicherheitsschlüssels. Geben Sie Ihre PIN ein und bestätigen Sie.</Step>
          <Step n={4} title="Goldene Fläche berühren" art={<TouchArt />}>Sobald der Key blinkt, berühren Sie kurz die goldene Fläche. Fertig, Sie sind angemeldet.</Step>
        </div>
        <Note kind="ok" title="Beim nächsten Mal">Microsoft merkt sich, dass Sie einen Sicherheitsschlüssel nutzen, und bietet ihn direkt an.</Note>
      </Section>

      <Section id="erste" n={4} title="Erste Anmeldung und neue PIN">
        <p>Ihre erste PIN steht im PIN-Brief, den Sie mit dem Key erhalten haben.</p>
        <ol className="pk-list numbered">
          <li>Melden Sie sich wie oben beschrieben an und geben Sie die PIN aus dem Brief ein.</li>
          <li>Steht im Brief, dass ein PIN-Wechsel nötig ist, fordert Windows Sie direkt auf, eine <strong>neue PIN</strong> zu wählen. Wie lang sie mindestens sein muss, zeigt Windows an.</li>
          <li>Geben Sie die neue PIN zweimal ein und berühren Sie den Key, wenn er blinkt.</li>
          <li>Vernichten Sie danach den PIN-Brief. Ihre neue PIN kennen nur Sie.</li>
        </ol>
        <Note kind="warn" title="Die IT fragt nie nach Ihrer PIN">Niemand braucht Ihre PIN, auch nicht die IT. Wer danach fragt, will Sie täuschen.</Note>
      </Section>

      <Section id="handy" n={5} title="Am Handy anmelden">
        <div className="pk-split">
          <div>
            <p>In Apps wie Outlook oder Teams auf dem Handy funktioniert der YubiKey ebenfalls.</p>
            <ul className="pk-list">
              <li><strong>Mit NFC:</strong> Wählen Sie „Sicherheitsschlüssel“, geben Sie die PIN ein und halten Sie den Key an die Rückseite des Handys, bis die Anmeldung durch ist.</li>
              <li><strong>Mit USB-C:</strong> Key einstecken, PIN eingeben, goldene Fläche berühren, genau wie am PC.</li>
            </ul>
            <p className="muted small">Wo die NFC-Stelle liegt, hängt vom Handy ab. Meist ist sie oben in der Mitte der Rückseite.</p>
          </div>
          <PhoneArt />
        </div>
      </Section>

      <Section id="pin" n={6} title="PIN selbst ändern">
        <p>Sie können Ihre PIN jederzeit ändern, zum Beispiel wenn jemand zugesehen hat.</p>
        <div className="pk-path">
          {['Einstellungen', 'Konten', 'Anmeldeoptionen', 'Sicherheitsschlüssel', 'Verwalten', 'Sicherheitsschlüssel-PIN „Ändern“'].map((s, i) => (
            <span key={s} style={{ animationDelay: i * 90 + 'ms' }}>{s}</span>
          ))}
        </div>
        <p className="muted small">Windows fragt nach der alten PIN, zweimal nach der neuen und bittet Sie, den Key zu berühren.</p>
      </Section>

      <Section id="regeln" n={7} title="Sicher aufbewahren">
        <div className="pk-cards two">
          <div className="pk-card good">
            <strong><Icon name="check" size={16} /> So ist es gut</strong>
            <ul><li>Am Schlüsselbund, den Sie ohnehin dabeihaben</li><li>PIN nur im Kopf</li><li>Verlust sofort der IT melden</li></ul>
          </div>
          <div className="pk-card bad">
            <strong><Icon name="x" size={16} /> Lieber nicht</strong>
            <ul><li>PIN auf einen Zettel am Key schreiben</li><li>Key dauerhaft im Firmen-PC stecken lassen</li><li>Key an Kollegen verleihen</li></ul>
          </div>
        </div>
      </Section>

      <Section id="hilfe" n={8} title="Es klappt nicht">
        <div className="pk-faq">
          {[
            ['Der Key blinkt nicht.', 'Ziehen Sie den Key ab und stecken Sie ihn erneut ein, gern in einen anderen Anschluss. Bei USB-Hubs und Dockingstationen hilft oft ein Anschluss direkt am Gerät.'],
            ['Es wird nur nach einem Passwort gefragt.', 'Klicken Sie auf „Andere Anmeldemöglichkeiten“ bzw. „Anmeldeoptionen“ und wählen Sie „Gesicht, Fingerabdruck, PIN oder Sicherheitsschlüssel“.'],
            ['Windows bietet Windows Hello statt des Keys an.', 'Wählen Sie im Fenster „Weitere Optionen“ und dann „Sicherheitsschlüssel“.'],
            ['Die PIN wird nicht angenommen.', 'Achten Sie auf Groß- und Kleinschreibung, falls Ihre PIN Buchstaben enthält. Nach 8 Fehlversuchen sperrt sich der Key, dann hilft nur die IT.'],
            ['Das Fenster ist einfach verschwunden.', 'Für die Berührung bleiben etwa 30 Sekunden. Starten Sie die Anmeldung einfach noch einmal.'],
            ['Ich habe meinen Key verloren.', 'Melden Sie es sofort der IT. Der Key wird gesperrt, und Sie bekommen einen neuen. Ein Finder kann ohne Ihre PIN nichts damit anfangen.'],
            ['Ich habe meine PIN vergessen.', 'Die PIN lässt sich nicht auslesen. Die IT setzt den Key zurück und richtet ihn neu für Sie ein.'],
          ].map(([q, a]) => (
            <details key={q}>
              <summary>{q}</summary>
              <p>{a}</p>
            </details>
          ))}
        </div>
        {brand?.contact && <Note title="Weiterhin Probleme?">Ihre IT hilft Ihnen gern weiter: <strong>{brand.contact}</strong></Note>}
      </Section>

      <div className="pk-cta pk-reveal">
        <div><strong>Neugierig, warum das so sicher ist?</strong><span className="muted">Animiert erklärt: Schlüsselpaar, Unterschrift und Schutz vor gefälschten Seiten.</span></div>
        <Link className="btn primary" to={'/passkey' + keep}>Passkey verstehen</Link>
      </div>
    </>
  );
}
