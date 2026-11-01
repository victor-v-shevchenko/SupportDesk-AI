import { TestBed } from '@angular/core/testing';
import { Category, ClassificationStatus, Priority } from '../api/tickets.models';
import { ClassificationBadges } from './classification-badges';

describe('ClassificationBadges', () => {
  function render(status: ClassificationStatus, category: Category | null = null, priority: Priority | null = null, reason: string | null = null) {
    const fixture = TestBed.createComponent(ClassificationBadges);
    fixture.componentRef.setInput('status', status);
    fixture.componentRef.setInput('category', category);
    fixture.componentRef.setInput('priority', priority);
    fixture.componentRef.setInput('reason', reason);
    fixture.detectChanges();
    return fixture.nativeElement as HTMLElement;
  }

  it('shows the category and priority with the reason as a tooltip when done', () => {
    const el = render('Done', 'Shipping', 'Urgent', 'Lost in transit for three weeks.');

    expect(el.querySelector('.category')?.textContent).toBe('Shipping');
    expect(el.querySelector('.priority')?.textContent).toBe('Urgent');
    expect(el.querySelector('.priority')?.getAttribute('data-priority')).toBe('Urgent');
    expect(el.querySelector('.badges')?.getAttribute('title')).toBe('Lost in transit for three weeks.');
    expect(el.querySelector('.manual')).toBeNull();
  });

  it('shows its own badge when the ticket needs manual classification', () => {
    const el = render('NeedsManual');

    expect(el.querySelector('.manual')?.textContent).toBe('Needs manual classification');
    expect(el.querySelector('.category')).toBeNull();
    expect(el.querySelector('.priority')).toBeNull();
  });

  it('says not classified before the first run', () => {
    const el = render('NotRun');

    expect(el.querySelector('.not-run')?.textContent).toBe('Not classified');
    expect(el.querySelector('.badge')).toBeNull();
  });
});
