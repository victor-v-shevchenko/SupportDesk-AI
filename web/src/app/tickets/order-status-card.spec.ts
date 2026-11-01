import { TestBed } from '@angular/core/testing';
import { OrderLookupResult } from '../api/replies.models';
import { OrderStatusCard } from './order-status-card';

describe('OrderStatusCard', () => {
  function render(result: OrderLookupResult) {
    const fixture = TestBed.createComponent(OrderStatusCard);
    fixture.componentRef.setInput('orderNumber', 'KO-205002');
    fixture.componentRef.setInput('result', result);
    fixture.detectChanges();
    return fixture.nativeElement as HTMLElement;
  }

  it('shows the status, carrier, last update and last event of a found order', () => {
    const el = render({ found: true, orderNumber: 'KO-205002', status: 'Delayed', carrier: 'FedEx', lastUpdate: '2026-10-11T07:30:00Z', lastEvent: 'Weather delay at the Memphis hub' });

    expect(el.querySelector('h3')?.textContent).toContain('KO-205002');
    expect(el.querySelector('.status')?.textContent).toBe('Delayed');
    expect(el.querySelector('.carrier')?.textContent).toBe('FedEx');
    expect(el.querySelector('time')?.getAttribute('datetime')).toBe('2026-10-11T07:30:00Z');
    expect(el.querySelector('.event')?.textContent).toBe('Weather delay at the Memphis hub');
    expect(el.querySelector('.not-found')).toBeNull();
  });

  it('says when an order has no carrier yet', () => {
    const el = render({ found: true, orderNumber: 'KO-205002', status: 'Processing', carrier: null, lastUpdate: '2026-10-17T09:05:00Z', lastEvent: 'Payment confirmed' });

    expect(el.querySelector('.carrier')?.textContent).toBe('Not shipped yet');
  });

  it('says the order was not found', () => {
    const el = render({ found: false });

    expect(el.querySelector('.not-found')?.textContent).toContain('No order with this number was found');
    expect(el.querySelector('.status')).toBeNull();
  });
});
