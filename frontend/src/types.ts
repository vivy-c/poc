export type CallParticipant = {
  id: string;
  demoUserId: string;
  displayName: string;
  acsIdentity: string;
};

export type StartCallResponse = {
  callSessionId: string;
  acsGroupId: string;
  acsToken: string;
  acsIdentity: string;
  acsTokenExpiresOn?: string;
  participants?: CallParticipant[];
};

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
  acsGroupId: string;
  callConnectionId?: string | null;
  segments: TranscriptSegment[];
};
