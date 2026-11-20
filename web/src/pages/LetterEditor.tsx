import { useEffect, useMemo, useRef, useState } from 'react';
import { api, ApiError } from '../api';
import { useToast } from '../feedback';
import { Icon } from '../icons';
import { Badge, Card, download, ErrorBox, Spinner, useAction, useApi, useDebounced } from '../ui';

interface LetterField { key: string; label: string; default: string; multiline: boolean; hint?: string | null }
interface LetterVariable { name: string; description: string; example: string }
interface LetterResponse { fields: LetterField[]; variables: LetterVariable[]; partner: Record<string, string>; tenant: Record<string, string> | null }
type Texts = Record<string, string>;

/** Abschnitte des Editors, in der Reihenfolge des Briefs. */
const groups: { id: string; label: string; keys: string[] }[] = [
  { id: 'head', label: 'Anschreiben', keys: ['title', 'recipient', 'intro'] },
  { id: 'pin', label: 'PIN', keys: ['pinForced', 'pinOwn', 'changeTitle', 'changeText'] },
  { id: 'guide', label: 'Anleitung', keys: ['guideTitle', 'step1Title', 'step1Text', 'step2Title', 'step2Text', 'step3Title', 'step3Text', 'step4Title', 'step4Text'] },
  { id: 'end', label: 'Hinweise und Schluss', keys: ['hintsTitle', 'hints', 'infoTitle', 'closing'] },
];

const clean = (t: Texts) => Object.fromEntries(Object.entries(t).map(([k, v]) => [k, v.trim()]).filter(([, v]) => v)) as Texts;
const same = (a: Texts, b: Texts) => JSON.stringify(Object.entries(clean(a)).sort()) === JSON.stringify(Object.entries(clean(b)).sort());

/**
 * Texte des PIN-Briefs bearbeiten, mit {{Platzhaltern}} und Live-Vorschau als PDF.
 * Ohne tenantId: Vorgabe des Partners. Mit tenantId: leere Felder übernehmen den Text des Partners.
 */
