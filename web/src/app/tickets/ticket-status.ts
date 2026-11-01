import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import { TicketStatus } from '../api/tickets.models';

@Component({
  selector: 'app-ticket-status',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `<span class="badge" [attr.data-status]="status()">{{ status() }}</span>`,
  styles: `
    .badge { display: inline-block; font-size: 0.75rem; font-weight: 600; padding: 0.125rem 0.5rem; border-radius: 999px; background: var(--gray-bg); color: var(--gray-fg); }
    .badge[data-status='Open'] { background: var(--blue-bg); color: var(--blue-fg); }
    .badge[data-status='Pending'] { background: var(--amber-bg); color: var(--amber-fg); }
  `,
})
export class TicketStatusBadge {
  readonly status = input.required<TicketStatus>();
}
