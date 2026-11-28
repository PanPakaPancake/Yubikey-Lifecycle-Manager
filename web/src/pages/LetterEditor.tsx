import { Fragment, useCallback, useEffect, useMemo, useRef, useState, type DragEvent, type ReactNode } from 'react';
import { api, ApiError } from '../api';
import { useConfirm, useToast } from '../feedback';
import { Icon } from '../icons';
import { Badge, Card, download, ErrorBox, Modal, Spinner, useAction, useApi } from '../ui';
import '../letter.css';
import { tr } from '../i18n';

interface PropOption { value: string; label: string }
interface PropDef { key: string; label: string; default: string; kind: 'text' | 'multiline' | 'select' | 'toggle'; options?: PropOption[] | null; hint?: string | null }
interface BlockType { type: string; label: string; description: string; props: PropDef[]; halfWidth: boolean; unique: boolean; required: boolean }
interface LetterVariable { name: string; description: string; example: string }
export interface LetterBlock { id: string; type: string; width: 'full' | 'half'; show: 'always' | 'forced' | 'own'; props: Record<string, string> }
interface LetterLayout { version: number; blocks: LetterBlock[] }
interface LetterResponse {
  types: BlockType[]; variables: LetterVariable[]; layout: LetterLayout | null; inherited: LetterLayout;
  sample: Record<string, string | null>; brand: { companyName?: string | null; accent: string; hasLogo: boolean }; infoUrl?: string | null;
}

/** Höchstzahl der Seiten, wie im Server (LetterBlocks.MaxPages). */
const maxPages = 6;
const newId = () => Math.random().toString(36).slice(2, 10);
const sameBlocks = (a: LetterBlock[], b: LetterBlock[]) => JSON.stringify(a) === JSON.stringify(b);
const normalize = (blocks: LetterBlock[]): LetterBlock[] =>
  blocks.map((b) => ({ id: b.id, type: b.type, width: b.width === 'half' ? 'half' : 'full', show: b.show ?? 'always', props: { ...(b.props ?? {}) } }));

const showLabels: Record<LetterBlock['show'], string> = {
  always: tr("Immer"),
  forced: tr("Nur mit Initial-PIN (PIN-Wechsel erzwungen)"),
  own: tr("Nur mit dauerhafter PIN (kein PIN-Wechsel)"),
};

/** Symbole der Bausteine in der Palette (24×24, Strich). */
const blockIcons: Record<string, ReactNode> = {
  header: <><rect x="3" y="4" width="18" height="16" rx="2" /><path d="M6 8h6" /><rect x="14" y="7" width="4" height="3" rx=".5" /></>,
  heading: <path d="M6 5v14M18 5v14M6 12h12" />,
  recipient: <><circle cx="12" cy="8" r="3.5" /><path d="M5 20a7 7 0 0 1 14 0" /></>,
  text: <path d="M4 6h16M4 10h16M4 14h12M4 18h9" />,
  pin: <><rect x="2.5" y="7" width="19" height="10" rx="2.5" /><path d="M7 12h.01M10.5 12h.01M14 12h.01M17.5 12h.01" strokeWidth="2.6" /></>,
  steps: <><rect x="3" y="5" width="7" height="6" rx="1.5" /><rect x="14" y="5" width="7" height="6" rx="1.5" /><rect x="3" y="14" width="7" height="6" rx="1.5" /><rect x="14" y="14" width="7" height="6" rx="1.5" /></>,
  pinChange: <><path d="M4 12a8 8 0 0 1 14-5.3L20 9" /><path d="M20 4v5h-5" /><path d="M20 12a8 8 0 0 1-14 5.3L4 15" /><path d="M4 20v-5h5" /></>,
  hints: <><path d="M10 6h10M10 12h10M10 18h10" /><path d="m3.5 6 1.2 1.2L7 5M3.5 12l1.2 1.2L7 11M3.5 18l1.2 1.2L7 17" /></>,
  callout: <><rect x="3" y="5" width="18" height="14" rx="2" /><path d="M7 5v14" /><path d="M10 10h7M10 14h5" /></>,
  info: <><path d="M10 14a4 4 0 0 0 5.7 0l3-3a4 4 0 0 0-5.7-5.7l-1 1" /><path d="M14 10a4 4 0 0 0-5.7 0l-3 3a4 4 0 0 0 5.7 5.7l1-1" /></>,
  divider: <><path d="M3 12h18" /><path d="M8 7h8M8 17h8" opacity=".4" /></>,
  pageBreak: <><path d="M6 3v5h12V3" /><path d="M6 21v-5h12v5" /><path d="M3 12h3M9 12h2M13 12h2M18 12h3" /></>,
  spacer: <><path d="M12 4v16" /><path d="m8 8 4-4 4 4M8 16l4 4 4-4" /></>,
};

function BlockIcon({ type, size = 18 }: { type: string; size?: number }) {
  return (
    <svg width={size} height={size} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">
      {blockIcons[type] ?? blockIcons.text}
    </svg>
  );
}

type Drag = { kind: 'new'; type: string } | { kind: 'move'; id: string };

/**
 * Brief-Editor für den PIN-Brief: Bausteine aus der Palette auf das Blatt ziehen, verschieben, halb oder voll breit stellen
 * und mit Texten und {{Platzhaltern}} füllen. Die Seite zeigt den Brief mit Beispieldaten, die PDF-Vorschau das genaue Ergebnis.
 * Ohne tenantId: Vorgabe des Partners. Mit tenantId: ohne eigenen Aufbau übernimmt der Tenant den Brief des Partners.
 */
