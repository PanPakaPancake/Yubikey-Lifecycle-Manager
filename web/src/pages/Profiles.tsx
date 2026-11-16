import { useState } from 'react';
import { api, type Profile } from '../api';
import { useCan } from '../auth';
import { Badge, Card, ErrorBox, Field, Info, Modal, PageHeader, Spinner, TenantSelect, useAction, useApi } from '../ui';

const knownModels = ['YubiKey 5 NFC', 'YubiKey 5C NFC', 'YubiKey 5C', 'YubiKey 5 Nano', 'YubiKey 5C Nano', 'YubiKey 5Ci', 'YubiKey Bio', 'YubiKey C Bio', 'Security Key NFC', 'Security Key C NFC'];

export default function ProfilesPage() {
  const can = useCan();
  const { data, error, loading, reload } = useApi<Profile[]>('/api/profiles');
  const { data: tenants } = useApi<{ id: string; displayName: string }[]>('/api/tenants');
  const [editing, setEditing] = useState<Partial<Profile> | null>(null);
  const tenantName = (id?: string) => (id ? tenants?.find((t) => t.id === id)?.displayName ?? 'Tenant' : 'Alle Tenants');

  return (
    <>
      <PageHeader title="Registrierungsprofile" subtitle="Legen fest, wie Keys bei der Registrierung vorbereitet werden." actions={can('ManageProfiles') && <button className="primary" onClick={() => setEditing({})}>Profil anlegen</button>} />
      <ErrorBox error={error} />
      {loading && !data && <Spinner />}
      <div className="grid cols-2">
        {data?.map((p) => (
          <Card key={p.id} title={<>{p.name} {p.isDefault && <Badge tone="info">Standard</Badge>}</>} actions={can('ManageProfiles') && <button onClick={() => setEditing(p)}>Bearbeiten</button>}>
            {p.description && <p className="muted">{p.description}</p>}
            <table>
              <tbody>
                <tr><th>Gültig für</th><td>{tenantName(p.tenantId)}</td></tr>
                <tr><th>PIN</th><td>{p.pinStrategy === 'Random' ? `Zufällig, ${p.pinLength} Ziffern` : 'Feste Standard-PIN'}</td></tr>
                <tr><th>PIN-Wechsel erzwingen</th><td>{p.forcePinChange ? 'Ja (ab Firmware 5.7)' : 'Nein'}</td></tr>
                <tr><th>Werksreset vorher</th><td>{p.factoryResetBeforeRegistration ? 'Ja' : 'Nein'}</td></tr>
                <tr><th>Anzeigename</th><td className="mono small">{p.displayNameTemplate}</td></tr>
                <tr><th>Erlaubte Modelle</th><td>{p.allowedModels.length ? p.allowedModels.join(', ') : 'alle'}</td></tr>
                <tr><th>Mindest-Firmware</th><td>{p.minimumFirmware ?? '–'}</td></tr>
              </tbody>
            </table>
          </Card>
        ))}
      </div>
      {editing && <ProfileModal profile={editing} onClose={() => setEditing(null)} onSaved={() => { setEditing(null); reload(); }} />}
    </>
  );
}

