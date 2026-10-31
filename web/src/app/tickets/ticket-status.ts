import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import { TicketStatus } from '../api/tickets.models';

@Component({
  selector: 'app-ticket-status',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `<span class="badge" [attr.data-status]="status()">{{ status() }}</span>`,
  styles: `
    .badge { display: inline-block; font-size: 0.75rem; font-weight: 600; padding: 0.125rem 0.5rem; border-radius: 999px; background: #eceff3; color: #3b4252; }
    .badge[data-status='Open'] { background: #e0ecff; color: #1e40af; }
    .badge[data-status='Pending'] { background: #fff4d6; color: #8a5a00; }
  `,
})
export class TicketStatusBadge {
  readonly status = input.required<TicketStatus>();
}
