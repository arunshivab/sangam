import { createContext, useCallback, useContext, useEffect, useMemo, useState, type ReactNode } from "react";
import { can, satisfies, type SangamUser, type StepUpRequirement } from "@sangam/client";
import { createSessionClient, type SessionClient, type SessionClientOptions } from "./session.js";

export * from "./session.js";
export { can, hasRole, rolesIn, satisfies, SangamAcr, type SangamUser, type SangamMembership, type StepUpRequirement } from "@sangam/client";

export interface SangamContextValue {
  user: SangamUser | null;
  loading: boolean;
  error: unknown;
  client: SessionClient;
  refresh(): Promise<void>;
}

const SangamContext = createContext<SangamContextValue | null>(null);

export interface SangamProviderProps extends SessionClientOptions {
  children?: ReactNode;
  /** A person already known (rendered on the server); skips the first fetch. */
  initialUser?: SangamUser | null;
}

/** Asks your back end who is signed in and shares the answer with the components below. */
export function SangamProvider({ children, initialUser, ...options }: SangamProviderProps) {
  // The client depends only on where the routes are; a new fetch or navigate on each render must not reset it.
  const client = useMemo(() => createSessionClient(options), [options.basePath]);
  const [user, setUser] = useState<SangamUser | null>(initialUser ?? null);
  const [loading, setLoading] = useState(initialUser === undefined);
  const [error, setError] = useState<unknown>(null);
  const refresh = useCallback(async () => {
    setLoading(true);
    try {
      setUser(await client.me());
      setError(null);
    } catch (e) {
      setError(e);
    } finally {
      setLoading(false);
    }
  }, [client]);
  useEffect(() => {
    if (initialUser === undefined) void refresh();
  }, [initialUser, refresh]);
  const value = useMemo(() => ({ user, loading, error, client, refresh }), [user, loading, error, client, refresh]);
  return <SangamContext.Provider value={value}>{children}</SangamContext.Provider>;
}

/** The provider's state: the person, whether it is still loading, and the session client. */
export function useSangam(): SangamContextValue {
  const value = useContext(SangamContext);
  if (!value) throw new Error("useSangam must be used inside <SangamProvider>.");
  return value;
}

/** The signed-in person, or null. */
export function useSangamUser(): SangamUser | null {
  return useSangam().user;
}

/** Whether the person holds the permission at the organisation (the shared rule; the back end still enforces). */
export function useCan(organisationPath: string, permission: string): boolean {
  return can(useSangam().user, organisationPath, permission);
}

/** Whether the person's sign-in meets a step-up requirement now, and a way to step up. */
export function useStepUp(requirement: StepUpRequirement): { satisfied: boolean; stepUp(returnTo?: string): void } {
  const { user, client } = useSangam();
  return {
    satisfied: !!user && satisfies({ acr: user.acr, authTime: user.authTime }, requirement),
    stepUp: (returnTo?: string) => client.stepUp(requirement, returnTo),
  };
}

export function SignedIn({ children }: { children?: ReactNode }) {
  const { user } = useSangam();
  return user ? <>{children}</> : null;
}

export function SignedOut({ children }: { children?: ReactNode }) {
  const { user, loading } = useSangam();
  return !user && !loading ? <>{children}</> : null;
}

/** Shows its children only to people who hold the permission there; `fallback` otherwise. */
export function Can({ organisation, permission, children, fallback = null }: { organisation: string; permission: string; children?: ReactNode; fallback?: ReactNode }) {
  return useCan(organisation, permission) ? <>{children}</> : <>{fallback}</>;
}

export function SignInButton({ children = "Sign in with Sangam", returnTo, className }: { children?: ReactNode; returnTo?: string; className?: string }) {
  const { client } = useSangam();
  return (
    <button type="button" className={className} onClick={() => client.signIn(returnTo)}>
      {children}
    </button>
  );
}

export function SignOutButton({ children = "Sign out", className }: { children?: ReactNode; className?: string }) {
  const { client } = useSangam();
  return (
    <button type="button" className={className} onClick={() => client.signOut()}>
      {children}
    </button>
  );
}
