/**
 * rc.12 (T1): shared ward devices. Your application decides when to lock (only it sees the person working); Sangam decides
 * how they get back in — their PIN within two hours on an enrolled shared device, a full sign-in anywhere else.
 */

/** Drops trailing "/" characters. */
function trimSlashes(value: string): string {
  let end = value.length;
  while (end > 0 && value.charCodeAt(end - 1) === 47) end--;
  return value.slice(0, end);
}

/**
 * Locks the person's Sangam session with their own access token (from their sign-in, not client credentials). Sangam tells
 * the person's other applications in that session (`session.locked`). Resolves false when that sign-in had already ended.
 * Call it from your back end.
 */
export async function lockSession(authority: string, accessToken: string, fetcher: typeof fetch = fetch): Promise<boolean> {
  const response = await fetcher(`${trimSlashes(authority)}/api/v1/sessions/lock`, { method: "POST", headers: { authorization: `Bearer ${accessToken}` } });
  if (response.status === 404) return false;
  if (!response.ok) throw new Error(`Sangam answered ${response.status} to the lock`);
  return true;
}

/** What `startIdleTimer` needs. */
export interface IdleTimerOptions {
  /** Minutes without a key, pointer or touch before `onIdle` runs (your application's own setting). */
  minutes: number;
  /** What to do when idle — usually go to your lock address (for example `/auth/lock`). */
  onIdle: () => void;
  /** Where to listen; the browser's `window` when left out. */
  target?: Pick<EventTarget, "addEventListener" | "removeEventListener">;
}

const ACTIVITY = ["keydown", "pointerdown", "pointermove", "touchstart", "wheel", "scroll"];

/**
 * A browser idle timer: runs `onIdle` once after `minutes` without activity, restarting on each key, pointer or touch.
 * Returns a function that stops it.
 */
export function startIdleTimer(options: IdleTimerOptions): () => void {
  if (!(options.minutes > 0)) throw new Error("minutes must be more than 0.");
  const target = options.target ?? (globalThis as unknown as EventTarget);
  let timer: ReturnType<typeof setTimeout> | undefined;
  let stopped = false;
  const fire = () => {
    if (stopped) return;
    stop();
    options.onIdle();
  };
  const reset = () => {
    if (stopped) return;
    if (timer !== undefined) clearTimeout(timer);
    timer = setTimeout(fire, options.minutes * 60_000);
  };
  const stop = () => {
    stopped = true;
    if (timer !== undefined) clearTimeout(timer);
    for (const name of ACTIVITY) target.removeEventListener(name, reset);
  };
  for (const name of ACTIVITY) target.addEventListener(name, reset, { passive: true } as AddEventListenerOptions);
  reset();
  return stop;
}

/**
 * rc.12 (M12): ends the person's emergency access early, with a reason (the patient left the ward, care handed over, the
 * emergency is over), with their own access token. Resolves false when none was in progress. Call it from your back end.
 */
export async function endEmergency(authority: string, accessToken: string, reason: string, fetcher: typeof fetch = fetch): Promise<boolean> {
  if (!reason.trim()) throw new Error("Say why emergency access ends.");
  const response = await fetcher(`${trimSlashes(authority)}/api/v1/sessions/emergency/end`, {
    method: "POST",
    headers: { authorization: `Bearer ${accessToken}`, "content-type": "application/json" },
    body: JSON.stringify({ reason }),
  });
  if (response.status === 404) return false;
  if (!response.ok) throw new Error(`Sangam answered ${response.status} to ending emergency access`);
  return true;
}
