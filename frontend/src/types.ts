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
