import { createContext, useCallback, useContext, useEffect, useMemo, useRef, useState, type ReactNode } from 'react';
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
          <input type="search" autoFocus placeholder="Tenant suchen (Name, Domain, ID) …" value={q} onChange={(e) => setQ(e.target.value)}
            onKeyDown={(e) => { if (e.key === 'Enter' && list[0]) pick(list[0].id); if (e.key === 'Escape') setOpen(false); }} />
          <label className="check small" style={{ margin: '6px 2px' }}>
            <input type="checkbox" checked={onlyProblems} onChange={(e) => setOnlyProblems(e.target.checked)} /> Nur Tenants mit Handlungsbedarf
          </label>
          <div className="tenant-options">
            {!term && !onlyProblems && (
              <button type="button" className={tenantId === '' ? 'selected' : ''} onClick={() => pick('')}>
                <strong>Alle Tenants</strong><span className="small muted">Übersicht über alle Kunden</span>
              </button>
            )}
            {list.map((t) => (
              <button type="button" key={t.id} className={t.id === tenantId ? 'selected' : ''} onClick={() => pick(t.id)}>
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
