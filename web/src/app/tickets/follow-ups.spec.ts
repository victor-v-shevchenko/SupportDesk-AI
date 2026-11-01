import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { FollowUps } from '../api/follow-ups.models';
import { FollowUpsList } from './follow-ups';

const list: FollowUps = {
  proposals: [
    { id: 'p2', replyRunId: 'r2', title: 'Call the customer back', notes: '', dueDate: null, status: 'Pending', createdAt: '2026-11-01T12:00:00Z', expiresAt: '2026-11-02T12:00:00Z' },
    { id: 'p1', replyRunId: 'r1', title: 'Open a carrier trace', notes: '', dueDate: '2026-11-03', status: 'Confirmed', createdAt: '2026-11-01T10:00:00Z', expiresAt: '2026-11-02T10:00:00Z' },
    { id: 'p0', replyRunId: 'r0', title: 'Old idea', notes: '', dueDate: null, status: 'Expired', createdAt: '2026-10-30T10:00:00Z', expiresAt: '2026-10-31T10:00:00Z' },
  ],
  tasks: [{ id: 1, proposalId: 'p1', title: 'Open a carrier trace', notes: 'Three weeks late.', dueDate: '2026-11-03', createdAt: '2026-11-01T11:00:00Z' }],
};

describe('FollowUpsList', () => {
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  function create() {
    const fixture = TestBed.createComponent(FollowUpsList);
    fixture.componentRef.setInput('ticketId', 8);
    TestBed.tick();
    return fixture;
  }

  async function settle(fixture: ReturnType<typeof create>) {
    await new Promise((resolve) => setTimeout(resolve));
    await fixture.whenStable();
    return fixture.nativeElement as HTMLElement;
  }

  it('shows loading, then tasks and the proposals still pending', async () => {
    const fixture = create();
    fixture.detectChanges();
    expect((fixture.nativeElement as HTMLElement).querySelector('.loading')?.textContent).toContain('Loading follow-ups');

    http.expectOne('/api/tickets/8/follow-ups').flush(list);
    const el = await settle(fixture);

    const tasks = el.querySelectorAll('.tasks li');
    expect(tasks).toHaveLength(1);
    expect(tasks[0].textContent).toContain('Open a carrier trace');
    expect(tasks[0].textContent).toContain('Nov 3, 2026');
    expect(tasks[0].textContent).toContain('Three weeks late.');
    const cards = el.querySelectorAll('app-proposal-card');
    expect(cards).toHaveLength(1);
    expect(cards[0].textContent).toContain('Call the customer back');
    expect(el.querySelector('.empty')).toBeNull();
  });

  it('says when there are no follow-ups', async () => {
    const fixture = create();
    http.expectOne('/api/tickets/8/follow-ups').flush({ proposals: [], tasks: [] });
    const el = await settle(fixture);

    expect(el.querySelector('.empty')?.textContent).toContain('No follow-ups yet');
  });

  it('shows an error with a retry', async () => {
    const fixture = create();
    http.expectOne('/api/tickets/8/follow-ups').flush(null, { status: 503, statusText: 'Service Unavailable' });
    const el = await settle(fixture);

    expect(el.querySelector('[role=alert]')?.textContent).toContain("Couldn't load the follow-ups");
    el.querySelector<HTMLButtonElement>('[role=alert] button')!.click();
    TestBed.tick();
    http.expectOne('/api/tickets/8/follow-ups').flush({ proposals: [], tasks: [] });
    expect((await settle(fixture)).querySelector('.empty')).not.toBeNull();
  });

  it('confirming a pending proposal here reloads the list, which then shows the task', async () => {
    const fixture = create();
    http.expectOne('/api/tickets/8/follow-ups').flush(list);
    let el = await settle(fixture);

    el.querySelector<HTMLButtonElement>('app-proposal-card .confirm')!.click();
    http.expectOne('/api/follow-ups/p2/confirm').flush({ id: 2, proposalId: 'p2', title: 'Call the customer back', notes: '', dueDate: null, createdAt: '2026-11-01T13:00:00Z' });
    // Not settle: the list isn't stable until the reload is answered
    await new Promise((resolve) => setTimeout(resolve));
    TestBed.tick();

    http.expectOne('/api/tickets/8/follow-ups').flush({
      proposals: list.proposals.map((p) => (p.id === 'p2' ? { ...p, status: 'Confirmed' } : p)),
      tasks: [{ id: 2, proposalId: 'p2', title: 'Call the customer back', notes: '', dueDate: null, createdAt: '2026-11-01T13:00:00Z' }, ...list.tasks],
    });
    el = await settle(fixture);

    expect(el.querySelectorAll('.tasks li')).toHaveLength(2);
    expect(el.querySelector('app-proposal-card')).toBeNull();
  });
});
