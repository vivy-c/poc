import { useCallback, useEffect, useMemo, useState } from 'react';
import './index.css';
import {
  AzureCommunicationTokenCredential,
  CallComposite,
  useAzureCommunicationCallAdapter,
  type AzureCommunicationCallAdapterArgs,
  type CallAdapter
} from './acsClient';
import { addParticipants, getTranscript, initDemoUser, startCall } from './api';
import { DEMO_USERS, type DemoUser } from './demoUsers';
import type {
  CallParticipant,
  StartCallResponse,
  TranscriptResponse,
  TranscriptSegment
} from './types';

type CallBootstrapState = {
  callSessionId: string;
  acsGroupId: string;
  acsToken: string;
  acsIdentity: string;
  displayName: string;
  participants: CallParticipant[];
};

type ViewMode = 'call' | 'summary';

const LOCAL_STORAGE_KEY = 'call-demo-user';

function App() {
  const [selectedUserId, setSelectedUserId] = useState<string | null>(null);
  const [preCallParticipantIds, setPreCallParticipantIds] = useState<string[]>([]);
  const [callParticipants, setCallParticipants] = useState<CallParticipant[]>([]);
  const [inviteSelection, setInviteSelection] = useState<string[]>([]);
  const [callInfo, setCallInfo] = useState<CallBootstrapState | null>(null);
  const [view, setView] = useState<ViewMode>('call');
  const [startInFlight, setStartInFlight] = useState(false);
  const [addInFlight, setAddInFlight] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [addError, setAddError] = useState<string | null>(null);
  const [transcriptSegments, setTranscriptSegments] = useState<TranscriptSegment[]>([]);
  const [transcriptLoading, setTranscriptLoading] = useState(false);
  const [transcriptError, setTranscriptError] = useState<string | null>(null);

  useEffect(() => {
    const stored = localStorage.getItem(LOCAL_STORAGE_KEY);
    if (stored) {
      try {
        const parsed = JSON.parse(stored) as { demoUserId?: string };
        if (parsed.demoUserId) {
          setSelectedUserId(parsed.demoUserId);
        }
      } catch {
        localStorage.removeItem(LOCAL_STORAGE_KEY);
      }
    }
  }, []);

  useEffect(() => {
    if (!selectedUserId) {
      setPreCallParticipantIds([]);
      setCallInfo(null);
      setCallParticipants([]);
      setInviteSelection([]);
      setError(null);
      setAddError(null);
      setTranscriptSegments([]);
      setTranscriptError(null);
      setTranscriptLoading(false);
      setView('call');
      localStorage.removeItem(LOCAL_STORAGE_KEY);
      return;
    }

    localStorage.setItem(LOCAL_STORAGE_KEY, JSON.stringify({ demoUserId: selectedUserId }));
    const defaultParticipants = DEMO_USERS.filter((user) => user.id !== selectedUserId)
      .slice(0, 1)
      .map((user) => user.id);
    setPreCallParticipantIds(defaultParticipants);
    setCallInfo(null);
    setCallParticipants([]);
    setInviteSelection([]);
    setError(null);
    setAddError(null);
    setTranscriptSegments([]);
    setTranscriptError(null);
    setTranscriptLoading(false);
    setView('call');
  }, [selectedUserId]);

  const selectedUser = selectedUserId
    ? DEMO_USERS.find((user) => user.id === selectedUserId) ?? null
    : null;

  const callAdapterArgs = useMemo<AzureCommunicationCallAdapterArgs | undefined>(() => {
    if (!callInfo || !selectedUser) return undefined;
    return {
      userId: { communicationUserId: callInfo.acsIdentity },
      displayName: selectedUser.displayName,
      credential: new AzureCommunicationTokenCredential(callInfo.acsToken),
      locator: { groupId: callInfo.acsGroupId }
    };
  }, [callInfo, selectedUser]);

  const callAdapter = useAzureCommunicationCallAdapter(callAdapterArgs);

  useEffect(() => {
    return () => {
      callAdapter?.dispose();
    };
  }, [callAdapter]);

  const togglePreCallParticipant = (demoUserId: string) => {
    setPreCallParticipantIds((current) =>
      current.includes(demoUserId)
        ? current.filter((id) => id !== demoUserId)
        : [...current, demoUserId]
    );
  };

  const toggleInvitee = (demoUserId: string) => {
    setInviteSelection((current) =>
      current.includes(demoUserId)
        ? current.filter((id) => id !== demoUserId)
        : [...current, demoUserId]
    );
  };

  const callSessionId = callInfo?.callSessionId;

  const refreshTranscript = useCallback(
    async (options?: { silent?: boolean }) => {
      if (!callSessionId) return;
      if (!options?.silent) {
        setTranscriptLoading(true);
      }
      try {
        const response = (await getTranscript(callSessionId)) as TranscriptResponse;
        setTranscriptSegments(response.segments ?? []);
        setTranscriptError(null);
      } catch (err) {
        const message = err instanceof Error ? err.message : 'Unable to fetch transcript.';
        setTranscriptError(message);
      } finally {
        if (!options?.silent) {
          setTranscriptLoading(false);
        }
      }
    },
    [callSessionId]
  );

  useEffect(() => {
    if (!callSessionId || view !== 'call') return;
    refreshTranscript({ silent: true });
    const interval = window.setInterval(() => {
      refreshTranscript({ silent: true });
    }, 3500);
    return () => window.clearInterval(interval);
  }, [callSessionId, view, refreshTranscript]);

  const handleStartCall = async () => {
    if (!selectedUser) return;
    setError(null);
    setAddError(null);
    setTranscriptSegments([]);
    setTranscriptError(null);
    setView('call');
    setStartInFlight(true);

    try {
      const initResponse = await initDemoUser(selectedUser.id);

      const startResponse = await startCall({
        demoUserId: selectedUser.id,
        participantIds: preCallParticipantIds.length ? preCallParticipantIds : undefined
      });

      const bootstrap = transformCallStart(
        startResponse,
        selectedUser,
        initResponse.acsIdentity
      );
      setCallInfo(bootstrap);
      setCallParticipants(bootstrap.participants);
      setInviteSelection([]);
    } catch (err) {
      const message = err instanceof Error ? err.message : 'Unable to start call.';
      setError(message);
    } finally {
      setStartInFlight(false);
    }
  };

  const handleAddParticipants = async () => {
    if (!callInfo || inviteSelection.length === 0) return;
    const idsToAdd = inviteSelection.filter(
      (id) => !callParticipants.some((participant) => participant.demoUserId === id)
    );
    if (!idsToAdd.length) {
      setInviteSelection([]);
      return;
    }

    setAddError(null);
    setAddInFlight(true);

    try {
      const response = await addParticipants(callInfo.callSessionId, idsToAdd);
      if (response.participants?.length) {
        setCallParticipants(response.participants);
      } else if (response.added?.length) {
        const existingIds = new Set(callParticipants.map((participant) => participant.demoUserId));
        const merged = [...callParticipants];
        response.added.forEach((participant) => {
          if (!existingIds.has(participant.demoUserId)) {
            merged.push({
              id: participant.id,
              demoUserId: participant.demoUserId,
              displayName: participant.displayName,
              acsIdentity: participant.acsIdentity
            });
            existingIds.add(participant.demoUserId);
          }
        });
        setCallParticipants(merged);
      }

      setInviteSelection([]);
    } catch (err) {
      const message = err instanceof Error ? err.message : 'Unable to add participants.';
      setAddError(message);
    } finally {
      setAddInFlight(false);
    }
  };

  const handleEndCall = async () => {
    if (!callInfo) return;
    callAdapter?.dispose();
    setView('summary');
    await refreshTranscript();
  };

  const handleStartNewCall = () => {
    setCallInfo(null);
    setCallParticipants([]);
    setInviteSelection([]);
    setTranscriptSegments([]);
    setTranscriptError(null);
    setTranscriptLoading(false);
    setView('call');
  };

  return (
    <main className="min-h-screen bg-slate-950 text-slate-50">
      <div className="mx-auto flex max-w-6xl flex-col gap-6 px-6 py-10">
        <Header />
        {!selectedUser ? (
          <PersonaSelection onSelect={setSelectedUserId} />
        ) : view === 'summary' && callInfo ? (
          <SummaryView
            callInfo={callInfo}
            callParticipants={callParticipants}
            onStartNewCall={handleStartNewCall}
            onBackToCall={() => setView('call')}
            transcriptSegments={transcriptSegments}
            transcriptLoading={transcriptLoading}
            transcriptError={transcriptError}
            onRefreshTranscript={() => refreshTranscript()}
          />
        ) : (
          <CallWorkspace
            selectedUser={selectedUser}
            preCallParticipantIds={preCallParticipantIds}
            onTogglePreCallParticipant={togglePreCallParticipant}
            onStartCall={handleStartCall}
            callInfo={callInfo}
            callAdapterReady={!!callAdapter}
            error={error}
            startInFlight={startInFlight}
            callParticipants={callParticipants}
            inviteSelection={inviteSelection}
            onToggleInvitee={toggleInvitee}
            onAddParticipants={handleAddParticipants}
            addInFlight={addInFlight}
            addError={addError}
            onResetUser={() => setSelectedUserId(null)}
            callAdapter={callAdapter}
            onEndCall={handleEndCall}
            transcriptSegments={transcriptSegments}
            transcriptLoading={transcriptLoading}
            transcriptError={transcriptError}
            onRefreshTranscript={() => refreshTranscript()}
          />
        )}
      </div>
    </main>
  );
}

