import { useCallback, useEffect, useState } from 'react';
import { NavLink, Navigate, Route, Routes, useLocation, useParams } from 'react-router';
import { api, type Me } from './api';
import { AuthContext } from './auth';
import { Spinner } from './ui';
import LoginPage from './pages/Login';
import SetupPage from './pages/Setup';
import DashboardPage from './pages/Dashboard';
import KeysPage from './pages/Keys';
import KeyDetailPage from './pages/KeyDetail';
import WizardPage from './pages/Wizard';
import TenantsPage from './pages/Tenants';
import TenantDetailPage from './pages/TenantDetail';
import ProfilesPage from './pages/Profiles';
import AgentsPage from './pages/Agents';
import AuditPage from './pages/Audit';
import UsersPage from './pages/Users';
import PlatformPage from './pages/Platform';
import PartnerStockPage from './pages/PartnerStock';
import { TenantProvider, TenantSwitcher, useTenant } from './tenant';

const roleLabel: Record<string, string> = { Owner: 'Owner', Operator: 'Operator', Helpdesk: 'Helpdesk', Auditor: 'Auditor' };

// Seiten der Partnerebene (gelten für alle Kunden); alles andere läuft im Kontext des gewählten Tenants.
const partnerPaths = ['/partner', '/tenants', '/users', '/platform', '/agents'];
const isPartnerPath = (path: string) => partnerPaths.some((p) => path === p || path.startsWith(p + '/'));

export default function App() {
  const [me, setMe] = useState<Me | null>(null);
  const location = useLocation();

  const reload = useCallback(async () => {
    try {
      setMe(await api.get<Me>('/api/me'));
    } catch {
      const status = await fetch('/api/setup/status').then((r) => r.json()).catch(() => ({ setupComplete: true }));
      setMe({ authenticated: false, setupComplete: status.setupComplete });
    }
  }, []);

  useEffect(() => { void reload(); }, [reload]);

  if (!me) return <div className="center-page"><Spinner label="Wird geladen …" /></div>;

  if (location.pathname.startsWith('/setup')) return <SetupPage />;
  if (!me.authenticated) {
    if (!me.setupComplete && location.pathname !== '/login') return <Navigate to="/setup" replace />;
    return <AuthContext.Provider value={{ me, reload }}><LoginPage setupComplete={me.setupComplete} /></AuthContext.Provider>;
  }

  const can = (p: string) => me.permissions?.includes(p);
  const logout = async () => {
    await api.post('/auth/logout');
    window.location.href = '/login';
  };

  return (
    <AuthContext.Provider value={{ me, reload }}>
      <TenantProvider>
        <Shell can={can} logout={logout} me={me} />
      </TenantProvider>
    </AuthContext.Provider>
  );
}

