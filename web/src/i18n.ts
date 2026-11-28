// Mehrsprachigkeit der Oberfläche. Die deutschen Texte stehen direkt im Code und sind zugleich die Schlüssel:
//   tr('Key registrieren'), tr('{n} Keys gesamt', { n })
// Übersetzungen liegen in locales/<sprache>.ts. Fehlt eine Übersetzung, erscheint der deutsche Text.
// Prüfung auf Vollständigkeit: npm run i18n:check (läuft auch in der CI).
import en from './locales/en';

export type Language = 'de' | 'en';
export const languages: { id: Language; label: string }[] = [
  { id: 'de', label: 'Deutsch' },
  { id: 'en', label: 'English' },
];

const storageKey = 'ylm-language';
const dictionaries: Record<Language, Record<string, string> | null> = { de: null, en };

function detect(): Language {
  // ?lang=en aus dem Link im PIN-Brief: die Infoseite erscheint in der Sprache des Briefs.
  try {
    const fromUrl = new URLSearchParams(window.location.search).get('lang');
    if (fromUrl === 'de' || fromUrl === 'en') return fromUrl;
  } catch { /* kein Browser */ }
  try {
    const stored = localStorage.getItem(storageKey);
    if (stored === 'de' || stored === 'en') return stored;
  } catch { /* Speicher gesperrt */ }
  const prefs = typeof navigator !== 'undefined' ? (navigator.languages?.length ? navigator.languages : [navigator.language]) : [];
  for (const p of prefs) {
    const l = (p || '').toLowerCase().slice(0, 2);
    if (l === 'de' || l === 'en') return l;
  }
  return 'en';
}

// Wird beim ersten Import festgelegt, also bevor irgendein Modul Texte erzeugt.
let current: Language = detect();
if (typeof document !== 'undefined') {
  document.documentElement.lang = current;
  // Für Downloads per Link (PDF, Excel): der Server liest die Sprache dann aus dem Cookie.
  try { document.cookie = `ylm-lang=${current}; path=/; max-age=31536000; SameSite=Lax`; } catch { /* Cookies gesperrt */ }
}

export const language = (): Language => current;
/** Gebietsschema für Datums- und Zahlenformate. */
export const locale = (): string => (current === 'de' ? 'de-DE' : 'en-GB');

/** Übersetzt einen deutschen Text; {name} wird durch vars.name ersetzt. */
export function tr(text: string, vars?: Record<string, string | number | null | undefined>): string {
  const dict = dictionaries[current];
  const s = (dict && dict[text]) || text;
  return vars ? s.replace(/\{(\w+)\}/g, (m, k: string) => (k in vars ? String(vars[k] ?? '') : m)) : s;
}

/** Ändert die Sprache. Gespeichert wird im Browser; die Seite lädt neu, damit alle Texte wechseln. */
export function setLanguage(l: Language, reload = true) {
  try { localStorage.setItem(storageKey, l); } catch { /* Speicher gesperrt */ }
  if (l !== current && reload) window.location.reload();
}

/** Übernimmt die im Benutzerkonto gespeicherte Sprache (nach der Anmeldung). */
export function adoptUserLanguage(l: string | null | undefined) {
  if ((l === 'de' || l === 'en') && l !== current) setLanguage(l);
}
