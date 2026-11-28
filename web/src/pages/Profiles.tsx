import { useState, type ReactNode } from 'react';
import { Link } from 'react-router';
import { api, type Profile } from '../api';
import { useCan } from '../auth';
import { useTenant } from '../tenant';
import { Badge, Card, Empty, ErrorBox, Field, Info, Modal, OriginBadge, PageHeader, Spinner, TenantSelect, useAction, useApi } from '../ui';
import { tr } from '../i18n';

const knownModels = [tr("YubiKey 5 NFC"), tr("YubiKey 5C NFC"), tr("YubiKey 5C"), tr("YubiKey 5 Nano"), tr("YubiKey 5C Nano"), tr("YubiKey 5Ci"), tr("YubiKey Bio"), tr("YubiKey C Bio"), tr("Security Key NFC"), tr("Security Key C NFC")];

/**
 * Partnerebene: Profile für alle Tenants (werden an jeden Tenant vererbt) und ein Überblick
 * über die eigenen Profile der Tenants. Pro Tenant pflegt man eigene Profile in den Tenant-Einstellungen.
 */
export default function ProfilesPage() {
  const can = useCan();
  const { tenants } = useTenant();
  const { data, error, loading, reload } = useApi<Profile[]>('/api/profiles', []);
  const [editing, setEditing] = useState<Partial<Profile> | null>(null);
  const global = data?.filter((p) => !p.tenantId) ?? [];
  const byTenant = (tenants ?? []).map((t) => ({ t, list: data?.filter((p) => p.tenantId === t.id) ?? [] })).filter((x) => x.list.length > 0);

  return (
    <>
      <PageHeader title={tr("Registrierungsprofile")} subtitle={tr("Profile hier gelten für alle Tenants. Ein Tenant kann eigene Profile ergänzen und ein eigenes Standardprofil wählen.")}
        actions={can('ManageProfiles') && <button className="primary" onClick={() => setEditing({})}>{tr("Profil für alle Tenants anlegen")}</button>} />
      <ErrorBox error={error} />
      {loading && !data && <Spinner />}
      {data && global.length === 0 && <Empty>{tr("Noch kein Profil für alle Tenants.")}</Empty>}
      <div className="grid cols-2">
        {global.map((p) => <ProfileCard key={p.id} p={p} origin={<Badge>{tr("An alle Tenants vererbt")}</Badge>} onEdit={can('ManageProfiles') ? () => setEditing(p) : undefined} />)}
      </div>
      {byTenant.length > 0 && <>
        <h2 className="section-title">{tr("Eigene Profile der Tenants")}</h2>
        <div className="grid cols-2">
          {byTenant.flatMap(({ t, list }) => list.map((p) => (
            <ProfileCard key={p.id} p={p} origin={<Badge tone="info">{tr("Nur {displayName}", { displayName: t.displayName })}</Badge>} onEdit={can('ManageProfiles') ? () => setEditing(p) : undefined} />
          )))}
        </div>
      </>}
      {editing && <ProfileModal profile={editing} onClose={() => setEditing(null)} onSaved={() => { setEditing(null); reload(); }} />}
    </>
  );
}

