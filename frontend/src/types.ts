export type StartCallResponse = {
  callSessionId: string;
  acsGroupId: string;
  acsToken: string;
  acsIdentity: string;
  acsTokenExpiresOn?: string;
  participants?: Array<{
    id: string;
    demoUserId: string;
    displayName: string;
    acsIdentity: string;
  }>;
};
