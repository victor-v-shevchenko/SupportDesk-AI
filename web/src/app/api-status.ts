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
    .status { font-size: 0.875rem; padding: 0.125rem 0.5rem; border-radius: 999px; background: #eceff3; color: #3b4252; }
    .status[data-state='ok'] { background: #e3f4e8; color: #1d6b36; }
    .status[data-state='error'] { background: #fbe7e7; color: #9b1c1c; }
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
