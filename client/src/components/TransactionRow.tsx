import { memo } from 'react';
import { StatusBadge } from './StatusBadge';
import type { Transaction } from '../types/transaction';

interface TransactionRowProps {
  readonly transaction: Transaction;
}

const AMOUNT_FORMAT = new Intl.NumberFormat('en-US', {
  minimumFractionDigits: 2,
  maximumFractionDigits: 4,
});

const TIME_FORMAT = new Intl.DateTimeFormat('en-GB', {
  hour: '2-digit',
  minute: '2-digit',
  second: '2-digit',
  fractionalSecondDigits: 3,
  timeZone: 'UTC',
});

function TransactionRowComponent({ transaction }: TransactionRowProps): JSX.Element {
  const occurredAt = new Date(transaction.timestamp);

  return (
    <tr className="transaction-row">
      <td className="transaction-row__id" title={transaction.transactionId}>
        {transaction.transactionId.slice(0, 8)}
      </td>
      <td className="transaction-row__amount">
        {AMOUNT_FORMAT.format(transaction.amount)}
        <span className="transaction-row__currency">{transaction.currency}</span>
      </td>
      <td>
        <StatusBadge status={transaction.status} />
      </td>
      <td className="transaction-row__time">
        <time dateTime={transaction.timestamp}>{TIME_FORMAT.format(occurredAt)}</time>
      </td>
    </tr>
  );
}

/**
 * Memoised and keyed by id in the parent. One arriving transaction re-renders one
 * row; the other 199 are reference-equal and skipped. Without this, every flush
 * re-renders the whole table, and the "send 100 rapid" case is where that becomes
 * visible as dropped input (S5.2).
 */
export const TransactionRow = memo(TransactionRowComponent);