function PersonaSelection({ onSelect }: { onSelect: (demoUserId: string) => void }) {
  return (
    <section className="grid gap-4 rounded-2xl border border-slate-800 bg-slate-900/60 p-6 shadow-xl shadow-black/50 md:grid-cols-[1fr,1.5fr]">
      <div>
        <p className="text-xs uppercase tracking-[0.1em] text-cyan-300">Choose a demo user</p>
        <h2 className="mt-2 text-2xl font-semibold">Log in as a persona</h2>
        <p className="mt-2 max-w-xl text-slate-300">
          No auth yet — just pick a persona and we will remember it in localStorage. You can swap
          roles anytime.
        </p>
      </div>
      <div className="grid gap-3 sm:grid-cols-2">
        {DEMO_USERS.map((user) => (
          <button
            key={user.id}
            className="rounded-xl border border-slate-800 bg-slate-950/40 p-4 text-left transition hover:border-cyan-400/70 hover:shadow-lg hover:shadow-cyan-500/15"
            onClick={() => onSelect(user.id)}
          >
            <div className="flex items-center justify-between text-sm text-slate-400">
              <span className="rounded-full bg-slate-800 px-3 py-1 text-xs uppercase tracking-[0.1em] text-slate-300">
                {user.role}
              </span>
              <span className="text-xs uppercase tracking-[0.08em]">{user.id}</span>
            </div>
            <h3 className="mt-3 text-xl font-semibold">{user.displayName}</h3>
            <p className="mt-1 text-slate-300">{user.focus}</p>
            <span className="mt-3 inline-block text-sm font-semibold text-cyan-300">
              Use this persona →
            </span>
          </button>
        ))}
      </div>
    </section>
  );
}