export function LetterEditorCard({ tenantId, editable }: { tenantId?: string; editable: boolean }) {
  const qs = tenantId ? '?tenantId=' + tenantId : '';
  const { data, error, reload } = useApi<LetterResponse>('/api/branding/letter' + qs, [tenantId]);
  const own = useMemo(() => (tenantId ? data?.tenant : data?.partner) ?? {}, [data, tenantId]);
  const [form, setForm] = useState<Texts>({});
  const [group, setGroup] = useState(groups[0].id);
  const [forced, setForced] = useState(true);
  const [focused, setFocused] = useState<string | null>(null);
  const refs = useRef<Record<string, HTMLInputElement | HTMLTextAreaElement | null>>({});
  const { busy, error: actError, run, setError } = useAction();
  const toast = useToast();
  useEffect(() => { if (data) setForm({ ...own }); }, [data]); // eslint-disable-line react-hooks/exhaustive-deps

  // Vorschau: nach kurzer Pause mit dem aktuellen Entwurf neu erzeugen.
  const draft = useDebounced(JSON.stringify({ texts: clean(form), forced }), 700);
  const preview = useLetterPreview(editable && data ? '/api/branding/letter/preview.pdf' + qs : null, draft);

  if (!data) return <Card title="Texte des PIN-Briefs" className="mt"><ErrorBox error={error} />{!error && <Spinner />}</Card>;

  const fields = new Map(data.fields.map((f) => [f.key, f]));
  const inherited = (key: string) => (tenantId ? data.partner[key] : undefined) || fields.get(key)!.default;
  const dirty = !same(form, own);
  const changed = Object.keys(clean(own)).length;
  const countIn = (keys: string[]) => keys.filter((k) => form[k]?.trim()).length;

  const set = (key: string, value: string) => setForm((f) => ({ ...f, [key]: value }));
  // Ein leeres Feld zeigt beim Anklicken den geerbten Text zum Bearbeiten; unverändert bleibt es leer (= Vorgabe).
  const onFocus = (key: string) => { setFocused(key); if (!form[key]?.trim()) set(key, inherited(key)); };
  const onBlur = (key: string) => { if ((form[key] ?? '').trim() === inherited(key).trim()) set(key, ''); };

  const insert = (name: string) => {
    const key = focused && groups.find((g) => g.id === group)!.keys.includes(focused) ? focused : null;
    const el = key ? refs.current[key] : null;
    if (!key || !el) { toast.error('Klicken Sie zuerst in das Textfeld, in das der Platzhalter soll.'); return; }
    const token = `{{${name}}}`;
    const value = el.value;
    const start = el.selectionStart ?? value.length, end = el.selectionEnd ?? value.length;
    set(key, value.slice(0, start) + token + value.slice(end));
    requestAnimationFrame(() => { el.focus(); el.setSelectionRange(start + token.length, start + token.length); });
  };

  const save = () => run(async () => {
    await api.put('/api/branding/letter' + qs, { texts: clean(form) });
    toast.ok('Brieftexte gespeichert');
    reload();
  });

  return (
    <Card
      title={<span id="letter">Texte des PIN-Briefs</span>}
      className="mt"
      actions={changed > 0 ? <Badge tone="info">{changed} {changed === 1 ? 'Text' : 'Texte'} angepasst</Badge> : undefined}
    >
      <p className="muted small" style={{ marginTop: 0 }}>
        {tenantId
          ? 'Leere Felder übernehmen den Text aus den globalen Einstellungen. Platzhalter wie {{Vorname}} werden beim Drucken ersetzt.'
          : 'Jeder Text lässt sich anpassen, leere Felder behalten den Standardtext. Tenants können einzelne Texte überschreiben. Platzhalter wie {{Vorname}} werden beim Drucken ersetzt.'}
      </p>
      <div className="letter-editor">
        <div className="le-form">
          <div className="segmented le-tabs" role="tablist">
            {groups.map((g) => {
              const n = countIn(g.keys);
              return (
                <button key={g.id} role="tab" aria-selected={group === g.id} className={group === g.id ? 'on' : ''} onClick={() => setGroup(g.id)}>
                  {g.label}{n > 0 && <span className="le-count">{n}</span>}
                </button>
              );
            })}
          </div>

          {editable && (
            <div className="le-vars" aria-label="Platzhalter einfügen">
              <span className="muted small">Platzhalter einfügen:</span>
              {data.variables.map((v) => (
                <button key={v.name} type="button" className="le-chip" title={`${v.description}, z. B. „${v.example}“`}
                  onMouseDown={(e) => e.preventDefault()} onClick={() => insert(v.name)}>
                  {v.name}
                </button>
              ))}
            </div>
          )}

          <div className="le-fields">
            {groups.find((g) => g.id === group)!.keys.map((key) => {
              const f = fields.get(key);
              if (!f) return null;
              const value = form[key] ?? '';
              const custom = !!(own[key]?.trim());
              const common = {
                id: 'lf-' + key, value, disabled: !editable, maxLength: 1500,
                placeholder: inherited(key),
                onChange: (e: { target: { value: string } }) => set(key, e.target.value),
                onFocus: () => onFocus(key), onBlur: () => onBlur(key),
              };
              const text = value || inherited(key);
              const rows = Math.min(9, Math.max(2, text.split('\n').reduce((n, l) => n + Math.max(1, Math.ceil(l.length / 85)), 0) + 1));
              return (
                <div key={key} className={'le-field' + (value.trim() ? ' le-custom' : '')}>
                  <div className="le-label">
                    <label htmlFor={'lf-' + key}>{f.label}</label>
                    {custom && <Badge tone="info">angepasst</Badge>}
                    {editable && value.trim() && focused !== key && (
                      <button type="button" className="link small" onClick={() => set(key, '')}>{tenantId ? 'Partner-Text' : 'Standardtext'}</button>
                    )}
                  </div>
                  {f.multiline
                    ? <textarea {...common} rows={rows} ref={(el) => { refs.current[key] = el; }} />
                    : <input {...common} ref={(el) => { refs.current[key] = el; }} />}
                  {f.hint && <span className="muted small">{f.hint}</span>}
                </div>
              );
            })}
          </div>

          <ErrorBox error={actError} />
          {editable && (
            <div className="actions-row">
              <button className="primary" disabled={busy || !dirty} onClick={save}>Speichern</button>
              {dirty && <button type="button" onClick={() => { setForm({ ...own }); setError(undefined); }}>Verwerfen</button>}
              {Object.keys(clean(form)).length > 0 && (
                <button type="button" className="ghost" onClick={() => setForm({})}>
                  <Icon name="refresh" size={14} />Alle auf {tenantId ? 'Partner-Texte' : 'Standard'}
                </button>
              )}
            </div>
          )}
        </div>

        <div className="le-preview">
          <div className="le-preview-head">
            <label className="check small">
              <input type="checkbox" checked={forced} onChange={(e) => setForced(e.target.checked)} disabled={!editable} />
              PIN-Wechsel erzwungen
            </label>
            {preview.busy && <span className="muted small le-busy"><Spinner />Aktualisiere …</span>}
            {preview.url
              ? <a className="btn ghost small" href={preview.url} target="_blank" rel="noreferrer"><Icon name="download" size={14} />Als PDF</a>
              : !editable && <button className="ghost small" onClick={() => download('/api/branding/preview.pdf' + qs)}><Icon name="download" size={14} />Muster-PIN-Brief</button>}
          </div>
          <div className="le-sheet">
            {preview.url
              ? <iframe title="Vorschau PIN-Brief" src={preview.url + '#toolbar=0&navpanes=0&view=FitH'} />
              : <div className="le-sheet-empty muted small">{preview.error ?? (editable ? 'Vorschau wird erstellt …' : 'Die Live-Vorschau steht nur mit Bearbeitungsrecht zur Verfügung.')}</div>}
          </div>
          <p className="muted small le-note">Vorschau mit Beispieldaten (Erika Mustermann, PIN 482913). {dirty && 'Zeigt den ungespeicherten Entwurf.'}</p>
        </div>
      </div>
    </Card>
  );
}

