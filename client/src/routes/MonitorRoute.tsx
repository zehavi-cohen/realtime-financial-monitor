import { useMemo, useState } from 'react';
import { ConnectionIndicator } from '../components/ConnectionIndicator';
import { TransactionTable } from '../components/TransactionTable';
import { useTransactionStream } from '../hooks/useTransactionStream';
import type { Transaction } from '../types/transaction';

/** How many rows reach the DOM. The window holds 500; an agent reads the newest few hundred. */
const MAX_RENDERED_ROWS = 200;

type Filter = 'all' | 'failed';

/**
 * The dashboard. It opens already populated from the connection snapshot and
 * updates from the same stream thereafter (S5.2).
 */
export function MonitorRoute(): JSX.Element {
  const { transactions, connectionState, error } = useTransactionStream();
  const [filter, setFilter] = useState<Filter>('all');

  /**
   * One selector: sort, filter, bound. Recomputed only when the map or the filter
   * changes, which with the buffered flush is at most ten times a second however
   * many messages arrive.
   *
   * The filter runs here, over data already in memory. It is never a server call -
   * a request per keystroke on a dashboard that already holds the answer is how a
   * filter control turns into a load test (S5.2).
   */
  const visible = useMemo<readonly Transaction[]>(() => {
    const all = Array.from(transactions.values());

    const filtered = filter === 'failed' ? all.filter((transaction) => transaction.status === 'Failed') : all;

    return filtered
      .sort((left, right) => Date.parse(right.timestamp) - Date.parse(left.timestamp))
      .slice(0, MAX_RENDERED_ROWS);
  }, [transactions, filter]);

  const failedCount = useMemo(
    () => Array.from(transactions.values()).filter((transaction) => transaction.status === 'Failed').length,
    [transactions],
  );

  return (
    <section className="monitor">
      <header className="monitor__header">
        <div>
          <h1>Live transactions</h1>
          <p className="monitor__summary">
            {transactions.size} in window &middot; {failedCount} failed &middot; showing {visible.length}
          </p>
        </div>
        <ConnectionIndicator state={connectionState} error={error} />
      </header>

      <div className="monitor__controls">
        <fieldset className="filter">
          <legend className="visually-hidden">Filter transactions</legend>
          <label>
            <input
              type="radio"
              name="filter"
              value="all"
              checked={filter === 'all'}
              onChange={() => setFilter('all')}
            />
            All
          </label>
          <label>
            <input
              type="radio"
              name="filter"
              value="failed"
              checked={filter === 'failed'}
              onChange={() => setFilter('failed')}
            />
            Failed only
          </label>
        </fieldset>
      </div>

      <TransactionTable transactions={visible} totalCount={transactions.size} />
    </section>
  );
}
