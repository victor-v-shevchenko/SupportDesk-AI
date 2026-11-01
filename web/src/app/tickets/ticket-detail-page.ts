import { DatePipe } from '@angular/common';
import { HttpErrorResponse } from '@angular/common/http';
import { ChangeDetectionStrategy, Component, computed, inject, input, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { TicketsService } from '../api/tickets.service';
import { ClassificationBadges } from './classification-badges';
import { TicketStatusBadge } from './ticket-status';
import { GeneratedSummary, TicketSummaryBox } from './ticket-summary';

@Component({
  selector: 'app-ticket-detail-page',
  imports: [RouterLink, DatePipe, TicketStatusBadge, TicketSummaryBox, ClassificationBadges],
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
        <app-ticket-summary [ticketId]="t.id" [summary]="t.summary" (generated)="showSummary($event)" />
      </section>

      <section class="slot classification" aria-labelledby="classification-heading">
        <h2 id="classification-heading">Classification</h2>
        @let c = t.classification;
        @if (c.status === 'NotRun') {
          <p class="empty">Not classified yet.</p>
        } @else {
          <p>
            <app-classification-badges [status]="c.status" [category]="c.category" [priority]="c.priority" [reason]="c.reason" />
          </p>
          @if (c.reason) {
            <p class="reason">{{ c.reason }}</p>
          } @else if (c.status === 'NeedsManual') {
            <p class="reason">The model's answer couldn't be used twice in a row, so this ticket needs a person to classify it.</p>
          }
          <p class="provenance">{{ c.promptVersion }} · <time [attr.datetime]="c.classifiedAt">{{ c.classifiedAt | date: 'medium' }}</time></p>
        }
        <div class="actions">
          <button type="button" class="classify" (click)="classify(t.id)" [disabled]="classifying()">
            {{ c.status === 'NotRun' ? 'Classify' : 'Reclassify' }}
          </button>
          @if (classifying()) {
            <span class="loading" role="status">Classifying…</span>
          }
        </div>
        @if (classifyError(); as message) {
          <p class="error" role="alert">{{ message }}</p>
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
    .reason { margin: 0.25rem 0; max-width: 70ch; }
    .provenance { margin: 0.25rem 0; font-size: 0.75rem; color: #6b7280; }
    .actions { display: flex; align-items: center; gap: 0.75rem; margin-top: 0.5rem; }
  `,
})
export class TicketDetailPage {
  /** Route parameter, bound by `withComponentInputBinding()`. */
  readonly id = input<string>();

  private readonly tickets = inject(TicketsService);

  protected readonly ticket = this.tickets.get(this.id);

  protected readonly classifying = signal(false);
  protected readonly classifyError = signal<string | null>(null);

  /** One request at a time: the button is disabled while it runs. A failure leaves the saved classification on screen. */
  protected async classify(ticketId: number): Promise<void> {
    if (this.classifying()) {
      return;
    }

    this.classifying.set(true);
    this.classifyError.set(null);
    try {
      const classification = await this.tickets.classify(ticketId);
      this.ticket.update((t) => (t && t.id === ticketId ? { ...t, classification } : t));
    } catch (error) {
      this.classifyError.set(errorMessage(error));
    } finally {
      this.classifying.set(false);
    }
  }

  protected readonly notFound = computed(() => {
    const error = this.ticket.error();
    return error instanceof HttpErrorResponse && error.status === 404;
  });

  /** Shows a summary the server just saved, unless the page has moved on to another ticket since it was asked for. */
  protected showSummary({ ticketId, summary }: GeneratedSummary): void {
    this.ticket.update((t) => (t?.id === ticketId ? { ...t, summary } : t));
  }
}

/** The problem details' detail from the API, which never carries provider text, or a generic message. */
function errorMessage(error: unknown): string {
  if (error instanceof HttpErrorResponse && typeof error.error?.detail === 'string') {
    return `Couldn't classify this ticket: ${error.error.detail}`;
  }

  return "Couldn't classify this ticket. Try again in a moment.";
}
