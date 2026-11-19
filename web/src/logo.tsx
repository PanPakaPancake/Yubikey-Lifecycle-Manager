import { useId } from 'react';

/**
 * Logo „YubiKey LC Manager“: ein Key in einer Umlaufbahn (der Lebenszyklus). Der Bogen dreht sich langsam,
 * ein Lichtpunkt läuft mit, die goldene Fläche pulsiert wie beim Berühren. Beim Darüberfahren dreht der
 * Bogen schneller und der Key „tippt“ einmal. Ohne Bewegungswunsch (reduced motion) steht alles still.
 */
export function LogoMark({ size = 40 }: { size?: number }) {
  const id = useId().replace(/:/g, '');
  return (
    <span className="ylm-logo" style={{ width: size, height: size }} aria-hidden>
      <svg viewBox="0 0 48 48" width={size} height={size}>
        <defs>
          <linearGradient id={id + 'bg'} x1="0" y1="0" x2="1" y2="1">
            <stop offset="0" stopColor="#151d36" /><stop offset="1" stopColor="#070b16" />
          </linearGradient>
          <linearGradient id={id + 'ring'} x1="0" y1="0" x2="1" y2="1">
            <stop offset="0" stopColor="#a78bfa" /><stop offset=".55" stopColor="#22d3ee" /><stop offset="1" stopColor="#34d399" />
          </linearGradient>
          <radialGradient id={id + 'disc'} cx=".4" cy=".35" r=".7">
            <stop offset="0" stopColor="#fde68a" /><stop offset=".6" stopColor="#f5c542" /><stop offset="1" stopColor="#c89211" />
          </radialGradient>
        </defs>
        <rect className="ylm-logo-tile" x="1" y="1" width="46" height="46" rx="13" fill={`url(#${id}bg)`} />
        <rect x="1.5" y="1.5" width="45" height="45" rx="12.5" fill="none" stroke={`url(#${id}ring)`} strokeOpacity=".45" />
        {/* Umlaufbahn: drei Viertel Bogen mit Pfeilspitze, dreht sich */}
        <g className="ylm-logo-orbit">
          <circle cx="24" cy="24" r="16" fill="none" stroke={`url(#${id}ring)`} strokeWidth="2.6" strokeLinecap="round" strokeDasharray="76 25" />
          <path d="M21 4.6 24.6 8 21 11.4" fill="none" stroke="#34d399" strokeWidth="2.4" strokeLinecap="round" strokeLinejoin="round" />
        </g>
        <g className="ylm-logo-sparkle"><circle cx="24" cy="8" r="1.7" fill="#e0f2fe" /></g>
        {/* Der Key */}
        <g className="ylm-logo-key">
          <rect x="15.5" y="19" width="15" height="10" rx="3.2" fill="#1f2a44" stroke="#3b4a6b" strokeWidth=".8" />
          <rect x="30" y="21.2" width="5" height="5.6" rx="1" fill="#9aa4b4" />
          <circle cx="18.6" cy="24" r="1.1" fill="none" stroke="#7d8ba6" strokeWidth=".8" />
          <circle className="ylm-logo-glow" cx="24.5" cy="24" r="3.4" fill="none" stroke="#fbbf24" strokeWidth="1.2" />
          <circle className="ylm-logo-disc" cx="24.5" cy="24" r="2.7" fill={`url(#${id}disc)`} />
        </g>
      </svg>
    </span>
  );
}

/** Wortmarke neben dem Logo. */
export function Wordmark({ sub }: { sub?: string }) {
  return (
    <span className="ylm-wordmark">
      <span className="ylm-wordmark-eyebrow">YubiKey</span>
      <span className="ylm-wordmark-name">LC <b>Manager</b></span>
      {sub && <small>{sub}</small>}
    </span>
  );
}
