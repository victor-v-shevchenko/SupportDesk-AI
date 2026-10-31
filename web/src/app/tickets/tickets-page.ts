import { ChangeDetectionStrategy, Component } from '@angular/core';

@Component({
  selector: 'app-tickets-page',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <h1>Tickets</h1>
    <p class="empty">No tickets yet.</p>
  `,
  styles: `
    .empty { color: #6b7280; }
  `,
})
export class TicketsPage {}
