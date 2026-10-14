import { useCallback, useRef, useState } from 'react';
import { ApiError, postTransaction } from '../api/transactionsApi';
import { TRANSACTION_STATUSES } from '../types/transaction';
import type { CreateTransactionRequest, TransactionStatus } from '../types/transaction';

/**
 * The simulator: this route stands in for the external producer.
 *
 * It never touches SignalR. A producer posts over HTTP and learns nothing about
 * who is watching; wiring the form to the hub would collapse two roles that the
 * whole architecture keeps apart (S5.1).
 */

const CURRENCIES = ['USD', 'EUR', 'GBP', 'ILS', 'JPY'] as const;

const RAPID_BATCH_SIZE = 100;

/** Matches the server's rule, so the obvious mistakes never leave the browser. */
const CURRENCY_PATTERN = /^[A-Z]{3}$/;

interface FormState {
  amount: string;
  currency: string;
  status: TransactionStatus;
}

type FieldErrors = Partial<Record<keyof FormState, string>>;

interface ActivityEntry {
  readonly key: string;
  readonly kind: 'success' | 'failure';
  readonly message: string;
}

const INITIAL_FORM: FormState = { amount: '1500.50', currency: 'USD', status: 'Pending' };

export function AddRoute(): JSX.Element {
  const [form, setForm] = useState<FormState>(INITIAL_FORM);
  const [fieldErrors, setFieldErrors] = useState<FieldErrors>({});
  const [activity, setActivity] = useState<readonly ActivityEntry[]>([]);
  const [busy, setBusy] = useState(false);

  // Only used to give each activity entry a stable React key.
  const sequence = useRef(0);

  const log = useCallback((kind: ActivityEntry['kind'], message: string) => {
    sequence.current += 1;
    const entry: ActivityEntry = { key: `${sequence.current}`, kind, message };
    setActivity((previous) => [entry, ...previous].slice(0, 50));
  }, []);

  const send = useCallback(
    async (request: CreateTransactionRequest): Promise<boolean> => {
      try {
        const response = await postTransaction(request);
        log('success', `${response.result}: ${request.status} ${request.amount} ${request.currency}`);
        return true;
      } catch (cause) {
        if (cause instanceof ApiError) {
          // Field errors from the server land next to the inputs, the same way
          // client-side ones do - the user should not have to tell the difference.
          setFieldErrors(toFieldErrors(cause));
          log('failure', `${cause.status || 'network'}: ${cause.message}`);
        } else {
          log('failure', 'Unexpected failure sending the transaction.');
        }
        return false;
      }
    },
    [log],
  );

  const handleSubmit = useCallback(
    async (event: React.FormEvent<HTMLFormElement>) => {
      event.preventDefault();

      const errors = validate(form);
      setFieldErrors(errors);
      if (Object.keys(errors).length > 0) {
        return;
      }

      setBusy(true);
      try {
        await send(toRequest(Number(form.amount), form.currency, form.status));
      } finally {
        setBusy(false);
      }
    },
    [form, send],
  );

  const handleGenerate = useCallback(() => {
    setFieldErrors({});
    setForm(randomForm());
  }, []);

  const handleRapid = useCallback(async () => {
    setBusy(true);
    setFieldErrors({});

    try {
      // Fired together rather than in sequence. The point of this button is to put
      // the dashboard's buffered rendering under a burst, and a sequential loop
      // paced by round trips would not produce one (S13).
      const requests = Array.from({ length: RAPID_BATCH_SIZE }, () => {
        const generated = randomForm();
        return toRequest(Number(generated.amount), generated.currency, generated.status);
      });

      const results = await Promise.allSettled(requests.map((request) => postTransaction(request)));
      const succeeded = results.filter((result) => result.status === 'fulfilled').length;

      log(
        succeeded === RAPID_BATCH_SIZE ? 'success' : 'failure',
        `Rapid batch: ${succeeded} of ${RAPID_BATCH_SIZE} accepted.`,
      );
    } finally {
      setBusy(false);
    }
  }, [log]);

  return (
    <section className="simulator">
      <h1>Transaction simulator</h1>
      <p className="simulator__intro">
        Stands in for the external producer. Every button below is one or more
        <code> POST /api/transactions</code>.
      </p>

      <form className="form" onSubmit={handleSubmit} noValidate>
        <div className="form__field">
          <label htmlFor="amount">Amount</label>
          <input
            id="amount"
            name="amount"
            inputMode="decimal"
            value={form.amount}
            aria-invalid={fieldErrors.amount !== undefined}
            aria-describedby={fieldErrors.amount !== undefined ? 'amount-error' : undefined}
            onChange={(event) => setForm((previous) => ({ ...previous, amount: event.target.value }))}
          />
          {fieldErrors.amount !== undefined ? (
            <p className="form__error" id="amount-error">
              {fieldErrors.amount}
            </p>
          ) : null}
        </div>

        <div className="form__field">
          <label htmlFor="currency">Currency</label>
          <input
            id="currency"
            name="currency"
            maxLength={3}
            value={form.currency}
            aria-invalid={fieldErrors.currency !== undefined}
            aria-describedby={fieldErrors.currency !== undefined ? 'currency-error' : undefined}
            onChange={(event) =>
              setForm((previous) => ({ ...previous, currency: event.target.value.toUpperCase() }))
            }
          />
          {fieldErrors.currency !== undefined ? (
            <p className="form__error" id="currency-error">
              {fieldErrors.currency}
            </p>
          ) : null}
        </div>

        <div className="form__field">
          <label htmlFor="status">Status</label>
          <select
            id="status"
            name="status"
            value={form.status}
            onChange={(event) =>
              setForm((previous) => ({ ...previous, status: event.target.value as TransactionStatus }))
            }
          >
            {TRANSACTION_STATUSES.map((status) => (
              <option key={status} value={status}>
                {status}
              </option>
            ))}
          </select>
        </div>

        <div className="form__actions">
          <button type="submit" disabled={busy}>
            Send one
          </button>
          <button type="button" onClick={handleGenerate} disabled={busy}>
            Generate random
          </button>
          <button type="button" onClick={() => void handleRapid()} disabled={busy}>
            Send {RAPID_BATCH_SIZE} rapid
          </button>
        </div>
      </form>

      <section className="activity" aria-live="polite">
        <h2>Activity</h2>
        {activity.length === 0 ? (
          <p className="empty-state">Nothing sent yet.</p>
        ) : (
          <ul className="activity__list">
            {activity.map((entry) => (
              <li key={entry.key} className={`activity__entry activity__entry--${entry.kind}`}>
                {entry.message}
              </li>
            ))}
          </ul>
        )}
      </section>
    </section>
  );
}

