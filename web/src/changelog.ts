import raw from './changelog.md?raw';

/** Art eines Eintrags, aus dem optionalen Präfix „Neu:“, „Verbessert:“, „Behoben:“ oder „Sicherheit:“. */
export type ChangeKind = 'new' | 'improved' | 'fixed' | 'security' | 'other';
export type Change = { kind: ChangeKind; text: string };
export type Release = { version: string; date?: string; changes: Change[] };

const kinds: Record<string, ChangeKind> = { neu: 'new', verbessert: 'improved', behoben: 'fixed', sicherheit: 'security' };
export const kindLabels: Record<ChangeKind, string> = { new: 'Neu', improved: 'Verbessert', fixed: 'Behoben', security: 'Sicherheit', other: 'Änderung' };

/**
 * Liest Abschnitte „## 1.2.3 · Datum“ mit Stichpunkten „- …“. Dasselbe Format kommt als Release-Beschreibung von GitHub zurück,
 * deshalb gilt der Parser auch für die Notizen einer neuen, noch nicht installierten Version.
 */
export function parseChangelog(md: string): Release[] {
  const out: Release[] = [];
  let cur: Release | null = null;
  for (const line of md.replace(/<!--[\s\S]*?-->/g, '').split(/\r?\n/)) {
    const h = /^##\s+v?(\d+\.\d+\.\d+\S*)(?:\s*[·–-]\s*(.+))?\s*$/.exec(line);
    if (h) { cur = { version: h[1], date: h[2]?.trim(), changes: [] }; out.push(cur); continue; }
    const b = /^\s*[-*]\s+(.+)$/.exec(line);
    // Automatisch erzeugte GitHub-Notizen älterer Releases: Autor/Link abschneiden, Erstbeitrag-Zeilen weglassen.
    if (!b || /made their first contribution/.test(b[1])) continue;
    b[1] = b[1].replace(/\s+by @\S+ in https?:\/\/\S+$/, '');
    if (!cur) { cur = { version: '', changes: [] }; out.push(cur); }
    const m = /^(Neu|Verbessert|Behoben|Sicherheit):\s*(.+)$/i.exec(b[1]);
    cur.changes.push(m ? { kind: kinds[m[1].toLowerCase()], text: m[2] } : { kind: 'other', text: b[1] });
  }
  return out.filter((r) => r.changes.length > 0);
}

/** Der in diese Version eingebaute Verlauf. */
export const changelog: Release[] = parseChangelog(raw);

export type UpdateSummary = {
  currentVersion: string; latestVersion?: string | null; updateAvailable: boolean; releaseUrl?: string | null;
  publishedAt?: string | null; notes?: string | null; error?: string | null; checkedAt: string; canApply: boolean;
};
