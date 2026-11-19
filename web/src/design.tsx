import { useEffect, useState } from 'react';
import { Icon } from './icons';

/**
 * Designvorschläge (Prototyp). Jedes Design ist eine CSS-Ebene unter :root[data-design="…"] über styles.css/design.css
 * und ändert nur das Aussehen. Wahl per ?design=… in der Adresse oder über den Umschalter unten rechts; gespeichert pro Browser.
 */
export const designs = [
  { id: '', name: 'Aktuell', hint: 'Stand v0.6 (Blau/Gold, dunkle Seitenleiste)' },
  { id: 'leitfaden', name: '1 · Leitfaden', hint: 'Passkey-Seite konsequent: Hero, nummerierte Abschnitte, Glas' },
  { id: 'kontrollraum', name: '2 · Kontrollraum', hint: 'Dunkel, Cyan/Violett, Leuchten, Live-Anzeigen' },
  { id: 'magazin', name: '3 · Magazin', hint: 'Navigation oben, warme Farben, große Typografie' },
  { id: 'bento', name: '4 · Bento', hint: 'Farbige Bereiche, Kachel-Raster, verspielte Bewegung' },
  { id: 'fokus', name: '5 · Fokus', hint: 'Schmale Icon-Leiste, Graphit + Smaragd, viel Ruhe' },
] as const;

export type DesignId = (typeof designs)[number]['id'];
const StorageKey = 'ylm-design';
const isDesign = (v: string | null): v is DesignId => v !== null && designs.some((d) => d.id === v);

export function readDesign(): DesignId {
  try {
    const fromUrl = new URLSearchParams(window.location.search).get('design');
    if (isDesign(fromUrl)) { localStorage.setItem(StorageKey, fromUrl); return fromUrl; }
    const v = localStorage.getItem(StorageKey);
    return isDesign(v) ? v : '';
  } catch { return ''; }
}

export function applyDesign(d: DesignId) {
  if (d) document.documentElement.setAttribute('data-design', d);
  else document.documentElement.removeAttribute('data-design');
}

export function useDesign() {
  const [design, setDesign] = useState<DesignId>(readDesign);
  useEffect(() => {
    applyDesign(design);
    try { if (design) localStorage.setItem(StorageKey, design); else localStorage.removeItem(StorageKey); } catch { /* ignore */ }
  }, [design]);
  return [design, setDesign] as const;
}

/** Schwebender Umschalter zum Vergleichen der Vorschläge (nur im Prototyp). */
export function DesignPicker({ design, setDesign }: { design: DesignId; setDesign: (d: DesignId) => void }) {
  const [open, setOpen] = useState(false);
  const current = designs.find((d) => d.id === design) ?? designs[0];
  return (
    <div className={'design-picker' + (open ? ' open' : '')}>
      {open && (
        <div className="design-picker-pop" role="listbox" aria-label="Designvorschlag">
          <div className="design-picker-title">Designvorschläge</div>
          {designs.map((d) => (
            <button key={d.id || 'current'} type="button" role="option" aria-selected={d.id === design} className={d.id === design ? 'on' : ''}
              onClick={() => { setDesign(d.id); setOpen(false); }}>
              <span className={'design-swatch sw-' + (d.id || 'aktuell')} aria-hidden />
              <span><strong>{d.name}</strong><small>{d.hint}</small></span>
            </button>
          ))}
        </div>
      )}
      <button type="button" className="design-picker-btn" onClick={() => setOpen(!open)} aria-expanded={open}>
        <Icon name="sliders" size={16} /> Design: {current.name}
      </button>
    </div>
  );
}
