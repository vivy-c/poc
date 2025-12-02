import type { AddParticipantsResponse, StartCallResponse } from './types';

const apiBase = normalizeApiBase(import.meta.env.VITE_API_BASE_URL);

function normalizeApiBase(raw?: string) {
  if (raw && raw.trim()) {
    const trimmed = raw.trim().replace(/\/$/, '');
    return trimmed.endsWith('/api') ? trimmed : `${trimmed}/api`;
  }
  const port = typeof window !== 'undefined' ? window.location.port : '';
  if (port === '5173') {
    return 'http://localhost:7071/api';
  }
  return '/api';
}

const buildUrl = (path: string) => `${apiBase}${path.startsWith('/') ? path : `/${path}`}`;

async function postJson<TResponse>(path: string, payload: unknown): Promise<TResponse> {
  const response = await fetch(buildUrl(path), {
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

async function getJson<TResponse>(path: string): Promise<TResponse> {
  const response = await fetch(buildUrl(path), {
    method: 'GET',
    headers: { 'Content-Type': 'application/json' }
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

export async function addParticipants(callSessionId: string, participantIds: string[]) {
  return postJson<AddParticipantsResponse>(`/calls/${callSessionId}/add-participant`, {
    participantIds
  });
}

export async function getTranscript(callSessionId: string) {
  return getJson(`/calls/${callSessionId}/transcript`);
}
