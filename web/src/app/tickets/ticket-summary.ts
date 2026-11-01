import { DatePipe } from '@angular/common';
import { HttpErrorResponse } from '@angular/common/http';
import { ChangeDetectionStrategy, Component, effect, inject, input, output, signal } from '@angular/core';
import { Subscription } from 'rxjs';
import { TicketSummary } from '../api/tickets.models';
import { TicketsService } from '../api/tickets.service';

/** A summary the server saved for a ticket; the page checks the ID before showing it. */
export interface GeneratedSummary {
  ticketId: number;
  summary: TicketSummary;
}

/**
 * The summary box on the ticket page (step 6): the saved summary with its prompt version and time, and a button that
 * generates a new one. One request at a time: the button is disabled while it runs. A failed request shows the server's
 * message and leaves the saved summary on screen, as the server leaves it in the database.
 */
@Component({
  selector: 'app-ticket-summary',
  imports: [DatePipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="header">
      <h2 id="summary-heading">Summary</h2>
      <button type="button" (click)="summarize()" [disabled]="pending()">
        {{ pending() ? 'Summarizing…' : summary() ? 'Regenerate' : 'Summarize' }}
      </button>
    </div>
    @if (summary(); as s) {
      <p class="text">{{ s.text }}</p>
      <p class="generated">
        <span class="version">{{ s.promptVersion }}</span> ·
        <time [attr.datetime]="s.generatedAt">{{ s.generatedAt | date: 'medium' }}</time>
      </p>
    } @else if (!pending()) {
      <p class="empty">No summary yet.</p>
    }
    @if (pending()) {
      <p class="loading" role="status">Generating a summary…</p>
    }
    @if (error(); as message) {
      <p class="error" role="alert">{{ message }}</p>
    }
  `,
  styles: `
    .header { display: flex; align-items: center; gap: 1rem; margin-bottom: 0.5rem; }
    h2 { font-size: 1rem; margin: 0; }
    .text { margin: 0 0 0.25rem; line-height: 1.5; max-width: 70ch; }
    .generated { margin: 0; font-size: 0.75rem; color: #6b7280; }
    .loading, .empty { color: #6b7280; }
    .error { color: #9b1c1c; }
  `,
})
export class TicketSummaryBox {
  readonly ticketId = input.required<number>();
  readonly summary = input<TicketSummary | null>(null);
  readonly generated = output<GeneratedSummary>();

  private readonly tickets = inject(TicketsService);
  protected readonly pending = signal(false);
  protected readonly error = signal<string | null>(null);
  private request?: Subscription;

  constructor() {
    // A new ticket starts clean: the old request is cancelled and its error cleared. Also runs when the box is destroyed
    effect((onCleanup) => {
      this.ticketId();
      onCleanup(() => {
        this.request?.unsubscribe();
        this.request = undefined;
        this.pending.set(false);
        this.error.set(null);
      });
    });
  }

  summarize(): void {
    if (this.pending()) {
      return;
    }

    const ticketId = this.ticketId();
    this.pending.set(true);
    this.error.set(null);
    this.request = this.tickets.summarize(ticketId).subscribe({
      next: (summary) => {
        this.pending.set(false);
        this.generated.emit({ ticketId, summary });
      },
      error: (error: unknown) => {
        this.pending.set(false);
        this.error.set(messageFor(error));
      },
    });
  }
}

/** The problem details' `detail` (written for people, PLAN.md 6.5), or a general message when there's none. */
function messageFor(error: unknown): string {
  if (error instanceof HttpErrorResponse) {
    const detail: unknown = (error.error as { detail?: unknown } | null)?.detail;
    if (typeof detail === 'string' && detail.length > 0) {
      return detail;
    }

    if (error.status === 0) {
      return "Couldn't reach the server. Check your connection and try again.";
    }
  }

  return "Couldn't generate a summary. Try again.";
}
