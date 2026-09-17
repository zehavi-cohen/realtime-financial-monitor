import { TransactionRow } from './TransactionRow';
import type { Transaction } from '../types/transaction';

interface TransactionTableProps {
  readonly transactions: readonly Transaction[];
  readonly totalCount: number;
}

/**
 * Pure presentation: it receives the already-sorted, already-filtered, already-
 * bounded array and renders it. No hooks, no fetching, no SignalR (S5.2).
 */
export function TransactionTable({ transactions, totalCount }: TransactionTableProps): JSX.Element {
  if (transactions.length === 0) {
    return (
      <p className="empty-state" role="status">
        {totalCount === 0
          ? 'No transactions yet. Open the simulator in another tab and send some.'
          : 'No transactions match the current filter.'}
      </p>
    );
  }

  return (
    <table className="transaction-table">
      <caption className="visually-hidden">
        Transactions, newest first. Showing {transactions.length} of {totalCount}.
      </caption>
      <thead>
        <tr>
          <th scope="col">ID</th>
          <th scope="col">Amount</th>
          <th scope="col">Status</th>
          <th scope="col">Occurred (UTC)</th>
        </tr>
      </thead>
      <tbody>
        {transactions.map((transaction) => (
          // Keyed by transactionId, never by index: a keyed-by-index list reuses a
          // row element for a different transaction when the head of the list
          // changes, which is every single flush here.
          <TransactionRow key={transaction.transactionId} transaction={transaction} />
        ))}
      </tbody>
    </table>
  );
}
