import { NavLink } from 'react-router';
import { Icon, type IconName } from './icons';

const modes: { to: string; icon: IconName; label: string; hint: string; end?: boolean }[] = [
  { to: '/register', icon: 'user', label: 'Für eine Person', hint: 'Regelfall: ein Key, eine Person', end: true },
  { to: '/register/shared', icon: 'share', label: 'Geteilter Key / Breakglass', hint: 'Mehrere Konten auf einem Key, beim Verwahrer' },
  { to: '/batches', icon: 'layers', label: 'Stapel', hint: 'Viele Personen nacheinander' },
];

/** Gemeinsame Umschaltung der drei Registrierungswege (ein Menüpunkt „Registrieren“). */
export function RegisterModes() {
  return (
    <nav className="mode-cards" aria-label="Art der Registrierung">
      {modes.map((m) => (
        <NavLink key={m.to} to={m.to} end={m.end} className={({ isActive }) => 'mode-card' + (isActive ? ' active' : '')}>
          <span className="mode-icon"><Icon name={m.icon} size={18} /></span>
          <span><strong>{m.label}</strong><span className="small muted">{m.hint}</span></span>
        </NavLink>
      ))}
    </nav>
  );
}

/** Pfade, unter denen der Menüpunkt „Registrieren“ aktiv ist. */
export const isRegisterPath = (path: string) => path === '/register' || path.startsWith('/register/') || path === '/batches' || path.startsWith('/batches/');
