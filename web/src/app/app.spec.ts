import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { App } from './app';
import { routes } from './app.routes';

describe('App shell', () => {
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      imports: [App],
      providers: [provideRouter(routes), provideHttpClient(), provideHttpClientTesting()],
    });
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  /** Renders the shell and answers its health request; the fixture isn't stable until that request completes. */
  async function render(respond: (request: ReturnType<HttpTestingController['expectOne']>) => void) {
    const fixture = TestBed.createComponent(App);
    TestBed.tick();
    respond(http.expectOne('/api/health'));
    await fixture.whenStable();
    return fixture.nativeElement as HTMLElement;
  }

  const healthy = (request: ReturnType<HttpTestingController['expectOne']>) =>
    request.flush({ status: 'ok', database: 'ok' });

  it('renders the header with the Tickets link', async () => {
    const el = await render(healthy);

    expect(el.querySelector('header .brand')?.textContent).toContain('SupportDesk AI');
    expect(el.querySelector('nav a')?.textContent).toContain('Tickets');
  });

  it('shows checking while the health request is pending', () => {
    const fixture = TestBed.createComponent(App);
    TestBed.tick();

    const status = (fixture.nativeElement as HTMLElement).querySelector('[role=status]');
    expect(status?.getAttribute('data-state')).toBe('loading');
    http.expectOne('/api/health').flush({ status: 'ok', database: 'ok' });
  });

  it('shows the API as ok when the health check passes', async () => {
    const el = await render(healthy);

    const status = el.querySelector('[role=status]');
    expect(status?.getAttribute('data-state')).toBe('ok');
    expect(status?.textContent).toContain('API ok');
  });

  it('shows the API as unavailable when the health check returns 503', async () => {
    const el = await render((request) =>
      request.flush({ status: 'degraded', database: 'unavailable' }, { status: 503, statusText: 'Service Unavailable' }),
    );

    const status = el.querySelector('[role=status]');
    expect(status?.getAttribute('data-state')).toBe('error');
    expect(status?.textContent).toContain('API unavailable');
  });
});