export function LetterEditorCard({ tenantId, editable }: { tenantId?: string; editable: boolean }) {
  const qs = tenantId ? '?tenantId=' + tenantId : '';
  const { data, error, reload } = useApi<LetterResponse>('/api/branding/letter' + qs, [tenantId]);
  const [blocks, setBlocks] = useState<LetterBlock[]>([]);
  const [history, setHistory] = useState<LetterBlock[][]>([]);
  const [selected, setSelected] = useState<string | null>(null);
  const [forced, setForced] = useState(true);
  const [drag, setDrag] = useState<Drag | null>(null);
  const [drop, setDrop] = useState<number | null>(null);
  const [pdf, setPdf] = useState<{ url?: string; busy: boolean; error?: string } | null>(null);
  const { busy, error: actError, run, setError } = useAction();
  const toast = useToast();
  const confirm = useConfirm();

  const saved = useMemo(() => normalize((data?.layout ?? data?.inherited)?.blocks ?? []), [data]);
  const inheriting = !!data && !data.layout;
  useEffect(() => { if (data) { setBlocks(saved); setHistory([]); setSelected(null); } }, [data]); // eslint-disable-line react-hooks/exhaustive-deps

  const types = useMemo(() => new Map((data?.types ?? []).map((t) => [t.type, t])), [data]);
  const dirty = !!data && !sameBlocks(blocks, saved);

  /** Jede Änderung landet im Verlauf, damit „Rückgängig“ sie zurücknehmen kann. */
  const change = useCallback((fn: (b: LetterBlock[]) => LetterBlock[]) => {
    setBlocks((cur) => {
      const next = fn(cur);
      if (next !== cur) setHistory((h) => [...h.slice(-49), cur]);
      return next;
    });
  }, []);
  const undo = useCallback(() => {
    setHistory((h) => {
      if (!h.length) return h;
      setBlocks(h[h.length - 1]);
      return h.slice(0, -1);
    });
  }, []);

  useEffect(() => {
    if (!editable) return;
    const onKey = (e: KeyboardEvent) => {
      const tag = (e.target as HTMLElement)?.tagName;
      if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === 'z' && tag !== 'INPUT' && tag !== 'TEXTAREA') { e.preventDefault(); undo(); }
    };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [editable, undo]);

  if (!data) return <Card title={tr("PIN-Brief gestalten")} className="mt"><ErrorBox error={error} />{!error && <Spinner />}</Card>;

  const sel = blocks.find((b) => b.id === selected) ?? null;
  const present = (type: string) => blocks.some((b) => b.type === type);

  const create = (type: string): LetterBlock => ({ id: newId(), type, width: 'full', show: 'always', props: {} });
  const insertAt = (type: string, index: number) => {
    const t = types.get(type);
    if (!t || (t.unique && present(type))) return;
    const b = create(type);
    change((cur) => [...cur.slice(0, index), b, ...cur.slice(index)]);
    setSelected(b.id);
  };
  const add = (type: string) => {
    const i = sel ? blocks.indexOf(sel) + 1 : blocks.length;
    insertAt(type, i);
  };
  const moveTo = (id: string, index: number) => change((cur) => {
    const from = cur.findIndex((b) => b.id === id);
    if (from < 0) return cur;
    const to = index > from ? index - 1 : index;
    if (to === from) return cur;
    const next = cur.filter((b) => b.id !== id);
    next.splice(to, 0, cur[from]);
    return next;
  });
  const update = (id: string, patch: Partial<LetterBlock>) => change((cur) => cur.map((b) => (b.id === id ? { ...b, ...patch } : b)));
  const setProp = (id: string, key: string, value: string | undefined) => change((cur) => cur.map((b) => {
    if (b.id !== id) return b;
    const props = { ...b.props };
    if (value === undefined) delete props[key]; else props[key] = value;
    return { ...b, props };
  }));
  const remove = (id: string) => { change((cur) => cur.filter((b) => b.id !== id)); if (selected === id) setSelected(null); };
  const duplicate = (id: string) => {
    const i = blocks.findIndex((b) => b.id === id);
    if (i < 0) return;
    const copy = { ...blocks[i], id: newId(), props: { ...blocks[i].props } };
    change((cur) => [...cur.slice(0, i + 1), copy, ...cur.slice(i + 1)]);
    setSelected(copy.id);
  };
  /** Seiten: jeder Baustein „Neue Seite“ beginnt eine neue. breakIndex ist die Stelle des Umbruchs (-1 für Seite 1), end exklusiv. */
  const pages: { start: number; end: number; breakIndex: number }[] = [];
  {
    let start = 0, breakIndex = -1;
    blocks.forEach((b, i) => {
      if (b.type !== 'pageBreak') return;
      pages.push({ start, end: i, breakIndex });
      breakIndex = i; start = i + 1;
    });
    pages.push({ start, end: blocks.length, breakIndex });
  }
  const addPage = () => {
    const b = create('pageBreak');
    change((cur) => [...cur, b]);
    setSelected(null);
  };
  const step = (id: string, dir: -1 | 1) => {
    const i = blocks.findIndex((b) => b.id === id);
    const j = i + dir;
    if (i < 0 || j < 0 || j >= blocks.length) return;
    change((cur) => { const n = [...cur]; [n[i], n[j]] = [n[j], n[i]]; return n; });
  };

  // ----- Ziehen und Ablegen -----
  const onDragStartNew = (e: DragEvent, type: string) => {
    e.dataTransfer.effectAllowed = 'copy';
    e.dataTransfer.setData('text/plain', type);
    setDrag({ kind: 'new', type });
  };
  const onDragStartMove = (e: DragEvent, id: string) => {
    e.dataTransfer.effectAllowed = 'move';
    e.dataTransfer.setData('text/plain', id);
    setDrag({ kind: 'move', id });
    setSelected(id);
  };
  const endDrag = () => { setDrag(null); setDrop(null); };
  /** Einfügestelle über einem Baustein: halbe Bausteine links/rechts, volle oben/unten. */
  const onDragOverBlock = (e: DragEvent, index: number, half: boolean) => {
    if (!drag) return;
    e.preventDefault();
    e.stopPropagation();
    const r = (e.currentTarget as HTMLElement).getBoundingClientRect();
    const after = half ? e.clientX > r.left + r.width / 2 : e.clientY > r.top + r.height / 2;
    setDrop(index + (after ? 1 : 0));
  };
  const onDrop = (e: DragEvent) => {
    e.preventDefault();
    if (drag && drop !== null) {
      if (drag.kind === 'new') insertAt(drag.type, drop);
      else moveTo(drag.id, drop);
    }
    endDrag();
  };

  // ----- Speichern und Vorschau -----
  const save = () => run(async () => {
    await api.put('/api/branding/letter' + qs, { layout: { version: 1, blocks } });
    toast.ok(tr("PIN-Brief gespeichert"));
    reload();
  });
  const resetToInherited = async () => {
    const ok = await confirm({
      title: tenantId ? tr("Globalen Brief übernehmen?") : tr("Auf den Standardbrief zurücksetzen?"),
      message: tenantId
        ? tr("Der eigene Brief dieses Tenants wird gelöscht. Danach gilt wieder der Brief aus den globalen Einstellungen.")
        : tr("Der gestaltete Brief wird gelöscht. Danach gilt wieder der Standardbrief (Tenants mit eigenem Brief behalten ihren)."),
      confirmLabel: tr("Zurücksetzen"), danger: true,
    });
    if (!ok) return;
    await run(async () => {
      await api.put('/api/branding/letter' + qs, { layout: null });
      toast.ok(tenantId ? tr("Tenant übernimmt wieder den globalen Brief") : tr("Standardbrief wiederhergestellt"));
      reload();
    });
  };
  const openPdf = async () => {
    setPdf({ busy: true });
    try {
      const res = await fetch('/api/branding/letter/preview.pdf' + qs, {
        method: 'POST', credentials: 'same-origin',
        headers: { 'X-YLM-CSRF': '1', 'Content-Type': 'application/json' },
        body: JSON.stringify({ layout: { version: 1, blocks }, pinChangeForced: forced }),
      });
      if (!res.ok) {
        const j = await res.json().catch(() => ({}));
        throw new ApiError(res.status, j.code ?? 'error', j.message ?? tr("Fehler {status}", { status: res.status }));
      }
      const blob = await res.blob();
      setPdf({ busy: false, url: URL.createObjectURL(blob.type ? blob : new Blob([blob], { type: 'application/pdf' })) });
    } catch (e) {
      setPdf({ busy: false, error: e instanceof Error ? e.message : String(e) });
    }
  };
  const closePdf = () => { if (pdf?.url) URL.revokeObjectURL(pdf.url); setPdf(null); };

  const fill = (text: string) => text.replace(/\{\{\s*([A-Za-zÄÖÜäöüß]+)\s*\}\}/g, (m, name: string) => (name in data.sample ? data.sample[name] ?? '' : m));
  const ctx: SheetCtx = { types, fill, forced, brand: data.brand, logoUrl: '/api/branding/logo?effective=true' + (tenantId ? '&tenantId=' + tenantId : ''), infoUrl: data.infoUrl };

  /** Ein Baustein auf dem Blatt; i ist seine Stelle im gesamten Brief (über alle Seiten). */
  const renderBlock = (b: LetterBlock, i: number) => {
    const t = types.get(b.type);
    const visible = b.show === 'always' || (b.show === 'forced') === forced;
    const hidden = !visible || (b.type === 'info' && !data.infoUrl);
    const half = b.width === 'half';
    const cls = ['lb-block', half ? 'lb-half' : '', hidden ? 'lb-hidden' : '', selected === b.id ? 'lb-selected' : '',
      drag?.kind === 'move' && drag.id === b.id ? 'lb-dragging' : '',
      drop === i ? (half ? 'lb-drop-left' : 'lb-drop-top') : '',
      drop === i + 1 && (i === blocks.length - 1 || blocks[i + 1]?.width !== 'half' || !half) ? (half ? 'lb-drop-right' : 'lb-drop-bottom') : ''].filter(Boolean).join(' ');
    return (
      <div key={b.id} className={cls} draggable={editable} tabIndex={0} role="button"
        aria-label={`${t?.label ?? b.type}${hidden ? tr(" (in dieser Variante ausgeblendet)") : ''}`}
        onDragStart={(e) => onDragStartMove(e, b.id)} onDragEnd={endDrag}
        onDragOver={(e) => onDragOverBlock(e, i, half)}
        onClick={(e) => { e.stopPropagation(); setSelected(b.id); }}
        onKeyDown={(e) => {
          if (!editable || e.target !== e.currentTarget) return;
          if (e.key === 'Delete' && !t?.required) remove(b.id);
          if (e.altKey && e.key === 'ArrowUp') { e.preventDefault(); step(b.id, -1); }
          if (e.altKey && e.key === 'ArrowDown') { e.preventDefault(); step(b.id, 1); }
        }}>
        <span className="lb-tag"><BlockIcon type={b.type} size={12} />{t?.label}{b.show !== 'always' && <em>{b.show === 'forced' ? tr("nur Initial-PIN") : tr("nur dauerhafte PIN")}</em>}</span>
        {editable && (
          <span className="lb-tools" onClick={(e) => e.stopPropagation()}>
            <button type="button" title={tr("Nach oben (Alt+↑)")} aria-label={tr("Nach oben")} disabled={i === 0} onClick={() => step(b.id, -1)}>↑</button>
            <button type="button" title={tr("Nach unten (Alt+↓)")} aria-label={tr("Nach unten")} disabled={i === blocks.length - 1} onClick={() => step(b.id, 1)}>↓</button>
            {t?.halfWidth && (
              <button type="button" title={half ? tr("Volle Breite") : tr("Halbe Breite (zwei nebeneinander)")} onClick={() => update(b.id, { width: half ? 'full' : 'half' })}>{half ? '▭' : '◫'}</button>
            )}
            {!t?.unique && <button type="button" title={tr("Duplizieren")} aria-label={tr("Duplizieren")} onClick={() => duplicate(b.id)}><Icon name="copy" size={12} /></button>}
            {!t?.required && <button type="button" title={tr("Entfernen (Entf)")} aria-label={tr("Entfernen")} onClick={() => remove(b.id)}><Icon name="trash" size={12} /></button>}
          </span>
        )}
        {hidden
          ? <div className="lb-hidden-note">{b.type === 'info' && !data.infoUrl ? tr("Erscheint, sobald die Adresse der Plattform eingetragen ist.") : tr("In der Variante „{n}“ ausgeblendet.", { n: forced ? 'Initial-PIN' : tr("Dauerhafte PIN") })}</div>
          : <SheetBlock block={b} ctx={ctx} />}
      </div>
    );
  };

  return (
    <Card
      title={<span id="letter">{tr("PIN-Brief gestalten")}</span>}
      className="mt"
      actions={inheriting
        ? <Badge tone="neutral">{tenantId ? tr("Übernimmt den globalen Brief") : tr("Standardbrief")}</Badge>
        : <Badge tone="info">{tenantId ? tr("Eigener Brief für diesen Tenant") : tr("Eigener Brief")}</Badge>}
    >
      <p className="muted small" style={{ marginTop: 0 }}>
        {editable
          ? 'Ziehen Sie Bausteine aus der Leiste auf das Blatt und verschieben Sie sie per Maus. Ein Klick auf einen Baustein öffnet rechts seine Texte; Platzhalter wie {{Vorname}} werden beim Drucken ersetzt. Zwei halbe Bausteine stehen nebeneinander. Mit „Seite hinzufügen“ oder dem Baustein „Neue Seite“ verteilen Sie den Brief auf mehrere Seiten.'
          : tr("So sieht der PIN-Brief aus. Zum Ändern fehlt Ihnen die Berechtigung.")}
        {editable && tenantId && inheriting && tr(" Sobald Sie hier etwas speichern, bekommt dieser Tenant einen eigenen Brief.")}
      </p>

      <div className="lb-toolbar">
        <div className="segmented" role="group" aria-label={tr("Variante der Vorschau")}>
          <button className={forced ? 'on' : ''} aria-pressed={forced} onClick={() => setForced(true)}>{tr("Initial-PIN (Wechsel erzwungen)")}</button>
          <button className={!forced ? 'on' : ''} aria-pressed={!forced} onClick={() => setForced(false)}>{tr("Dauerhafte PIN")}</button>
        </div>
        <span className="lb-spacer" />
        {editable && <button type="button" className="ghost small" disabled={!history.length} onClick={undo} title={tr("Rückgängig (Strg+Z)")}><Icon name="history" size={14} />{tr("Rückgängig")}</button>}
        {editable
          ? <button type="button" className="ghost small" onClick={openPdf}><Icon name="download" size={14} />{tr("PDF-Vorschau")}</button>
          : <button type="button" className="ghost small" onClick={() => download('/api/branding/preview.pdf' + qs)}><Icon name="download" size={14} />{tr("Muster-PIN-Brief")}</button>}
      </div>

      <div className={'letter-builder' + (editable ? '' : ' lb-readonly')}>
        {editable && (
          <aside className="lb-palette" aria-label={tr("Bausteine")}>
            <div className="lb-palette-title">{tr("Bausteine")}</div>
            {data.types.map((t) => {
              const used = t.unique && present(t.type);
              return (
                <button key={t.type} type="button" className="lb-tile" draggable={!used} disabled={used}
                  title={used ? tr("{label}: steht schon im Brief", { label: t.label }) : tr("{description} Ziehen oder klicken zum Einfügen.", { description: t.description })}
                  onDragStart={(e) => onDragStartNew(e, t.type)} onDragEnd={endDrag} onClick={() => add(t.type)}>
                  <span className="lb-tile-icon"><BlockIcon type={t.type} /></span>
                  <span className="lb-tile-label">{t.label}</span>
                </button>
              );
            })}
          </aside>
        )}

        <div className="lb-stage" onClick={() => setSelected(null)}>
          {pages.map((pg, pi) => {
            const brk = pg.breakIndex >= 0 ? blocks[pg.breakIndex] : null;
            return (
              <Fragment key={brk?.id ?? 'erste-seite'}>
                {brk && (
                  <div style={{ ['--lb-accent' as string]: data.brand.accent }} className={'lb-break' + (selected === brk.id ? ' lb-selected' : '') + (drag?.kind === 'move' && drag.id === brk.id ? ' lb-dragging' : '')}
                    draggable={editable} tabIndex={0} role="button" aria-label={tr("Seitenumbruch vor Seite {n}", { n: pi + 1 })}
                    onDragStart={(e) => onDragStartMove(e, brk.id)} onDragEnd={endDrag}
                    onDragOver={(e) => onDragOverBlock(e, pg.breakIndex, false)} onDrop={onDrop}
                    onClick={(e) => { e.stopPropagation(); setSelected(brk.id); }}
                    onKeyDown={(e) => {
                      if (!editable || e.target !== e.currentTarget) return;
                      if (e.key === 'Delete') remove(brk.id);
                      if (e.altKey && e.key === 'ArrowUp') { e.preventDefault(); step(brk.id, -1); }
                      if (e.altKey && e.key === 'ArrowDown') { e.preventDefault(); step(brk.id, 1); }
                    }}>
                    <span className="lb-break-label">
                      <BlockIcon type="pageBreak" size={14} />{tr("Seite {n}", { n: pi + 1 })}
                      {brk.show !== 'always' && <em>{brk.show === 'forced' ? tr("Umbruch nur mit Initial-PIN") : tr("Umbruch nur mit dauerhafter PIN")}</em>}
                    </span>
                    {editable && (
                      <span className="lb-break-tools" onClick={(e) => e.stopPropagation()}>
                        <button type="button" title={tr("Seitenumbruch einen Baustein nach oben (Alt+↑)")} aria-label={tr("Umbruch nach oben")} disabled={pg.breakIndex === 0} onClick={() => step(brk.id, -1)}>↑</button>
                        <button type="button" title={tr("Seitenumbruch einen Baustein nach unten (Alt+↓)")} aria-label={tr("Umbruch nach unten")} disabled={pg.breakIndex === blocks.length - 1} onClick={() => step(brk.id, 1)}>↓</button>
                        <button type="button" title={tr("Seite entfernen: die Bausteine rücken auf die Seite davor")} onClick={() => remove(brk.id)}><Icon name="trash" size={12} />{tr("Seite entfernen")}</button>
                      </span>
                    )}
                  </div>
                )}
                <div className="lb-paper" style={{ ['--lb-accent' as string]: data.brand.accent }}
                  onDragOver={(e) => { if (drag) { e.preventDefault(); if (e.target === e.currentTarget) setDrop(pg.end); } }}
                  onDrop={onDrop}>
                  <div className="lb-sheet">
                    <div className="lb-pageend" aria-hidden="true"><span>{tr("Seitenende (ungefähr)")}</span></div>
                    {pg.end === pg.start && <div className="lb-empty"
                      onDragOver={(e) => { if (drag) { e.preventDefault(); e.stopPropagation(); setDrop(pg.end); } }}>
                      {pi === 0 ? tr("Ziehen Sie Bausteine hierher.") : tr("Leere Seite: Ziehen Sie Bausteine hierher. Leere Seiten fallen im PDF weg.")}
                    </div>}
                    {blocks.slice(pg.start, pg.end).map((b, k) => renderBlock(b, pg.start + k))}
                    {editable && drag && pg.end > pg.start && <div className={'lb-dropzone' + (drop === pg.end ? ' on' : '')}
                      onDragOver={(e) => { e.preventDefault(); e.stopPropagation(); setDrop(pg.end); }}>{tr("Hier ablegen")}</div>}
                  </div>
                  <div className="lb-footer">{[pages.length > 1 ? tr("Seite {n} von {length}", { n: pi + 1, length: pages.length }) : null, data.sample.Firma, data.sample.Tenant, tr("Die PIN wurde nicht gespeichert.")].filter(Boolean).join(' · ')}</div>
                </div>
              </Fragment>
            );
          })}
          {editable && (
            <div className="lb-addpage">
              <button type="button" className="ghost small" disabled={pages.length >= maxPages}
                title={pages.length >= maxPages ? tr("Höchstens {maxPages} Seiten pro Brief", { maxPages }) : tr("Fügt am Ende eine neue, leere Seite an")}
                onClick={(e) => { e.stopPropagation(); addPage(); }}>
                <BlockIcon type="pageBreak" size={14} />{tr("Seite hinzufügen")}
              </button>
            </div>
          )}
        </div>

        <aside className="lb-panel" aria-label={tr("Eigenschaften")}>
          {sel
            ? <BlockProps key={sel.id} block={sel} type={types.get(sel.type)} variables={data.variables} editable={editable}
                onProp={(k, v) => setProp(sel.id, k, v)} onChange={(p) => update(sel.id, p)}
                onRemove={() => remove(sel.id)} onClose={() => setSelected(null)} />
            : (
              <div className="lb-panel-empty">
                <div className="lb-panel-title">{tr("Eigenschaften")}</div>
                <p className="muted small">{tr("Klicken Sie auf einen Baustein im Brief, um seine Texte, Breite und Sichtbarkeit zu bearbeiten.")}</p>
                <ul className="lb-tips small">
                  <li><strong>{tr("Ziehen")}</strong> {tr("verschiebt Bausteine, auch neben einen anderen halben Baustein.")}</li>
                  <li><strong>{tr("Sichtbarkeit")}</strong> {tr("steuert, ob ein Baustein nur im Brief mit Initial-PIN oder nur mit dauerhafter PIN erscheint.")}</li>
                  <li><strong>{tr("Seite hinzufügen")}</strong> {tr("legt unten eine neue Seite an. Den Umbruch zwischen zwei Seiten können Sie wie einen Baustein verschieben.")}</li>
                  <li>{tr("Oben umschalten, um beide Varianten zu prüfen. Die")} <strong>{tr("PDF-Vorschau")}</strong> {tr("zeigt das genaue Druckbild.")}</li>
                </ul>
              </div>
            )}
        </aside>
      </div>

      <ErrorBox error={actError} />
      {editable && (
        <div className="actions-row lb-actions">
          <button className="primary" disabled={busy || !dirty} onClick={save}>{tr("Speichern")}</button>
          {dirty && <button type="button" onClick={() => { setBlocks(saved); setHistory([]); setError(undefined); }}>{tr("Verwerfen")}</button>}
          {!inheriting && (
            <button type="button" className="ghost" disabled={busy} onClick={resetToInherited}>
              <Icon name="refresh" size={14} />{tenantId ? tr("Wieder globalen Brief übernehmen") : tr("Auf Standardbrief zurücksetzen")}
            </button>
          )}
          {dirty && <span className="muted small">{tr("Ungespeicherte Änderungen")}</span>}
        </div>
      )}

      {pdf && (
        <Modal title={tr("PDF-Vorschau")} onClose={closePdf}
          footer={pdf.url ? <a className="btn" href={pdf.url} download={tr("Muster-PIN-Brief.pdf")}><Icon name="download" size={14} />{tr("Herunterladen")}</a> : undefined}>
          <p className="muted small" style={{ marginTop: 0 }}>{tr("Mit Beispieldaten (Erika Mustermann, PIN 482913), Variante „")}{forced ? tr("Initial-PIN") : tr("Dauerhafte PIN")}“{dirty ? tr(", ungespeicherter Entwurf") : ''}.</p>
          {pdf.busy && <Spinner label={tr("PDF wird erstellt …")} />}
          <ErrorBox error={pdf.error} />
          {pdf.url && <iframe className="lb-pdf" title={tr("PDF-Vorschau PIN-Brief")} src={pdf.url + '#toolbar=0&navpanes=0&view=FitH'} />}
        </Modal>
      )}
    </Card>
  );
}