type CallWorkspaceProps = {
  selectedUser: DemoUser;
  preCallParticipantIds: string[];
  onTogglePreCallParticipant: (demoUserId: string) => void;
  onStartCall: () => void;
  callInfo: CallBootstrapState | null;
  callParticipants: CallParticipant[];
  inviteSelection: string[];
  onToggleInvitee: (demoUserId: string) => void;
  onAddParticipants: () => void;
  callAdapterReady: boolean;
  error: string | null;
  addError: string | null;
  startInFlight: boolean;
  addInFlight: boolean;
  onResetUser: () => void;
  callAdapter: CallAdapter | undefined;
  onEndCall: () => void;
  onRefreshTranscript: () => void;
  transcriptSegments: TranscriptSegment[];
  transcriptLoading: boolean;
  transcriptError: string | null;
};

function CallWorkspace({
  selectedUser,
  preCallParticipantIds,
  onTogglePreCallParticipant,
  onStartCall,
  callInfo,
  callParticipants,
  inviteSelection,
  onToggleInvitee,
  onAddParticipants,
  callAdapterReady,
  error,
  addError,
  startInFlight,
  addInFlight,
  onResetUser,
  callAdapter,
  onEndCall,
  transcriptSegments,
  transcriptLoading,
  transcriptError,
  onRefreshTranscript
}: CallWorkspaceProps) {
  const otherUsers = DEMO_USERS.filter((user) => user.id !== selectedUser.id);
  const availableInvitees = otherUsers.filter(
    (user) => !callParticipants.some((participant) => participant.demoUserId === user.id)
  );

  return (
    <section className="grid gap-4 lg:grid-cols-[320px,1fr]">
      <div className="flex flex-col gap-3">
        <div className="rounded-xl border border-slate-800 bg-slate-900/60 p-4 shadow-lg shadow-black/40">
          <div className="flex items-center justify-between gap-3">
            <span className="rounded-full bg-slate-800 px-3 py-1 text-xs uppercase tracking-[0.1em] text-slate-300">
              Signed in as
            </span>
            <button
              className="text-sm text-slate-300 underline-offset-4 hover:underline"
              onClick={onResetUser}
            >
              Switch persona
            </button>
          </div>
          <h3 className="mt-2 text-xl font-semibold">{selectedUser.displayName}</h3>
          <p className="text-slate-300">{selectedUser.role}</p>
          <p className="text-sm text-slate-400">{selectedUser.focus}</p>
        </div>

        <div className="rounded-xl border border-slate-800 bg-slate-900/60 p-4 shadow-lg shadow-black/40">
          <div className="flex items-center justify-between">
            <span className="rounded-full bg-slate-800 px-3 py-1 text-xs uppercase tracking-[0.1em] text-slate-300">
              Participants
            </span>
            <span className="text-xs text-slate-400">
              {callInfo ? `${callParticipants.length} in call` : 'Pick who to invite'}
            </span>
          </div>
          {callInfo ? (
            <>
              <div className="mt-3 space-y-2">
                {callParticipants.map((participant) => (
                  <div
                    key={participant.id}
                    className="flex items-center justify-between rounded-lg border border-slate-800/80 bg-slate-950/40 px-3 py-2"
                  >
                    <div>
                      <div className="font-semibold">{participant.displayName}</div>
                      <div className="text-xs text-slate-500">{participant.demoUserId}</div>
                    </div>
                    <span className="rounded-full bg-slate-800 px-2 py-1 text-[11px] uppercase tracking-[0.08em] text-slate-300">
                      {participant.demoUserId === selectedUser.id ? 'You' : 'Invitee'}
                    </span>
                  </div>
                ))}
              </div>

              <div className="mt-4 border-t border-slate-800/70 pt-3">
                <div className="flex items-center justify-between">
                  <span className="text-sm font-semibold">Invite more demo users</span>
                  <span className="text-xs text-slate-400">Sends add-participant</span>
                </div>
                {availableInvitees.length ? (
                  <div className="mt-2 space-y-2">
                    {availableInvitees.map((user) => (
                      <label
                        key={user.id}
                        className="flex cursor-pointer items-start gap-3 rounded-lg border border-transparent px-2 py-1 hover:border-slate-700"
                      >
                        <input
                          className="mt-1 h-4 w-4 accent-cyan-400"
                          type="checkbox"
                          checked={inviteSelection.includes(user.id)}
                          onChange={() => onToggleInvitee(user.id)}
                        />
                        <div>
                          <div className="font-semibold">{user.displayName}</div>
                          <div className="text-sm text-slate-400">{user.role}</div>
                        </div>
                      </label>
                    ))}
                  </div>
                ) : (
                  <p className="mt-2 text-sm text-slate-400">
                    All demo users are already part of this call.
                  </p>
                )}
                <button
                  className="mt-3 w-full rounded-lg bg-gradient-to-r from-cyan-400 to-blue-400 px-4 py-2 font-semibold text-slate-950 transition hover:brightness-110 disabled:cursor-not-allowed disabled:opacity-70"
                  type="button"
                  onClick={onAddParticipants}
                  disabled={addInFlight || inviteSelection.length === 0}
                >
                  {addInFlight ? 'Adding...' : 'Add Participants'}
                </button>
                {addError ? <p className="mt-2 text-sm text-rose-300">{addError}</p> : null}
              </div>

              <button
                className="mt-4 w-full rounded-lg border border-slate-800 px-4 py-2 text-sm font-semibold text-slate-200 transition hover:border-cyan-400/80 hover:text-cyan-100"
                type="button"
                onClick={onEndCall}
              >
                End call & open transcript
              </button>
            </>
          ) : (
            <>
              <div className="mt-3 space-y-2">
                {otherUsers.map((user) => (
                  <label
                    key={user.id}
                    className="flex cursor-pointer items-start gap-3 rounded-lg border border-transparent px-2 py-1 hover:border-slate-700"
                  >
                    <input
                      className="mt-1 h-4 w-4 accent-cyan-400"
                      type="checkbox"
                      checked={preCallParticipantIds.includes(user.id)}
                      onChange={() => onTogglePreCallParticipant(user.id)}
                    />
                    <div>
                      <div className="font-semibold">{user.displayName}</div>
                      <div className="text-sm text-slate-400">{user.role}</div>
                    </div>
                  </label>
                ))}
              </div>
              <button
                className="mt-3 w-full rounded-lg bg-gradient-to-r from-cyan-400 to-blue-400 px-4 py-2 font-semibold text-slate-950 transition hover:brightness-110 disabled:cursor-not-allowed disabled:opacity-70"
                type="button"
                onClick={onStartCall}
                disabled={startInFlight}
              >
                {startInFlight ? 'Starting...' : 'Start Call'}
              </button>
              {error ? <p className="mt-2 text-sm text-rose-300">{error}</p> : null}
            </>
          )}
          {callInfo ? (
            <div className="mt-3 space-y-2 text-sm text-slate-300">
              <div>
                <div className="text-xs uppercase tracking-[0.08em] text-slate-500">Call Session</div>
                <code>{callInfo.callSessionId}</code>
              </div>
              <div>
                <div className="text-xs uppercase tracking-[0.08em] text-slate-500">Group ID</div>
                <code>{callInfo.acsGroupId}</code>
              </div>
            </div>
          ) : null}
        </div>
      </div>

      <div className="grid gap-3">
        <div className="min-h-[420px] rounded-2xl border border-slate-800 bg-slate-900/60 p-3 shadow-xl shadow-black/50">
          {!callInfo ? (
            <div className="flex h-full flex-col items-center justify-center gap-3 rounded-xl border border-dashed border-slate-800 bg-slate-950/40 p-6 text-center">
              <p className="text-xs uppercase tracking-[0.1em] text-cyan-300">Call UI</p>
              <h3 className="text-xl font-semibold">Start a call to load the ACS CallComposite</h3>
              <p className="max-w-xl text-sm text-slate-400">
                We will initialize ACS identities for all selected participants and return a VoIP token
                scoped to {selectedUser.displayName}.
              </p>
            </div>
          ) : callAdapterReady ? (
            <CallComposite adapter={callAdapter!} />
          ) : (
            <div className="flex h-full flex-col items-center justify-center gap-3 rounded-xl border border-dashed border-slate-800 bg-slate-950/40 p-6 text-center">
              <p className="text-xs uppercase tracking-[0.1em] text-cyan-300">Preparing adapter</p>
              <h3 className="text-xl font-semibold">Loading ACS CallComposite...</h3>
            </div>
          )}
        </div>

        {callInfo ? (
          <TranscriptPanel
            title="Live transcript"
            subtitle="Polling /api/calls/{id}/transcript while the call is active"
            segments={transcriptSegments}
            loading={transcriptLoading}
            error={transcriptError}
            onRefresh={onRefreshTranscript}
            compact
          />
        ) : null}
      </div>
    </section>
  );
}

