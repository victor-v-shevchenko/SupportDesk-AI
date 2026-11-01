import { DatePipe } from '@angular/common';
import { HttpErrorResponse } from '@angular/common/http';
import { ChangeDetectionStrategy, Component, computed, inject, input, linkedSignal, output, signal } from '@angular/core';
import { FollowUpTask, ProposalStatus } from '../api/follow-ups.models';
import { FollowUpsService } from '../api/follow-ups.service';
import { ProposalPayload } from '../api/replies.models';

/**
 * A follow-up task the model proposed: title, notes, due date and a Confirm button. Only Confirm creates the task
 * (PLAN.md 10); confirming twice is safe, since the server returns the same task. An expired proposal can't be confirmed.
 */
@Component({
  selector: 'app-proposal-card',
  imports: [DatePipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <p class="label">Proposed follow-up</p>
    <h3 class="title">{{ proposal().title }}</h3>
    @if (proposal().notes) {
      <p class="notes">{{ proposal().notes }}</p>
    }
    <p class="due">
      @if (proposal().dueDate; as due) {
        Due <time [attr.datetime]="due">{{ due | date: 'mediumDate' : 'UTC' }}</time>
      } @else {
        No due date
      }
    </p>
    @switch (state()) {
      @case ('Confirmed') {
        <p class="confirmed" role="status">Task created.</p>
      }
      @case ('Expired') {
        <p class="expired">This proposal has expired and can't be confirmed.</p>
      }
      @default {
        <button type="button" class="confirm" (click)="confirm()" [disabled]="confirming()">{{ confirming() ? 'Confirming…' : 'Confirm' }}</button>
      }
    }
    @if (error(); as message) {
      <p class="error" role="alert">{{ message }}</p>
    }
  `,
  styles: `
    :host { display: block; padding: 0.75rem 1rem; border: 1px solid #e5e7eb; border-left: 3px solid #d97706; border-radius: 6px; background: #fffbeb; }
    .label { margin: 0; font-size: 0.7rem; text-transform: uppercase; letter-spacing: 0.04em; color: #92400e; }
    .title { margin: 0.25rem 0; font-size: 0.95rem; }
    .notes { margin: 0.25rem 0; white-space: pre-wrap; max-width: 70ch; }
    .due { margin: 0.25rem 0 0.5rem; font-size: 0.85rem; color: #6b7280; }
    button { padding: 0.35rem 0.75rem; font: inherit; }
    .confirmed { margin: 0; color: #166534; font-weight: 600; }
    .expired { margin: 0; color: #6b7280; }
    .error { margin: 0.5rem 0 0; color: #9b1c1c; }
  `,
})
export class ProposalCard {
  readonly proposal = input.required<ProposalPayload>();
  /** The status the server last reported; a proposal straight from the reply stream is pending. */
  readonly status = input<ProposalStatus>('Pending');
  readonly confirmed = output<FollowUpTask>();

  private readonly followUps = inject(FollowUpsService);
  /** Follows the input, and moves on when this card confirms the proposal or finds it expired. */
  protected readonly state = linkedSignal(() => this.status());
  protected readonly confirming = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly canConfirm = computed(() => this.state() === 'Pending' && !this.confirming());

  async confirm(): Promise<void> {
    if (!this.canConfirm()) {
      return;
    }

    this.confirming.set(true);
    this.error.set(null);
    try {
      const task = await this.followUps.confirm(this.proposal().id);
      this.state.set('Confirmed');
      this.confirmed.emit(task);
    } catch (error) {
      if (error instanceof HttpErrorResponse && error.status === 409) {
        this.state.set('Expired');
      } else {
        this.error.set(error instanceof HttpErrorResponse && error.status === 404 ? 'This proposal no longer exists.' : "Couldn't confirm the task. Try again.");
      }
    } finally {
      this.confirming.set(false);
    }
  }
}
