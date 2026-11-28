import { useCallback, useEffect, useState, type ReactNode } from 'react';
import { DemoBanner } from './demo';
import { Link, NavLink, Navigate, Route, Routes, useLocation, useParams } from 'react-router';
import { anyReminder, api, type Me } from './api';
import { AuthContext } from './auth';
import { Empty, PageHeader, Spinner, useApi } from './ui';
import { Icon, type IconName } from './icons';
import { ThemeToggle } from './theme';
import { LogoMark, Wordmark } from './logo';
import LoginPage from './pages/Login';
import SetupPage from './pages/Setup';
import SetupCallbackPage from './pages/SetupCallback';
import AccountSetupPage from './pages/AccountSetup';
import AccountPage from './pages/Account';
import DashboardPage from './pages/Dashboard';
import KeysPage from './pages/Keys';
import AdvisoriesPage from './pages/Advisories';
import RemindersPage from './pages/Reminders';
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
import PasskeyInfoPage from './pages/PasskeyInfo';
import RolloutPage from './pages/Rollout';
import VersionsPage from './pages/Versions';
import type { UpdateSummary } from './changelog';
import { isRegisterPath } from './register';
import { ChooseTenant, TenantProvider, TenantSwitcher, useTenant } from './tenant';
import { adoptUserLanguage, tr } from './i18n';
import { LanguageToggle } from './language';

// Dunkle Seitenleiste (Entscheidung Timo, 2026-11-19); 'sidebar-light' wäre die helle Variante.
const SidebarStyle = 'sidebar-dark';

const roleLabel: Record<string, string> = { Owner: tr("Owner"), Operator: tr("Operator"), Helpdesk: tr("Helpdesk"), Auditor: tr("Auditor") };

// Seiten der Partnerebene (gelten für alle Kunden); alles andere läuft im Kontext des gewählten Tenants.
const partnerPaths = ['/partner', '/tenants', '/profiles', '/users', '/platform', '/agents'];
const isPartnerPath = (path: string) => partnerPaths.some((p) => path === p || path.startsWith(p + '/'));

