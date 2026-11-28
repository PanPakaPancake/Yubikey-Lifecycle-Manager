import { api } from './api';
import { Icon } from './icons';
import { language, languages, setLanguage, tr, type Language } from './i18n';

/** Umschalter Deutsch/English. Angemeldet wird die Wahl zusätzlich im Benutzerkonto gespeichert. */
export function LanguageToggle({ saveToAccount = false }: { saveToAccount?: boolean }) {
  const current = language();
  const other: Language = current === 'de' ? 'en' : 'de';
  const otherLabel = languages.find((l) => l.id === other)!.label;
  const switchTo = async () => {
    if (saveToAccount) {
      await api.put('/api/me/language', { language: other }).catch(() => undefined);
    }
    setLanguage(other);
  };
  return (
    <button type="button" className="ghost small lang-toggle" onClick={() => void switchTo()} lang={other}
      title={tr("Sprache: {n} (klicken für {other})", { n: languages.find((l) => l.id === current)!.label, other: otherLabel })}>
      <Icon name="globe" size={16} /> {current.toUpperCase()}
    </button>
  );
}
