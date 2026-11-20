import { createPortal } from 'react-dom';
import { createContext, useCallback, useContext, useEffect, useRef, useState, type ReactNode } from 'react';
import { Icon } from './icons';

// Kurze Rückmeldungen (Toasts) und Bestätigungsdialoge. Ersetzt dauerhafte Hinweiskästen nach Aktionen
// und window.confirm, damit Ergebnisse animiert erscheinen und von selbst wieder verschwinden.

type ToastTone = 'ok' | 'info' | 'warn' | 'error';
interface Toast { id: number; tone: ToastTone; text: ReactNode; leaving?: boolean }
interface ToastApi {
  ok: (text: ReactNode) => void;
  info: (text: ReactNode) => void;
  warn: (text: ReactNode) => void;
  error: (text: ReactNode) => void;
}

interface ConfirmOptions {
  title: string;
  message: ReactNode;
  confirmLabel?: string;
  danger?: boolean;
}

const ToastCtx = createContext<ToastApi>({ ok: () => {}, info: () => {}, warn: () => {}, error: () => {} });
const ConfirmCtx = createContext<(o: ConfirmOptions) => Promise<boolean>>(async () => false);

const toneIcon: Record<ToastTone, 'check' | 'info' | 'alert' | 'x'> = { ok: 'check', info: 'info', warn: 'alert', error: 'x' };

export function FeedbackProvider({ children }: { children: ReactNode }) {
  const [toasts, setToasts] = useState<Toast[]>([]);
  const seq = useRef(0);

  const dismiss = useCallback((id: number) => {
    setToasts((ts) => ts.map((t) => (t.id === id ? { ...t, leaving: true } : t)));
    window.setTimeout(() => setToasts((ts) => ts.filter((t) => t.id !== id)), 220);
  }, []);

  const push = useCallback((tone: ToastTone, text: ReactNode) => {
    const id = ++seq.current;
    setToasts((ts) => [...ts.slice(-3), { id, tone, text }]);
    window.setTimeout(() => dismiss(id), tone === 'error' ? 8000 : 4500);
  }, [dismiss]);

  const [api] = useState<ToastApi>(() => ({
    ok: (t) => push('ok', t), info: (t) => push('info', t), warn: (t) => push('warn', t), error: (t) => push('error', t),
  }));
  // push is stable, so the api object above never needs to change.

  const [dialog, setDialog] = useState<(ConfirmOptions & { resolve: (v: boolean) => void }) | null>(null);
  const confirm = useCallback((o: ConfirmOptions) => new Promise<boolean>((resolve) => setDialog({ ...o, resolve })), []);
  const close = useCallback((v: boolean) => { setDialog((d) => { d?.resolve(v); return null; }); }, []);

  return (
    <ToastCtx.Provider value={api}>
      <ConfirmCtx.Provider value={confirm}>
        {children}
        {createPortal(<div className="toasts" aria-live="polite">
          {toasts.map((t) => (
            <div key={t.id} className={'toast tone-' + t.tone + (t.leaving ? ' leaving' : '')} role={t.tone === 'error' ? 'alert' : 'status'}>
              <span className="toast-icon"><Icon name={toneIcon[t.tone]} size={16} /></span>
              <div className="toast-text">{t.text}</div>
              <button type="button" className="icon-btn" aria-label="Schließen" onClick={() => dismiss(t.id)}><Icon name="x" size={14} /></button>
            </div>
          ))}
        </div>, document.body)}
        {/* An body hängen und über alle Dialoge legen: Bestätigungen kommen oft aus einem offenen Dialog heraus. */}
        {dialog && createPortal(<ConfirmDialog {...dialog} onClose={close} />, document.body)}
      </ConfirmCtx.Provider>
    </ToastCtx.Provider>
  );
}

function ConfirmDialog({ title, message, confirmLabel = 'Bestätigen', danger, onClose }: ConfirmOptions & { onClose: (v: boolean) => void }) {
  const ok = useRef<HTMLButtonElement>(null);
  useEffect(() => {
    ok.current?.focus();
    const h = (e: KeyboardEvent) => { if (e.key === 'Escape') onClose(false); };
    window.addEventListener('keydown', h);
    return () => window.removeEventListener('keydown', h);
  }, [onClose]);
  return (
    <div className="modal-backdrop confirm-backdrop" onMouseDown={() => onClose(false)}>
      <div className="modal confirm" role="alertdialog" aria-modal="true" aria-label={title} onMouseDown={(e) => e.stopPropagation()}>
        <div className="modal-body confirm-body">
          <span className={'confirm-icon' + (danger ? ' danger' : '')}><Icon name={danger ? 'alert' : 'info'} size={20} /></span>
          <div>
            <h2>{title}</h2>
            <div className="muted">{message}</div>
          </div>
        </div>
        <div className="modal-foot">
          <button type="button" onClick={() => onClose(false)}>Abbrechen</button>
          <button type="button" ref={ok} className={danger ? 'primary danger-fill' : 'primary'} onClick={() => onClose(true)}>{confirmLabel}</button>
        </div>
      </div>
    </div>
  );
}

export function useToast() {
  return useContext(ToastCtx);
}

/** Returns an async confirm(): resolves true when the user confirms. */
export function useConfirm() {
  return useContext(ConfirmCtx);
}
