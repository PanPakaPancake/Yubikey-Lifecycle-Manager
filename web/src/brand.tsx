import { KeyArt } from './pages/PasskeyInfo';

/** Markenzeichen der Oberfläche: der YubiKey wie auf der Passkey-Seite, auf heller Kachel. */
export function BrandMark({ size = 36 }: { size?: number }) {
  return (
    <span className="brand-mark" style={{ width: size, height: size }} aria-hidden>
      <KeyArt size={size * 0.8} />
    </span>
  );
}

/** Großer, schwebender Key für die Anmeldeseite; die goldene Fläche pulsiert wie beim Berühren. */
export function HeroKey() {
  return <div className="hero-key" aria-hidden><span className="pk-float"><KeyArt size={300} glow /></span></div>;
}