/** Tenant-Einstellungen: geerbte Profile des Partners und eigene Profile des Tenants. */
export function TenantProfilesCard({ tenantId, editable }: { tenantId: string; editable: boolean }) {
  const { data, error, reload } = useApi<Profile[]>('/api/profiles?tenantId=' + tenantId, [tenantId]);
  const [editing, setEditing] = useState<Partial<Profile> | null>(null);
  const own = data?.filter((p) => p.tenantId === tenantId) ?? [];
  const inherited = data?.filter((p) => !p.tenantId) ?? [];
  const effectiveDefault = (own.find((p) => p.isDefault) ?? inherited.find((p) => p.isDefault) ?? data?.[0])?.id;
  const row = (p: Profile, isOwn: boolean) => (
    <tr key={p.id}>
      <td><strong>{p.name}</strong>{p.id === effectiveDefault && <> <Badge tone="info">{tr("Standard hier")}</Badge></>}{p.description && <div className="small muted">{p.description}</div>}</td>
      <td className="small">{p.pinStrategy === 'Random' ? tr("Zufällig, {pinLength} Ziffern", { pinLength: p.pinLength }) : tr("Feste PIN")}{p.forcePinChange ? tr(", Wechsel erzwungen") : ''}</td>
      <td>{isOwn ? <OriginBadge origin="Tenant" /> : <OriginBadge origin="Global" />}</td>
      <td className="num">{isOwn && editable && <button className="small" onClick={() => setEditing(p)}>{tr("Bearbeiten")}</button>}</td>
    </tr>
  );
  return (
    <Card title={tr("Registrierungsprofile")} className="mt" actions={editable && <button onClick={() => setEditing({ tenantId })}>{tr("Eigenes Profil anlegen")}</button>}>
      <p className="muted small" style={{ marginTop: 0 }}>
        {tr("Profile der Partnerebene gelten automatisch auch hier; ändern lassen sie sich unter")} <Link to="/profiles">{tr("Partner → Profile")}</Link>{tr(". Ein eigenes Profil mit „Als Standard verwenden“ ersetzt hier das Standardprofil des Partners.")}
      </p>
      <ErrorBox error={error} />
      {!data ? <Spinner /> : (
        <div className="table-wrap">
          <table>
            <thead><tr><th>{tr("Profil")}</th><th>{tr("PIN")}</th><th>{tr("Herkunft")}</th><th /></tr></thead>
            <tbody>{own.map((p) => row(p, true))}{inherited.map((p) => row(p, false))}</tbody>
          </table>
        </div>
      )}
      {editing && <ProfileModal profile={editing} fixedTenant onClose={() => setEditing(null)} onSaved={() => { setEditing(null); reload(); }} />}
    </Card>
  );
}

function ProfileCard({ p, origin, onEdit }: { p: Profile; origin: ReactNode; onEdit?: () => void }) {
  return (
    <Card title={<>{p.name} {p.isDefault && <Badge tone="info">{tr("Standard")}</Badge>}</>} actions={onEdit && <button onClick={onEdit}>{tr("Bearbeiten")}</button>}>
      <div className="profile-origin">{origin}</div>
      {p.description && <p className="muted">{p.description}</p>}
      <table>
        <tbody>
          <tr><th>{tr("PIN")}</th><td>{p.pinStrategy === 'Random' ? tr("Zufällig, {pinLength} Ziffern", { pinLength: p.pinLength }) : tr("Feste Standard-PIN")}</td></tr>
          <tr><th>{tr("PIN-Wechsel erzwingen")}</th><td>{p.forcePinChange ? tr("Ja (ab Firmware 5.7)") : tr("Nein")}</td></tr>
          <tr><th>{tr("Werksreset vorher")}</th><td>{p.factoryResetBeforeRegistration ? tr("Ja") : tr("Nein")}</td></tr>
          <tr><th>{tr("Anzeigename")}</th><td className="mono small">{p.displayNameTemplate}</td></tr>
          <tr><th>{tr("Erlaubte Modelle")}</th><td>{p.allowedModels.length ? p.allowedModels.join(', ') : 'alle'}</td></tr>
          <tr><th>{tr("Mindest-Firmware")}</th><td>{p.minimumFirmware ?? '–'}</td></tr>
        </tbody>
      </table>
    </Card>
  );
}

