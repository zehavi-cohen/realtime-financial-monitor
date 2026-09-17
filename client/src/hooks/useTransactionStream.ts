import { useEffect, useRef, useState } from 'react';
import { HUB_METHODS, createTransactionsConnection } from '../api/transactionsHub';
import type { Transaction } from '../types/transaction';

export type ConnectionState = 'connecting' | 'connected' | 'reconnecting' | 'disconnected';

export interface TransactionStream {
  /**
   * Keyed by transactionId. A repeated transaction overwrites itself, which makes
   * snapshot/stream overlap, status transitions, reconnect re-snapshots and any
   * duplicate delivery harmless (S5.2). This is the client half of the
   * subscribe-before-snapshot decision, and the reason the server needs no
   * deduplication at all.
   */
  readonly transactions: ReadonlyMap<string, Transaction>;
  readonly connectionState: ConnectionState;
  readonly error: string | null;
}

/**
 * How long inbound messages accumulate before being flushed into React state.
 *
 * A burst of 100 socket messages is 100 separate events, so React's automatic
 * batching does not apply to them - without this, 100 messages means 100 renders
 * of a 200-row table and the page stops responding to input. At 100ms the flush is
 * still below the threshold where a human perceives delay.
 */
const FLUSH_INTERVAL_MS = 100;

/**
 * The client keeps at most this many transactions, mirroring the server's hot
 * window size.
 *
 * The Map is a union of everything this connection has ever seen and nothing
 * removes from it, which is exactly what makes duplicate delivery free — but over
 * a shift-long session it would grow without bound, and a transaction that left
 * the server's window hours ago is not something an agent is looking at.
 */
const MAX_TRACKED = 500;

/** Trim only once there is enough excess to be worth a sort. */
const TRIM_THRESHOLD = Math.floor(MAX_TRACKED * 1.5);

/**
 * Owns the connection and the inbound buffer. All of the real-time machinery is
 * here; everything that renders receives plain data as props (S5.2).
 */
export function useTransactionStream(): TransactionStream {
  const [transactions, setTransactions] = useState<ReadonlyMap<string, Transaction>>(() => new Map());
  const [connectionState, setConnectionState] = useState<ConnectionState>('connecting');
  const [error, setError] = useState<string | null>(null);

  /**
   * The buffer is a ref, not state, and that is the point: mutating it does not
   * schedule a render, so an arriving message costs an array push and nothing else.
   */
  const buffer = useRef<Transaction[]>([]);

  useEffect(() => {
    const connection = createTransactionsConnection();
    let disposed = false;

    connection.on(HUB_METHODS.snapshot, (snapshot: readonly Transaction[]) => {
      // The snapshot goes through the same buffer as the stream. It has to: the
      // server subscribes this connection before sending the snapshot, so streamed
      // transactions can arrive first, and the Map's last-write-wins on a key would
      // otherwise let a stale snapshot entry overwrite a newer streamed one.
      buffer.current.push(...snapshot);
    });

    connection.on(HUB_METHODS.transaction, (transaction: Transaction) => {
      buffer.current.push(transaction);
    });

    connection.onreconnecting((cause) => {
      setConnectionState('reconnecting');
      setError(cause?.message ?? 'The connection was lost.');
    });

    connection.onreconnected(() => {
      setConnectionState('connected');
      setError(null);
    });

    connection.onclose((cause) => {
      setConnectionState('disconnected');
      setError(cause?.message ?? 'The connection is closed.');
    });

    const flush = window.setInterval(() => {
      const pending = buffer.current;
      if (pending.length === 0) {
        return;
      }

      buffer.current = [];

      setTransactions((previous) => {
        const next = new Map(previous);
        for (const transaction of pending) {
          next.set(transaction.transactionId, transaction);
        }
        return next.size > TRIM_THRESHOLD ? trimToNewest(next) : next;
      });
    }, FLUSH_INTERVAL_MS);

    void connection
      .start()
      .then(() => {
        if (!disposed) {
          setConnectionState('connected');
          setError(null);
        }
      })
      .catch((cause: unknown) => {
        if (!disposed) {
          setConnectionState('disconnected');
          setError(cause instanceof Error ? cause.message : 'Could not connect to the transaction stream.');
        }
      });

    return () => {
      disposed = true;
      window.clearInterval(flush);
      void connection.stop();
    };
  }, []);

  return { transactions, connectionState, error };
}

/**
 * Drops the oldest entries by the producer's timestamp, the same ordering the
 * server's window evicts by — so the client discards what the server has already
 * discarded, rather than something it is still serving.
 */
function trimToNewest(transactions: Map<string, Transaction>): Map<string, Transaction> {
  const newest = Array.from(transactions.values())
    .sort((left, right) => Date.parse(right.timestamp) - Date.parse(left.timestamp))
    .slice(0, MAX_TRACKED);

  return new Map(newest.map((transaction) => [transaction.transactionId, transaction]));
}