export default function App() {
  const [me, setMe] = useState<Me | null>(null);
  const location = useLocation();

  const reload = useCallback(async () => {
    try {
      const m = await api.get<Me>('/api/me');
      adoptUserLanguage(m.language);
      setMe(m);
    } catch {
      const status = await fetch('/api/setup/status').then((r) => r.json()).catch(() => ({ setupComplete: true }));
      setMe({ authenticated: false, setupComplete: status.setupComplete });
    }
  }, []);

  useEffect(() => { void reload(); }, [reload]);

  // Öffentliche Infoseiten für Endnutzer (Link im PIN-Brief): ohne Anmeldung, ohne Navigation der Plattform.
  // Angemeldet läuft sie im App-Rahmen mit Menü; ?oeffentlich=1 zeigt sie so, wie Endbenutzer sie sehen.
  const passkeyPath = location.pathname === '/passkey' || location.pathname.startsWith('/passkey/');
  const publicPasskey = passkeyPath && (!me?.authenticated || new URLSearchParams(location.search).has('oeffentlich'));
  if (!me) return <div className="center-page"><Spinner label={tr("Wird geladen …")} /></div>;
  if (publicPasskey) return <PasskeyInfoPage authenticated={me.authenticated} />;

  // Rücksprung der Microsoft-Anmeldung aus dem Setup-Assistenten (http://localhost:<port>/?code=…&state=…).
  if (!me.setupComplete && location.pathname === '/' && /[?&](code|error)=/.test(location.search) && /[?&]state=/.test(location.search)) return <SetupCallbackPage />;
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
  // Freigaben nur, wenn ein Tenant die Vier-Augen-Freigabe nutzt; offene Anträge verlinkt sonst der Handlungsbedarf auf der Übersicht.
  const fourEyes = tenants.some((t) => t.requireBreakglassApproval);
  // Erinnerungen sind optional: der Menüpunkt erscheint erst, wenn ein Tenant sie eingeschaltet hat.
  const reminders = tenants.some((t) => anyReminder(t.reminders));
  const partner = isPartnerPath(location.pathname);
  const [menuOpen, setMenuOpen] = useState(false);
  // Close the mobile menu on every navigation.
  useEffect(() => { setMenuOpen(false); }, [location.pathname]);
  const { data: updates } = useApi<UpdateSummary>('/api/updates/summary');
  useReveal(location.pathname);
  const item = (to: string, icon: IconName, label: ReactNode, end = false) => (
    <NavLink to={to} end={end} title={typeof label === 'string' ? label : undefined}><Icon name={icon} /><span className="nav-label">{label}</span></NavLink>
  );
  return (
    <div className={'shell ' + SidebarStyle + (menuOpen ? ' menu-open' : '')}>
      <header className="mobilebar">
        <button type="button" className="icon-btn" aria-label={tr("Menü öffnen")} aria-expanded={menuOpen} onClick={() => setMenuOpen(true)}><Icon name="menu" size={22} /></button>
        <span className="mobilebar-title"><LogoMark size={28} /><Wordmark /></span>
      </header>
      <div className="sidebar-backdrop" onClick={() => setMenuOpen(false)} aria-hidden />
      <aside className="sidebar">
        <div className="brand">
          <LogoMark size={54} />
          <Wordmark sub={me.organizationName || undefined} />
          <button type="button" className="icon-btn sidebar-close" aria-label={tr("Menü schließen")} onClick={() => setMenuOpen(false)}><Icon name="x" size={20} /></button>
        </div>
        <nav className="nav">
          <div className="nav-group nav-group-customer">
          <div className="section customer"><span className="context-dot" />{tr("Kunde")}</div>
          <div className="section-sub" title={tenant?.displayName}>{tenant ? tenant.displayName : tr("Alle Tenants")}</div>
          {item('/', 'home', tr("Übersicht"), true)}
          {item('/keys', 'key', tr("Inventar"))}
          {item('/sicherheitshinweise', 'shield', tr("Sicherheitshinweise"))}
          {reminders && item('/erinnerungen', 'bell', tr("Erinnerungen"))}
          {can('ReadReports') && item('/rollout', 'target', tr("Rollout Übersicht"))}
          {can('RegisterKeys') && <NavLink to="/register" className={() => (isRegisterPath(location.pathname) ? 'active' : '')}><Icon name="keyPlus" /><span className="nav-label">{tr("Registrieren")}</span></NavLink>}
          {can('RegisterKeys') && item('/todos', 'list', tr("ToDo-Liste"))}
          {fourEyes && item('/approvals', 'shield', tr("Freigaben"))}
          {tenant && item('/tenant', 'building', tr("Tenant-Einstellungen"))}
          {can('ReadAudit') && item('/audit', 'scroll', tr("Audit-Log"))}
          </div>

          <div className="nav-group nav-group-partner" tabIndex={-1}>
          <div className="section partner"><span className="context-dot" />{tr("Partner")}</div>
          {item('/tenants', 'building', tr("Tenants"))}
          {can('ManagePartnerStock') && item('/partner/stock', 'box', tr("Partnerlager"))}
          {item('/profiles', 'sliders', tr("Profile"))}
          {can('ManageUsers') && item('/users', 'users', tr("Benutzer & Rollen"))}
          {can('ManageApiKeys') && item('/partner/notifications', 'bell', tr("Benachrichtigungen"))}
          {item('/platform', 'settings', <>{tr("Globale Einstellungen")}{updates?.updateAvailable && updates.canApply && <span className="dot-warn pulse" title={tr("Update auf {latestVersion} verfügbar", { latestVersion: updates.latestVersion })} />}</>)}
          {(can('RegisterKeys') || can('ManageInventory')) && item('/agents', 'monitor', tr("Mein Agent"))}
          </div>

          <div className="nav-group nav-group-help">
          <div className="section">{tr("Hilfe")}</div>
          {item('/passkey', 'info', tr("Passkeys erklärt"))}
          </div>
        </nav>
        <div className="sidebar-foot">
          <NavLink to="/account" className="account-link">
            <Avatar name={me.user?.displayName} photo={me.user?.loginKind === 'Entra'} />
            <span className="account-text">
              <strong>{me.user?.displayName}</strong>
              <span className="muted small">{me.user?.role === 'Custom' ? me.user?.customRoleName : roleLabel[me.user?.role ?? ''] ?? me.user?.role}</span>
            </span>
          </NavLink>
          <div className="foot-actions">
            <ThemeToggle />
            <LanguageToggle saveToAccount />
            <button type="button" className="ghost small" onClick={logout}><Icon name="logout" size={16} />{tr("Abmelden")}</button>
          </div>
          {/* Klick auf die Version öffnet den Versionsverlauf (kein eigener Menüpunkt). */}
          {me.version && <Link to="/versionen" className="small muted version" title={tr("Versionsverlauf anzeigen")}><Icon name="history" size={13} /><span>{tr("Version {version}", { version: me.version })}</span>{updates?.updateAvailable && <span className="version-update"><span className="dot-warn pulse" />{tr("Update auf {latestVersion}", { latestVersion: updates.latestVersion })}</span>}</Link>}
        </div>
      </aside>
      <main className="main">
        <DemoBanner />
        <div className="topbar">
          <TenantSwitcher partnerContext={partner} />
          {partner
            ? <span className="context-badge partner" title={tr("Einstellungen hier gelten für die ganze Plattform und alle Kunden")}>{tr("Partnerebene")}</span>
            : <span className="context-badge customer">{tr("Kundenebene")}{tenant ? ': ' + tenant.displayName : tr(", alle Tenants")}</span>}
        </div>
        <div className="page" key={location.pathname}>
        {me.recoveryCodes != null && me.recoveryCodes <= 2 && location.pathname !== '/account' && (
          <div className="alert warn">
            {me.recoveryCodes === 0
              ? tr("Für Ihr Owner-Konto gibt es noch keine Notfallcodes. Damit kommen Sie auch dann in die Plattform, wenn die Anmeldung mit Microsoft oder Passkey nicht klappt.")
              : tr("Nur noch {recoveryCodes} Notfallcode{n} übrig.", { recoveryCodes: me.recoveryCodes, n: me.recoveryCodes === 1 ? '' : 's' })}{' '}
            <Link to="/account#notfallcodes">{me.recoveryCodes === 0 ? tr("Jetzt erzeugen und ausdrucken") : tr("Neue Codes erzeugen")}</Link>
          </div>
        )}
        <Routes>
          <Route path="/" element={<DashboardPage />} />
          <Route path="/keys" element={<KeysPage />} />
          <Route path="/sicherheitshinweise" element={<AdvisoriesPage />} />
          <Route path="/erinnerungen" element={<RemindersPage />} />
          <Route path="/rollout" element={<RolloutPage />} />
          <Route path="/assignments" element={<ToPeopleView />} />
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
          <Route path="/partner/notifications" element={<IntegrationsPage />} />
          <Route path="/partner/integrations" element={<Navigate to="/partner/notifications" replace />} />
          <Route path="/partner/audit" element={<AllTenants to="/audit" />} />
          <Route path="/tenants" element={<TenantsPage />} />
          <Route path="/tenants/:id" element={<TenantRoute />} />
          <Route path="/agents" element={<AgentsPage />} />
          <Route path="/users" element={<UsersPage />} />
          <Route path="/platform" element={<PlatformPage />} />
          <Route path="/versionen" element={<VersionsPage />} />
          <Route path="/account" element={<AccountPage />} />
          <Route path="/passkey/*" element={<PasskeyInfoPage authenticated embedded />} />
          <Route path="/login" element={<Navigate to="/" replace />} />
          <Route path="*" element={<Empty>{tr("Seite nicht gefunden.")} <Link to="/">{tr("Zur Übersicht")}</Link></Empty>} />
        </Routes>
        </div>
      </main>
    </div>
  );
}