type SummaryViewProps = {
  callInfo: CallBootstrapState;
  callParticipants: CallParticipant[];
  transcriptSegments: TranscriptSegment[];
  transcriptLoading: boolean;
  transcriptError: string | null;
  onRefreshTranscript: () => void;
  onStartNewCall: () => void;
  onBackToCall: () => void;
};

function SummaryView({
  callInfo,
  callParticipants,
  transcriptSegments,
  transcriptLoading,
  transcriptError,
  onRefreshTranscript,
  onStartNewCall,
  onBackToCall
}: SummaryViewProps) {
  return (
    <section className="grid gap-4 lg:grid-cols-[320px,1fr]">
      <div className="flex flex-col gap-3">
        <div className="rounded-xl border border-slate-800 bg-gradient-to-br from-slate-900/70 via-slate-900 to-slate-950 p-5 shadow-xl shadow-black/50">
          <p className="text-xs uppercase tracking-[0.1em] text-cyan-300">Call summary</p>
          <h3 className="mt-2 text-xl font-semibold text-slate-50">Transcript captured</h3>
          <p className="mt-2 text-sm text-slate-300">
            This view queries the Functions endpoint for transcript segments and renders them in
            order. Use refresh to replay the latest ACS/Speech webhook events.
          </p>
          <div className="mt-3 space-y-2 text-sm text-slate-200">
            <div>
              <div className="text-xs uppercase tracking-[0.08em] text-slate-500">Call Session</div>
              <code>{callInfo.callSessionId}</code>
            </div>
            <div>
              <div className="text-xs uppercase tracking-[0.08em] text-slate-500">Group ID</div>
              <code>{callInfo.acsGroupId}</code>
            </div>
            <div>
              <div className="text-xs uppercase tracking-[0.08em] text-slate-500">Participants</div>
              <ul className="mt-1 space-y-1 text-slate-200">
                {callParticipants.map((participant) => (
                  <li key={participant.id} className="flex items-center justify-between rounded-lg border border-slate-800/70 bg-slate-950/40 px-2 py-1">
                    <span className="font-semibold">{participant.displayName}</span>
                    <span className="text-xs text-slate-400">{participant.demoUserId}</span>
                  </li>
                ))}
              </ul>
            </div>
          </div>

          <div className="mt-4 flex flex-wrap gap-3">
            <button
              className="rounded-lg bg-gradient-to-r from-cyan-400 to-blue-400 px-4 py-2 text-sm font-semibold text-slate-950 transition hover:brightness-110"
              onClick={onStartNewCall}
            >
              Start new call
            </button>
            <button
              className="rounded-lg border border-slate-700 px-4 py-2 text-sm font-semibold text-slate-200 transition hover:border-cyan-400/80 hover:text-cyan-100"
              onClick={onBackToCall}
            >
              Back to call view
            </button>
          </div>
        </div>
      </div>

      <TranscriptPanel
        title="Call transcript"
        subtitle="Ordered by ACS/Speech offsets; fetched via /api/calls/{id}/transcript"
        segments={transcriptSegments}
        loading={transcriptLoading}
        error={transcriptError}
        onRefresh={onRefreshTranscript}
      />
    </section>
  );
}

