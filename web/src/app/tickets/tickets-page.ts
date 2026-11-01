import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { RouterLink } from '@angular/router';
import { TicketsService } from '../api/tickets.service';
import { ClassificationBadges } from './classification-badges';
import { TicketStatusBadge } from './ticket-status';

@Component({
  selector: 'app-tickets-page',
  imports: [RouterLink, DatePipe, TicketStatusBadge, ClassificationBadges],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <h1>Tickets</h1>
    @if (tickets.isLoading() && !tickets.hasValue()) {
      <p class="loading">Loading tickets…</p>
    } @else if (tickets.error()) {
      <div class="error" role="alert">
        <p>Couldn't load the tickets.</p>
        <button type="button" (click)="tickets.reload()">Try again</button>
      </div>
    } @else if (tickets.hasValue() && tickets.value().length > 0) {
      <table>
        <thead>
          <tr><th scope="col">Number</th><th scope="col">Subject</th><th scope="col">Status</th><th scope="col">Classification</th><th scope="col">Created</th></tr>
        </thead>
        <tbody>
          @for (ticket of tickets.value(); track ticket.id) {
            <tr>
              <td class="number">{{ ticket.number }}</td>
              <td><a [routerLink]="['/tickets', ticket.id]">{{ ticket.subject }}</a></td>
              <td><app-ticket-status [status]="ticket.status" /></td>
              <td class="classification">
                <app-classification-badges [status]="ticket.classificationStatus" [category]="ticket.category" [priority]="ticket.priority" />
              </td>
              <td class="created"><time [attr.datetime]="ticket.createdAt">{{ ticket.createdAt | date: 'medium' }}</time></td>
            </tr>
          }
        </tbody>
      </table>
    } @else {
      <p class="empty">No tickets yet.</p>
    }
  `,
  styles: `
    table { width: 100%; border-collapse: collapse; }
    th, td { text-align: left; padding: 0.5rem 0.75rem; border-bottom: 1px solid #e5e7eb; }
    th { font-size: 0.75rem; text-transform: uppercase; letter-spacing: 0.04em; color: #6b7280; }
    .number, .created { white-space: nowrap; color: #4b5563; }
    a { color: #1d4ed8; text-decoration: none; }
    a:hover { text-decoration: underline; }
    .loading, .empty { color: #6b7280; }
    .error { color: #9b1c1c; }
  `,
})
export class TicketsPage {
  protected readonly tickets = inject(TicketsService).list();
}
