import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { HealthService } from './api/health.service';

@Component({
  selector: 'app-api-status',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <span class="status" role="status" [attr.data-state]="state()">
      @switch (state()) {
        @case ('loading') { Checking API… }
        @case ('ok') { API ok }
        @default { API unavailable }
      }
    </span>
  `,
  styles: `
    .status { white-space: nowrap; font-size: 0.875rem; padding: 0.125rem 0.5rem; border-radius: 999px; background: var(--gray-bg); color: var(--gray-fg); }
    .status[data-state='ok'] { background: var(--ok-bg); color: var(--ok-fg); }
    .status[data-state='error'] { background: var(--red-bg); color: var(--danger); }
  `,
})
export class ApiStatus {
  private readonly health = inject(HealthService).health;

  protected state(): 'loading' | 'ok' | 'error' {
    if (this.health.isLoading()) {
      return 'loading';
    }
    return this.health.hasValue() && this.health.value().database === 'ok' ? 'ok' : 'error';
  }
}
