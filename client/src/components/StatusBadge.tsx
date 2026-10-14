import { memo } from 'react';
import type { TransactionStatus } from '../types/transaction';

/**
 * Status, with a shape and a word as well as a colour.
 *
 * Colour alone fails for roughly one in twelve men, and fails entirely in a
 * monochrome screenshot pasted into a ticket - which is exactly how a support
 * agent escalates one of these (S5.2).
 */
const GLYPHS: Readonly<Record<TransactionStatus, string>> = {
  Pending: '◷',
  Completed: '✓',
  Failed: '✕',
};

interface StatusBadgeProps {
  readonly status: TransactionStatus;
}

function StatusBadgeComponent({ status }: StatusBadgeProps): JSX.Element {
  return (
    // Keyed on the status value: React remounts the element when the status
    // changes, which re-runs the CSS mount animation. That is the whole
    // status-change animation - no library, no timers, no state (S5.2).
    <span key={status} className={`status-badge status-badge--${status.toLowerCase()}`}>
      <span aria-hidden="true" className="status-badge__glyph">
        {GLYPHS[status]}
      </span>
      {status}
    </span>
  );
}

export const StatusBadge = memo(StatusBadgeComponent);
