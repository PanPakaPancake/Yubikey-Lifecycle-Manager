/** Markenzeichen der Oberfläche: stilisierter Security-Key auf Farbverlauf. */
export function BrandMark({ size = 34 }: { size?: number }) {
  return (
    <span className="brand-mark" style={{ width: size, height: size }} aria-hidden>
      <svg width={size * 0.62} height={size * 0.62} viewBox="0 0 24 24" fill="none">
        <rect x="3" y="7" width="14" height="10" rx="4" fill="#fff" fillOpacity=".95" />
        <rect x="17" y="9.5" width="4.5" height="5" rx="1" fill="#fff" fillOpacity=".6" />
        <circle cx="9" cy="12" r="2.6" fill="#e0b03a" />
      </svg>
    </span>
  );
}

/** Großer, schwebender Key für die Anmeldeseite; der Kontakt pulsiert wie beim Berühren. */
export function HeroKey() {
  return (
    <div className="hero-key" aria-hidden>
      <svg viewBox="0 0 320 170">
        <defs>
          <linearGradient id="hk-body" x1="0" y1="0" x2="0" y2="1">
            <stop offset="0" stopColor="#3a4258" />
            <stop offset="1" stopColor="#1b2131" />
          </linearGradient>
          <linearGradient id="hk-metal" x1="0" y1="0" x2="0" y2="1">
            <stop offset="0" stopColor="#e9edf3" />
            <stop offset="1" stopColor="#9aa3b1" />
          </linearGradient>
          <radialGradient id="hk-gold" cx=".4" cy=".35" r=".7">
            <stop offset="0" stopColor="#ffe08a" />
            <stop offset="1" stopColor="#c9921f" />
          </radialGradient>
        </defs>
        <rect x="222" y="58" width="78" height="44" rx="6" fill="url(#hk-metal)" />
        <rect x="236" y="70" width="12" height="8" rx="1.5" fill="#6b7383" />
        <rect x="262" y="70" width="12" height="8" rx="1.5" fill="#6b7383" />
        <rect x="20" y="22" width="216" height="116" rx="40" fill="url(#hk-body)" />
        <rect x="20" y="22" width="216" height="116" rx="40" fill="none" stroke="rgba(255,255,255,.12)" />
        <circle cx="50" cy="80" r="9" fill="none" stroke="rgba(255,255,255,.18)" strokeWidth="5" />
        <circle className="touch-ring" cx="118" cy="80" r="26" fill="none" stroke="#ffd56b" strokeOpacity=".7" strokeWidth="2" />
        <circle className="touch-ring r2" cx="118" cy="80" r="26" fill="none" stroke="#ffd56b" strokeOpacity=".7" strokeWidth="2" />
        <circle className="touch-pad" cx="118" cy="80" r="26" fill="url(#hk-gold)" />
        <path d="M108 80a10 10 0 0 1 20 0M112 86a6 6 0 0 1 12 0M104 74a14 14 0 0 1 28 0" stroke="#8a5f0f" strokeOpacity=".55" strokeWidth="2" fill="none" strokeLinecap="round" />
      </svg>
    </div>
  );
}