function ProfileModal({ profile, fixedTenant, onClose, onSaved }: { profile: Partial<Profile>; fixedTenant?: boolean; onClose: () => void; onSaved: () => void }) {
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
  const { tenants } = useTenant();
  const tenantName = (id: string) => tenants?.find((t) => t.id === id)?.displayName ?? 'Tenant';

  const save = () => run(async () => {
    const body = { ...p, tenantId: p.tenantId || null, minimumFirmware: p.minimumFirmware || null, fixedPin: p.pinStrategy === 'Fixed' && p.fixedPin ? p.fixedPin : null, description: p.description || null };
    if (isNew) await api.post('/api/profiles', body); else await api.put('/api/profiles/' + profile.id, body);
    onSaved();
  });
  const remove = () => run(async () => { await api.del('/api/profiles/' + profile.id); onSaved(); });

  return (
    <Modal title={isNew ? tr("Profil anlegen") : tr("Profil bearbeiten")} onClose={onClose} footer={<>
      {!isNew && <button className="danger" style={{ marginRight: 'auto' }} disabled={busy} onClick={remove}>{tr("Löschen")}</button>}
      <button onClick={onClose}>{tr("Abbrechen")}</button>
      <button className="primary" disabled={!p.name || busy} onClick={save}>{tr("Speichern")}</button>
    </>}>
      <div className="grid cols-2">
        <Field label={tr("Name")}><input value={p.name} onChange={(e) => set('name', e.target.value)} /></Field>
        <Field label={tr("Gültig für")} hint={isNew ? undefined : tr("Lässt sich nach dem Anlegen nicht mehr ändern.")}>
          {fixedTenant || !isNew ? <input value={p.tenantId ? tenantName(p.tenantId) : tr("Alle Tenants (vererbt)")} disabled /> : <TenantSelect value={p.tenantId} onChange={(v) => set('tenantId', v)} />}
        </Field>
      </div>
      <Field label={tr("Beschreibung")}><input value={p.description} onChange={(e) => set('description', e.target.value)} /></Field>
      <div className="grid cols-2">
        <Field label={tr("PIN-Strategie")}>
          <select value={p.pinStrategy} onChange={(e) => set('pinStrategy', e.target.value as 'Random' | 'Fixed')}>
            <option value="Random">{tr("Zufällige PIN pro Key (empfohlen)")}</option>
            <option value="Fixed">{tr("Feste Standard-PIN")}</option>
          </select>
        </Field>
        {p.pinStrategy === 'Random'
          ? <Field label={tr("PIN-Länge")}><input type="number" min={6} max={16} value={p.pinLength} onChange={(e) => set('pinLength', Number(e.target.value))} /></Field>
          : <Field label={tr("Standard-PIN")} hint={profile.hasFixedPin ? tr("Leer lassen, um die gespeicherte PIN zu behalten.") : tr("Wird verschlüsselt gespeichert.")}><input type="password" autoComplete="new-password" value={p.fixedPin} onChange={(e) => set('fixedPin', e.target.value)} /></Field>}
      </div>
      {p.pinStrategy === 'Fixed' && !p.forcePinChange && <Info tone="warn">{tr("Eine feste PIN ohne erzwungenen Wechsel ist unsicher.")}</Info>}
      <label className="check"><input type="checkbox" checked={p.forcePinChange} onChange={(e) => set('forcePinChange', e.target.checked)} /> {tr("PIN-Wechsel bei der ersten Verwendung erzwingen (ab Firmware 5.7)")}</label>
      <label className="check"><input type="checkbox" checked={p.factoryResetBeforeRegistration} onChange={(e) => set('factoryResetBeforeRegistration', e.target.checked)} /> {tr("FIDO-Werksreset vor jeder Registrierung")}</label>
      <label className="check"><input type="checkbox" checked={p.isDefault} onChange={(e) => set('isDefault', e.target.checked)} /> {p.tenantId ? tr("Als Standardprofil dieses Tenants verwenden (ersetzt hier das des Partners)") : tr("Als Standardprofil für alle Tenants verwenden")}</label>
      <Field label={tr("Anzeigename in Entra")} hint="Platzhalter: {Seriennummer}, {Modell}, {UPN}, {Datum}">
        <input value={p.displayNameTemplate} onChange={(e) => set('displayNameTemplate', e.target.value)} />
      </Field>
      <div className="grid cols-2">
        <Field label={tr("Erlaubte Modelle")} hint={tr("Keine Auswahl = alle Modelle")}>
          <select multiple size={5} value={p.allowedModels} onChange={(e) => set('allowedModels', Array.from(e.target.selectedOptions).map((o) => o.value))}>
            {[...new Set([...knownModels, ...p.allowedModels])].map((m) => <option key={m} value={m}>{m}</option>)}
          </select>
        </Field>
        <Field label={tr("Mindest-Firmware")} hint={tr("z. B. 5.7.0")}><input value={p.minimumFirmware} onChange={(e) => set('minimumFirmware', e.target.value)} placeholder="5.7.0" /></Field>
      </div>
      <ErrorBox error={error} />
    </Modal>
  );
}