// ----- Eigenschaften eines Bausteins -----

function BlockProps({ block, type, variables, editable, onProp, onChange, onRemove, onClose }: {
  block: LetterBlock; type?: BlockType; variables: LetterVariable[]; editable: boolean;
  onProp: (key: string, value: string | undefined) => void; onChange: (p: Partial<LetterBlock>) => void; onRemove: () => void; onClose: () => void;
}) {
  const [focused, setFocused] = useState<string | null>(null);
  const refs = useRef<Record<string, HTMLInputElement | HTMLTextAreaElement | null>>({});
  const toast = useToast();
  if (!type) return null;
  const textProps = type.props.filter((p) => p.kind === 'text' || p.kind === 'multiline');

  const insert = (name: string) => {
    const key = focused ?? (textProps.length === 1 ? textProps[0].key : null);
    const el = key ? refs.current[key] : null;
    if (!key || !el) { toast.error(tr("Klicken Sie zuerst in das Textfeld, in das der Platzhalter soll.")); return; }
    const token = `{{${name}}}`;
    const value = el.value;
    const start = el.selectionStart ?? value.length, end = el.selectionEnd ?? value.length;
    onProp(key, value.slice(0, start) + token + value.slice(end));
    requestAnimationFrame(() => { el.focus(); el.setSelectionRange(start + token.length, start + token.length); });
  };

  return (
    <div className="lb-props">
      <div className="lb-props-head">
        <span className="lb-tile-icon"><BlockIcon type={type.type} /></span>
        <div>
          <div className="lb-panel-title">{type.label}</div>
          <div className="muted small">{type.description}</div>
        </div>
        <button type="button" className="icon-btn" aria-label={tr("Schließen")} onClick={onClose}>×</button>
      </div>

      {type.halfWidth && (
        <div className="lb-prop">
          <span className="lb-prop-label">{tr("Breite")}</span>
          <div className="segmented">
            <button className={block.width === 'full' ? 'on' : ''} disabled={!editable} onClick={() => onChange({ width: 'full' })}>{tr("Volle Breite")}</button>
            <button className={block.width === 'half' ? 'on' : ''} disabled={!editable} onClick={() => onChange({ width: 'half' })}>{tr("Halbe Breite")}</button>
          </div>
        </div>
      )}
      {!type.required && (
        <label className="lb-prop">
          <span className="lb-prop-label">{tr("Sichtbarkeit")}</span>
          <select value={block.show} disabled={!editable} onChange={(e) => onChange({ show: e.target.value as LetterBlock['show'] })}>
            {(Object.keys(showLabels) as LetterBlock['show'][]).map((k) => <option key={k} value={k}>{showLabels[k]}</option>)}
          </select>
        </label>
      )}

      {editable && textProps.length > 0 && (
        <div className="le-vars" aria-label={tr("Platzhalter einfügen")}>
          <span className="muted small">{tr("Platzhalter einfügen:")}</span>
          {variables.map((v) => (
            <button key={v.name} type="button" className="le-chip" title={tr("{description}, z. B. „{example}“", { description: v.description, example: v.example })}
              onMouseDown={(e) => e.preventDefault()} onClick={() => insert(v.name)}>{v.name}</button>
          ))}
        </div>
      )}

      {type.props.map((p) => {
        const custom = block.props[p.key] !== undefined && block.props[p.key] !== p.default;
        const value = block.props[p.key] ?? p.default;
        const reset = editable && custom && (
          <button type="button" className="link small" onClick={() => onProp(p.key, undefined)}>{tr("Standard")}</button>
        );
        if (p.kind === 'select') return (
          <label key={p.key} className="lb-prop">
            <span className="lb-prop-label">{p.label}</span>
            <select value={value} disabled={!editable} onChange={(e) => onProp(p.key, e.target.value)}>
              {(p.options ?? []).map((o) => <option key={o.value} value={o.value}>{o.label}</option>)}
            </select>
          </label>
        );
        if (p.kind === 'toggle') return (
          <label key={p.key} className="check lb-prop-check">
            <input type="checkbox" checked={value !== 'false'} disabled={!editable} onChange={(e) => onProp(p.key, e.target.checked ? 'true' : 'false')} />
            {p.label}
          </label>
        );
        const rows = Math.min(10, Math.max(2, value.split('\n').reduce((n, l) => n + Math.max(1, Math.ceil(l.length / 38)), 0)));
        const common = {
          id: `lp-${block.id}-${p.key}`, value, disabled: !editable, maxLength: 1500,
          onChange: (e: { target: { value: string } }) => onProp(p.key, e.target.value),
          onFocus: () => setFocused(p.key),
        };
        return (
          <div key={p.key} className={'lb-prop' + (custom ? ' le-custom' : '')}>
            <div className="lb-prop-label">
              <label htmlFor={common.id}>{p.label}</label>
              {custom && <Badge tone="info">{tr("angepasst")}</Badge>}
              {reset}
            </div>
            {p.kind === 'multiline'
              ? <textarea {...common} rows={rows} ref={(el) => { refs.current[p.key] = el; }} />
              : <input {...common} ref={(el) => { refs.current[p.key] = el; }} />}
            {p.hint && <span className="muted small">{p.hint}</span>}
          </div>
        );
      })}

      {editable && !type.required && (
        <button type="button" className="ghost small lb-remove" onClick={onRemove}><Icon name="trash" size={14} />{type.type === 'pageBreak' ? tr("Seite entfernen") : tr("Baustein entfernen")}</button>
      )}
      {type.required && <p className="muted small">{tr("Die PIN muss im Brief stehen und lässt sich nicht entfernen, nur verschieben.")}</p>}
    </div>
  );
}

