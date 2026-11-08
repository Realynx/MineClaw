'use client';

import { useEffect, useRef, useState } from 'react';
import type { GameVersion } from '@mineclaw/world-viewer';
import { loadWorldViewer } from '@/features/world/integration';
import { Button } from '@/components/ui/primitives';

interface Installed {
  version: GameVersion;
  name: string;
  sounds: number;
}
export function GameAssets() {
  const jar = useRef<HTMLInputElement>(null),
    folder = useRef<HTMLInputElement>(null);
  const [packs, setPacks] = useState<Installed[]>([]),
    [version, setVersion] = useState<GameVersion>('1.12.2');
  const [busy, setBusy] = useState(false),
    [message, setMessage] = useState(''),
    [error, setError] = useState('');
  const refresh = async () => {
    const api = await loadWorldViewer();
    if (!api) return;
    const values = await Promise.all((['1.12.2', 'b1.8.1'] as const).map((v) => api.readLocalPack(v)));
    setPacks(
      values.flatMap((p) => (p ? [{ version: p.version, name: p.name, sounds: Object.keys(p.sounds).length }] : [])),
    );
  };
  useEffect(() => {
    void refresh().catch((e) => setError(String(e)));
  }, []);
  const run = async (action: () => Promise<void>) => {
    setBusy(true);
    setError('');
    setMessage('');
    try {
      await action();
      await refresh();
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    } finally {
      setBusy(false);
      if (jar.current) jar.current.value = '';
      if (folder.current) folder.current.value = '';
    }
  };
  return (
    <section
      aria-labelledby="game-assets-title"
      className="mb-8 space-y-3 rounded-xl border border-border bg-surface p-4"
    >
      <h2 id="game-assets-title" className="text-base font-semibold">
        Minecraft textures and sounds
      </h2>
      <p className="text-sm text-muted">
        Import your own Java 1.12.2 or Beta 1.8.1 client JAR. Game files stay in this browser on this device; they are
        never uploaded. Without a pack, worlds use a simple colour palette.
      </p>
      <div className="flex flex-wrap items-center gap-2">
        <Button disabled={busy} onClick={() => jar.current?.click()}>
          Import Minecraft JAR
        </Button>
        <select
          aria-label="Sound pack version"
          value={version}
          disabled={busy}
          onChange={(e) => setVersion(e.target.value as GameVersion)}
          className="rounded border border-border bg-canvas px-2 py-1.5"
        >
          <option value="1.12.2">Java 1.12.2</option>
          <option value="b1.8.1">Beta 1.8.1</option>
        </select>
        <Button disabled={busy || !packs.some((p) => p.version === version)} onClick={() => folder.current?.click()}>
          Import sound assets folder
        </Button>
      </div>
      <input
        ref={jar}
        type="file"
        accept=".jar,.zip"
        hidden
        onChange={(e) => {
          const file = e.target.files?.[0];
          if (file)
            void run(async () => {
              const api = await loadWorldViewer();
              if (!api) throw new Error('Viewer could not load.');
              const pack = await api.importGameJar(file, setMessage);
              setMessage(`${pack.version} textures installed. Open views update automatically.`);
            });
        }}
      />
      <input
        ref={folder}
        type="file"
        multiple
        {...{ webkitdirectory: '' }}
        hidden
        onChange={(e) => {
          const files = Array.from(e.target.files ?? []);
          if (files.length)
            void run(async () => {
              setMessage('Reading local sound files…');
              const api = await loadWorldViewer();
              if (!api) throw new Error('Viewer could not load.');
              const count = await api.importSoundFolder(files, version);
              setMessage(`${count} sounds installed.`);
            });
        }}
      />
      <p className="text-xs text-subtle">
        The client JAR is in .minecraft/versions/&lt;version&gt;/. Sounds are stored separately: select
        .minecraft/assets after launching that game version. Browser storage is separate for each site address,
        including HTTP versus HTTPS.
      </p>
      <ul className="space-y-2">
        {packs.map((p) => (
          <li key={p.version} className="flex items-center gap-3 text-sm">
            <span className="flex-1">
              {p.version} · {p.name} · {p.sounds} sounds
            </span>
            <Button
              size="sm"
              variant="ghost"
              disabled={busy}
              onClick={() =>
                void run(async () => {
                  const api = await loadWorldViewer();
                  if (api) await api.removeLocalPack(p.version);
                })
              }
            >
              Remove from this browser
            </Button>
          </li>
        ))}
      </ul>
      {message && (
        <p role="status" className="text-sm text-muted">
          {message}
        </p>
      )}
      {error && (
        <p role="alert" className="text-sm text-danger">
          {error}
        </p>
      )}
    </section>
  );
}
