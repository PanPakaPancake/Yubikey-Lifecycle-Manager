import { useEffect, useState } from 'react';
import { Icon } from './icons';
import { tr } from './i18n';

// Dunkel/Hell/System. Gespeichert pro Browser; ohne Wahl startet die Oberfläche dunkel (Entscheidung Timo, 2026-11-19).
type Theme = 'system' | 'light' | 'dark';
const StorageKey = 'ylm-theme';

function read(): Theme {
  try {
    const v = localStorage.getItem(StorageKey);
    return v === 'light' || v === 'system' ? v : 'dark';
  } catch { return 'dark'; }
}

export function applyStoredTheme() {
  const t = read();
  const root = document.documentElement;
  if (t === 'system') root.removeAttribute('data-theme');
  else root.setAttribute('data-theme', t);
  // data-dark: tatsächlich dunkel (gewählt oder vom System); der dunkle Bento-Stil hängt daran.
  const dark = t === 'dark' || (t === 'system' && !!window.matchMedia?.('(prefers-color-scheme: dark)').matches);
  root.toggleAttribute('data-dark', dark);
}

// Systemwechsel (z. B. Windows schaltet abends auf dunkel) sofort übernehmen.
window.matchMedia?.('(prefers-color-scheme: dark)').addEventListener?.('change', () => applyStoredTheme());

const next: Record<Theme, Theme> = { dark: 'light', light: 'system', system: 'dark' };
const label: Record<Theme, string> = { system: tr("Wie System"), light: tr("Hell"), dark: tr("Dunkel") };
const icon = { system: 'auto', light: 'sun', dark: 'moon' } as const;

export function ThemeToggle() {
  const [theme, setTheme] = useState<Theme>(read);
  useEffect(() => {
    try { if (theme === 'dark') localStorage.removeItem(StorageKey); else localStorage.setItem(StorageKey, theme); } catch { /* ignore */ }
    applyStoredTheme();
  }, [theme]);
  return (
    <button type="button" className="ghost small theme-toggle" onClick={() => setTheme(next[theme])} title={tr("Darstellung: {n} (klicken zum Wechseln)", { n: label[theme] })}>
      <Icon name={icon[theme]} size={16} /> {label[theme]}
    </button>
  );
}