type TranscriptPanelProps = {
  title: string;
  subtitle?: string;
  segments: TranscriptSegment[];
  loading: boolean;
  error: string | null;
  onRefresh?: () => void;
  compact?: boolean;
};

function TranscriptPanel({
  title,
  subtitle,
  segments,
  loading,
  error,
  onRefresh,
  compact
}: TranscriptPanelProps) {
  const heightClass = compact ? 'max-h-[260px]' : 'max-h-[520px]';
  return (
    <div className="rounded-2xl border border-slate-800 bg-slate-900/60 p-4 shadow-xl shadow-black/50">
      <div className="flex items-start justify-between gap-3">
        <div>
          <p className="text-xs uppercase tracking-[0.1em] text-cyan-300">{title}</p>
          {subtitle ? <p className="text-sm text-slate-400">{subtitle}</p> : null}
        </div>
        {onRefresh ? (
          <button
            className="rounded-lg border border-slate-700 px-3 py-1 text-sm font-semibold text-slate-200 transition hover:border-cyan-400/80 hover:text-cyan-100 disabled:cursor-not-allowed disabled:opacity-60"
            onClick={onRefresh}
            disabled={loading}
          >
            {loading ? 'Refreshing...' : 'Refresh'}
          </button>
        ) : null}
      </div>
      {error ? <p className="mt-2 text-sm text-rose-300">{error}</p> : null}
      <div className={`mt-3 flex flex-col gap-2 overflow-auto ${heightClass}`}>
        {loading && segments.length === 0 ? (
          <p className="text-sm text-slate-400">Listening for transcript...</p>
        ) : null}
        {!loading && segments.length === 0 ? (
          <p className="text-sm text-slate-400">No transcript received yet.</p>
        ) : null}
        {segments.map((segment) => (
          <TranscriptEntry key={segment.id} segment={segment} />
        ))}
      </div>
    </div>
  );
}

