import { useState } from 'react';
import { api, fmtDate, loginKindLabel, type LoginKind, type PlatformUser, type Role, type Tenant, type UserPasskey } from '../api';
import { useAuth } from '../auth';
import { Badge, Card, ErrorBox, Field, Info, Modal, PageHeader, Spinner, useAction, useApi } from '../ui';
import { useConfirm, useToast } from '../feedback';

type CatalogEntry = { permission: string; group: string; label: string; description: string; ownerOnly: boolean };
type BuiltInRole = { role: Role; description: string; permissions: string[] };
type CustomRole = { id: string; name: string; description?: string | null; permissions: string[]; users: number };
type RolesResponse = { builtIn: BuiltInRole[]; custom: CustomRole[]; catalog: CatalogEntry[] };

export default function UsersPage() {
  const { me } = useAuth();
  const [tab, setTab] = useState<'users' | 'roles'>('users');
  const { data, error, loading, reload } = useApi<PlatformUser[]>('/api/users');
  const { data: tenants } = useApi<Tenant[]>('/api/tenants');
  const roles = useApi<RolesResponse>('/api/roles');
  const [editing, setEditing] = useState<Partial<PlatformUser> | null>(null);
  const [editingRole, setEditingRole] = useState<Partial<CustomRole> | null>(null);
  const [link, setLink] = useState<{ user: string; url: string; expiresAt: string } | null>(null);

  return (
    <>
      <PageHeader title="Benutzer & Rollen" subtitle="Wer die Plattform nutzen darf und was er darf. Gilt für die ganze Partnerebene."
        actions={tab === 'users'
          ? <button className="primary" onClick={() => setEditing({})}>Benutzer hinzufügen</button>
          : <button className="primary" onClick={() => setEditingRole({})}>Rolle anlegen</button>} />
      <div className="tabs">
        <button className={tab === 'users' ? 'selected' : ''} onClick={() => setTab('users')}>Benutzer</button>
        <button className={tab === 'roles' ? 'selected' : ''} onClick={() => setTab('roles')}>Rollen</button>
      </div>
      {tab === 'users' && <>
        <ErrorBox error={error} />
        {loading && !data && <Spinner />}
        {data && (
          <div className="card table-wrap" style={{ padding: 0 }}>
            <table>
              <thead><tr><th>Person</th><th>Anmeldung</th><th>Rolle</th><th>Zuständig für</th><th>Letzte Anmeldung</th><th>Status</th><th /></tr></thead>
              <tbody>
                {data.map((u) => (
                  <tr key={u.id}>
                    <td>{u.displayName}<div className="small muted">{u.upn}</div></td>
                    <td className="small"><LoginState u={u} /></td>
                    <td>{u.role === 'Custom' ? <>{u.customRoleName ?? 'Eigene Rolle'} <Badge>eigene</Badge></> : u.role}</td>
                    <td>{u.allTenants ? 'Alle Tenants' : u.tenantIds.map((id) => tenants?.find((t) => t.id === id)?.displayName ?? '…').join(', ') || 'keine'}</td>
                    <td className="small">{u.lastLoginAt ? fmtDate(u.lastLoginAt) : u.linked ? '–' : <span className="muted">noch nie</span>}</td>
                    <td>{!u.isActive ? <Badge>deaktiviert</Badge> : u.lockedUntil ? <Badge tone="error">gesperrt bis {fmtDate(u.lockedUntil)}</Badge> : <Badge tone="ok">aktiv</Badge>}</td>
                    <td><button className="link" onClick={() => setEditing(u)}>Bearbeiten</button></td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </>}
      {tab === 'roles' && <RolesTab roles={roles.data} error={roles.error} onEdit={setEditingRole} />}
      {editing && <UserModal user={editing} self={editing.id === me.user?.id} tenants={tenants ?? []} roles={roles.data}
        onClose={() => setEditing(null)} onSaved={(l) => { setEditing(null); reload(); roles.reload(); if (l) setLink(l); }} />}
      {link && <Modal title="Einrichtungslink" onClose={() => setLink(null)} footer={<button className="primary" onClick={() => setLink(null)}>Fertig</button>}>
        <p>Schicken Sie diesen Link an <strong>{link.user}</strong>. Damit richtet die Person ihren Zugang selbst ein. Der Link gilt einmal, bis {fmtDate(link.expiresAt)}.</p>
        <div className="setup-link">
          <input readOnly value={link.url} onFocus={(e) => e.target.select()} />
          <button onClick={() => void navigator.clipboard?.writeText(link.url)}>Kopieren</button>
        </div>
        <Info>Der Link wird nur jetzt angezeigt. Bei Bedarf erzeugen Sie unter „Bearbeiten“ einen neuen.</Info>
      </Modal>}
      {editingRole && roles.data && <RoleModal role={editingRole} catalog={roles.data.catalog}
        onClose={() => setEditingRole(null)} onSaved={() => { setEditingRole(null); roles.reload(); reload(); }} />}
    </>
  );
}

function RolesTab({ roles, error, onEdit }: { roles?: RolesResponse; error?: string; onEdit: (r: CustomRole) => void }) {
  if (error) return <ErrorBox error={error} />;
  if (!roles) return <Spinner />;
  const label = (p: string) => roles.catalog.find((c) => c.permission === p)?.label ?? p;
  return (
    <>
      <Card title="Eigene Rollen">
        {roles.custom.length === 0
          ? <p className="muted">Noch keine eigenen Rollen. Eigene Rollen fassen einzelne Berechtigungen zusammen, z. B. „Nur Lager pflegen“.</p>
          : <table>
              <thead><tr><th>Rolle</th><th>Berechtigungen</th><th>Benutzer</th><th /></tr></thead>
              <tbody>
                {roles.custom.map((r) => (
                  <tr key={r.id}>
                    <td><strong>{r.name}</strong>{r.description && <div className="small muted">{r.description}</div>}</td>
                    <td className="small">{r.permissions.map(label).join(', ') || <span className="muted">keine</span>}</td>
                    <td>{r.users}</td>
                    <td><button className="link" onClick={() => onEdit(r)}>Bearbeiten</button></td>
                  </tr>
                ))}
              </tbody>
            </table>}
      </Card>
      <Card title="Feste Rollen">
        <table>
          <thead><tr><th>Rolle</th><th>Berechtigungen</th></tr></thead>
          <tbody>
            {roles.builtIn.map((r) => (
              <tr key={r.role}>
                <td><strong>{r.role}</strong><div className="small muted">{r.description}</div></td>
                <td className="small">{r.permissions.map(label).join(', ')}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </Card>
    </>
  );
}

function RoleModal({ role, catalog, onClose, onSaved }: { role: Partial<CustomRole>; catalog: CatalogEntry[]; onClose: () => void; onSaved: () => void }) {
  const [name, setName] = useState(role.name ?? '');
  const [description, setDescription] = useState(role.description ?? '');
  const [perms, setPerms] = useState<string[]>(role.permissions ?? ['ReadInventory']);
  const { busy, error, run } = useAction();
  const isNew = !role.id;
  const groups = [...new Set(catalog.map((c) => c.group))];
  const toggle = (p: string, on: boolean) => setPerms((ps) => on ? [...ps, p] : ps.filter((x) => x !== p));
  const save = () => run(async () => {
    const body = { name, description: description || null, permissions: perms };
    if (isNew) await api.post('/api/roles', body); else await api.put('/api/roles/' + role.id, body);
    onSaved();
  });
  const remove = () => run(async () => { await api.del('/api/roles/' + role.id); onSaved(); });
  return (
    <Modal title={isNew ? 'Rolle anlegen' : 'Rolle bearbeiten'} onClose={onClose} footer={<>
      {!isNew && <button className="danger" style={{ marginRight: 'auto' }} disabled={busy || (role.users ?? 0) > 0} title={(role.users ?? 0) > 0 ? 'Rolle ist noch Benutzern zugewiesen' : undefined} onClick={remove}>Löschen</button>}
      <button onClick={onClose}>Abbrechen</button>
      <button className="primary" disabled={!name.trim() || busy} onClick={save}>Speichern</button>
    </>}>
      <div className="grid cols-2">
        <Field label="Name"><input value={name} onChange={(e) => setName(e.target.value)} placeholder="z. B. Lagerverwaltung" /></Field>
        <Field label="Beschreibung"><input value={description} onChange={(e) => setDescription(e.target.value)} /></Field>
      </div>
      {groups.map((g) => (
        <div key={g}>
          <div className="perm-group">{g}</div>
          <div className="perm-grid">
            {catalog.filter((c) => c.group === g).map((c) => (
              <label key={c.permission} className="check" title={c.ownerOnly ? 'Nur für Owner' : c.description}>
                <input type="checkbox" disabled={c.ownerOnly} checked={!c.ownerOnly && perms.includes(c.permission)} onChange={(e) => toggle(c.permission, e.target.checked)} />
                <span>{c.label}{c.ownerOnly && <span className="small muted"> (nur Owner)</span>}<br /><span className="small muted">{c.description}</span></span>
              </label>
            ))}
          </div>
        </div>
      ))}
      {!isNew && (role.users ?? 0) > 0 && <Info>Änderungen gelten sofort für {role.users} Benutzer.</Info>}
      <ErrorBox error={error} />
    </Modal>
  );
}

type SetupLink = { user: string; url: string; expiresAt: string };

function LoginState({ u }: { u: PlatformUser }) {
  const kind = loginKindLabel[u.loginKind] ?? u.loginKind;
  if (u.loginKind === 'Entra') return <>{kind}</>;
  const ready = u.loginKind === 'Local' ? u.passkeyCount > 0 : u.emergencyReady;
  return <>{kind}<div>{ready
    ? <span className="muted">{u.loginKind === 'Local' ? `${u.passkeyCount} Passkey${u.passkeyCount === 1 ? '' : 's'}` : 'eingerichtet'}</span>
    : u.setupPending ? <Badge tone="warn">Einrichtung offen</Badge> : <Badge tone="error">nicht eingerichtet</Badge>}</div></>;
}

const kinds: { kind: LoginKind; text: string }[] = [
  { kind: 'Entra', text: 'Konto aus dem Heim-Tenant, Anmeldung über Microsoft mit YubiKey.' },
  { kind: 'Local', text: 'Konto nur auf der Plattform, z. B. für Kundenzugriffe oder Breakglass. Anmeldung mit Passkey bzw. YubiKey. Empfohlen.' },
  { kind: 'Emergency', text: 'Konto nur auf der Plattform, Anmeldung mit Passwort und Code aus einer Authenticator-App.' },
];

function UserModal({ user, self, tenants, roles, onClose, onSaved }: { user: Partial<PlatformUser>; self: boolean; tenants: Tenant[]; roles?: RolesResponse; onClose: () => void; onSaved: (link?: SetupLink) => void }) {
  const [loginKind, setLoginKind] = useState<LoginKind>(user.loginKind ?? 'Entra');
  const [upn, setUpn] = useState(user.upn ?? '');
  const [displayName, setDisplayName] = useState(user.displayName ?? '');
  const [role, setRole] = useState<Role>(user.role ?? 'Operator');
  const [customRoleId, setCustomRoleId] = useState<string | null>(user.customRoleId ?? null);
  const [allTenants, setAllTenants] = useState(user.allTenants ?? true);
  const [tenantIds, setTenantIds] = useState<string[]>(user.tenantIds ?? []);
  const [isActive, setIsActive] = useState(user.isActive ?? true);
  const { busy, error, run } = useAction();
  const confirm = useConfirm();
  const isNew = !user.id;
  const save = () => run(async () => {
    const body = { upn, displayName: displayName || null, role, customRoleId: role === 'Custom' ? customRoleId : null, allTenants, tenantIds: allTenants ? [] : tenantIds, isActive };
    if (isNew) {
      const created = await api.post<PlatformUser>('/api/users', { ...body, loginKind });
      if (loginKind !== 'Entra') {
        const l = await api.post<{ url: string; expiresAt: string }>(`/api/users/${created.id}/setup-link`);
        onSaved({ user: created.displayName, ...l });
        return;
      }
    } else await api.put('/api/users/' + user.id, body);
    onSaved();
  });
  const newLink = () => run(async () => {
    const msg = user.loginKind === 'Emergency'
      ? 'Passwort und Authenticator des Kontos werden dabei zurückgesetzt.'
      : 'Bestehende Passkeys bleiben gültig, mit dem Link kommt ein weiterer dazu.';
    if (!(await confirm({ title: 'Neuen Einrichtungslink erzeugen?', message: msg, confirmLabel: 'Link erzeugen', danger: user.loginKind === 'Emergency' }))) return;
    const l = await api.post<{ url: string; expiresAt: string }>(`/api/users/${user.id}/setup-link`);
    onSaved({ user: user.displayName ?? '', ...l });
  });
  const pick = (r: Role, id: string | null = null) => { setRole(r); setCustomRoleId(id); };
  return (
    <Modal title={isNew ? 'Benutzer hinzufügen' : 'Benutzer bearbeiten'} onClose={onClose} footer={<><button onClick={onClose}>Abbrechen</button><button className="primary" disabled={!upn || busy || (role === 'Custom' && !customRoleId)} onClick={save}>Speichern</button></>}>
      {isNew && (
        <Field label="Anmeldung">
          {kinds.map((k) => (
            <label key={k.kind} className={'choice' + (loginKind === k.kind ? ' selected' : '')}>
              <input type="radio" checked={loginKind === k.kind} onChange={() => setLoginKind(k.kind)} />
              <span><strong>{loginKindLabel[k.kind]}</strong><br /><span className="small muted">{k.text}</span></span>
            </label>
          ))}
        </Field>
      )}
      <div className="grid cols-2">
        {isNew && loginKind === 'Emergency' && <Info tone="warn">Empfohlen ist ein lokales Konto mit Passkey. Passwort und Authenticator-Code lassen sich abfangen (Phishing). Diese Variante nur nutzen, wenn kein Passkey bzw. YubiKey möglich ist.</Info>}
        <Field label={loginKind === 'Entra' ? 'UPN' : 'Anmeldename'} hint={loginKind === 'Entra' ? 'Konto aus dem Heim-Tenant der Installation' : 'z. B. die E-Mail-Adresse der Person'}><input value={upn} disabled={!isNew} onChange={(e) => setUpn(e.target.value)} placeholder="name@contoso.com" /></Field>
        <Field label="Anzeigename"><input value={displayName} onChange={(e) => setDisplayName(e.target.value)} /></Field>
      </div>
      <Field label="Rolle">
        <div className="pick">
          {(roles?.builtIn ?? []).map((r) => (
            <button key={r.role} type="button" className={role === r.role ? 'selected' : ''} onClick={() => pick(r.role)}>
              <span><strong>{r.role}</strong><br /><span className="small muted">{r.description}</span></span>
            </button>
          ))}
          {(roles?.custom ?? []).map((r) => (
            <button key={r.id} type="button" className={role === 'Custom' && customRoleId === r.id ? 'selected' : ''} onClick={() => pick('Custom', r.id)}>
              <span><strong>{r.name}</strong> <Badge>eigene</Badge><br /><span className="small muted">{r.description || `${r.permissions.length} Berechtigungen`}</span></span>
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
      {!isNew && user.loginKind && user.loginKind !== 'Entra' && <UserCredentials user={user as PlatformUser} busy={busy} onNewLink={newLink} />}
      {self && <Info>Sie bearbeiten Ihr eigenes Konto. Der letzte aktive Owner kann nicht herabgestuft werden.</Info>}
      <ErrorBox error={error} />
    </Modal>
  );
}

function UserCredentials({ user, busy, onNewLink }: { user: PlatformUser; busy: boolean; onNewLink: () => void }) {
  const { data, reload } = useApi<UserPasskey[]>(user.loginKind === 'Local' ? `/api/users/${user.id}/passkeys` : null);
  const { error, run } = useAction();
  const confirm = useConfirm();
  const toast = useToast();
  const remove = async (p: UserPasskey) => {
    if (!(await confirm({ title: 'Passkey entfernen?', message: `„${p.name}“ von ${user.displayName} wird entfernt.`, confirmLabel: 'Entfernen', danger: true }))) return;
    await run(async () => {
      await api.del(`/api/users/${user.id}/passkeys/${p.id}`);
      toast.ok(`Passkey „${p.name}“ entfernt.`);
      reload();
    });
  };
  return (
    <div className="mt">
      <div className="perm-group">Zugang ({loginKindLabel[user.loginKind]})</div>
      {user.loginKind === 'Local' && (data?.length
        ? <table><tbody>{data.map((p) => (
            <tr key={p.id}><td>{p.name}</td><td className="small muted">{p.lastUsedAt ? 'zuletzt ' + fmtDate(p.lastUsedAt) : 'noch nicht verwendet'}</td>
              <td><button className="link" onClick={() => remove(p)}>Entfernen</button></td></tr>))}</tbody></table>
        : <p className="muted small">Noch kein Passkey eingerichtet.</p>)}
      {user.loginKind === 'Emergency' && <p className="muted small">{user.emergencyReady ? 'Passwort und Authenticator sind eingerichtet.' : 'Noch nicht eingerichtet.'}</p>}
      {user.setupPending && <p className="small"><Badge tone="warn">Einrichtungslink offen</Badge> gültig bis {fmtDate(user.setupTokenExpiresAt!)}</p>}
      <button disabled={busy} onClick={onNewLink}>{user.loginKind === 'Local' ? 'Link für weiteren Passkey erzeugen' : 'Zurücksetzen und neuen Link erzeugen'}</button>
      <ErrorBox error={error} />
    </div>
  );
}
