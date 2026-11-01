import { DecimalPipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, computed, DestroyRef, inject, input, output, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { Citation, ReplyRequestError, ReplyStreamEndedError, ReplyUsage, ToolCall } from '../api/replies.models';
import { RepliesService } from '../api/replies.service';
import { citedArticles, segmentReply } from './citations';
import { OrderStatusCard } from './order-status-card';
import { ProposalCard } from './proposal-card';

type ReplyStatus = 'idle' | 'streaming' | 'done' | 'stopped' | 'error';

/** What the panel says for each `error` code (PLAN.md 7); an unknown code shows the server's message. */
export const replyErrorMessages: Record<string, string> = {
  model_unavailable: 'The model is unavailable right now. Try again in a moment.',
  timeout: 'The model took too long to answer. Try again.',
  budget_exceeded: 'This ticket is too long for a suggested reply.',
  model_refused: 'The model declined to write a reply for this ticket.',
  internal_error: 'Something went wrong while writing the reply. Try again.',
};

/**
 * "Suggest reply" on the ticket page: streams the reply as it's written, with a stop button, then links its citations
 * and shows the cost. One reply at a time: a second click aborts the first request, and so does leaving the page.
 * Tools the model called show as cards above the text: an order's status, and a proposed follow-up to confirm.
 */
@Component({
  selector: 'app-reply-panel',
  imports: [RouterLink, DecimalPipe, OrderStatusCard, ProposalCard],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="actions">
      <button type="button" class="suggest" (click)="suggest()">{{ status() === 'idle' ? 'Suggest reply' : 'Suggest again' }}</button>
      @if (status() === 'streaming') {
        <button type="button" class="stop" (click)="stop()">Stop</button>
      }
    </div>

    @if (status() === 'idle') {
      <p class="empty">No suggested reply yet.</p>
    } @else {
      @if (toolCalls().length > 0) {
        <div class="tool-calls">
          @for (call of toolCalls(); track toolCallKey($index, call)) {
            @if (call.name === 'lookup_order_status') {
              <app-order-status-card [orderNumber]="call.arguments.orderNumber" [result]="call.result" />
            } @else {
              <app-proposal-card [proposal]="call.proposal" (confirmed)="followUpsChanged.emit()" />
            }
          }
        </div>
      }

      @if (text()) {
        <!-- Each piece is a span with nothing between them, so the template adds no whitespace to the reply's own -->
        <p class="reply-text" [attr.aria-busy]="status() === 'streaming'">
          @for (segment of segments(); track $index) {
            @if (segment.kind === 'text') {
              <span>{{ segment.text }}</span>
            } @else {
              <span class="cite"
                ><span>[</span
                >@for (n of segment.numbers; track $index) {
                  @if (!$first) {
                    <span>, </span>
                  }
                  @if (n.citation; as c) {
                    <a [routerLink]="['/articles', c.articleSlug]" [title]="c.articleTitle">{{ n.marker }}</a>
                  } @else {
                    <span>{{ n.marker }}</span>
                  }
                }<span>]</span></span
              >
            }
          }
        </p>
      } @else if (status() === 'streaming') {
        <p class="loading">Writing a reply…</p>
      } @else if (status() === 'done') {
        <p class="empty">The model returned an empty reply.</p>
      }

      @if (status() === 'stopped') {
        <p class="note">Stopped.</p>
      }

      @if (status() === 'error') {
        <p class="error" role="alert">{{ errorMessage() }}</p>
      }

      @if (articles().length > 0) {
        <h3>Sources</h3>
        <ol class="citations">
          @for (article of articles(); track article.slug) {
            <li>
              <span class="markers">[{{ article.markers.join(', ') }}]</span>
              <a [routerLink]="['/articles', article.slug]">{{ article.title }}</a>
            </li>
          }
        </ol>
      } @else if (status() === 'done' && citations()) {
        <p class="empty no-citations">No sources cited.</p>
      }

      @if (usage(); as u) {
        <p class="usage">
          {{ u.inputTokens | number }} input + {{ u.outputTokens | number }} output tokens{{ u.estimated ? ' (estimated)' : '' }} ·
          {{ u.costUsd === null ? 'cost unknown' : '$' + (u.costUsd | number: '1.4-6') }}
        </p>
      }
    }
  `,
  styles: `
    .actions { display: flex; gap: 0.5rem; margin-bottom: 0.75rem; }
    .tool-calls { display: grid; gap: 0.5rem; margin-bottom: 0.75rem; max-width: 70ch; }
    button { padding: 0.4rem 0.75rem; font: inherit; }
    .reply-text { white-space: pre-wrap; line-height: 1.5; max-width: 70ch; }
    .cite a { color: #1d4ed8; }
    h3 { font-size: 0.875rem; margin: 1rem 0 0.25rem; }
    .citations { padding-left: 1.25rem; margin: 0; }
    .citations a { color: #1d4ed8; }
    .markers { color: #6b7280; margin-right: 0.35rem; font-variant-numeric: tabular-nums; }
    .usage { color: #6b7280; font-size: 0.85rem; }
    .loading, .empty, .note { color: #6b7280; }
    .error { color: #9b1c1c; }
  `,
})
export class ReplyPanel {
  readonly ticketId = input.required<number>();
  /** A reply proposed a follow-up, or one was confirmed here, so the page's follow-ups list is out of date. */
  readonly followUpsChanged = output<void>();

  private readonly replies = inject(RepliesService);
  private controller: AbortController | undefined;

  protected readonly status = signal<ReplyStatus>('idle');
  protected readonly text = signal('');
  protected readonly toolCalls = signal<ToolCall[]>([]);
  protected readonly citations = signal<Citation[] | undefined>(undefined);
  protected readonly usage = signal<ReplyUsage | undefined>(undefined);
  protected readonly errorMessage = signal('');

  /** Plain text while streaming; markers become links once the checked citations arrive. */
  protected readonly segments = computed(() => segmentReply(this.text(), this.citations() ?? []));
  protected readonly articles = computed(() => citedArticles(this.citations() ?? []));

  constructor() {
    inject(DestroyRef).onDestroy(() => this.controller?.abort());
  }

  /** Starts a reply, aborting the one in progress first. */
  async suggest(): Promise<void> {
    this.controller?.abort();
    const controller = new AbortController();
    this.controller = controller;
    this.status.set('streaming');
    this.text.set('');
    this.toolCalls.set([]);
    this.citations.set(undefined);
    this.usage.set(undefined);
    this.errorMessage.set('');

    try {
      for await (const event of this.replies.stream(this.ticketId(), controller.signal)) {
        if (controller !== this.controller) {
          return;
        }

        switch (event.type) {
          case 'token':
            this.text.update((t) => t + event.text);
            break;
          case 'tool_call':
            this.addToolCall(event);
            break;
          case 'citations':
            this.citations.set(event.citations);
            break;
          case 'usage':
            this.usage.set({ inputTokens: event.inputTokens, outputTokens: event.outputTokens, costUsd: event.costUsd, estimated: event.estimated });
            break;
          case 'error':
            this.fail(replyErrorMessages[event.code] ?? event.message);
            break;
          case 'done':
            this.status.set('done');
            break;
        }
      }
    } catch (error) {
      // An aborted request (stopped, replaced or left) isn't an error
      if (controller === this.controller && !controller.signal.aborted) {
        this.fail(describe(error));
      }
    }
  }

  /** Aborts the reply in progress and keeps the text so far. */
  stop(): void {
    if (this.status() === 'streaming') {
      this.controller?.abort();
      this.status.set('stopped');
    }
  }

  /** One card per proposal: a repeated proposal in the same run updates the one the server saved (same ID). */
  protected toolCallKey(index: number, call: ToolCall): string | number {
    return call.name === 'propose_follow_up_task' ? call.proposal.id : index;
  }

  private addToolCall(call: ToolCall): void {
    if (call.name === 'propose_follow_up_task') {
      const id = call.proposal.id;
      this.toolCalls.update((calls) =>
        calls.some((c) => c.name === 'propose_follow_up_task' && c.proposal.id === id)
          ? calls.map((c) => (c.name === 'propose_follow_up_task' && c.proposal.id === id ? call : c))
          : [...calls, call],
      );
      this.followUpsChanged.emit();
    } else {
      this.toolCalls.update((calls) => [...calls, call]);
    }
  }

  private fail(message: string): void {
    this.errorMessage.set(message);
    this.status.set('error');
  }
}

function describe(error: unknown): string {
  if (error instanceof ReplyRequestError) {
    return error.status === 404 ? 'This ticket no longer exists.' : `Couldn't start the reply (status ${error.status}).`;
  }

  if (error instanceof ReplyStreamEndedError) {
    return 'The connection dropped before the reply finished. Try again.';
  }

  return "Couldn't reach the server. Try again.";
}