// ----- Darstellung der Bausteine auf dem Blatt (angelehnt an das PDF) -----

interface SheetCtx {
  types: Map<string, BlockType>;
  fill: (text: string) => string;
  forced: boolean;
  brand: LetterResponse['brand'];
  logoUrl: string;
  infoUrl?: string | null;
}

function SheetBlock({ block, ctx }: { block: LetterBlock; ctx: SheetCtx }) {
  const t = ctx.types.get(block.type);
  const p = (key: string) => block.props[key] ?? t?.props.find((x) => x.key === key)?.default ?? '';
  const f = (key: string) => ctx.fill(p(key));
  switch (block.type) {
    case 'header': return <SheetHeader ctx={ctx} />;
    case 'heading': return <div className={'lc-heading lc-' + (p('size') || 'l')}>{f('text')}</div>;
    case 'recipient': return (
      <div className="lc-recipient">
        <div>{f('text')}</div>
        {p('keyLine') !== 'false' && <div className="lc-muted lc-small">{ctx.fill('{{Modell}} · Seriennummer {{Seriennummer}} · erstellt am {{Datum}}')}</div>}
      </div>
    );
    case 'text': return p('text').trim() === '-' ? <div className="lc-muted lc-small">{tr("(Kein Text)")}</div> : <div className={'lc-text' + (p('size') === 's' ? ' lc-small' : '')}>{f('text')}</div>;
    case 'pin': {
      const forced = ctx.forced;
      return (
        <div className={'lc-pin' + (forced ? ' lc-forced' : '')}>
          <div className="lc-pin-left">
            <div className="lc-pin-label">
              <span>{(forced ? f('forcedLabel') : f('ownLabel')).toUpperCase()}</span>
              {forced && f('forcedBadge').trim() && <span className="lc-badge">{f('forcedBadge')}</span>}
            </div>
            <div className="lc-digits">{'482913'.split('').map((d, i) => <span key={i}><b>{d}</b></span>)}</div>
          </div>
          <div className="lc-pin-hint">{forced ? f('forcedText') : f('ownText')}</div>
        </div>
      );
    }
    case 'steps': return (
      <div className="lc-steps">
        <div className="lc-heading lc-m">{f('title')}</div>
        <div className="lc-step-grid">
          {[1, 2, 3, 4].map((n) => (
            <div key={n} className="lc-step">
              <div className="lc-step-art"><StepArt n={n} /></div>
              <div className="lc-step-title"><span className="lc-num">{n}</span>{f(`step${n}Title`)}</div>
              <div className="lc-step-text">{f(`step${n}Text`)}</div>
            </div>
          ))}
        </div>
      </div>
    );
    case 'pinChange': return (
      <div className="lc-box lc-accent">
        <div className="lc-box-title">{f('title')}</div>
        {f('text').split('\n').filter((l) => l.trim()).map((l, i) => {
          const m = /^([^:]{1,31}?):\s(.*)$/.exec(l.trim());
          return m && !m[1].includes('//')
            ? <div key={i} className="lc-labeled"><strong>{m[1]}:</strong><span>{m[2]}</span></div>
            : <div key={i} className="lc-line">{l}</div>;
        })}
      </div>
    );
    case 'hints': return (
      <div className="lc-hints">
        <div className="lc-heading lc-s">{f('title')}</div>
        <ul>{f('items').split('\n').filter((l) => l.trim()).map((l, i) => <li key={i}>{l}</li>)}</ul>
      </div>
    );
    case 'callout': return (
      <div className={'lc-box lc-' + (p('tone') || 'accent')}>
        {f('title').trim() && <div className="lc-box-title">{f('title')}</div>}
        <div className="lc-line">{f('text')}</div>
      </div>
    );
    case 'info': return (
      <div className="lc-info">
        <div className="lc-box-title">{f('title')}</div>
        <div>{(ctx.infoUrl ?? 'https://ylm.example/passkey').replace(/^https?:\/\//, '')} →</div>
      </div>
    );
    case 'divider': return <div className="lc-divider"><span /></div>;
    case 'spacer': return <div className={'lc-spacer lc-sp-' + (p('size') || 'm')} />;
    default: return null;
  }
}

function SheetHeader({ ctx }: { ctx: SheetCtx }) {
  const [logo, setLogo] = useState(ctx.brand.hasLogo);
  const name = (ctx.brand.companyName ?? ctx.fill('{{Firma}}')).toUpperCase();
  if (!logo && !name.trim()) return <div className="lc-muted lc-small">{tr("(Kopfzeile ohne Firmenname und Logo)")}</div>;
  return (
    <div className={'lc-header' + (logo ? ' lc-has-logo' : '')}>
      <span className="lc-org">{name}</span>
      {logo && <img src={ctx.logoUrl} alt="" onError={() => setLogo(false)} />}
    </div>
  );
}

/** Vereinfachte Bilder der vier Schritte, wie im PDF. */
function StepArt({ n }: { n: number }) {
  const accent = 'var(--lb-accent)';
  if (n === 1) return (
    <svg viewBox="0 0 100 60" aria-hidden="true">
      <path d="M22 16h30m-6-5 6 5-6 5" stroke={accent} strokeWidth="2" fill="none" strokeLinecap="round" />
      <rect x="10" y="30" width="36" height="16" rx="7" fill="#eef0f4" stroke="#2b2f36" />
      <circle cx="22" cy="38" r="4" fill="#d4a52a" />
      <rect x="46" y="34" width="12" height="8" fill="#e4e7ec" stroke="#2b2f36" strokeWidth=".7" />
      <rect x="62" y="24" width="32" height="28" rx="4" fill="#f1f3f6" stroke="#5b6575" />
    </svg>
  );
  if (n === 2) return (
    <svg viewBox="0 0 100 60" aria-hidden="true">
      <rect x="8" y="4" width="84" height="52" rx="4" fill="#fff" stroke="#d6dbe3" />
      <rect x="14" y="10" width="5" height="5" fill="#f25022" /><rect x="20" y="10" width="5" height="5" fill="#7fba00" />
      <rect x="14" y="16" width="5" height="5" fill="#00a4ef" /><rect x="20" y="16" width="5" height="5" fill="#ffb900" />
      <text x="30" y="19" fontSize="7" fontWeight="700" fill="#1d232e">{tr("Anmelden")}</text>
      <rect x="14" y="26" width="72" height="6" fill="#d6dbe3" />
      <rect x="14" y="37" width="72" height="13" rx="2" fill="color-mix(in srgb, var(--lb-accent) 10%, #fff)" stroke={accent} />
      <rect x="18" y="41" width="14" height="5" rx="2" fill="#eef0f4" stroke="#2b2f36" strokeWidth=".6" />
      <text x="35" y="46" fontSize="4.6" fontWeight="700" fill={accent} textLength="48" lengthAdjust="spacingAndGlyphs">{tr("Sicherheitsschlüssel")}</text>
    </svg>
  );
  if (n === 3) return (
    <svg viewBox="0 0 100 60" aria-hidden="true">
      <text x="8" y="14" fontSize="7" fontWeight="700" fill="#1d232e">{tr("Sicherheitsschlüssel-PIN")}</text>
      <rect x="8" y="19" width="84" height="16" rx="3" fill="#fff" stroke={accent} />
      {[0, 1, 2, 3, 4, 5].map((i) => <circle key={i} cx={16 + i * 9} cy="27" r="2.4" fill="#5b6575" />)}
      <rect x="60" y="41" width="32" height="12" rx="3" fill="color-mix(in srgb, var(--lb-accent) 10%, #fff)" stroke={accent} />
      <text x="76" y="49.5" fontSize="6" fontWeight="700" fill={accent} textAnchor="middle">{tr("OK")}</text>
    </svg>
  );
  return (
    <svg viewBox="0 0 100 60" aria-hidden="true">
      <rect x="40" y="6" width="18" height="34" rx="7" fill="#eef0f4" stroke="#2b2f36" />
      <rect x="44" y="40" width="10" height="14" fill="#e4e7ec" stroke="#2b2f36" strokeWidth=".7" />
      <circle cx="49" cy="22" r="10" fill="none" stroke="#d4a52a" opacity=".45" />
      <circle cx="49" cy="22" r="5" fill="#d4a52a" />
      <ellipse cx="66" cy="22" rx="9" ry="12" fill="#f6d7c3" stroke="#5b6575" />
      <path d="M59 32v22M73 32v22" stroke="#5b6575" />
    </svg>
  );
}
