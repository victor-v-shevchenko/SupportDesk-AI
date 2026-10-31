import { TestBed } from '@angular/core/testing';
import { TicketsPage } from './tickets-page';

describe('TicketsPage', () => {
  it('renders the empty state', async () => {
    const fixture = TestBed.createComponent(TicketsPage);
    await fixture.whenStable();
    const el = fixture.nativeElement as HTMLElement;

    expect(el.querySelector('h1')?.textContent).toBe('Tickets');
    expect(el.querySelector('.empty')?.textContent).toContain('No tickets yet');
  });
});
