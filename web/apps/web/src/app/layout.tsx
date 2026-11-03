import type { Metadata, Viewport } from 'next';
import type { ReactNode } from 'react';
import { Providers } from '@/components/shell/Providers';
import { themeBootScript } from '@/lib/prefs';
import './globals.css';

export const metadata: Metadata = {
  title: { default: 'MineClaw Explorer', template: '%s · MineClaw' },
  description: 'Live hive, memory and scripts of the MineClaw bots.',
  robots: { index: false, follow: false },
};

export const viewport: Viewport = {
  width: 'device-width',
  initialScale: 1,
  themeColor: [
    { media: '(prefers-color-scheme: light)', color: '#f5f6f8' },
    { media: '(prefers-color-scheme: dark)', color: '#0c0e12' },
  ],
};

export default function RootLayout({ children }: { children: ReactNode }) {
  return (
    <html lang="en" suppressHydrationWarning>
      <head>
        {/* Sets the theme class before first paint (no flash). */}
        <script dangerouslySetInnerHTML={{ __html: themeBootScript }} />
      </head>
      <body>
        <Providers>{children}</Providers>
      </body>
    </html>
  );
}
