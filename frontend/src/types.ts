export type CallParticipant = {
  id: string;
  demoUserId: string;
  displayName: string;
  acsIdentity: string;
};

export type StartCallResponse = {
  callSessionId: string;
  acsGroupId: string;
  callConnectionId?: string | null;
  acsToken: string;
  acsIdentity: string;
  acsTokenExpiresOn?: string;
  participants?: CallParticipant[];
};

export type JoinCallResponse = StartCallResponse;

export type AddParticipantsResponse = {
  callSessionId: string;
  acsGroupId: string;
  added?: Array<
    CallParticipant & {
      acsInviteDispatched?: boolean;
    }
  >;
  participants?: CallParticipant[];
  skipped?: Array<{ demoUserId: string; reason?: string }>;
};

export type TranscriptSegment = {
  id: string;
  callSessionId: string;
  text: string;
  speakerAcsIdentity?: string | null;
  speakerDemoUserId?: string | null;
  speakerDisplayName?: string | null;
  offsetSeconds?: number | null;
  durationSeconds?: number | null;
  createdAtUtc: string;
  source?: string | null;
};

export type TranscriptResponse = {
  callSessionId: string;
  status: string;
  startedAtUtc: string;
  endedAtUtc?: string | null;
  transcriptionStartedAtUtc?: string | null;
  startedByDemoUserId?: string | null;
  acsGroupId: string;
  callConnectionId?: string | null;
  participants?: CallParticipant[];
  segments: TranscriptSegment[];
};

export type CallSummaryResponse = {
  callSessionId: string;
  status: string;
  startedAtUtc: string;
  endedAtUtc?: string | null;
  transcriptionStartedAtUtc?: string | null;
  startedByDemoUserId?: string | null;
  acsGroupId: string;
  callConnectionId?: string | null;
  participants: CallParticipant[];
  summaryStatus: 'ready' | 'pending' | 'failed';
  summary?: string | null;
  keyPoints: string[];
  actionItems: string[];
  summaryGeneratedAtUtc?: string | null;
  summarySource?: string | null;
};
