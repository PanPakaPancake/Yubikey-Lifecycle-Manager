import { useCallback, useEffect, useState, type ReactNode } from 'react';
import { Link, NavLink, Navigate, Route, Routes, useLocation, useParams } from 'react-router';
import { api, type Me } from './api';
import { AuthContext } from './auth';
import { Empty, PageHeader, Spinner, useApi } from './ui';
import { Icon, type IconName } from './icons';
import { ThemeToggle } from './theme';
import { BrandMark } from './brand';
import LoginPage from './pages/Login';
import SetupPage from './pages/Setup';
import AccountSetupPage from './pages/AccountSetup';
import AccountPage from './pages/Account';
import DashboardPage from './pages/Dashboard';
import KeysPage from './pages/Keys';
import AssignmentsPage from './pages/Assignments';
import KeyDetailPage from './pages/KeyDetail';
import WizardPage from './pages/Wizard';
import BatchesPage, { NewBatchPage } from './pages/Batches';
import BatchRunPage from './pages/BatchRun';
import TenantsPage from './pages/Tenants';
import TenantDetailPage from './pages/TenantDetail';
import ProfilesPage from './pages/Profiles';
import AgentsPage from './pages/Agents';
import AuditPage from './pages/Audit';
import UsersPage from './pages/Users';
import PlatformPage from './pages/Platform';
import PartnerStockPage from './pages/PartnerStock';
import SharedWizardPage from './pages/SharedWizard';
import ApprovalsPage from './pages/Approvals';
import TodosPage from './pages/Todos';
import IdentifyPage from './pages/Identify';
import IntegrationsPage from './pages/Integrations';
import { isRegisterPath } from './register';
import { ChooseTenant, TenantProvider, TenantSwitcher, useTenant } from './tenant';

