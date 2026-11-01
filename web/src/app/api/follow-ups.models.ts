/** Types for the follow-up endpoints (PLAN.md 4, 10). Dates are ISO strings; `dueDate` is a date without a time. */

export type ProposalStatus = 'Pending' | 'Confirmed' | 'Expired';

/** One proposal in `GET /api/tickets/{id}/follow-ups`. */
export interface FollowUpProposal {
  id: string;
  replyRunId: string;
  title: string;
  notes: string;
  dueDate: string | null;
  status: ProposalStatus;
  createdAt: string;
  expiresAt: string;
}

/** A confirmed follow-up: in the list, and the response of `POST /api/follow-ups/{proposalId}/confirm`. */
export interface FollowUpTask {
  id: number;
  proposalId: string;
  title: string;
  notes: string;
  dueDate: string | null;
  createdAt: string;
}

/** `GET /api/tickets/{id}/follow-ups`: both lists newest first. */
export interface FollowUps {
  proposals: FollowUpProposal[];
  tasks: FollowUpTask[];
}