/** Erzeugt die PDF-Vorschau per POST und hält eine Blob-URL; ältere Antworten werden verworfen. */
function useLetterPreview(url: string | null, draft: string) {
  const [state, setState] = useState<{ url?: string; busy: boolean; error?: string }>({ busy: false });
  const seq = useRef(0);
  useEffect(() => {
    if (!url) return;
    const mine = ++seq.current;
    const { texts, forced } = JSON.parse(draft) as { texts: Texts; forced: boolean };
    setState((s) => ({ ...s, busy: true }));
    (async () => {
      try {
        const res = await fetch(url, {
          method: 'POST', credentials: 'same-origin',
          headers: { 'X-YLM-CSRF': '1', 'Content-Type': 'application/json' },
          body: JSON.stringify({ texts, pinChangeForced: forced }),
        });
        if (!res.ok) {
          const j = await res.json().catch(() => ({}));
          throw new ApiError(res.status, j.code ?? 'error', j.message ?? `Fehler ${res.status}`);
        }
        const blob = await res.blob();
        if (mine !== seq.current) return;
        const next = URL.createObjectURL(blob.type ? blob : new Blob([blob], { type: 'application/pdf' }));
        setState((s) => { if (s.url) URL.revokeObjectURL(s.url); return { url: next, busy: false }; });
      } catch (e) {
        if (mine === seq.current) setState((s) => ({ ...s, busy: false, error: e instanceof Error ? e.message : String(e) }));
      }
    })();
  }, [url, draft]);
  useEffect(() => () => { setState((s) => { if (s.url) URL.revokeObjectURL(s.url); return s; }); }, []);
  return state;
}