function ProfileModal({ profile, onClose, onSaved }: { profile: Partial<Profile>; onClose: () => void; onSaved: () => void }) {
  const [p, setP] = useState({
    tenantId: profile.tenantId ?? '',
    name: profile.name ?? '',
    description: profile.description ?? '',
    pinStrategy: profile.pinStrategy ?? 'Random',
    pinLength: profile.pinLength ?? 8,
    fixedPin: '',
    forcePinChange: profile.forcePinChange ?? true,
    factoryResetBeforeRegistration: profile.factoryResetBeforeRegistration ?? false,
    displayNameTemplate: profile.displayNameTemplate ?? 'YubiKey {Seriennummer}',
    allowedModels: profile.allowedModels ?? [],
    minimumFirmware: profile.minimumFirmware ?? '',
    isDefault: profile.isDefault ?? false,
  });
  const set = <K extends keyof typeof p>(k: K, v: (typeof p)[K]) => setP((s) => ({ ...s, [k]: v }));
  const { busy, error, run } = useAction();
  const isNew = !profile.id;

  const save = () => run(async () => {
    const body = { ...p, tenantId: p.tenantId || null, minimumFirmware: p.minimumFirmware || null, fixedPin: p.pinStrategy === 'Fixed' && p.fixedPin ? p.fixedPin : null, description: p.description || null };
    if (isNew) await api.post('/api/profiles', body); else await api.put('/api/profiles/' + profile.id, body);
    onSaved();
  });
  const remove = () => run(async () => { await api.del('/api/profiles/' + profile.id); onSaved(); });

  return (
    <Modal title={isNew ? 'Profil anlegen' : 'Profil bearbeiten'} onClose={onClose} footer={<>
      {!isNew && <button className="danger" style={{ marginRight: 'auto' }} disabled={busy} onClick={remove}>Löschen</button>}
      <button onClick={onClose}>Abbrechen</button>
      <button className="primary" disabled={!p.name || busy} onClick={save}>Speichern</button>
    </>}>
      <div className="grid cols-2">
        <Field label="Name"><input value={p.name} onChange={(e) => set('name', e.target.value)} /></Field>
        <Field label="Gültig für"><TenantSelect value={p.tenantId} onChange={(v) => set('tenantId', v)} /></Field>
      </div>
      <Field label="Beschreibung"><input value={p.description} onChange={(e) => set('description', e.target.value)} /></Field>
      <div className="grid cols-2">
        <Field label="PIN-Strategie">
          <select value={p.pinStrategy} onChange={(e) => set('pinStrategy', e.target.value as 'Random' | 'Fixed')}>
            <option value="Random">Zufällige PIN pro Key (empfohlen)</option>
            <option value="Fixed">Feste Standard-PIN</option>
          </select>
        </Field>
        {p.pinStrategy === 'Random'
          ? <Field label="PIN-Länge"><input type="number" min={6} max={16} value={p.pinLength} onChange={(e) => set('pinLength', Number(e.target.value))} /></Field>
          : <Field label="Standard-PIN" hint={profile.hasFixedPin ? 'Leer lassen, um die gespeicherte PIN zu behalten.' : 'Wird verschlüsselt gespeichert.'}><input type="password" autoComplete="new-password" value={p.fixedPin} onChange={(e) => set('fixedPin', e.target.value)} /></Field>}
      </div>
      {p.pinStrategy === 'Fixed' && !p.forcePinChange && <Info tone="warn">Eine feste PIN ohne erzwungenen Wechsel ist unsicher.</Info>}
      <label className="check"><input type="checkbox" checked={p.forcePinChange} onChange={(e) => set('forcePinChange', e.target.checked)} /> PIN-Wechsel bei der ersten Verwendung erzwingen (ab Firmware 5.7)</label>
      <label className="check"><input type="checkbox" checked={p.factoryResetBeforeRegistration} onChange={(e) => set('factoryResetBeforeRegistration', e.target.checked)} /> FIDO-Werksreset vor jeder Registrierung</label>
      <label className="check"><input type="checkbox" checked={p.isDefault} onChange={(e) => set('isDefault', e.target.checked)} /> Als Standardprofil verwenden</label>
      <Field label="Anzeigename in Entra" hint="Platzhalter: {Seriennummer}, {Modell}, {UPN}, {Datum}">
        <input value={p.displayNameTemplate} onChange={(e) => set('displayNameTemplate', e.target.value)} />
      </Field>
      <div className="grid cols-2">
        <Field label="Erlaubte Modelle" hint="Keine Auswahl = alle Modelle">
          <select multiple size={5} value={p.allowedModels} onChange={(e) => set('allowedModels', Array.from(e.target.selectedOptions).map((o) => o.value))}>
            {[...new Set([...knownModels, ...p.allowedModels])].map((m) => <option key={m} value={m}>{m}</option>)}
          </select>
        </Field>
        <Field label="Mindest-Firmware" hint="z. B. 5.7.0"><input value={p.minimumFirmware} onChange={(e) => set('minimumFirmware', e.target.value)} placeholder="5.7.0" /></Field>
      </div>
      <ErrorBox error={error} />
    </Modal>
  );
}
