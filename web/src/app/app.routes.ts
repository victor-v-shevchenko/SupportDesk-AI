import { Routes } from '@angular/router';

export const routes: Routes = [
  { path: '', pathMatch: 'full', redirectTo: 'tickets' },
  { path: 'tickets', title: 'Tickets · SupportDesk AI', loadComponent: () => import('./tickets/tickets-page').then((m) => m.TicketsPage) },
  {
    path: 'tickets/:id',
    title: 'Ticket · SupportDesk AI',
    loadComponent: () => import('./tickets/ticket-detail-page').then((m) => m.TicketDetailPage),
  },
  {
    path: 'articles',
    title: 'Knowledge base · SupportDesk AI',
    loadComponent: () => import('./articles/articles-page').then((m) => m.ArticlesPage),
  },
  { path: '**', redirectTo: 'tickets' },
];
