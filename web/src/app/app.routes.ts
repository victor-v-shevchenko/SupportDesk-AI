import { Routes } from '@angular/router';

export const routes: Routes = [
  { path: '', pathMatch: 'full', redirectTo: 'tickets' },
  { path: 'tickets', title: 'Tickets · SupportDesk AI', loadComponent: () => import('./tickets/tickets-page').then((m) => m.TicketsPage) },
  { path: '**', redirectTo: 'tickets' },
];
