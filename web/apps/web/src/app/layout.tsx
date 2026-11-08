import type { Metadata, Viewport } from 'next';
import { headers } from 'next/headers';
import type { ReactNode } from 'react';
import { Providers } from '@/components/shell/Providers';
import { themeBootScript } from '@/lib/prefs';
import './globals.css';

export const metadata: Metadata = {
  title: { default: 'MineClaw Explorer', template: '%s · MineClaw' },
  description: 'Live hive, memory and scripts of the MineClaw bots.',
  robots: { index: false, follow: false },
  manifest: '/manifest.webmanifest',
  applicationName: 'MineClaw',
  appleWebApp: { capable: true, title: 'MineClaw', statusBarStyle: 'default' },
  icons: { icon: '/icons/mineclaw-192.png', apple: '/icons/mineclaw-192.png' },
};

export const viewport: Viewport = {
  width: 'device-width',
  initialScale: 1,
  themeColor: [
    { media: '(prefers-color-scheme: light)', color: '#f5f6f8' },
    { media: '(prefers-color-scheme: dark)', color: '#0c0e12' },
  ],
};

/**
 * Pages render per request so the CSP nonce applies: in production Nest sends the nonce as the `x-nonce` request header (and
 * the policy as `content-security-policy`, from which Next tags its own scripts). Our one inline script carries it too.
 */
export default async function RootLayout({ children }: { children: ReactNode }) {
  const nonce = (await headers()).get('x-nonce') ?? undefined;
  return (
    <html lang="en" suppressHydrationWarning>
      <head>
        {/* Sets the theme class before first paint (no flash). */}
        <script nonce={nonce} dangerouslySetInnerHTML={{ __html: themeBootScript }} />
      </head>
      <body>
        <Providers>{children}</Providers>
      </body>
    </html>
  );
}
