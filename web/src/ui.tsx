import { useCallback, useEffect, useRef, useState, type ReactNode } from 'react';
import { Icon, type IconName } from './icons';
import { api, ApiError, statusLabels, type GroupRef, type KeyStatus, type Tenant } from './api';

export function useApi<T>(url: string | null, deps: unknown[] = []) {
  const [data, setData] = useState<T | undefined>();
  const [error, setError] = useState<string | undefined>();
  const [loading, setLoading] = useState(false);
  const load = useCallback(async () => {
    if (!url) return;
    setLoading(true);
    try {
      setData(await api.get<T>(url));
      setError(undefined);
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    } finally {
      setLoading(false);
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [url, ...deps]);
  useEffect(() => { void load(); }, [load]);
  return { data, error, loading, reload: load, setData };
}

/** Runs an async action and keeps busy/error state for buttons. */
export function useAction() {
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | undefined>();
  const run = useCallback(async <T,>(fn: () => Promise<T>): Promise<T | undefined> => {
    setBusy(true);
    setError(undefined);
    try {
      return await fn();
    } catch (e) {
      setError(e instanceof ApiError || e instanceof Error ? e.message : String(e));
      return undefined;
    } finally {
      setBusy(false);
    }
  }, []);
  return { busy, error, run, setError };
}

/**
 * Search-as-you-type: queries `url(q)` 300 ms after the last keystroke once `q` has `minLength` characters.
 * Answers to older queries are dropped, so fast typing never shows stale results.
 */
export function useLiveSearch<T>(url: ((q: string) => string) | null, q: string, minLength = 2, delay = 300) {
  const [results, setResults] = useState<T[] | undefined>();
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | undefined>();
  const seq = useRef(0);
  const urlRef = useRef(url);
  urlRef.current = url;
  const enabled = url !== null;
  useEffect(() => {
    const term = q.trim();
    const mine = ++seq.current;
    if (!enabled || term.length < minLength) { setResults(undefined); setBusy(false); setError(undefined); return; }
    setBusy(true);
    const id = window.setTimeout(async () => {
      try {
        const data = await api.get<T[]>(urlRef.current!(term));
        if (mine === seq.current) { setResults(data); setError(undefined); }
      } catch (e) {
        if (mine === seq.current) setError(e instanceof Error ? e.message : String(e));
      } finally {
        if (mine === seq.current) setBusy(false);
      }
    }, delay);
    return () => window.clearTimeout(id);
  }, [q, enabled, minLength, delay]);
  return { results, busy, error, clear: () => setResults(undefined) };
}

export function usePolling(fn: () => void, ms: number, active = true) {
  const ref = useRef(fn);
  ref.current = fn;
  useEffect(() => {
    if (!active) return;
    const id = window.setInterval(() => ref.current(), ms);
    return () => window.clearInterval(id);
  }, [ms, active]);
}

export function PageHeader({ title, subtitle, actions }: { title: string; subtitle?: ReactNode; actions?: ReactNode }) {
  return (
    <header className="page-header">
      <div>
        <h1>{title}</h1>
        {subtitle && <p className="muted">{subtitle}</p>}
      </div>
      {actions && <div className="actions">{actions}</div>}
    </header>
  );
}

export function Card({ title, children, actions, className }: { title?: ReactNode; children: ReactNode; actions?: ReactNode; className?: string }) {
  return (
    <section className={'card ' + (className ?? '')}>
      {(title || actions) && (
        <div className="card-head">
          {title && <h2>{title}</h2>}
          {actions && <div className="actions">{actions}</div>}
        </div>
      )}
      {children}
    </section>
  );
}

export function ErrorBox({ error }: { error?: string }) {
  return error ? <div className="alert error" role="alert">{error}</div> : null;
}

export function Info({ children, tone = 'info' }: { children: ReactNode; tone?: 'info' | 'warn' | 'ok' | 'error' }) {
  return <div className={'alert ' + tone}>{children}</div>;
}

export function Spinner({ label }: { label?: string }) {
  return <span className="spinner-wrap"><span className="spinner" aria-hidden />{label && <span>{label}</span>}</span>;
}

export function StatusBadge({ status }: { status: KeyStatus }) {
  return <span className={'badge status-' + status}>{statusLabels[status]}</span>;
}

export function Badge({ children, tone = 'neutral' }: { children: ReactNode; tone?: 'neutral' | 'ok' | 'warn' | 'error' | 'info' }) {
  return <span className={'badge tone-' + tone}>{children}</span>;
}

export function Modal({ title, onClose, children, footer }: { title: string; onClose: () => void; children: ReactNode; footer?: ReactNode }) {
  useEffect(() => {
    const h = (e: KeyboardEvent) => e.key === 'Escape' && onClose();
    window.addEventListener('keydown', h);
    return () => window.removeEventListener('keydown', h);
  }, [onClose]);
  return (
    <div className="modal-backdrop" onMouseDown={onClose}>
      <div className="modal" role="dialog" aria-modal="true" aria-label={title} onMouseDown={(e) => e.stopPropagation()}>
        <div className="modal-head">
          <h2>{title}</h2>
          <button className="icon-btn" onClick={onClose} aria-label="Schließen">×</button>
        </div>
        <div className="modal-body">{children}</div>
        {footer && <div className="modal-foot">{footer}</div>}
      </div>
    </div>
  );
}

export function Field({ label, hint, children }: { label: string; hint?: ReactNode; children: ReactNode }) {
  return (
    <label className="field">
      <span className="field-label">{label}</span>
      {children}
      {hint && <span className="field-hint">{hint}</span>}
    </label>
  );
}

export function TenantSelect({ value, onChange, allowAll = true, tenants }: { value: string; onChange: (v: string) => void; allowAll?: boolean; tenants?: Tenant[] }) {
  const { data } = useApi<Tenant[]>(tenants ? null : '/api/tenants');
  const list = (tenants ?? data ?? []).filter((t) => t.status === 'Active');
  return (
    <select value={value} onChange={(e) => onChange(e.target.value)} aria-label="Tenant">
      {allowAll && <option value="">Alle Tenants</option>}
      {!allowAll && !value && <option value="">Tenant wählen …</option>}
      {list.map((t) => <option key={t.id} value={t.id}>{t.displayName}</option>)}
    </select>
  );
}

export function Empty({ children }: { children: ReactNode }) {
  return <div className="empty">{children}</div>;
}

export function download(url: string) {
  const a = document.createElement('a');
  a.href = url;
  a.rel = 'noopener';
  document.body.appendChild(a);
  a.click();
  a.remove();
}

/** Searches Entra groups of a tenant; calls onPick with the chosen group. */
export function GroupSearch({ tenantId, onPick, exclude = [], placeholder = 'Gruppe suchen …' }: { tenantId: string; onPick: (g: GroupRef) => void; exclude?: string[]; placeholder?: string }) {
  const [q, setQ] = useState('');
  // Searches while typing (from 2 characters, debounced).
  const search = useLiveSearch<{ id: string; displayName: string }>((term) => `/api/tenants/${tenantId}/groups?q=${encodeURIComponent(term)}`, q);
  const visible = search.results?.filter((g) => !exclude.includes(g.id));
  return (
    <div>
      <div className="toolbar" style={{ margin: 0 }}>
        <input type="search" placeholder={placeholder + ' (ab 2 Zeichen)'} value={q} onChange={(e) => setQ(e.target.value)}
          onKeyDown={(e) => { if (e.key === 'Enter') e.preventDefault(); }} />
        {search.busy && <Spinner label="Suche …" />}
        {search.results && <button type="button" className="link" onClick={() => setQ('')}>Ergebnisse schließen</button>}
      </div>
      <ErrorBox error={search.error} />
      {visible && (
        <div className="pick" style={{ marginTop: 8 }}>
          {visible.map((g) => <button type="button" key={g.id} onClick={() => { onPick({ id: g.id, name: g.displayName }); setQ(''); }}>{g.displayName}</button>)}
          {visible.length === 0 && <span className="muted small">Keine (weitere) Gruppe gefunden.</span>}
        </div>
      )}
    </div>
  );
}

/** Removable list of groups as chips. */
export function GroupChips({ groups, onRemove, empty }: { groups: GroupRef[]; onRemove?: (id: string) => void; empty: ReactNode }) {
  if (groups.length === 0) return <div className="muted small" style={{ margin: '4px 0 8px' }}>{empty}</div>;
  return (
    <div className="chips">
      {groups.map((g) => (
        <span key={g.id} className="chip" title={g.id}>
          {g.name}
          {onRemove && <button type="button" className="chip-x" aria-label={`${g.name} entfernen`} onClick={() => onRemove(g.id)}>×</button>}
        </span>
      ))}
    </div>
  );
}

/** Small badge showing where a setting comes from in the hierarchy Plattform → Tenant. */
export function OriginBadge({ origin, state }: { origin: 'Global' | 'Tenant'; state?: 'Inherited' | 'Modified' | 'Disabled' | 'Local' }) {
  if (origin === 'Tenant') return <Badge tone="info">Lokal im Tenant</Badge>;
  if (state === 'Modified') return <Badge tone="warn">Global · hier verändert</Badge>;
  if (state === 'Disabled') return <Badge tone="error">Global · hier deaktiviert</Badge>;
  return <Badge>Global · geerbt</Badge>;
}

/** Returns `value` once it has not changed for `ms` milliseconds (for search-as-you-type into URL filters). */
export function useDebounced<T>(value: T, ms = 300) {
  const [v, setV] = useState(value);
  useEffect(() => {
    const id = window.setTimeout(() => setV(value), ms);
    return () => window.clearTimeout(id);
  }, [value, ms]);
  return v;
}

/** Numbered step indicator with check marks for finished steps. */
export function Steps({ names, current }: { names: string[]; current: number }) {
  return (
    <ol className="steps">
      {names.map((s, i) => {
        const state = i < current ? 'done' : i === current ? 'current' : '';
        return (
          <li key={s} className={'step ' + state} aria-current={i === current ? 'step' : undefined}>
            <span className="step-no" aria-hidden>{i < current ? <Icon name="check" size={14} /> : i + 1}</span>
            <span className="step-name">{s}</span>
          </li>
        );
      })}
    </ol>
  );
}

/** Search field that applies its value while typing (debounced); Enter applies at once. */
export function LiveSearchInput({ value, onSearch, placeholder, busy }: { value: string; onSearch: (q: string) => void; placeholder: string; busy?: boolean }) {
  const [text, setText] = useState(value);
  const debounced = useDebounced(text, 350);
  const last = useRef(value);
  const apply = useCallback((q: string) => { const t = q.trim(); if (t !== last.current) { last.current = t; onSearch(t); } }, [onSearch]);
  useEffect(() => { apply(debounced); }, [debounced, apply]);
  // Follow outside changes (e.g. browser back).
  useEffect(() => { if (value !== last.current) { last.current = value; setText(value); } }, [value]);
  return (
    <div className={'live-search' + (busy ? ' busy' : '')}>
      <Icon name="search" size={16} />
      <input type="search" placeholder={placeholder} value={text} onChange={(e) => setText(e.target.value)}
        onKeyDown={(e) => { if (e.key === 'Enter') { e.preventDefault(); apply(text); } }} aria-label={placeholder} />
      {busy && <span className="spinner" aria-hidden />}
    </div>
  );
}

export type SortState = { key: string; desc: boolean };
export const parseSort = (s: string | null | undefined, fallback: SortState): SortState =>
  s ? { key: s.replace(/^-/, ''), desc: s.startsWith('-') } : fallback;
export const formatSort = (s: SortState) => (s.desc ? '-' : '') + s.key;

/** Table header cell that toggles sorting by `sortKey`. */
export function SortTh({ label, sortKey, sort, onSort, className }: { label: string; sortKey: string; sort: SortState; onSort: (s: SortState) => void; className?: string }) {
  const active = sort.key === sortKey;
  return (
    <th className={className} aria-sort={active ? (sort.desc ? 'descending' : 'ascending') : 'none'}>
      <button type="button" className={'sort-btn' + (active ? ' active' : '')} onClick={() => onSort({ key: sortKey, desc: active ? !sort.desc : false })}>
        {label}<span className="sort-arrow" aria-hidden>{active ? (sort.desc ? '↓' : '↑') : '↕'}</span>
      </button>
    </th>
  );
}

export interface MenuItem { label: string; hint?: string; icon?: IconName; onClick: () => void }

/** Knopf mit aufklappender Auswahl, z. B. für Exporte. Schließt bei Klick daneben und mit Escape. */
export function MenuButton({ label, items, icon = 'download', primary }: { label: string; items: MenuItem[]; icon?: IconName; primary?: boolean }) {
  const [open, setOpen] = useState(false);
  const ref = useRef<HTMLDivElement>(null);
  useEffect(() => {
    if (!open) return;
    const close = (e: MouseEvent | KeyboardEvent) => {
      if (e instanceof KeyboardEvent ? e.key === 'Escape' : !ref.current?.contains(e.target as Node)) setOpen(false);
    };
    document.addEventListener('mousedown', close);
    document.addEventListener('keydown', close);
    return () => { document.removeEventListener('mousedown', close); document.removeEventListener('keydown', close); };
  }, [open]);
  return (
    <div className="menu" ref={ref}>
      <button type="button" className={primary ? 'primary' : undefined} aria-haspopup="menu" aria-expanded={open} onClick={() => setOpen(!open)}>
        <Icon name={icon} size={16} />{label}<span className={'menu-caret' + (open ? ' open' : '')} aria-hidden>▾</span>
      </button>
      {open && (
        <div className="menu-pop" role="menu">
          {items.map((i) => (
            <button key={i.label} type="button" role="menuitem" onClick={() => { setOpen(false); i.onClick(); }}>
              {i.icon && <Icon name={i.icon} size={16} />}
              <span><strong>{i.label}</strong>{i.hint && <span className="small muted">{i.hint}</span>}</span>
            </button>
          ))}
        </div>
      )}
    </div>
  );
}

/** Ring-Diagramm aus Anteilen; Segmente zeichnen sich beim Laden ein. */
export function Donut({ parts, size = 132, thickness = 16, center }: { parts: { key: string; value: number; color: string; label: string }[]; size?: number; thickness?: number; center?: ReactNode }) {
  const total = parts.reduce((s, p) => s + p.value, 0);
  const r = (size - thickness) / 2;
  const c = 2 * Math.PI * r;
  let offset = 0;
  return (
    <div className="donut" style={{ width: size, height: size }}>
      <svg width={size} height={size} viewBox={`0 0 ${size} ${size}`} role="img" aria-label={parts.map((p) => `${p.label}: ${p.value}`).join(', ')}>
        <circle cx={size / 2} cy={size / 2} r={r} fill="none" stroke="var(--surface-2)" strokeWidth={thickness} />
        {total > 0 && parts.filter((p) => p.value > 0).map((p, i) => {
          const len = (p.value / total) * c;
          const el = (
            <circle key={p.key} cx={size / 2} cy={size / 2} r={r} fill="none" stroke={p.color} strokeWidth={thickness}
              strokeDasharray={`${Math.max(len - 2, 0.1)} ${c}`} strokeDashoffset={-offset} transform={`rotate(-90 ${size / 2} ${size / 2})`}
              className="donut-seg" style={{ animationDelay: i * 120 + 'ms', ['--len' as string]: len }}>
              <title>{p.label}: {p.value}</title>
            </circle>
          );
          offset += len;
          return el;
        })}
      </svg>
      {center && <div className="donut-center">{center}</div>}
    </div>
  );
}
