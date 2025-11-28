import type { StartCallResponse } from './types';

const apiBase = '/api';

async function postJson<TResponse>(path: string, payload: unknown): Promise<TResponse> {
  const response = await fetch(`${apiBase}${path}`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(payload)
  });

  if (!response.ok) {
    const message = await response.text();
    try {
      const parsed = JSON.parse(message) as { error?: string };
      if (parsed.error) {
        throw new Error(parsed.error);
      }
    } catch {
      /* ignore */
    }

    throw new Error(message || `Request to ${path} failed`);
  }

  return (await response.json()) as TResponse;
}

export async function initDemoUser(demoUserId: string) {
  return postJson<{ demoUserId: string; acsIdentity: string; displayName: string; role: string }>(
    '/demo-users/init',
    { demoUserId }
  );
}

export async function startCall(payload: { demoUserId: string; participantIds?: string[] }) {
  return postJson<StartCallResponse>('/calls/start', payload);
}
