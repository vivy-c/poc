export type DemoUser = {
  id: string;
  displayName: string;
  role: string;
  focus: string;
};

export const DEMO_USERS: DemoUser[] = [
  {
    id: 'agent-1',
    displayName: 'Avery Chen',
    role: 'Recruiter',
    focus: 'Keeps the conversation warm and structured.'
  },
  {
    id: 'agent-2',
    displayName: 'Jordan Malik',
    role: 'Hiring Manager',
    focus: 'Goes deep on product and technical signals.'
  },
  {
    id: 'candidate-1',
    displayName: 'Taylor Brooks',
    role: 'Candidate',
    focus: 'Collaborative, pragmatic, asks thoughtful questions.'
  }
];
