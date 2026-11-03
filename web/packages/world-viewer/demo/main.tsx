import { StrictMode, useMemo, useRef, useState } from 'react';
import { createRoot } from 'react-dom/client';
import { WorldThumbnail, WorldViewer, WorldViewerProvider, type CameraMode, type LayerToggles, type PickedBlock, type ViewerStats, type WorldViewerHandle, type XRayMode, DEFAULT_LAYERS } from '../src';
import { createFixtureSource } from '../src/testing';
import { makeDemoAtlas } from './demoAtlas';

function App() {
  const params = new URLSearchParams(location.search);
  const [textured, setTextured] = useState(params.get('atlas') !== '0');
  const [rain, setRain] = useState(params.get('rain') === '1');
  const source = useMemo(
    () =>
      createFixtureSource({
        radius: 8,
        ticksPerSecond: 400,
        startTime: 23000,
        rain: rain ? 1 : 0,
        thunder: rain ? 1 : 0,
        buildRate: 1,
        atlas: textured ? async () => makeDemoAtlas() : undefined,
      }),
    [textured, rain],
  );
  const viewer = useRef<WorldViewerHandle>(null);
  (window as unknown as { __viewer: typeof viewer }).__viewer = viewer; // for poking at it from devtools
  const [mode, setMode] = useState<CameraMode>('follow');
  const [follow, setFollow] = useState<string>('Bob');
  const [xray, setXray] = useState<XRayMode>('auto');
  const [layers, setLayers] = useState<LayerToggles>(DEFAULT_LAYERS);
  const [stats, setStats] = useState<ViewerStats | null>(null);
  const [picked, setPicked] = useState<PickedBlock | null>(null);
  const [thumbs, setThumbs] = useState(params.get('thumbs') !== '0');

  return (
    // One provider: the big view and the thumbnails share one WebGL context, one world store and one mesh per section.
    <WorldViewerProvider source={source}>
    <div style={{ display: 'grid', gridTemplateColumns: '1fr 280px', height: '100vh', font: '13px system-ui, sans-serif', background: '#11151c', color: '#dde' }}>
      <div style={{ position: 'relative' }}>
        <WorldViewer
          ref={viewer}
          source={source}
          follow={follow}
          cameraMode={mode}
          xray={xray}
          layers={layers}
          drawDistance={128}
          onSelectBot={setFollow}
          onPick={setPicked}
          onStats={setStats}
          onCameraModeChange={setMode}
          onError={(r, e) => console.warn(r, e)}
        />
      </div>
      <aside style={{ padding: 12, overflow: 'auto', borderLeft: '1px solid #2a3140' }}>
        <h3 style={{ margin: '0 0 8px' }}>world-viewer demo</h3>
        <p style={{ opacity: 0.7, marginTop: 0 }}>Fixture world (synthetic MCWC). Click a bot to follow it, a block to pick it. Fly: click to lock the mouse, WASD/QE, Shift fast, wheel = speed, Esc to release.</p>
        <fieldset>
          <legend>camera</legend>
          {(['follow', 'fly', 'iso'] as CameraMode[]).map((m) => (
            <button key={m} onClick={() => viewer.current?.setCameraMode(m)} style={{ fontWeight: mode === m ? 700 : 400 }}>
              {m}
            </button>
          ))}
          <div>
            follow:{' '}
            {['Alice', 'Bob'].map((b) => (
              <button key={b} onClick={() => setFollow(b)} style={{ fontWeight: follow === b ? 700 : 400 }}>
                {b}
              </button>
            ))}
            <button onClick={() => viewer.current?.focus(-8, 75, 10)}>focus house</button>
          </div>
          <div>
            x-ray:{' '}
            {(['auto', 'on', 'off'] as XRayMode[]).map((m) => (
              <button key={m} onClick={() => setXray(m)} style={{ fontWeight: xray === m ? 700 : 400 }}>
                {m}
              </button>
            ))}
          </div>
        </fieldset>
        <fieldset>
          <legend>layers</legend>
          {(Object.keys(DEFAULT_LAYERS) as Array<keyof LayerToggles>).map((k) => (
            <label key={k} style={{ display: 'block' }}>
              <input type="checkbox" checked={layers[k]} onChange={(e) => setLayers({ ...layers, [k]: e.target.checked })} /> {k}
            </label>
          ))}
          <label style={{ display: 'block' }}>
            <input type="checkbox" checked={textured} onChange={(e) => setTextured(e.target.checked)} /> procedural atlas
          </label>
          <label style={{ display: 'block' }}>
            <input type="checkbox" checked={rain} onChange={(e) => setRain(e.target.checked)} /> thunderstorm
          </label>
          <label style={{ display: 'block' }}>
            <input type="checkbox" checked={thumbs} onChange={(e) => setThumbs(e.target.checked)} /> shared thumbnails
          </label>
        </fieldset>
        {thumbs && (
          <div style={{ display: 'grid', gridTemplateColumns: '1fr 1fr', gap: 6, marginTop: 8 }}>
            {['Alice', 'Bob', 'Alice', 'Bob'].map((b, i) => (
              <div key={i} style={{ height: 90, borderRadius: 6, overflow: 'hidden', border: follow === b ? '2px solid #9ec5ff' : '2px solid transparent' }}>
                <WorldThumbnail follow={b} fps={4} angle={i < 2 ? 'behind' : 'fixed'} onClick={() => setFollow(b)} />
              </div>
            ))}
          </div>
        )}
        {picked && (
          <pre style={{ whiteSpace: 'pre-wrap' }}>
            picked {picked.name} ({picked.id}:{picked.meta}) at {picked.x},{picked.y},{picked.z} face {picked.face}
          </pre>
        )}
        {stats && (
          <pre id="stats" style={{ whiteSpace: 'pre-wrap', opacity: 0.8 }}>
            {[
              `${stats.fps.toFixed(0)} fps, ${stats.drawCalls} calls, ${(stats.triangles / 1000).toFixed(0)}k tris, res x${stats.resolutionScale.toFixed(2)}`,
              `${stats.sections} sections (${stats.memory.sectionGeometries} geometries, ${stats.sectionsQueued} queued), ${stats.columns} cols`,
              `GPU ${(stats.memory.gpuBytes / 1048576).toFixed(1)} MB, worker ~${(stats.memory.workerBytes / 1048576).toFixed(1)} MB, ${(stats.bytesReceived / 1024).toFixed(0)} KB in`,
              `${stats.memory.views} views, ${stats.blockChanges} block changes`,
              `${stats.version}, ${stats.textured ? 'textured' : 'flat'}, ${stats.gpu}`,
            ].join('\n')}
          </pre>
        )}
      </aside>
    </div>
    </WorldViewerProvider>
  );
}

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <App />
  </StrictMode>,
);
