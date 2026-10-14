/**
 * The transaction contract, declared once for the whole client (S10).
 *
 * This is the shape the API returns and the shape the hub pushes; they are the
 * same DTO server-side, and keeping one type here is what makes a snapshot entry
 * and a streamed entry interchangeable in the dashboard's state.
 */

/** Exactly the three values the server accepts. "Success" is not one of them. */
export const TRANSACTION_STATUSES = ['Pending', 'Completed', 'Failed'] as const;

export type TransactionStatus = (typeof TRANSACTION_STATUSES)[number];

export interface Transaction {
  readonly transactionId: string;
  readonly amount: number;
  readonly currency: string;
  readonly status: TransactionStatus;
  /** ISO-8601 instant asserted by the producer. */
  readonly timestamp: string;
}

/** What the producer sends. The server owns identity and never invents it. */
export interface CreateTransactionRequest {
  readonly transactionId: string;
  readonly amount: number;
  readonly currency: string;
  readonly status: TransactionStatus;
  readonly timestamp: string;
}

/** The server's answer: what it actually did with the assertion. */
export type IngestResultName = 'Created' | 'Updated' | 'Ignored';

export interface IngestionResponse {
  readonly result: IngestResultName;
  readonly transactionId: string;
  /** Present only when the server actually wrote something: Created or Updated. */
  readonly transaction?: Transaction;
}
