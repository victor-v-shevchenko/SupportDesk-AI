import { DecimalPipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, computed, effect, inject, input, untracked } from '@angular/core';
import { totalCalls } from '../api/model-calls.models';
import { ModelCallsService } from '../api/model-calls.service';

/**
 * The usage writer saves rows in the background, so a call's row can land a moment after the call ends. After a call the
 * rows are read at once and then again after each of these waits, until there are more rows than before the call.
 */
export const reloadBackoffMs = [500, 1000, 2000, 4000];

/**
 * The ticket's model cost (step 11): tokens and dollars added up from its `ModelCalls` rows, so it covers every
 * summary, classification and reply, not just the one on screen. `refresh` changes whenever one of them finishes.
 */
@Component({
  selector: 'app-ticket-cost',
  imports: [DecimalPipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <h2 id="cost-heading">Model usage</h2>
    @if (rows.isLoading() && !rows.hasValue()) {
      <p class="loading" role="status">Loading usage…</p>
    } @else if (rows.error()) {
      <div class="error" role="alert">
        <p>Couldn't load the usage for this ticket.</p>
        <button type="button" (click)="rows.reload()">Try again</button>
      </div>
    } @else if (totals(); as t) {
      @if (t.calls === 0) {
        <p class="empty">No model calls yet.</p>
      } @else {
        <p class="totals">
          <span class="tokens">{{ t.inputTokens + t.outputTokens | number }} tokens</span>
          ({{ t.inputTokens | number }} input + {{ t.outputTokens | number }} output){{ t.estimated ? ', some estimated' : '' }} ·
          <span class="cost">{{ t.costIncomplete ? 'at least ' : '' }}\${{ t.costUsd | number: '1.4-6' }}</span>
          · {{ t.calls }} {{ t.calls === 1 ? 'call' : 'calls' }}
        </p>
      }
    }
  `,
  styles: `
    h2 { font-size: 1rem; margin: 0 0 0.5rem; }
    .totals { margin: 0; }
    .loading, .empty { color: var(--muted); }
    .error { color: var(--danger); }
  `,
})
export class TicketCost {
  readonly ticketId = input.required<number>();
  /** Bump to read the rows again. */
  readonly refresh = input(0);

  protected readonly rows = inject(ModelCallsService).forTicket(this.ticketId);
  protected readonly totals = computed(() => (this.rows.hasValue() ? totalCalls(this.rows.value()) : undefined));

  /**
   * How many rows the ticket has once every call that has ended is saved. Each ended call adds one, on top of what is
   * already shown, so calls that end while an earlier read is still loading (a reload then does nothing) are counted too.
   */
  private target = 0;

  constructor() {
    // A different ticket starts counting again
    effect(() => {
      this.ticketId();
      this.target = 0;
    });

    effect((onCleanup) => {
      if (this.refresh() === 0) {
        return;
      }

      untracked(() => {
        this.target = Math.max(this.target, this.totals()?.calls ?? 0) + 1;
      });
      let timer: ReturnType<typeof setTimeout> | undefined;
      const attempt = (index: number): void => {
        this.rows.reload();
        if (index < reloadBackoffMs.length) {
          timer = setTimeout(() => {
            // Stop once every ended call's row is in
            if ((this.totals()?.calls ?? 0) < this.target) {
              attempt(index + 1);
            }
          }, reloadBackoffMs[index]);
        }
      };
      untracked(() => attempt(0));
      onCleanup(() => clearTimeout(timer));
    });
  }
}
