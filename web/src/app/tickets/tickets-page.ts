import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, effect, inject, signal, untracked } from '@angular/core';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { TicketsService } from '../api/tickets.service';
import { ClassificationBadges } from './classification-badges';
import { TicketStatusBadge } from './ticket-status';

/**
 * The tickets layout (step 11): the list on the left and the open ticket on the right on wide screens. On narrow
 * screens one of them fills the width: the list, or the ticket once one is open (it has a link back to the list).
 */
@Component({
  selector: 'app-tickets-page',
  imports: [RouterLink, RouterLinkActive, RouterOutlet, DatePipe, TicketStatusBadge, ClassificationBadges],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="split" [class.detail-open]="detailOpen()">
      <section class="list" aria-labelledby="tickets-heading">
        <h1 id="tickets-heading">Tickets</h1>
        @if (tickets.isLoading() && !tickets.hasValue()) {
          <p class="loading" role="status">Loading tickets…</p>
        } @else if (tickets.error()) {
          <div class="error" role="alert">
            <p>Couldn't load the tickets.</p>
            <button type="button" (click)="tickets.reload()">Try again</button>
          </div>
        } @else if (tickets.hasValue() && tickets.value().length > 0) {
          <ul class="tickets">
            @for (ticket of tickets.value(); track ticket.id) {
              <li>
                <a class="ticket" [routerLink]="['/tickets', ticket.id]" routerLinkActive="selected" ariaCurrentWhenActive="page">
                  <span class="top">
                    <span class="number">{{ ticket.number }}</span>
                    <app-ticket-status [status]="ticket.status" />
                  </span>
                  <span class="subject">{{ ticket.subject }}</span>
                  <span class="bottom">
                    <app-classification-badges [status]="ticket.classificationStatus" [category]="ticket.category" [priority]="ticket.priority" />
                    <time class="created" [attr.datetime]="ticket.createdAt">{{ ticket.createdAt | date: 'medium' }}</time>
                  </span>
                </a>
              </li>
            }
          </ul>
        } @else {
          <p class="empty">No tickets yet.</p>
        }
      </section>

      <section class="detail" aria-label="Ticket">
        @if (!detailOpen()) {
          <p class="empty placeholder">Select a ticket to see its details.</p>
        }
        <router-outlet (activate)="detailOpen.set(true)" (deactivate)="detailOpen.set(false)" />
      </section>
    </div>
  `,
  styles: `
    .split { display: grid; grid-template-columns: 1fr; gap: 1.5rem; }
    h1 { margin: 0 0 0.75rem; font-size: 1.5rem; }
    .tickets { list-style: none; margin: 0; padding: 0; display: grid; gap: 0.5rem; }
    .ticket { display: grid; gap: 0.35rem; padding: 0.6rem 0.75rem; border: 1px solid var(--border); border-radius: 6px; color: var(--fg); text-decoration: none; }
    .ticket:hover { background: var(--surface); }
    .ticket.selected { background: var(--selected); border-color: var(--link); }
    .top, .bottom { display: flex; align-items: center; justify-content: space-between; gap: 0.5rem; flex-wrap: wrap; }
    .number, .created { color: var(--muted); font-size: 0.8rem; white-space: nowrap; }
    .subject { font-weight: 600; }
    .loading, .empty { color: var(--muted); }
    .error { color: var(--danger); }
    .placeholder { margin: 0; }

    /* Narrow: one pane at a time */
    .split.detail-open .list { display: none; }
    .split:not(.detail-open) .detail { display: none; }

    @media (min-width: 900px) {
      .split { grid-template-columns: minmax(18rem, 24rem) minmax(0, 1fr); align-items: start; }
      .split.detail-open .list, .split:not(.detail-open) .detail { display: block; }
      .list { position: sticky; top: 1rem; max-height: calc(100vh - 2rem); overflow-y: auto; padding: 0.25rem; }
    }
  `,
})
export class TicketsPage {
  private readonly service = inject(TicketsService);
  protected readonly tickets = this.service.list();
  protected readonly detailOpen = signal(false);

  constructor() {
    // The ticket beside the list was classified: read the list again, keeping what it shows meanwhile
    effect(() => {
      if (this.service.listStale() > 0) {
        untracked(() => this.tickets.reload());
      }
    });
  }
}