// Seitenleiste: 'sidebar-dark' (dunkel, kontrastreich) oder 'sidebar-light'.
const SidebarStyle = 'sidebar-dark';

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
  if (location.pathname.startsWith('/account-setup')) return <AccountSetupPage />;
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
  const { tenant, tenants } = useTenant();
  // Freigaben gibt es nur, wenn ein Tenant die Vier-Augen-Freigabe eingeschaltet hat.
  const fourEyes = tenants.some((t) => t.requireBreakglassApproval);
  const partner = isPartnerPath(location.pathname);
  const [menuOpen, setMenuOpen] = useState(false);
  // Close the mobile menu on every navigation.
  useEffect(() => { setMenuOpen(false); }, [location.pathname]);
  const { data: updates } = useApi<{ updateAvailable: boolean; latestVersion?: string }>(can('ManageSettings') ? '/api/platform/updates' : null);
  const item = (to: string, icon: IconName, label: ReactNode, end = false) => (
    <NavLink to={to} end={end}><Icon name={icon} />{label}</NavLink>
  );
  return (
    <div className={'shell ' + SidebarStyle + (menuOpen ? ' menu-open' : '')}>
      <header className="mobilebar">
        <button type="button" className="icon-btn" aria-label="Menü öffnen" aria-expanded={menuOpen} onClick={() => setMenuOpen(true)}><Icon name="menu" size={22} /></button>
        <span className="mobilebar-title">Lifecycle Manager</span>
      </header>
      <div className="sidebar-backdrop" onClick={() => setMenuOpen(false)} aria-hidden />
      <aside className="sidebar">
        <div className="brand">
          <BrandMark />
          <div>Lifecycle Manager<small>{me.organizationName || 'YubiKeys'}</small></div>
          <button type="button" className="icon-btn sidebar-close" aria-label="Menü schließen" onClick={() => setMenuOpen(false)}><Icon name="x" size={20} /></button>
        </div>
        <nav className="nav">
          <div className="section customer"><span className="context-dot" />Kunde</div>
          <div className="section-sub" title={tenant?.displayName}>{tenant ? tenant.displayName : 'Alle Tenants'}</div>
          {item('/', 'home', 'Übersicht', true)}
          {item('/keys', 'key', 'Inventar')}
          {item('/assignments', 'users', 'Wer hat welchen Key')}
          {can('RegisterKeys') && <NavLink to="/register" className={() => (isRegisterPath(location.pathname) ? 'active' : '')}><Icon name="keyPlus" />Registrieren</NavLink>}
          {can('RegisterKeys') && item('/todos', 'list', 'ToDo-Liste')}
          {(fourEyes || can('ApproveRegistrations')) && item('/approvals', 'shield', 'Freigaben')}
          {tenant && item('/tenant', 'building', 'Tenant-Einstellungen')}
          {item('/profiles', 'sliders', 'Profile')}
          {can('ReadAudit') && item('/audit', 'scroll', 'Audit-Log')}

          <div className="section partner"><span className="context-dot" />Partner</div>
          {item('/tenants', 'building', 'Tenants')}
          {can('ManagePartnerStock') && item('/partner/stock', 'box', 'Partnerlager')}
          {can('ManageUsers') && item('/users', 'users', 'Benutzer & Rollen')}
          {can('ManageApiKeys') && item('/partner/integrations', 'plug', 'Integrationen')}
          {item('/platform', 'settings', <>Globale Einstellungen{updates?.updateAvailable && <span className="dot-warn pulse" title={`Update auf ${updates.latestVersion} verfügbar`} />}</>)}
          {(can('RegisterKeys') || can('ManageInventory')) && item('/agents', 'monitor', 'Mein Agent')}
        </nav>
        <div className="sidebar-foot">
          <NavLink to="/account" className="account-link">
            <span className="avatar" aria-hidden>{initials(me.user?.displayName)}</span>
            <span className="account-text">
              <strong>{me.user?.displayName}</strong>
              <span className="muted small">{me.user?.role === 'Custom' ? me.user?.customRoleName : roleLabel[me.user?.role ?? ''] ?? me.user?.role}</span>
            </span>
          </NavLink>
          <div className="foot-actions">
            <ThemeToggle />
            <button type="button" className="ghost small" onClick={logout}><Icon name="logout" size={16} />Abmelden</button>
          </div>
          {me.version && <div className="small muted version">Version {me.version}</div>}
        </div>
      </aside>
      <main className="main">
        <div className="topbar">
          <TenantSwitcher partnerContext={partner} />
          {partner
            ? <span className="context-badge partner" title="Einstellungen hier gelten für die ganze Plattform und alle Kunden">Partnerebene</span>
            : <span className="context-badge customer">Kundenebene{tenant ? ': ' + tenant.displayName : ', alle Tenants'}</span>}
        </div>
        <div className="page" key={location.pathname}>
        <Routes>
          <Route path="/" element={<DashboardPage />} />
          <Route path="/keys" element={<KeysPage />} />
          <Route path="/assignments" element={<AssignmentsPage />} />
          <Route path="/keys/identify" element={<IdentifyPage />} />
          <Route path="/keys/:id" element={<KeyDetailPage />} />
          <Route path="/register" element={<WizardPage />} />
          <Route path="/register/shared" element={<SharedWizardPage />} />
          <Route path="/todos" element={<TodosPage />} />
          <Route path="/approvals" element={<ApprovalsPage />} />
          <Route path="/batches" element={<BatchesPage />} />
          <Route path="/batches/new" element={<NewBatchPage />} />
          <Route path="/batches/:id" element={<BatchRunPage />} />
          <Route path="/tenant" element={<SelectedTenantPage />} />
          <Route path="/profiles" element={<ProfilesPage />} />
          <Route path="/audit" element={<AuditPage />} />
          <Route path="/partner" element={<AllTenants to="/" />} />
          <Route path="/partner/stock" element={<PartnerStockPage />} />
          <Route path="/partner/integrations" element={<IntegrationsPage />} />
          <Route path="/partner/audit" element={<AllTenants to="/audit" />} />
          <Route path="/tenants" element={<TenantsPage />} />
          <Route path="/tenants/:id" element={<TenantRoute />} />
          <Route path="/agents" element={<AgentsPage />} />
          <Route path="/users" element={<UsersPage />} />
          <Route path="/platform" element={<PlatformPage />} />
          <Route path="/account" element={<AccountPage />} />
          <Route path="/login" element={<Navigate to="/" replace />} />
          <Route path="*" element={<Empty>Seite nicht gefunden. <Link to="/">Zur Übersicht</Link></Empty>} />
        </Routes>
        </div>
      </main>
    </div>
  );
}

/** Frühere Partner-Seiten: Umschalter auf „Alle Tenants“ und weiter zur Seite auf Kundenebene. */
function AllTenants({ to }: { to: string }) {
  const { setTenantId } = useTenant();
  useEffect(() => { setTenantId(''); }, [setTenantId]);
  return <Navigate to={to} replace />;
}

/** Tenant-Einstellungen des oben gewählten Tenants. */
function SelectedTenantPage() {
  const { tenant } = useTenant();
  if (!tenant) return <><PageHeader title="Tenant-Einstellungen" /><ChooseTenant purpose="Für die Einstellungen" /></>;
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

function initials(name?: string) {
  const parts = (name ?? '').trim().split(/\s+/).filter(Boolean);
  return ((parts[0]?.[0] ?? '') + (parts.length > 1 ? parts[parts.length - 1][0] : '')).toUpperCase() || '?';
}
