import type { CreateTransactionRequest, IngestionResponse } from '../types/transaction';

/**
 * Every HTTP call the client makes, in one module. Components never call fetch
 * (S10): the error shape, the URL and the headers are decided here once, so a
 * change to the contract touches one file.
 */

const ENDPOINT = '/api/transactions';

/** RFC 9457 problem details, in the shape ASP.NET Core emits for a validation failure. */
interface ValidationProblemDetails {
  readonly title?: string;
  readonly detail?: string;
  readonly errors?: Readonly<Record<string, readonly string[]>>;
}

/** A rejection the caller can render: a message, and field errors when the server named fields. */
export class ApiError extends Error {
  public readonly status: number;

  public readonly fieldErrors: Readonly<Record<string, readonly string[]>>;

  public constructor(message: string, status: number, fieldErrors: Readonly<Record<string, readonly string[]>> = {}) {
    super(message);
    this.name = 'ApiError';
    this.status = status;
    this.fieldErrors = fieldErrors;
  }
}

export async function postTransaction(
  request: CreateTransactionRequest,
  signal?: AbortSignal,
): Promise<IngestionResponse> {
  let response: Response;

  try {
    response = await fetch(ENDPOINT, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(request),
      ...(signal ? { signal } : {}),
    });
  } catch (cause) {
    // A network-level failure is not an HTTP status, and the caller still has to
    // report something to the user. 0 is the conventional stand-in.
    throw new ApiError(cause instanceof Error ? cause.message : 'The request could not be sent.', 0);
  }

  if (response.ok) {
    return (await response.json()) as IngestionResponse;
  }

  throw await toApiError(response);
}

async function toApiError(response: Response): Promise<ApiError> {
  let problem: ValidationProblemDetails | undefined;

  try {
    problem = (await response.json()) as ValidationProblemDetails;
  } catch {
    // Not every failure has a JSON body - a proxy 502 will not.
    problem = undefined;
  }

  const message = problem?.detail ?? problem?.title ?? `Request failed with status ${response.status}.`;
  return new ApiError(message, response.status, problem?.errors ?? {});
}
