import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { FollowUpTask, ProposalStatus } from '../api/follow-ups.models';
import { ProposalPayload } from '../api/replies.models';
import { ProposalCard } from './proposal-card';

const proposal: ProposalPayload = { id: 'p1', title: 'Open a carrier trace', notes: 'No delivery after three weeks.', dueDate: '2026-11-03', status: 'Pending' };
const task: FollowUpTask = { id: 4, proposalId: 'p1', title: proposal.title, notes: proposal.notes, dueDate: proposal.dueDate, createdAt: '2026-11-01T12:00:00Z' };

describe('ProposalCard', () => {
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  function create(value: ProposalPayload = proposal, status?: ProposalStatus) {
    const fixture = TestBed.createComponent(ProposalCard);
    fixture.componentRef.setInput('proposal', value);
    if (status) {
      fixture.componentRef.setInput('status', status);
    }

    fixture.detectChanges();
    const confirmed: FollowUpTask[] = [];
    fixture.componentInstance.confirmed.subscribe((t) => confirmed.push(t));
    return { fixture, el: fixture.nativeElement as HTMLElement, confirmed };
  }

  async function settle(fixture: ReturnType<typeof create>['fixture']) {
    await new Promise((resolve) => setTimeout(resolve));
    await fixture.whenStable();
    fixture.detectChanges();
  }

  it('shows the title, notes and due date with a Confirm button', () => {
    const { el } = create();

    expect(el.querySelector('.title')?.textContent).toBe('Open a carrier trace');
    expect(el.querySelector('.notes')?.textContent).toBe('No delivery after three weeks.');
    expect(el.querySelector('.due')?.textContent).toContain('Nov 3, 2026');
    expect(el.querySelector('.confirm')?.textContent?.trim()).toBe('Confirm');
  });

  it('says when there is no due date and leaves out empty notes', () => {
    const { el } = create({ ...proposal, notes: '', dueDate: null });

    expect(el.querySelector('.due')?.textContent).toContain('No due date');
    expect(el.querySelector('.notes')).toBeNull();
  });

  it('confirms with one request while the button is disabled, then shows the task was created', async () => {
    const { fixture, el, confirmed } = create();
    const button = el.querySelector<HTMLButtonElement>('.confirm')!;

    button.click();
    button.click();
    fixture.detectChanges();

    expect(button.disabled).toBe(true);
    expect(button.textContent?.trim()).toBe('Confirming…');
    const request = http.expectOne('/api/follow-ups/p1/confirm');
    expect(request.request.method).toBe('POST');
    request.flush(task);
    await settle(fixture);

    expect(el.querySelector('.confirmed')?.textContent).toContain('Task created');
    expect(el.querySelector('.confirm')).toBeNull();
    expect(confirmed).toEqual([task]);
  });

  it('shows an expired proposal without a Confirm button after a 409', async () => {
    const { fixture, el, confirmed } = create();

    el.querySelector<HTMLButtonElement>('.confirm')!.click();
    http.expectOne('/api/follow-ups/p1/confirm').flush({ status: 409, title: 'Proposal expired' }, { status: 409, statusText: 'Conflict' });
    await settle(fixture);

    expect(el.querySelector('.expired')?.textContent).toContain('expired');
    expect(el.querySelector('.confirm')).toBeNull();
    expect(confirmed).toEqual([]);
  });

  it('keeps the button after another failure so the user can try again', async () => {
    const { fixture, el } = create();

    el.querySelector<HTMLButtonElement>('.confirm')!.click();
    http.expectOne('/api/follow-ups/p1/confirm').flush(null, { status: 503, statusText: 'Service Unavailable' });
    await settle(fixture);

    expect(el.querySelector('[role=alert]')?.textContent).toContain("Couldn't confirm the task");
    expect(el.querySelector<HTMLButtonElement>('.confirm')!.disabled).toBe(false);
  });

  it('shows the status the server reported for a proposal that is no longer pending', () => {
    expect(create(proposal, 'Confirmed').el.querySelector('.confirmed')).not.toBeNull();
    expect(create(proposal, 'Expired').el.querySelector('.expired')).not.toBeNull();
  });
});
