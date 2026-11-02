import { DecimalPipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, computed, effect, inject, input, untracked } from '@angular/core';
import { totalCalls } from '../api/model-calls.models';
import { ModelCallsService } from '../api/model-calls.service';

/**
 * The usage writer saves rows in the background, so a call's row can land a moment after the call ends. After a call the
 * rows are read at once and then again after each of these waits, until the ticket has a row for every call that ended.
 */
export const reloadBackoffMs = [500, 1000, 2000, 4000];

/** After the last read, how long to wait for it to answer before giving up on the target. */
const lastReadSettleMs = 500;

/** A model call run ended. `calls` is how many model calls it made, so how many rows it writes (a reply can make several). */
export interface CostRefresh {
  readonly calls: number;
}

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
  /** Set a new value whenever a run of model calls ends, to read the rows again. */
  readonly refresh = input<CostRefresh | undefined>(undefined);

  protected readonly rows = inject(ModelCallsService).forTicket(this.ticketId);
  protected readonly totals = computed(() => (this.rows.hasValue() ? totalCalls(this.rows.value()) : undefined));

  /**
   * How many rows the ticket has once every call that has ended is saved. Each ended call adds a row, on top of what is
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
      const refresh = this.refresh();
      if (!refresh) {
        return;
      }

      untracked(() => {
        this.target = Math.max(this.target, this.totals()?.calls ?? 0) + refresh.calls;
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
        } else {
          // Gave up after the last read. A call that wrote no row (a 404, a failed request) must not leave the target
          // too high for the calls after it, or each of them would run every retry. When that read failed, the rows shown
          // are older, so the target stays
          timer = setTimeout(() => {
            if (this.rows.hasValue()) {
              this.target = Math.min(this.target, this.totals()?.calls ?? 0);
            }
          }, lastReadSettleMs);
        }
      };
      untracked(() => attempt(0));
      onCleanup(() => clearTimeout(timer));
    });
  }
}
