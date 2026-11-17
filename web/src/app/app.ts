import { ChangeDetectionStrategy, Component } from '@angular/core';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { ApiStatus } from './api-status';

@Component({
  selector: 'app-root',
  imports: [RouterOutlet, RouterLink, RouterLinkActive, ApiStatus],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <header class="header">
      <a class="brand" routerLink="/">SupportDesk AI</a>
      <nav aria-label="Main">
        <a routerLink="/tickets" routerLinkActive="active">Tickets</a>
        <a routerLink="/articles" routerLinkActive="active">Knowledge base</a>
      </nav>
      <app-api-status />
    </header>
    <main class="content">
      <router-outlet />
    </main>
  `,
  styles: `
    /* At least --header-height tall, and exactly that from 900 px, where it is one line, so a page can size itself to the window below it */
    .header { display: flex; flex-wrap: wrap; align-items: center; gap: 0.5rem 1.5rem; box-sizing: border-box; min-height: var(--header-height); padding: 0.75rem 1.5rem; border-bottom: 1px solid var(--border); }
    @media (min-width: 900px) { .header { height: var(--header-height); } }
    .brand { font-weight: 600; color: var(--fg); text-decoration: none; white-space: nowrap; }
    nav { flex: 1; display: flex; gap: 1rem; white-space: nowrap; }
    nav a { color: var(--muted); text-decoration: none; }
    nav a.active { color: var(--fg); font-weight: 600; }
    .content { padding: var(--content-padding); }
  `,
})
export class App {}