function Shell({ me, can, logout }: { me: Me; can: (p: string) => boolean | undefined; logout: () => void }) {
  const location = useLocation();
  const { tenant } = useTenant();
  const partner = isPartnerPath(location.pathname);
  return (
    <div className="shell">
      <aside className="sidebar">
        <div className="brand">
          <svg width="30" height="30" viewBox="0 0 32 32" aria-hidden><rect x="21" y="12" width="9" height="8" fill="#b8bec7" /><rect x="2" y="8" width="22" height="16" rx="6" fill="#2b2f36" /><circle cx="11" cy="16" r="5" fill="#d4a52a" /></svg>
          <div>Lifecycle Manager<small>{me.organizationName || 'YubiKeys'}</small></div>
        </div>
        <nav className="nav">
          <div className="section customer"><span className="context-dot" />Kunde</div>
          <div className="section-sub" title={tenant?.displayName}>{tenant ? tenant.displayName : 'Alle Tenants'}</div>
          <NavLink to="/" end>Übersicht</NavLink>
          <NavLink to="/keys">Inventar</NavLink>
          {can('RegisterKeys') && <NavLink to="/register">Key registrieren</NavLink>}
          {tenant && <NavLink to="/tenant">Tenant-Einstellungen</NavLink>}
          <NavLink to="/profiles">Profile</NavLink>
          {can('ReadAudit') && <NavLink to="/audit">Audit-Log</NavLink>}

          <div className="section partner"><span className="context-dot" />Partner</div>
          <NavLink to="/partner" end>Übersicht aller Kunden</NavLink>
          <NavLink to="/tenants">Tenants</NavLink>
          {can('ManagePartnerStock') && <NavLink to="/partner/stock">Partnerlager</NavLink>}
          {can('ManageUsers') && <NavLink to="/users">Benutzer &amp; Rollen</NavLink>}
          <NavLink to="/platform">Globale Einstellungen</NavLink>
          {can('ReadAudit') && <NavLink to="/partner/audit">Audit-Log (alle)</NavLink>}
          {(can('RegisterKeys') || can('ManageInventory')) && <NavLink to="/agents">Mein Agent</NavLink>}
        </nav>
        <div className="sidebar-foot">
          <div><strong>{me.user?.displayName}</strong></div>
          <div className="muted small">{me.user?.role === 'Custom' ? me.user?.customRoleName : roleLabel[me.user?.role ?? ''] ?? me.user?.role}</div>
          <button className="link small" onClick={logout}>Abmelden</button>
        </div>
      </aside>
      <main className="main">
        <div className="topbar">
          <TenantSwitcher partnerContext={partner} />
          {partner
            ? <span className="context-badge partner" title="Einstellungen hier gelten für die ganze Plattform und alle Kunden">Partnerebene</span>
            : <span className="context-badge customer">Kundenebene{tenant ? ': ' + tenant.displayName : ', alle Tenants'}</span>}
        </div>
        <Routes>
          <Route path="/" element={<DashboardPage />} />
          <Route path="/keys" element={<KeysPage />} />
          <Route path="/keys/:id" element={<KeyDetailPage />} />
          <Route path="/register" element={<WizardPage />} />
          <Route path="/tenant" element={<SelectedTenantPage />} />
          <Route path="/profiles" element={<ProfilesPage />} />
          <Route path="/audit" element={<AuditPage />} />
          <Route path="/partner" element={<DashboardPage scope="all" />} />
          <Route path="/partner/stock" element={<PartnerStockPage />} />
          <Route path="/partner/audit" element={<AuditPage scope="all" />} />
          <Route path="/tenants" element={<TenantsPage />} />
          <Route path="/tenants/:id" element={<TenantRoute />} />
          <Route path="/agents" element={<AgentsPage />} />
          <Route path="/users" element={<UsersPage />} />
          <Route path="/platform" element={<PlatformPage />} />
          <Route path="/login" element={<Navigate to="/" replace />} />
          <Route path="*" element={<p>Seite nicht gefunden.</p>} />
        </Routes>
      </main>
    </div>
  );
}

/** Tenant-Einstellungen des oben gewählten Tenants. */
function SelectedTenantPage() {
  const { tenant } = useTenant();
  if (!tenant) return <p className="muted">Bitte oben einen Kunden-Tenant wählen.</p>;
  return <TenantDetailPage key={tenant.id} id={tenant.id} />;
}

/** /tenants/:id setzt den Tenant als gewählten Kunden-Tenant und öffnet dessen Einstellungen. */
function TenantRoute() {
  const { id } = useParams();
  const location = useLocation();
  const { tenantId, setTenantId, tenants, reloadTenants } = useTenant();
  const known = tenants.some((t) => t.id === id);
  // Nach dem Onboarding kennt die Liste den neuen Tenant evtl. noch nicht.
  useEffect(() => { if (id && !known) reloadTenants(); }, [id, known, reloadTenants]);
  useEffect(() => { if (id && known && id !== tenantId) setTenantId(id); }, [id, known, tenantId, setTenantId]);
  if (id && known && id === tenantId) return <Navigate to={'/tenant' + location.search} replace />;
  return <TenantDetailPage id={id} />;
}