/** Frühere Partner-Seiten: Umschalter auf „Alle Tenants“ und weiter zur Seite auf Kundenebene. */
/** Frühere Seite „Wer hat welchen Key“: jetzt die Personenansicht des Inventars. */
function ToPeopleView() {
  const location = useLocation();
  const params = new URLSearchParams(location.search);
  params.delete('view');
  return <Navigate to={'/keys?view=people' + (params.size ? '&' + params.toString() : '')} replace />;
}

function AllTenants({ to }: { to: string }) {
  const { setTenantId } = useTenant();
  useEffect(() => { setTenantId(''); }, [setTenantId]);
  return <Navigate to={to} replace />;
}

/** Tenant-Einstellungen des oben gewählten Tenants. */
function SelectedTenantPage() {
  const { tenant } = useTenant();
  if (!tenant) return <><PageHeader title={tr("Tenant-Einstellungen")} /><ChooseTenant purpose={tr("Für die Einstellungen")} /></>;
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

/**
 * Karten blenden beim Hineinscrollen ein (wie auf der Passkey-Seite). Sichtbare Karten bekommen sofort „in“;
 * nachgeladene Karten werden über einen MutationObserver erfasst. Ohne IntersectionObserver bleibt alles sichtbar.
 */
function useReveal(path: string) {
  useEffect(() => {
    const page = document.querySelector('.page');
    if (!page || typeof IntersectionObserver === 'undefined' || window.matchMedia?.('(prefers-reduced-motion: reduce)').matches) return;
    const root = document.documentElement;
    const io = new IntersectionObserver((entries) => {
      for (const e of entries) {
        if (!e.isIntersecting) continue;
        const el = e.target as HTMLElement;
        el.classList.add('in');
        io.unobserve(el);
        setTimeout(() => { el.style.transitionDelay = ''; }, 800);
      }
    }, { rootMargin: '0px 0px -40px 0px' });
    const scan = () => page.querySelectorAll('.card:not(.in):not([data-reveal])').forEach((el, i) => {
      el.setAttribute('data-reveal', '');
      (el as HTMLElement).style.transitionDelay = Math.min(i, 4) * 60 + 'ms';
      io.observe(el);
    });
    scan();
    root.classList.add('reveal-on');
    const mo = new MutationObserver(scan);
    mo.observe(page, { childList: true, subtree: true });
    return () => { mo.disconnect(); io.disconnect(); root.classList.remove('reveal-on'); };
  }, [path]);
}

/** Profilbild aus Microsoft 365 (nur Entra-Konten); fehlt es, bleiben die Initialen. */
function Avatar({ name, photo }: { name?: string; photo: boolean }) {
  const [failed, setFailed] = useState(false);
  return (
    <span className="avatar" aria-hidden>
      {photo && !failed ? <img src="/api/me/photo" alt="" onError={() => setFailed(true)} /> : initials(name)}
    </span>
  );
}

function initials(name?: string) {
  const parts = (name ?? '').trim().split(/\s+/).filter(Boolean);
  return ((parts[0]?.[0] ?? '') + (parts.length > 1 ? parts[parts.length - 1][0] : '')).toUpperCase() || '?';
}
