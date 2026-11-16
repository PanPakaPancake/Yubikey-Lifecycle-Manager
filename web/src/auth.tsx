import { createContext, useContext } from 'react';
import type { Me } from './api';

export const AuthContext = createContext<{ me: Me; reload: () => void }>({ me: { authenticated: false, setupComplete: false }, reload: () => {} });

export function useAuth() {
  return useContext(AuthContext);
}

export function useCan() {
  const { me } = useAuth();
  return (permission: string) => me.permissions?.includes(permission) ?? false;
}
