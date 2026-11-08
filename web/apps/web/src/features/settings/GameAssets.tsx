'use client';

import { Check, Copy } from 'lucide-react';
import { useEffect, useRef, useState, type ReactNode } from 'react';
import type { GameVersion } from '@mineclaw/world-viewer';
import { loadWorldViewer } from '@/features/world/integration';
import { Badge, Button, Select } from '@/components/ui/primitives';
import { cn } from '@/lib/utils';

interface Installed {
  version: GameVersion;
  name: string;
  sounds: number;
  events: number;
}
type Launcher = 'minecraft' | 'prism' | 'other';
type ImportStep = 'textures' | 'sounds' | 'remove';
const versionName = (version: GameVersion) => (version === '1.12.2' ? 'Java 1.12.2' : 'Beta 1.8.1');

function FolderHint({ path }: { path: string }) {
  const [copied, setCopied] = useState(false);
  const [copyError, setCopyError] = useState(false);
  useEffect(() => {
    setCopied(false);
    setCopyError(false);
  }, [path]);
  const copy = async () => {
    try {
      await navigator.clipboard.writeText(path);
      setCopied(true);
      setCopyError(false);
    } catch {
      setCopyError(true);
    }
  };
  return (
    <div className="space-y-1.5 rounded-lg bg-surface-2 p-3 text-xs">
      <p className="text-muted">Typical Windows folder — paste into the file picker’s address bar:</p>
      <code className="block break-all select-text">{path}</code>
      <Button size="sm" variant="ghost" aria-label={`Copy folder path ${path}`} onClick={() => void copy()}>
        {copied ? <Check className="size-3.5" aria-hidden /> : <Copy className="size-3.5" aria-hidden />}
        {copied ? 'Copied' : 'Copy folder path'}
      </Button>
      {copyError ? (
        <p role="status" className="text-muted">
          Select and copy the path above.
        </p>
      ) : null}
    </div>
  );
}

function ImportCard({
  number,
  title,
  done,
  next,
  status,
  children,
}: {
  number: number;
  title: string;
  done: boolean;
  next: boolean;
  status: string;
  children: ReactNode;
}) {
  return (
    <li
      aria-current={next ? 'step' : undefined}
      className={cn('min-w-0 space-y-3 rounded-xl border p-4', next ? 'border-accent/50' : 'border-border')}
    >
      <div className="flex flex-wrap items-center gap-2.5">
        <span
          aria-hidden
          className={cn(
            'grid size-7 shrink-0 place-items-center rounded-full text-sm font-semibold',
            done ? 'bg-ok-soft text-ok' : 'bg-surface-2 text-muted',
          )}
        >
          {done ? <Check className="size-4" /> : number}
        </span>
        <h3 className="flex-1 text-sm font-semibold" aria-label={`${number}. ${title}`}>
          {title}
        </h3>
        <Badge tone={done ? 'ok' : next ? 'accent' : 'neutral'}>{status}</Badge>
      </div>
      {children}
    </li>
  );
}

