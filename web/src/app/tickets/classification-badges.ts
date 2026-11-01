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
    .badge { display: inline-block; font-size: 0.75rem; font-weight: 600; padding: 0.125rem 0.5rem; border-radius: 999px; background: #eceff3; color: #3b4252; white-space: nowrap; }
    .category { background: #ede9fe; color: #5b21b6; }
    .priority[data-priority='Low'] { background: #eceff3; color: #4b5563; }
    .priority[data-priority='Normal'] { background: #e0ecff; color: #1e40af; }
    .priority[data-priority='High'] { background: #fff4d6; color: #8a5a00; }
    .priority[data-priority='Urgent'] { background: #fde2e2; color: #9b1c1c; }
    .manual { background: #fde2e2; color: #9b1c1c; }
    .not-run { font-size: 0.75rem; color: #6b7280; }
  `,
})
export class ClassificationBadges {
  readonly status = input.required<ClassificationStatus>();
  readonly category = input<Category | null>(null);
  readonly priority = input<Priority | null>(null);
  /** Shown as a tooltip on the badges. */
  readonly reason = input<string | null>(null);
}
