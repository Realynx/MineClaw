'use client';

import { createContext, useContext, useEffect, useState, type ReactNode } from 'react';
import { Download } from 'lucide-react';
import { Button } from '@/components/ui/primitives';
import { Dialog } from '@/components/ui/overlays';

interface InstallPrompt extends Event {
  prompt(): Promise<void>;
  userChoice: Promise<{ outcome: 'accepted' | 'dismissed' }>;
}
interface PwaState {
  secure: boolean;
  installed: boolean;
  available: boolean;
  install(): Promise<void>;
}
const PwaContext = createContext<PwaState>({
  secure: false,
  installed: false,
  available: false,
  install: async () => {},
});

export function PwaProvider({ children }: { children: ReactNode }) {
  const [prompt, setPrompt] = useState<InstallPrompt | null>(null);
  const [secure, setSecure] = useState(false);
  const [installed, setInstalled] = useState(false);
  useEffect(() => {
    const mode = window.matchMedia('(display-mode: standalone)');
    const isInstalled = () =>
      setInstalled(mode.matches || !!(navigator as Navigator & { standalone?: boolean }).standalone);
    setSecure(window.isSecureContext);
    isInstalled();
    const before = (event: Event) => {
      event.preventDefault();
      setPrompt(event as InstallPrompt);
    };
    const done = () => {
      setInstalled(true);
      setPrompt(null);
    };
    window.addEventListener('beforeinstallprompt', before);
    window.addEventListener('appinstalled', done);
    mode.addEventListener('change', isInstalled);
    if (
      window.isSecureContext &&
      'serviceWorker' in navigator &&
      process.env.NODE_ENV === 'production' &&
      process.env.NEXT_PUBLIC_MOCK !== '1'
    )
      void navigator.serviceWorker.register('/sw.js', { scope: '/', updateViaCache: 'none' }).catch(() => {});
    return () => {
      window.removeEventListener('beforeinstallprompt', before);
      window.removeEventListener('appinstalled', done);
      mode.removeEventListener('change', isInstalled);
    };
  }, []);
  const install = async () => {
    if (!prompt) return;
    setPrompt(null);
    await prompt.prompt();
    if ((await prompt.userChoice).outcome === 'accepted') setInstalled(true);
  };
  return (
    <PwaContext.Provider value={{ secure, installed, available: prompt !== null, install }}>
      {children}
    </PwaContext.Provider>
  );
}

export function InstallApp() {
  const state = useContext(PwaContext);
  const [help, setHelp] = useState(false);
  if (state.installed) return null;
  return (
    <>
      <Button
        variant="ghost"
        size="icon"
        aria-label="Install MineClaw"
        title="Install MineClaw"
        onClick={() => {
          if (state.available) void state.install().catch(() => setHelp(true));
          else setHelp(true);
        }}
      >
        <Download className="size-4" />
      </Button>
      <Dialog
        open={help}
        onOpenChange={setHelp}
        title="Install MineClaw"
        description="Keep the colony one click away in its own app window."
      >
        <p className="text-sm text-muted">
          {state.secure
            ? 'Use your browser’s Install app menu. On iPhone or iPad, open this page in Safari, choose Share, then Add to Home Screen. If installation is already available, the install button opens the browser prompt.'
            : 'Open MineClaw using HTTPS with a certificate your browser trusts. An administrator can configure the hostname and certificate in Administration, or use an existing HTTPS proxy.'}
        </p>
        <p className="mt-3 text-sm text-muted">
          Live world views and commands need a connection to the colony. Signing in works the same way as in your
          browser.
        </p>
      </Dialog>
    </>
  );
}
