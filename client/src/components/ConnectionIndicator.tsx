import type { ConnectionState } from '../hooks/useTransactionStream';

interface ConnectionIndicatorProps {
  readonly state: ConnectionState;
  readonly error: string | null;
}

const LABELS: Readonly<Record<ConnectionState, string>> = {
  connecting: 'Connecting',
  connected: 'Connected',
  reconnecting: 'Reconnecting',
  disconnected: 'Disconnected',
};

/**
 * An agent needs to know whether an empty screen means "nothing is happening" or
 * "you are not receiving anything". aria-live announces the change, because the
 * one moment this matters is the one where nobody is looking at the corner of the
 * screen.
 */
export function ConnectionIndicator({ state, error }: ConnectionIndicatorProps): JSX.Element {
  return (
    <div className={`connection connection--${state}`} role="status" aria-live="polite">
      <span aria-hidden="true" className="connection__dot" />
      <span className="connection__label">{LABELS[state]}</span>
      {error !== null && state !== 'connected' ? <span className="connection__error">{error}</span> : null}
    </div>
  );
}
