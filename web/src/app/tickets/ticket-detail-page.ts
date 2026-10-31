import { DatePipe } from '@angular/common';
import { HttpErrorResponse } from '@angular/common/http';
import { ChangeDetectionStrategy, Component, computed, inject, input } from '@angular/core';
import { RouterLink } from '@angular/router';
import { TicketsService } from '../api/tickets.service';
import { TicketStatusBadge } from './ticket-status';

@Component({
  selector: 'app-ticket-detail-page',
  imports: [RouterLink, DatePipe, TicketStatusBadge],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <a class="back" routerLink="/tickets">← All tickets</a>
    @if (ticket.isLoading() && !ticket.hasValue()) {
      <p class="loading">Loading ticket…</p>
    } @else if (ticket.error()) {
      <div class="error" role="alert">
        @if (notFound()) {
          <p>Ticket not found.</p>
        } @else {
          <p>Couldn't load this ticket.</p>
          <button type="button" (click)="ticket.reload()">Try again</button>
        }
      </div>
    } @else if (ticket.hasValue()) {
      @let t = ticket.value();
      <h1><span class="number">{{ t.number }}</span> {{ t.subject }}</h1>
      <dl class="meta">
        <div><dt>Status</dt><dd><app-ticket-status [status]="t.status" /></dd></div>
        <div><dt>Customer</dt><dd>{{ t.customerName }}</dd></div>
        <div><dt>Order</dt><dd class="order">{{ t.orderNumber ?? 'None' }}</dd></div>
        <div><dt>Created</dt><dd><time [attr.datetime]="t.createdAt">{{ t.createdAt | date: 'medium' }}</time></dd></div>
      </dl>
      <p class="body">{{ t.body }}</p>

      <section class="slot summary" aria-labelledby="summary-heading">
        <h2 id="summary-heading">Summary</h2>
        @if (t.summary; as summary) {
          <p>{{ summary.text }}</p>
        } @else {
          <p class="empty">No summary yet.</p>
        }
      </section>

      <section class="slot classification" aria-labelledby="classification-heading">
        <h2 id="classification-heading">Classification</h2>
        @switch (t.classification.status) {
          @case ('Done') { <p>{{ t.classification.category }} · {{ t.classification.priority }}</p> }
          @case ('NeedsManual') { <p>Needs manual classification.</p> }
          @default { <p class="empty">Not classified yet.</p> }
        }
      </section>

      <section class="slot reply" aria-labelledby="reply-heading">
        <h2 id="reply-heading">Suggested reply</h2>
        <p class="empty">No suggested reply yet.</p>
      </section>
    }
  `,
  styles: `
    .back { display: inline-block; margin-bottom: 1rem; color: #1d4ed8; text-decoration: none; }
    h1 { margin: 0 0 1rem; font-size: 1.5rem; }
    h1 .number { color: #6b7280; font-weight: 500; margin-right: 0.25rem; }
    .meta { display: flex; flex-wrap: wrap; gap: 0.75rem 2rem; margin: 0 0 1.25rem; }
    .meta dt { font-size: 0.75rem; text-transform: uppercase; letter-spacing: 0.04em; color: #6b7280; }
    .meta dd { margin: 0.125rem 0 0; }
    .body { white-space: pre-wrap; line-height: 1.5; max-width: 70ch; padding: 1rem; background: #f8f9fb; border-radius: 6px; }
    .slot { margin-top: 1.5rem; padding-top: 0.75rem; border-top: 1px solid #e5e7eb; }
    .slot h2 { font-size: 1rem; margin: 0 0 0.5rem; }
    .loading, .empty { color: #6b7280; }
    .error { color: #9b1c1c; }
  `,
})
export class TicketDetailPage {
  /** Route parameter, bound by `withComponentInputBinding()`. */
  readonly id = input<string>();

  protected readonly ticket = inject(TicketsService).get(this.id);

  protected readonly notFound = computed(() => {
    const error = this.ticket.error();
    return error instanceof HttpErrorResponse && error.status === 404;
  });
}
