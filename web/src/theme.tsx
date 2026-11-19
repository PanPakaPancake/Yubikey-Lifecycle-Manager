import { useEffect, useState } from 'react';
import { Icon } from './icons';

// Hell/Dunkel/System. Gespeichert pro Browser; ohne Wahl folgt die Oberfläche dem Betriebssystem.
type Theme = 'system' | 'light' | 'dark';
const StorageKey = 'ylm-theme';

function read(): Theme {
  try {
    const v = localStorage.getItem(StorageKey);
    return v === 'light' || v === 'dark' ? v : 'system';
  } catch { return 'system'; }
}

export function applyStoredTheme() {
  const t = read();
  const root = document.documentElement;
  if (t === 'system') root.removeAttribute('data-theme');
  else root.setAttribute('data-theme', t);
  // data-dark: tatsächlich dunkel (gewählt oder vom System), damit Designvorschläge nur eine Regel pro Dunkel-Stil brauchen.
  const dark = t === 'dark' || (t === 'system' && !!window.matchMedia?.('(prefers-color-scheme: dark)').matches);
  root.toggleAttribute('data-dark', dark);
}

// Systemwechsel (z. B. Windows schaltet abends auf dunkel) sofort übernehmen.
window.matchMedia?.('(prefers-color-scheme: dark)').addEventListener?.('change', () => applyStoredTheme());

const next: Record<Theme, Theme> = { system: 'light', light: 'dark', dark: 'system' };
const label: Record<Theme, string> = { system: 'Wie System', light: 'Hell', dark: 'Dunkel' };
const icon = { system: 'auto', light: 'sun', dark: 'moon' } as const;

export function ThemeToggle() {
  const [theme, setTheme] = useState<Theme>(read);
  useEffect(() => {
    try { if (theme === 'system') localStorage.removeItem(StorageKey); else localStorage.setItem(StorageKey, theme); } catch { /* ignore */ }
    applyStoredTheme();
  }, [theme]);
  return (
    <button type="button" className="ghost small theme-toggle" onClick={() => setTheme(next[theme])} title={`Darstellung: ${label[theme]} (klicken zum Wechseln)`}>
      <Icon name={icon[theme]} size={16} /> {label[theme]}
    </button>
  );
}
