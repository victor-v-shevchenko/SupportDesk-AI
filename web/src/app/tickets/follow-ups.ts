import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, computed, inject, input } from '@angular/core';
import { FollowUpsService } from '../api/follow-ups.service';
import { ProposalCard } from './proposal-card';

/**
 * The ticket's follow-ups (PLAN.md 10): the tasks agents confirmed, and the proposals still waiting for a decision,
 * which can be confirmed here too. Proposals that were confirmed or expired aren't repeated: a confirmed one is its task.
 */
@Component({
  selector: 'app-follow-ups',
  imports: [DatePipe, ProposalCard],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <h2 id="follow-ups-heading">Follow-ups</h2>
    @if (followUps.isLoading() && !followUps.hasValue()) {
      <p class="loading">Loading follow-ups…</p>
    } @else if (followUps.error()) {
      <div class="error" role="alert">
        <p>Couldn't load the follow-ups.</p>
        <button type="button" (click)="followUps.reload()">Try again</button>
      </div>
    } @else if (followUps.hasValue()) {
      @if (tasks().length === 0 && pending().length === 0) {
        <p class="empty">No follow-ups yet.</p>
      }
      @if (tasks().length > 0) {
        <ul class="tasks">
          @for (task of tasks(); track task.id) {
            <li>
              <span class="title">{{ task.title }}</span>
              @if (task.dueDate; as due) {
                <span class="due">due <time [attr.datetime]="due">{{ due | date: 'mediumDate' : 'UTC' }}</time></span>
              }
              @if (task.notes) {
                <p class="notes">{{ task.notes }}</p>
              }
            </li>
          }
        </ul>
      }
      @for (proposal of pending(); track proposal.id) {
        <app-proposal-card [proposal]="proposal" [status]="proposal.status" (confirmed)="reload()" />
      }
    }
  `,
  styles: `
    h2 { font-size: 1rem; margin: 0 0 0.5rem; }
    .tasks { margin: 0 0 0.75rem; padding-left: 1.25rem; }
    .tasks li { margin-bottom: 0.5rem; }
    .title { font-weight: 600; }
    .due { margin-left: 0.5rem; color: #6b7280; font-size: 0.85rem; }
    .notes { margin: 0.125rem 0 0; max-width: 70ch; }
    app-proposal-card { margin-bottom: 0.5rem; }
    .loading, .empty { color: #6b7280; }
    .error { color: #9b1c1c; }
  `,
})
export class FollowUpsList {
  readonly ticketId = input.required<number>();

  protected readonly followUps = inject(FollowUpsService).list(this.ticketId);
  protected readonly tasks = computed(() => this.followUps.value()?.tasks ?? []);
  protected readonly pending = computed(() => this.followUps.value()?.proposals.filter((p) => p.status === 'Pending') ?? []);

  /** Called when a reply proposed something or a proposal was confirmed elsewhere on the page. */
  reload(): void {
    this.followUps.reload();
  }
}