function TranscriptEntry({ segment }: { segment: TranscriptSegment }) {
  const timestamp = formatClock(segment.createdAtUtc);
  const speaker = segment.speakerDisplayName || segment.speakerDemoUserId || 'Unknown speaker';
  return (
    <div className="rounded-xl border border-slate-800/80 bg-slate-950/40 px-3 py-2 shadow-sm shadow-black/30">
      <div className="flex items-center justify-between text-xs text-slate-400">
        <span className="font-semibold text-slate-200">{speaker}</span>
        <span>{timestamp}</span>
      </div>
      <p className="mt-1 text-sm text-slate-100">{segment.text}</p>
      {segment.offsetSeconds != null ? (
        <p className="mt-1 text-[11px] uppercase tracking-[0.08em] text-slate-500">
          Offset {segment.offsetSeconds?.toFixed(2)}s{segment.durationSeconds ? ` • ${segment.durationSeconds.toFixed(2)}s` : ''}
        </p>
      ) : null}
    </div>
  );
}

function Header() {
  return (
    <header className="rounded-2xl border border-slate-800 bg-gradient-to-br from-slate-900/80 via-slate-900 to-slate-950 p-6 shadow-xl shadow-black/50">
      <div className="flex flex-wrap items-center gap-3 text-sm text-slate-300">
        <span className="rounded-full bg-slate-800 px-3 py-1 text-xs uppercase tracking-[0.1em]">
          Phase 3
        </span>
        <span className="rounded-full bg-cyan-400/20 px-3 py-1 text-xs uppercase tracking-[0.1em] text-cyan-200">
          ACS webhook + transcript pipeline
        </span>
      </div>
      <h1 className="mt-4 text-4xl font-bold leading-tight sm:text-5xl">
        Call studio with <span className="text-cyan-300">live transcripts</span> from Functions
      </h1>
      <p className="mt-3 max-w-3xl text-lg text-slate-300">
        Start a call, receive ACS events in the Functions backend, and watch transcript segments flow
        into the UI. Jump to the summary view to replay the captured conversation.
      </p>
    </header>
  );
}

