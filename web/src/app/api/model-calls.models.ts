/** One row of `GET /api/model-calls` (PLAN.md 4 and 6.6); only the fields the UI reads. */
export interface ModelCallRow {
  id: number;
  feature: 'Reply' | 'Summary' | 'Classification';
  inputTokens: number;
  outputTokens: number;
  /** Null when the model has no known price. */
  costUsd: number | null;
  isEstimated: boolean;
}

/** What the ticket page shows: the rows added up. */
export interface TicketCostTotals {
  calls: number;
  inputTokens: number;
  outputTokens: number;
  /** The known costs added up. */
  costUsd: number;
  /** Some row has no price, so `costUsd` is a lower bound. */
  costIncomplete: boolean;
  estimated: boolean;
}

export function totalCalls(rows: readonly ModelCallRow[]): TicketCostTotals {
  return rows.reduce<TicketCostTotals>(
    (total, row) => ({
      calls: total.calls + 1,
      inputTokens: total.inputTokens + row.inputTokens,
      outputTokens: total.outputTokens + row.outputTokens,
      costUsd: total.costUsd + (row.costUsd ?? 0),
      costIncomplete: total.costIncomplete || row.costUsd === null,
      estimated: total.estimated || row.isEstimated,
    }),
    { calls: 0, inputTokens: 0, outputTokens: 0, costUsd: 0, costIncomplete: false, estimated: false },
  );
}
