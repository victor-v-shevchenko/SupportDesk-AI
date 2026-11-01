import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { ArticleDetailPage } from './article-detail-page';

describe('ArticleDetailPage', () => {
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [provideRouter([]), provideHttpClient(), provideHttpClientTesting()] });
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  function create(slug = 'shipping-times') {
    const fixture = TestBed.createComponent(ArticleDetailPage);
    fixture.componentRef.setInput('slug', slug);
    TestBed.tick();
    return fixture;
  }

  it('shows loading, then the title and body', async () => {
    const fixture = create();
    expect((fixture.nativeElement as HTMLElement).querySelector('.loading')?.textContent).toContain('Loading article');

    http.expectOne('/api/articles/shipping-times').flush({ slug: 'shipping-times', title: 'Shipping times', updatedAt: '2026-10-01T00:00:00Z', bodyMarkdown: '## Standard\n3 to 5 days.' });
    await fixture.whenStable();
    const el = fixture.nativeElement as HTMLElement;

    expect(el.querySelector('h1')?.textContent).toBe('Shipping times');
    expect(el.querySelector('.body')?.textContent).toBe('## Standard\n3 to 5 days.');
  });

  it('says when an article is empty', async () => {
    const fixture = create();
    http.expectOne('/api/articles/shipping-times').flush({ slug: 'shipping-times', title: 'Shipping times', updatedAt: '2026-10-01T00:00:00Z', bodyMarkdown: ' ' });
    await fixture.whenStable();

    expect((fixture.nativeElement as HTMLElement).querySelector('.empty')?.textContent).toContain('This article is empty');
  });

  it('says not found on a 404 and offers a retry for other errors', async () => {
    const missing = create('nope');
    http.expectOne('/api/articles/nope').flush(null, { status: 404, statusText: 'Not Found' });
    await missing.whenStable();
    expect((missing.nativeElement as HTMLElement).querySelector('[role=alert]')?.textContent).toContain('Article not found');

    const failing = create('broken');
    http.expectOne('/api/articles/broken').flush(null, { status: 500, statusText: 'Server Error' });
    await failing.whenStable();
    expect((failing.nativeElement as HTMLElement).querySelector('[role=alert] button')?.textContent).toContain('Try again');
  });
});