function transformCallStart(
  payload: StartCallResponse,
  selectedUser: DemoUser,
  fallbackAcsIdentity: string
): CallBootstrapState {
  return {
    callSessionId: payload.callSessionId,
    acsGroupId: payload.acsGroupId,
    acsToken: payload.acsToken,
    acsIdentity: payload.acsIdentity ?? fallbackAcsIdentity,
    displayName: selectedUser.displayName,
    participants: normalizeParticipants(
      payload.participants,
      selectedUser,
      payload.acsIdentity ?? fallbackAcsIdentity
    )
  };
}

function normalizeParticipants(
  participants: CallParticipant[] | undefined,
  selectedUser: DemoUser,
  selectedUserAcsIdentity: string
): CallParticipant[] {
  if (participants?.length) {
    return participants;
  }

  const fallbackId =
    typeof crypto !== 'undefined' && typeof crypto.randomUUID === 'function'
      ? crypto.randomUUID()
      : `${selectedUser.id}-${Date.now()}`;

  return [
    {
      id: fallbackId,
      demoUserId: selectedUser.id,
      displayName: selectedUser.displayName,
      acsIdentity: selectedUserAcsIdentity
    }
  ];
}

function formatClock(dateString: string) {
  const date = new Date(dateString);
  return `${date.getHours().toString().padStart(2, '0')}:${date
    .getMinutes()
    .toString()
    .padStart(2, '0')}:${date.getSeconds().toString().padStart(2, '0')}`;
}

export default App;
