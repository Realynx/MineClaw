import * as THREE from 'three';
import type { AtlasResource } from '../types';
import type { ChunkUniforms } from './chunkMaterial';

/** The atlas image as a pixel-exact texture (no flip: rows count from the top like the C# GpuAtlas; no colour conversion). */
export function atlasTexture(res: AtlasResource): THREE.Texture {
  const tex = new THREE.Texture(res.image as TexImageSource);
  tex.flipY = false;
  tex.magFilter = THREE.NearestFilter;
  tex.minFilter = THREE.NearestFilter;
  tex.generateMipmaps = false;
  tex.colorSpace = THREE.NoColorSpace;
  tex.needsUpdate = true;
  return tex;
}

export function bindAtlas(u: ChunkUniforms, tex: THREE.Texture, res: AtlasResource): void {
  u.uAtlas.value = tex;
  u.uGrid.value.set(res.map.columns, res.map.rows);
  u.uInset.value = 0.5 / Math.max(1, res.map.tileSize);
}
