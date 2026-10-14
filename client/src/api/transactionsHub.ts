import { HttpTransportType, HubConnection, HubConnectionBuilder, LogLevel } from '@microsoft/signalr';
import type { Transaction } from '../types/transaction';

/**
 * The hub connection, built in one place (S10). Components never touch SignalR.
 */

const HUB_URL = '/hub/transactions';

/** The server-to-client method names, matching ITransactionsClient on the server. */
export const HUB_METHODS = {
  snapshot: 'ReceiveSnapshot',
  transaction: 'ReceiveTransaction',
} as const;

export type SnapshotHandler = (transactions: readonly Transaction[]) => void;
export type TransactionHandler = (transaction: Transaction) => void;

export function createTransactionsConnection(): HubConnection {
  return (
    new HubConnectionBuilder()
      .withUrl(HUB_URL, {
        // Negotiation exists so a client can discover which transports the server
        // supports and get a connection token. Skipping it removes a round trip
        // and, more importantly here, removes the need for sticky sessions across
        // the five replicas: without a negotiate step there is no per-connection
        // state on any one replica to come back to (S7).
        //
        // The cost is that there is no fallback: WebSockets or nothing. Recorded
        // in the README as a deliberate trade-off.
        skipNegotiation: true,
        transport: HttpTransportType.WebSockets,
      })
      // Reconnect attempts are the default backoff schedule (0s, 2s, 10s, 30s).
      // On every reconnect the server sends a fresh snapshot, so the dashboard
      // repairs whatever it missed while disconnected - which is why the client
      // needs no gap detection of its own.
      .withAutomaticReconnect()
      .configureLogging(import.meta.env.DEV ? LogLevel.Information : LogLevel.Warning)
      .build()
  );
}
