import { createContext, useCallback, useContext, useEffect, useMemo, useRef, useState, type KeyboardEvent, type ReactNode } from 'react';
import { api, type Tenant } from './api';

// Kundenebene: der oben gewählte Tenant gilt für alle Kundenseiten (ähnlich dem Tenant-Umschalter im Admin Center).
interface TenantCtx {
  tenants: Tenant[];
  /** '' = alle Tenants */
  tenantId: string;
  tenant?: Tenant;
  setTenantId: (id: string) => void;
  reloadTenants: () => void;
}

const StorageKey = 'ylm-tenant';
const Ctx = createContext<TenantCtx>({ tenants: [], tenantId: '', setTenantId: () => {}, reloadTenants: () => {} });

export function TenantProvider({ children }: { children: ReactNode }) {
  const [tenants, setTenants] = useState<Tenant[]>([]);
  const [tenantId, setId] = useState(() => { try { return localStorage.getItem(StorageKey) ?? ''; } catch { return ''; } });
  const reloadTenants = useCallback(() => { void api.get<Tenant[]>('/api/tenants').then(setTenants).catch(() => setTenants([])); }, []);
  useEffect(reloadTenants, [reloadTenants]);
  const setTenantId = useCallback((id: string) => {
    setId(id);
    try { localStorage.setItem(StorageKey, id); } catch { /* ignore */ }
  }, []);
  const tenant = tenants.find((t) => t.id === tenantId);
  // A remembered tenant that is gone (offboarded, no access) falls back to "all".
  useEffect(() => { if (tenantId && tenants.length > 0 && !tenant) setTenantId(''); }, [tenantId, tenants, tenant, setTenantId]);
  const value = useMemo(() => ({ tenants, tenantId: tenant ? tenantId : '', tenant, setTenantId, reloadTenants }), [tenants, tenantId, tenant, setTenantId, reloadTenants]);
  return <Ctx.Provider value={value}>{children}</Ctx.Provider>;
}

export function useTenant() {
  return useContext(Ctx);
}

/** Searchable tenant switcher for the top bar. */
export function TenantSwitcher({ partnerContext }: { partnerContext: boolean }) {
  const { tenants, tenantId, tenant, setTenantId } = useTenant();
  const [open, setOpen] = useState(false);
  const [q, setQ] = useState('');
  const [onlyProblems, setOnlyProblems] = useState(false);
  const [cursor, setCursor] = useState(0);
  const ref = useRef<HTMLDivElement>(null);
  useEffect(() => {
    if (!open) return;
    const close = (e: MouseEvent) => { if (ref.current && !ref.current.contains(e.target as Node)) setOpen(false); };
    document.addEventListener('mousedown', close);
    return () => document.removeEventListener('mousedown', close);
  }, [open]);

  const active = tenants.filter((t) => t.status === 'Active');
  const term = q.trim().toLowerCase();
  const hasProblems = (t: Tenant) => t.checks.some((c) => c.status === 'Failed') || t.lastSyncSucceeded === false;
  const list = active
    .filter((t) => !term || t.displayName.toLowerCase().includes(term) || (t.defaultDomain ?? '').toLowerCase().includes(term) || t.entraTenantId.includes(term))
    .filter((t) => !onlyProblems || hasProblems(t))
    .sort((a, b) => a.displayName.localeCompare(b.displayName, 'de'));
  const pick = (id: string) => { setTenantId(id); setOpen(false); setQ(''); };
  // Keyboard: ↑/↓ moves through the list (incl. „Alle Tenants“), Enter picks, Esc closes.
  const showAll = !term && !onlyProblems;
  const options = [...(showAll ? [''] : []), ...list.map((t) => t.id)];
  useEffect(() => { setCursor(Math.max(0, options.indexOf(tenantId))); /* eslint-disable-next-line react-hooks/exhaustive-deps */ }, [open]);
  useEffect(() => { if (cursor >= options.length) setCursor(0); }, [options.length, cursor]);
  const optionsRef = useRef<HTMLDivElement>(null);
  useEffect(() => { optionsRef.current?.querySelector('.kb-active')?.scrollIntoView({ block: 'nearest' }); }, [cursor]);
  const onKey = (e: KeyboardEvent<HTMLInputElement>) => {
    if (e.key === 'ArrowDown') { e.preventDefault(); setCursor((a) => Math.min(options.length - 1, a + 1)); }
    else if (e.key === 'ArrowUp') { e.preventDefault(); setCursor((a) => Math.max(0, a - 1)); }
    else if (e.key === 'Enter') { e.preventDefault(); if (options[cursor] !== undefined) pick(options[cursor]); }
    else if (e.key === 'Escape') setOpen(false);
  };

  return (
    <div className={'tenant-switcher' + (partnerContext ? ' dim' : '')} ref={ref}>
      <button type="button" className="tenant-current" onClick={() => setOpen((o) => !o)} aria-haspopup="listbox" aria-expanded={open}>
        <span className="tenant-label">Kunden-Tenant</span>
        <span className="tenant-name">{tenant ? tenant.displayName : 'Alle Tenants'}</span>
        {tenant?.defaultDomain && <span className="tenant-domain">{tenant.defaultDomain}</span>}
        <span aria-hidden>▾</span>
      </button>
      {open && (
        <div className="tenant-menu" role="listbox">
          <input type="search" autoFocus placeholder="Tenant suchen (Name, Domain, ID) …" value={q} onChange={(e) => { setQ(e.target.value); setCursor(0); }}
            onKeyDown={onKey} aria-activedescendant={options[cursor] !== undefined ? 'tenant-opt-' + (options[cursor] || 'all') : undefined} />
          <label className="check small" style={{ margin: '6px 2px' }}>
            <input type="checkbox" checked={onlyProblems} onChange={(e) => setOnlyProblems(e.target.checked)} /> Nur Tenants mit Handlungsbedarf
          </label>
          <div className="tenant-options" ref={optionsRef}>
            {showAll && (
              <button type="button" id="tenant-opt-all" role="option" aria-selected={tenantId === ''} className={(tenantId === '' ? 'selected' : '') + (cursor === 0 ? ' kb-active' : '')} onClick={() => pick('')} onMouseEnter={() => setCursor(0)}>
                <strong>Alle Tenants</strong><span className="small muted">Übersicht über alle Kunden</span>
              </button>
            )}
            {list.map((t, i) => (
              <button type="button" key={t.id} id={'tenant-opt-' + t.id} role="option" aria-selected={t.id === tenantId} className={(t.id === tenantId ? 'selected' : '') + (cursor === i + (showAll ? 1 : 0) ? ' kb-active' : '')}
                onClick={() => pick(t.id)} onMouseEnter={() => setCursor(i + (showAll ? 1 : 0))}>
                <strong>{t.displayName}{hasProblems(t) && <span className="dot-warn" title="Handlungsbedarf" />}</strong>
                <span className="small muted">{t.defaultDomain ?? t.entraTenantId}</span>
              </button>
            ))}
            {list.length === 0 && <span className="muted small" style={{ padding: 8 }}>Kein Tenant gefunden.</span>}
          </div>
        </div>
      )}
    </div>
  );
}
