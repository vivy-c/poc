import { useEffect, useRef, useState } from 'react';
import { AzureCommunicationTokenCredential } from '@azure/communication-common';
import {
  CallComposite,
  createAzureCommunicationCallAdapter,
  type AdapterError,
  type AzureCommunicationCallAdapterArgs,
  type CallAdapter
} from '@azure/communication-react';

export function useAzureCommunicationCallAdapter(
  args: AzureCommunicationCallAdapterArgs | undefined,
  onCreateError?: (error: Error) => void
): CallAdapter | undefined {
  const [adapter, setAdapter] = useState<CallAdapter>();
  const adapterRef = useRef<CallAdapter>();
  const onCreateErrorRef = useRef(onCreateError);
  onCreateErrorRef.current = onCreateError;

  useEffect(() => {
    let cancelled = false;

    const disposeAdapter = () => {
      adapterRef.current?.dispose();
      adapterRef.current = undefined;
      setAdapter(undefined);
    };

    if (!args) {
      disposeAdapter();
      return;
    }

    const init = async () => {
      try {
        const newAdapter = await createAzureCommunicationCallAdapter(args);
        if (cancelled) {
          newAdapter.dispose();
          return;
        }
        adapterRef.current?.dispose();
        adapterRef.current = newAdapter;
        setAdapter(newAdapter);
      } catch (error) {
        console.error('Failed to create ACS call adapter', error);
        onCreateErrorRef.current?.(error as Error);
        disposeAdapter();
      }
    };

    init();

    return () => {
      cancelled = true;
      disposeAdapter();
    };
  }, [
    args?.userId?.communicationUserId,
    args?.displayName,
    args?.credential,
    args?.locator
  ]);

  return adapter;
}

export {
  CallComposite,
  AzureCommunicationTokenCredential
};

export type { AdapterError, AzureCommunicationCallAdapterArgs, CallAdapter };