function validate(form: FormState): FieldErrors {
  const errors: FieldErrors = {};

  const amount = Number(form.amount);
  if (form.amount.trim() === '' || Number.isNaN(amount)) {
    errors.amount = 'Enter a number.';
  } else if (amount <= 0) {
    errors.amount = 'Amount must be greater than zero.';
  } else if (decimalPlaces(form.amount) > 4) {
    errors.amount = 'Amount must have no more than four decimal places.';
  }

  if (!CURRENCY_PATTERN.test(form.currency)) {
    errors.currency = 'Three upper-case letters, for example USD.';
  }

  return errors;
}

function decimalPlaces(value: string): number {
  const [, fraction] = value.split('.');
  return fraction?.length ?? 0;
}

function toRequest(amount: number, currency: string, status: TransactionStatus): CreateTransactionRequest {
  return {
    // The producer owns identity. Generating it here means a retry of the same
    // logical transaction carries the same id, which is what lets the server treat
    // it as a status change rather than a new row.
    transactionId: crypto.randomUUID(),
    amount,
    currency,
    status,
    timestamp: new Date().toISOString(),
  };
}

function randomForm(): FormState {
  const amount = Math.round((Math.random() * 9_900 + 100) * 100) / 100;
  const currency = CURRENCIES[Math.floor(Math.random() * CURRENCIES.length)] ?? 'USD';
  const status = TRANSACTION_STATUSES[Math.floor(Math.random() * TRANSACTION_STATUSES.length)] ?? 'Pending';

  return { amount: amount.toFixed(2), currency, status };
}

function toFieldErrors(error: ApiError): FieldErrors {
  const errors: FieldErrors = {};

  for (const [field, messages] of Object.entries(error.fieldErrors)) {
    const message = messages[0];
    if (message === undefined) {
      continue;
    }

    if (field === 'amount' || field === 'currency' || field === 'status') {
      errors[field] = message;
    }
  }

  return errors;
}
