/** Every error that escapes a handler (unmatched routes, Nest internals) leaves as an `ApiError`, never a stack trace. */
import { Catch, HttpException, type ArgumentsHost, type ExceptionFilter } from '@nestjs/common';
import type { ServerResponse } from 'node:http';
import { ApiHttpError, Errors, sendApiError } from './errors.js';
import type { Logger } from './logger.js';

@Catch()
export class ApiExceptionFilter implements ExceptionFilter {
  constructor(private readonly log: Logger) {}

  catch(exception: unknown, host: ArgumentsHost): void {
    const res = host.switchToHttp().getResponse<ServerResponse>();
    if (exception instanceof ApiHttpError) return sendApiError(res, exception);
    if (exception instanceof HttpException) {
      const status = exception.getStatus();
      if (status === 404) return sendApiError(res, Errors.notFound());
      if (status === 413) return sendApiError(res, Errors.tooLarge());
      if (status >= 400 && status < 500)
        return sendApiError(res, new ApiHttpError(status, { error: 'request failed', code: 'bad_request' }));
    }
    this.log.error({ err: exception instanceof Error ? exception.message : String(exception) }, 'unhandled error');
    sendApiError(res, Errors.internal());
  }
}
