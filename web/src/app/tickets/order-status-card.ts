import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import { OrderLookupResult } from '../api/replies.models';

/** What `lookup_order_status` found while the reply was written: the order's status, or that there's no such order. */
@Component({
  selector: 'app-order-status-card',
  imports: [DatePipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <h3>Order {{ orderNumber() }}</h3>
    @if (result(); as r) {
      @if (r.found) {
        <dl>
          <div><dt>Status</dt><dd class="status">{{ r.status }}</dd></div>
          <div><dt>Carrier</dt><dd class="carrier">{{ r.carrier ?? 'Not shipped yet' }}</dd></div>
          <div><dt>Last update</dt><dd><time [attr.datetime]="r.lastUpdate">{{ r.lastUpdate | date: 'medium' }}</time></dd></div>
        </dl>
        <p class="event">{{ r.lastEvent }}</p>
      } @else {
        <p class="not-found">No order with this number was found.</p>
      }
    }
  `,
  styles: `
    :host { display: block; padding: 0.75rem 1rem; border: 1px solid #e5e7eb; border-left: 3px solid #2563eb; border-radius: 6px; background: #f8fafc; }
    h3 { margin: 0 0 0.5rem; font-size: 0.875rem; }
    dl { display: flex; flex-wrap: wrap; gap: 0.5rem 1.5rem; margin: 0; }
    dt { font-size: 0.7rem; text-transform: uppercase; letter-spacing: 0.04em; color: #6b7280; }
    dd { margin: 0.125rem 0 0; }
    .event { margin: 0.5rem 0 0; }
    .not-found { margin: 0; color: #9b1c1c; }
  `,
})
export class OrderStatusCard {
  readonly orderNumber = input.required<string>();
  readonly result = input.required<OrderLookupResult>();
}
