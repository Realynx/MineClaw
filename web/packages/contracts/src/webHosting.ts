import { z } from 'zod';

/** HTTPS is opt-in and additive: the existing HTTP gateway remains available for local health checks. */
export const WebTlsSettings = z
  .object({
    enabled: z.boolean().default(false),
    hostname: z
      .string()
      .trim()
      .max(253)
      .regex(/^$|^[a-z0-9](?:[a-z0-9.-]*[a-z0-9])?$/i, 'Use a hostname or IPv4 address, without a scheme, port or path')
      .default(''),
    port: z.number().int().min(1024).max(65535).default(8443),
    certFile: z
      .string()
      .trim()
      .max(4096)
      // eslint-disable-next-line no-control-regex -- refusing control characters is the point
      .regex(/^[^\x00-\x1f]*$/)
      .default(''),
    keyFile: z
      .string()
      .trim()
      .max(4096)
      // eslint-disable-next-line no-control-regex -- refusing control characters is the point
      .regex(/^[^\x00-\x1f]*$/)
      .default(''),
  })
  .strict();
export type WebTlsSettings = z.infer<typeof WebTlsSettings>;

export const WebHostingResponse = z.object({
  running: WebTlsSettings,
  saved: WebTlsSettings,
  pendingRestart: z.boolean(),
  editable: z.boolean(),
  overridePath: z.string(),
  httpsOrigin: z.string().nullable(),
  savedOrigin: z.string().nullable(),
});
export type WebHostingResponse = z.infer<typeof WebHostingResponse>;

export const WebHostingSaveRequest = z
  .object({
    tls: WebTlsSettings,
    reason: z.string().trim().min(3).max(300),
  })
  .strict();
export type WebHostingSaveRequest = z.infer<typeof WebHostingSaveRequest>;
