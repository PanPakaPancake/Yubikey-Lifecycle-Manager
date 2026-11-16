import { useState } from 'react';
import { api, fmtDate, type PlatformUser, type Role, type Tenant } from '../api';
import { useAuth } from '../auth';
import { Badge, ErrorBox, Field, Info, Modal, PageHeader, Spinner, useAction, useApi } from '../ui';

const roles: { role: Role; text: string }[] = [
  { role: 'Owner', text: 'Alles, inkl. Tenants, Benutzer, Profile und API-Schlüssel' },
  { role: 'Operator', text: 'Keys registrieren, zurücksetzen, sperren, Inventar pflegen, synchronisieren' },
  { role: 'Helpdesk', text: 'Inventar ansehen und Keys sperren' },
  { role: 'Auditor', text: 'Nur lesen: Inventar, Berichte und Audit-Log' },
];

export default function UsersPage() {
  const { me } = useAuth();
  const { data, error, loading, reload } = useApi<PlatformUser[]>('/api/users');
  const { data: tenants } = useApi<Tenant[]>('/api/tenants');
  const [editing, setEditing] = useState<Partial<PlatformUser> | null>(null);

  return (
    <>
      <PageHeader title="Benutzer & Rollen" subtitle="Wer die Plattform nutzen darf. Anmeldung per Entra-SSO mit YubiKey." actions={<button className="primary" onClick={() => setEditing({})}>Benutzer hinzufügen</button>} />
      <ErrorBox error={error} />
      {loading && !data && <Spinner />}
      {data && (
        <div className="card table-wrap" style={{ padding: 0 }}>
          <table>
            <thead><tr><th>Person</th><th>Rolle</th><th>Zuständig für</th><th>Letzte Anmeldung</th><th>Status</th><th /></tr></thead>
            <tbody>
              {data.map((u) => (
                <tr key={u.id}>
                  <td>{u.displayName}<div className="small muted">{u.upn}</div></td>
                  <td>{u.role}</td>
                  <td>{u.allTenants ? 'Alle Tenants' : u.tenantIds.map((id) => tenants?.find((t) => t.id === id)?.displayName ?? '…').join(', ') || 'keine'}</td>
                  <td className="small">{u.lastLoginAt ? fmtDate(u.lastLoginAt) : u.linked ? '–' : <span className="muted">noch nie</span>}</td>
                  <td>{u.isActive ? <Badge tone="ok">aktiv</Badge> : <Badge>deaktiviert</Badge>}</td>
                  <td><button className="link" onClick={() => setEditing(u)}>Bearbeiten</button></td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
      {editing && <UserModal user={editing} self={editing.id === me.user?.id} tenants={tenants ?? []} onClose={() => setEditing(null)} onSaved={() => { setEditing(null); reload(); }} />}
    </>
  );
}

function UserModal({ user, self, tenants, onClose, onSaved }: { user: Partial<PlatformUser>; self: boolean; tenants: Tenant[]; onClose: () => void; onSaved: () => void }) {
  const [upn, setUpn] = useState(user.upn ?? '');
  const [displayName, setDisplayName] = useState(user.displayName ?? '');
  const [role, setRole] = useState<Role>(user.role ?? 'Operator');
  const [allTenants, setAllTenants] = useState(user.allTenants ?? true);
  const [tenantIds, setTenantIds] = useState<string[]>(user.tenantIds ?? []);
  const [isActive, setIsActive] = useState(user.isActive ?? true);
  const { busy, error, run } = useAction();
  const isNew = !user.id;
  const save = () => run(async () => {
    const body = { upn, displayName: displayName || null, role, allTenants, tenantIds: allTenants ? [] : tenantIds, isActive };
    if (isNew) await api.post('/api/users', body); else await api.put('/api/users/' + user.id, body);
    onSaved();
  });
  return (
    <Modal title={isNew ? 'Benutzer hinzufügen' : 'Benutzer bearbeiten'} onClose={onClose} footer={<><button onClick={onClose}>Abbrechen</button><button className="primary" disabled={!upn || busy} onClick={save}>Speichern</button></>}>
      <div className="grid cols-2">
        <Field label="UPN" hint="Konto aus dem Heim-Tenant der Installation"><input value={upn} disabled={!isNew} onChange={(e) => setUpn(e.target.value)} placeholder="name@contoso.com" /></Field>
        <Field label="Anzeigename"><input value={displayName} onChange={(e) => setDisplayName(e.target.value)} /></Field>
      </div>
      <Field label="Rolle">
        <div className="pick">
          {roles.map((r) => (
            <button key={r.role} type="button" className={role === r.role ? 'selected' : ''} onClick={() => setRole(r.role)}>
              <span><strong>{r.role}</strong><br /><span className="small muted">{r.text}</span></span>
            </button>
          ))}
        </div>
      </Field>
      <label className="check"><input type="checkbox" checked={allTenants} onChange={(e) => setAllTenants(e.target.checked)} /> Zuständig für alle Tenants (auch künftige)</label>
      {!allTenants && (
        <div style={{ margin: '8px 0 12px 24px' }}>
          {tenants.map((t) => (
            <label key={t.id} className="check">
              <input type="checkbox" checked={tenantIds.includes(t.id)} onChange={(e) => setTenantIds((ids) => e.target.checked ? [...ids, t.id] : ids.filter((x) => x !== t.id))} /> {t.displayName}
            </label>
          ))}
          {tenants.length === 0 && <span className="muted">Noch keine Tenants.</span>}
        </div>
      )}
      <label className="check"><input type="checkbox" checked={isActive} disabled={self} onChange={(e) => setIsActive(e.target.checked)} /> Aktiv</label>
      {self && <Info>Sie bearbeiten Ihr eigenes Konto. Der letzte aktive Owner kann nicht herabgestuft werden.</Info>}
      <ErrorBox error={error} />
    </Modal>
  );
}
