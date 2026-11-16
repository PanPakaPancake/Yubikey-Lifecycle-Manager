import { useCallback, useEffect, useState } from 'react';
import { NavLink, Navigate, Route, Routes, useLocation } from 'react-router';
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

const roleLabel: Record<string, string> = { Owner: 'Owner', Operator: 'Operator', Helpdesk: 'Helpdesk', Auditor: 'Auditor' };

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
      <div className="shell">
        <aside className="sidebar">
          <div className="brand">
            <svg width="30" height="30" viewBox="0 0 32 32" aria-hidden><rect x="21" y="12" width="9" height="8" fill="#b8bec7" /><rect x="2" y="8" width="22" height="16" rx="6" fill="#2b2f36" /><circle cx="11" cy="16" r="5" fill="#d4a52a" /></svg>
            <div>Lifecycle Manager<small>{me.organizationName || 'YubiKeys'}</small></div>
          </div>
          <nav className="nav">
            <NavLink to="/" end>Dashboard</NavLink>
            <NavLink to="/keys">Inventar</NavLink>
            {can('RegisterKeys') && <NavLink to="/register">Key registrieren</NavLink>}
            <div className="section">Verwaltung</div>
            <NavLink to="/tenants">Tenants</NavLink>
            <NavLink to="/profiles">Profile</NavLink>
            {(can('RegisterKeys') || can('ManageInventory')) && <NavLink to="/agents">Agent</NavLink>}
            {can('ReadAudit') && <NavLink to="/audit">Audit-Log</NavLink>}
            {can('ManageUsers') && <NavLink to="/users">Benutzer &amp; Rollen</NavLink>}
          </nav>
          <div className="sidebar-foot">
            <div><strong>{me.user?.displayName}</strong></div>
            <div className="muted small">{roleLabel[me.user?.role ?? ''] ?? me.user?.role}</div>
            <button className="link small" onClick={logout}>Abmelden</button>
          </div>
        </aside>
        <main className="main">
          <Routes>
            <Route path="/" element={<DashboardPage />} />
            <Route path="/keys" element={<KeysPage />} />
            <Route path="/keys/:id" element={<KeyDetailPage />} />
            <Route path="/register" element={<WizardPage />} />
            <Route path="/tenants" element={<TenantsPage />} />
            <Route path="/tenants/:id" element={<TenantDetailPage />} />
            <Route path="/profiles" element={<ProfilesPage />} />
            <Route path="/agents" element={<AgentsPage />} />
            <Route path="/audit" element={<AuditPage />} />
            <Route path="/users" element={<UsersPage />} />
            <Route path="/login" element={<Navigate to="/" replace />} />
            <Route path="*" element={<p>Seite nicht gefunden.</p>} />
          </Routes>
        </main>
      </div>
    </AuthContext.Provider>
  );
}