export function GameAssets() {
  const jar = useRef<HTMLInputElement>(null),
    folder = useRef<HTMLInputElement>(null);
  const [packs, setPacks] = useState<Installed[]>([]);
  const [version, setVersion] = useState<GameVersion>('1.12.2');
  const [launcher, setLauncher] = useState<Launcher>('minecraft');
  const [loading, setLoading] = useState(true);
  const [busy, setBusy] = useState<ImportStep | null>(null);
  const [message, setMessage] = useState(''),
    [error, setError] = useState('');
  const refresh = async () => {
    const api = await loadWorldViewer();
    if (!api) throw new Error('Viewer could not load. Reload Settings to try again.');
    const values = await Promise.all((['1.12.2', 'b1.8.1'] as const).map((v) => api.readLocalPack(v)));
    setPacks(
      values.flatMap((p) =>
        p
          ? [
              {
                version: p.version,
                name: p.name,
                sounds: Object.keys(p.sounds).length,
                events: Object.keys(p.soundEvents).length,
              },
            ]
          : [],
      ),
    );
  };
  useEffect(() => {
    void refresh()
      .catch((e) => setError(String(e)))
      .finally(() => setLoading(false));
  }, []);
  const run = async (step: ImportStep, action: () => Promise<void>) => {
    setBusy(step);
    setError('');
    setMessage('');
    try {
      await action();
      await refresh();
    } catch (e) {
      setMessage('');
      const detail = e instanceof Error ? e.message : String(e);
      setError(
        `${detail} ${step === 'remove' ? 'Try removing the imports again.' : 'Choose the file or folder again to retry.'}`,
      );
    } finally {
      setBusy(null);
      if (jar.current) jar.current.value = '';
      if (folder.current) folder.current.value = '';
    }
  };
  const installed = packs.find((p) => p.version === version);
  const soundsReady = !!installed?.sounds && !!installed.events;
  const soundsSupported = version === '1.12.2';
  const disabled = loading || busy !== null;
  const launcherRoot = launcher === 'prism' ? '%APPDATA%\\PrismLauncher' : '%APPDATA%\\.minecraft';
  const jarFolder =
    launcher === 'prism'
      ? `${launcherRoot}\\libraries\\com\\mojang\\minecraft\\${version}`
      : `${launcherRoot}\\versions\\${version}`;

  return (
    <section
      aria-labelledby="game-assets-title"
      className="mb-8 space-y-4 rounded-xl border border-border bg-surface p-4"
    >
      <div>
        <h2 id="game-assets-title" className="text-base font-semibold">
          Set up Minecraft textures and sounds
        </h2>
        <p className="mt-1 text-sm text-muted">
          Two imports: the client JAR supplies textures, and your launcher’s assets folder supplies sounds.
        </p>
        <p className="mt-1 text-xs text-subtle">Your files stay in this browser on this device. Nothing is uploaded.</p>
      </div>
      <div className="flex flex-wrap gap-3">
        <label className="min-w-0 space-y-1 text-xs text-muted">
          <span className="block">Game version</span>
          <Select
            value={version}
            disabled={disabled}
            onChange={(e) => {
              setVersion(e.target.value as GameVersion);
              setMessage('');
              setError('');
            }}
          >
            <option value="1.12.2">Java 1.12.2</option>
            <option value="b1.8.1">Beta 1.8.1</option>
          </Select>
        </label>
        <label className="min-w-0 space-y-1 text-xs text-muted">
          <span className="block">Minecraft launcher</span>
          <Select value={launcher} disabled={disabled} onChange={(e) => setLauncher(e.target.value as Launcher)}>
            <option value="minecraft">Minecraft Launcher</option>
            <option value="prism">Prism Launcher</option>
            <option value="other">Other / custom location</option>
          </Select>
        </label>
      </div>
      {loading ? (
        <p role="status" className="text-sm text-muted">
          Checking imports on this device…
        </p>
      ) : null}
      <ol aria-label="Minecraft resource setup" className="space-y-3">
        <ImportCard
          number={1}
          title="Import textures"
          done={!!installed}
          next={!loading && !installed}
          status={installed ? 'Textures ready' : 'Start here'}
        >
          <p className="text-sm text-muted">
            Choose the Minecraft client <strong>.jar</strong> file. The version is detected automatically.
          </p>
          {installed ? (
            <p className="break-all text-sm">
              <span className="text-muted">Installed:</span> {installed.name}
            </p>
          ) : null}
          <details className="text-sm" open={!installed}>
            <summary className="cursor-pointer text-muted">Where do I find the JAR?</summary>
            <div className="mt-2 space-y-2">
              {launcher !== 'other' ? <FolderHint path={jarFolder} /> : null}
              <p className="text-xs text-muted">
                Open your launcher’s game files and look under <code>versions</code> or <code>libraries</code>. Select
                the client JAR for {versionName(version)}, not a mod or server JAR.
              </p>
            </div>
          </details>
          <Button
            variant={installed ? 'secondary' : 'primary'}
            disabled={disabled}
            loading={busy === 'textures'}
            onClick={() => jar.current?.click()}
          >
            {installed ? 'Replace textures (.jar)' : 'Choose textures JAR'}
          </Button>
          {installed && !soundsReady && soundsSupported ? (
            <p className="text-sm text-accent">Textures are ready. Continue with step 2 to hear the colony.</p>
          ) : null}
        </ImportCard>
        <ImportCard
          number={2}
          title="Import sounds"
          done={soundsReady}
          next={!loading && !!installed && !soundsReady && soundsSupported}
          status={
            soundsReady
              ? 'Sounds imported'
              : !soundsSupported
                ? 'Java 1.12.2 only'
                : installed
                  ? 'Next step'
                  : 'After textures'
          }
        >
          <p className="text-sm text-muted">
            Sounds are downloaded separately by the launcher. Choose the entire <strong>assets</strong> folder,
            including its <code>indexes</code> and <code>objects</code> folders.
          </p>
          {soundsSupported ? (
            <>
              <details className="text-sm" open={!soundsReady}>
                <summary className="cursor-pointer text-muted">Where do I find the sounds?</summary>
                <div className="mt-2 space-y-2">
                  {launcher !== 'other' ? <FolderHint path={`${launcherRoot}\\assets`} /> : null}
                  <p className="text-xs text-muted">
                    {launcher === 'prism'
                      ? 'Prism shares this folder beside instances and libraries. It is outside the individual instance’s minecraft folder.'
                      : 'Find assets in your launcher’s shared game folder. Custom locations, macOS and Linux may use a different path.'}
                  </p>
                  <p className="text-xs text-muted">
                    Launch Java 1.12.2 once so its sounds are downloaded. Select assets itself, not indexes, objects, or
                    the JAR folder.
                  </p>
                </div>
              </details>
              {soundsReady ? (
                <p className="text-sm text-ok">
                  {installed.sounds.toLocaleString()} sounds imported for {versionName(version)}.
                </p>
              ) : null}
              <Button
                variant={soundsReady ? 'secondary' : 'primary'}
                disabled={disabled || !installed}
                loading={busy === 'sounds'}
                onClick={() => folder.current?.click()}
              >
                {soundsReady ? 'Replace sounds folder' : 'Choose sounds folder'}
              </Button>
              {!installed ? <p className="text-xs text-muted">Import textures in step 1 to enable this step.</p> : null}
            </>
          ) : (
            <p className="text-sm text-muted">
              Beta textures are supported. Importing Beta’s legacy sound folders is not available yet.
            </p>
          )}
        </ImportCard>
      </ol>
      <input
        ref={jar}
        type="file"
        accept=".jar,.zip"
        aria-label="Textures JAR file"
        hidden
        onChange={(e) => {
          const file = e.target.files?.[0];
          if (file)
            void run('textures', async () => {
              const api = await loadWorldViewer();
              if (!api) throw new Error('Viewer could not load.');
              const pack = await api.importGameJar(file, setMessage);
              setVersion(pack.version);
              const hasSounds = Object.keys(pack.sounds).length > 0 && Object.keys(pack.soundEvents).length > 0;
              setMessage(
                `${versionName(pack.version)} textures installed.${hasSounds ? ' Your sounds are ready too.' : pack.version === '1.12.2' ? ' Next: choose the sounds folder in step 2.' : ''}`,
              );
            });
        }}
      />
      <input
        ref={folder}
        type="file"
        multiple
        {...{ webkitdirectory: '' }}
        aria-label="Sound assets folder"
        hidden
        onChange={(e) => {
          const files = Array.from(e.target.files ?? []);
          if (files.length)
            void run('sounds', async () => {
              setMessage('Reading local sound files…');
              const api = await loadWorldViewer();
              if (!api) throw new Error('Viewer could not load.');
              const count = await api.importSoundFolder(files, version);
              setMessage(
                `${count.toLocaleString()} sounds imported. Open Controller and use its Sound menu to adjust the volume.`,
              );
            });
        }}
      />
      {message ? (
        <p role="status" className="text-sm text-muted">
          {message}
        </p>
      ) : null}
      {error ? (
        <p role="alert" className="text-sm text-danger">
          {error}
        </p>
      ) : null}
      {soundsReady ? (
        <p className="text-sm text-ok">
          {versionName(version)} is ready. Open Controller to see the textures and hear the colony.
        </p>
      ) : null}
      {installed ? (
        <Button
          size="sm"
          variant="ghost"
          disabled={disabled}
          onClick={() =>
            void run('remove', async () => {
              const api = await loadWorldViewer();
              if (!api) throw new Error('Viewer could not load.');
              await api.removeLocalPack(version);
              setMessage(`${versionName(version)} textures and sounds removed from this browser.`);
            })
          }
        >
          Remove {versionName(version)} imports
        </Button>
      ) : null}
      <p className="text-xs text-subtle">
        Imports are saved for this site address. Switching between HTTP and HTTPS, another browser or another device
        requires importing again.
      </p>
    </section>
  );
}
