import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import { Category, ClassificationStatus, Priority } from '../api/tickets.models';

/** Category and priority badges, or a badge for a ticket that needs manual classification (PLAN.md 9). */
@Component({
  selector: 'app-classification-badges',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @switch (status()) {
      @case ('Done') {
        <span class="badges" [attr.title]="reason()">
          <span class="badge category" [attr.data-category]="category()">{{ category() }}</span>
          <span class="badge priority" [attr.data-priority]="priority()">{{ priority() }}</span>
        </span>
      }
      @case ('NeedsManual') {
        <span class="badge manual">Needs manual classification</span>
      }
      @default {
        <span class="not-run">Not classified</span>
      }
    }
  `,
  styles: `
    .badges { display: inline-flex; gap: 0.375rem; }
    .badge { display: inline-block; font-size: 0.75rem; font-weight: 600; padding: 0.125rem 0.5rem; border-radius: 999px; background: var(--gray-bg); color: var(--gray-fg); white-space: nowrap; }
    .category { background: var(--violet-bg); color: var(--violet-fg); }
    .priority[data-priority='Low'] { background: var(--gray-bg); color: var(--muted); }
    .priority[data-priority='Normal'] { background: var(--blue-bg); color: var(--blue-fg); }
    .priority[data-priority='High'] { background: var(--amber-bg); color: var(--amber-fg); }
    .priority[data-priority='Urgent'] { background: var(--red-bg); color: var(--danger); }
    .manual { background: var(--red-bg); color: var(--danger); }
    .not-run { font-size: 0.75rem; color: var(--muted); }
  `,
})
export class ClassificationBadges {
  readonly status = input.required<ClassificationStatus>();
  readonly category = input<Category | null>(null);
  readonly priority = input<Priority | null>(null);
  /** Shown as a tooltip on the badges. */
  readonly reason = input<string | null>(null);
}
