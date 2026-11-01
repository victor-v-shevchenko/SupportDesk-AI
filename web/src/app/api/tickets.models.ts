/** Types for the ticket endpoints (PLAN.md 4 and 5). Enum values arrive as their names. */

export type TicketStatus = 'Open' | 'Pending' | 'Closed';
export type Category = 'Billing' | 'Shipping' | 'Returns' | 'Account' | 'Technical' | 'Other';
export type Priority = 'Low' | 'Normal' | 'High' | 'Urgent';
export type ClassificationStatus = 'NotRun' | 'Done' | 'NeedsManual';

/** One row of `GET /api/tickets`. */
export interface TicketListItem {
  id: number;
  number: string;
  subject: string;
  status: TicketStatus;
  category: Category | null;
  priority: Priority | null;
  classificationStatus: ClassificationStatus;
  createdAt: string;
}

export interface TicketSummary {
  text: string;
  promptVersion: string;
  generatedAt: string;
}

/** The saved classification: in the ticket detail, and the response of `POST /api/tickets/{id}/classification`. */
export interface TicketClassification {
  status: ClassificationStatus;
  category: Category | null;
  priority: Priority | null;
  reason: string | null;
  promptVersion: string | null;
  classifiedAt: string | null;
}

/** `GET /api/tickets/{id}`. */
export interface TicketDetail {
  id: number;
  number: string;
  subject: string;
  body: string;
  customerName: string;
  orderNumber: string | null;
  status: TicketStatus;
  createdAt: string;
  summary: TicketSummary | null;
  classification: TicketClassification;
}
