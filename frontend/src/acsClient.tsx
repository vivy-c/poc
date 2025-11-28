import type { ComponentType } from 'react';

export type AzureCommunicationCallAdapterArgs = {
  userId: { communicationUserId: string };
  displayName: string;
  credential: AzureCommunicationTokenCredential;
  locator: { groupId: string };
};

export interface CallAdapter {
  dispose(): void;
}

export class AzureCommunicationTokenCredential {
  token: string;

  constructor(token: string) {
    this.token = token;
  }
}

export function useAzureCommunicationCallAdapter(
  args: AzureCommunicationCallAdapterArgs | undefined
): CallAdapter | undefined {
  if (!args) return undefined;
  // Placeholder adapter; in a full build, replace with the real ACS adapter.
  return {
    dispose() {
      /* no-op */
    }
  };
}

export const CallComposite: ComponentType<{ adapter: CallAdapter }> = () => (
  <div className="flex h-full flex-col items-center justify-center gap-2 rounded-xl border border-dashed border-slate-800 bg-slate-950/40 p-6 text-center text-slate-300">
    <p className="text-xs uppercase tracking-[0.1em] text-cyan-300">Call UI placeholder</p>
    <p className="text-sm">
      ACS UI SDK is stubbed locally; wire the real ACS CallComposite when SDK packages are available.
    </p>
  </div>
);
