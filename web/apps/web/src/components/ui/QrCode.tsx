'use client';

import { useMemo } from 'react';
import { encode } from 'uqr';

/**
 * A QR code rendered in the browser (uqr) as one SVG path, so a secret such as an otpauth:// URI never leaves the page or
 * hits a server. Always dark modules on white, with a quiet zone, whatever the theme: scanners need the contrast.
 */
export function QrCode({ value, label, size = 184 }: { value: string; label: string; size?: number }) {
  const { path, n } = useMemo(() => {
    const qr = encode(value, { ecc: 'M', border: 3 });
    let d = '';
    qr.data.forEach((row, y) =>
      row.forEach((dark, x) => {
        if (dark) d += `M${x} ${y}h1v1h-1z`;
      }),
    );
    return { path: d, n: qr.data.length };
  }, [value]);
  return (
    <svg
      role="img"
      aria-label={label}
      viewBox={`0 0 ${n} ${n}`}
      width={size}
      height={size}
      shapeRendering="crispEdges"
      className="rounded-lg"
    >
      <rect width={n} height={n} fill="#fff" />
      <path d={path} fill="#000" />
    </svg>
  );
}
