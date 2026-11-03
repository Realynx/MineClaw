// The entity shaders: GPU skinning by bone matrices fetched from one float texture (row = entity, 64 slots × 3 texels per
// row), vanilla model lighting (ambient 0.4 + two fixed lights), the world light (sky light minus the time-of-day
// subtraction, block light, the same curve as the section shader), fog, the hurt flash (red overlay) and a white flash
// (creeper fuse, primed TNT). One shader source, three variants:
//   MODEL  box models, texture = the entity sheet (per-instance rectangle), part layer masking
//   TILE   dropped/held items and block cubes: sprites from the items/blocks/entity sheets, cubes from the blocks grid
//   SHADOW soft round blobs under entities (procedural, no texture)

import * as THREE from 'three';

export interface WorldUniforms {
  uSkySub: THREE.IUniform<number>;
  uBase: THREE.IUniform<number>;
  uMinBright: THREE.IUniform<number>;
  uFogColor: THREE.IUniform<THREE.Color>;
  uFogCentre: THREE.IUniform<THREE.Vector3>;
  uFogRange: THREE.IUniform<THREE.Vector2>;
}

export function defaultWorldUniforms(): WorldUniforms {
  return {
    uSkySub: { value: 0 },
    uBase: { value: 0 },
    uMinBright: { value: 0.12 },
    uFogColor: { value: new THREE.Color(0.75, 0.85, 1) },
    uFogCentre: { value: new THREE.Vector3() },
    uFogRange: { value: new THREE.Vector2(1e6, 1e6 + 1) },
  };
}

const common = /* glsl */ `
uniform float uSkySub;
uniform float uBase;
uniform float uMinBright;
uniform vec3 uFogColor;
uniform vec3 uFogCentre;
uniform vec2 uFogRange;
uniform float uTime;
float bright(float level) {
  float f1 = 1.0 - clamp(level, 0.0, 15.0) / 15.0;
  return (1.0 - f1) / (f1 * 3.0 + 1.0) * (1.0 - uBase) + uBase;
}
bool flag(float f, float bit) { return mod(floor(f / bit), 2.0) >= 1.0; }
`;

const vertex = /* glsl */ `
${common}
uniform highp sampler2D uBones;
attribute float aPart;
attribute vec3 aBone;
attribute vec4 aTex;
attribute vec4 aColor;
attribute vec4 aFx;
#ifdef TILE
attribute float aFace;
attribute vec3 aFaceA;
attribute vec4 aFaceB;
uniform vec2 uBlocksGrid;
#endif
varying vec2 vUv;
varying vec3 vLit;
varying vec3 vWorld;
varying vec4 vFx;
flat varying float vFlags;
flat varying vec4 vTex;

mat4 bone(int slot, int row) {
  int x = slot * 3;
  vec4 r0 = texelFetch(uBones, ivec2(x, row), 0);
  vec4 r1 = texelFetch(uBones, ivec2(x + 1, row), 0);
  vec4 r2 = texelFetch(uBones, ivec2(x + 2, row), 0);
  return mat4(r0.x, r1.x, r2.x, 0.0, r0.y, r1.y, r2.y, 0.0, r0.z, r1.z, r2.z, 0.0, r0.w, r1.w, r2.w, 1.0);
}

const vec3 L1 = vec3(0.16169, 0.80845, -0.56592);
const vec3 L2 = vec3(-0.16169, 0.80845, 0.56592);

void main() {
  float layer = floor(aPart / 256.0 + 0.001);
  float part = aPart - layer * 256.0;
  if (abs(layer - aBone.z) > 0.5) { gl_Position = vec4(2.0, 2.0, 2.0, 1.0); return; }
  mat4 m = bone(int(part + aBone.y + 0.5), int(aBone.x + 0.5));
  vec3 p = position;
  vec4 w;
  vec3 n;
  vFlags = aColor.w;
#ifdef TILE
  if (flag(aColor.w, 16.0)) {
    // Billboard about y toward the camera (b1.8.1 drops, fire): the bone gives the centre and the size.
    vec3 c = (m * vec4(0.0, 0.0, 0.0, 1.0)).xyz;
    float s = length(m[0].xyz);
    vec3 to = cameraPosition - c;
    // Fire is drawn in front of its entity (Render.renderEntityOnFire pushes it toward the camera).
    if (flag(aColor.w, 64.0)) c += normalize(vec3(to.x, 0.0, to.z) + vec3(1e-5, 0.0, 0.0)) * s * 0.3;
    vec3 r = normalize(vec3(to.z, 0.0, -to.x) + vec3(1e-5, 0.0, 0.0));
    w = vec4(c + r * p.x * s + vec3(0.0, p.y * s, 0.0), 1.0);
    n = normalize(vec3(to.x, 0.0, to.z) + vec3(0.0, 1e-5, 0.0));
  } else {
    w = m * vec4(p, 1.0);
    n = normalize(mat3(m) * normal);
  }
  if (aFace >= 0.0) {
    // A cube face: its terrain cell from the per-instance face tiles.
    float t = aFace < 0.5 ? aFaceA.x : aFace < 1.5 ? aFaceA.y : aFace < 2.5 ? aFaceA.z : aFace < 3.5 ? aFaceB.x : aFace < 4.5 ? aFaceB.y : aFaceB.z;
    vec2 cell = vec2(mod(t, uBlocksGrid.x), floor(t / uBlocksGrid.x));
    vTex = vec4(cell / uBlocksGrid, 1.0 / uBlocksGrid);
    bool tinted = flag(aFaceB.w, pow(2.0, floor(aFace + 0.5)));
    vLit = tinted ? aColor.rgb : vec3(1.0);
  } else {
    vTex = aTex;
    vLit = aColor.rgb;
  }
#else
  w = m * vec4(p, 1.0);
  n = normalize(mat3(m) * normal);
  vTex = aTex;
  vLit = aColor.rgb;
#endif
  vUv = uv;
  vWorld = w.xyz;
  vFx = aFx;
  float shade = min(1.0, 0.4 + 0.6 * (max(0.0, dot(n, L1)) + max(0.0, dot(n, L2))));
  bool emissive = flag(aColor.w, 1.0);
  float b = emissive ? 1.0 : max(bright(max(aFx.z, aFx.y - uSkySub)), uMinBright) * shade;
  vLit *= b;
  gl_Position = projectionMatrix * viewMatrix * w;
}
`;

const fragment = /* glsl */ `
${common}
uniform sampler2D uTex;
uniform sampler2D uItems;
uniform sampler2D uBlocks;
uniform float uAlphaTest;
varying vec2 vUv;
varying vec3 vLit;
varying vec3 vWorld;
varying vec4 vFx;
flat varying float vFlags;
flat varying vec4 vTex;
void main() {
  vec4 c = vec4(1.0);
  if (flag(vFlags, 32.0)) {
    // Procedural flames (b1.8.1 animates fire in code; its terrain tile is a placeholder): 16×16 cells flickering at 8 Hz,
    // hotter and denser toward the bottom.
    vec2 cell = floor(clamp(vUv, 0.0, 0.999) * 16.0);
    float h = 1.0 - (cell.y + 0.5) / 16.0;
    float n = fract(sin(dot(cell + floor(uTime * 8.0) * vec2(3.1, 7.7), vec2(12.9898, 78.233))) * 43758.5453);
    float edge = 1.0 - abs(cell.x - 7.5) / 8.0;
    float k = (1.0 - h) * (0.55 + 0.65 * n) * sqrt(max(edge, 0.0));
    if (k < 0.33) discard;
    c = vec4(mix(vec3(0.85, 0.25, 0.0), vec3(1.0, 0.85, 0.3), clamp((k - 0.33) * 1.6, 0.0, 1.0)), 1.0);
  } else if (!flag(vFlags, 2.0)) {
    // Scrolling layers (creeper charge, wither armour: flag 128) slide their texture with time, wrapping in their rect.
    vec2 luv = flag(vFlags, 128.0) ? fract(vUv + vec2(uTime * 0.02, uTime * 0.01)) : clamp(vUv, 0.0, 1.0);
    vec2 uv = vTex.xy + luv * vTex.zw;
#ifdef TILE
    // Atlas: bits 4 and 8 of the flags (0 items, 1 blocks, 2 entity sheet).
    float atlas = mod(floor(vFlags / 4.0), 4.0);
    c = atlas < 0.5 ? texture2D(uItems, uv) : atlas < 1.5 ? texture2D(uBlocks, uv) : texture2D(uTex, uv);
#else
    c = texture2D(uTex, uv);
#endif
  }
  if (c.a < uAlphaTest) discard;
  vec3 rgb = c.rgb * vLit;
  // Hurt / death: vanilla's red overlay (1, 0, 0, 0.3); fuse / TNT: a white flash.
  rgb = mix(rgb, vec3(1.0, 0.0, 0.0), vFx.x * 0.3);
  rgb = mix(rgb, vec3(1.0), clamp(vFx.w, 0.0, 1.0) * 0.6);
  float fog = smoothstep(uFogRange.x, uFogRange.y, length(vWorld.xz - uFogCentre.xz));
  gl_FragColor = vec4(mix(rgb, uFogColor, fog), c.a);
}
`;

const shadowVertex = /* glsl */ `
attribute vec4 aShadow;
attribute float aAlpha;
varying vec2 vUv;
varying float vAlpha;
varying vec3 vWorld;
void main() {
  vUv = position.xz * 2.0;
  vAlpha = aAlpha;
  vec3 w = vec3(aShadow.x + position.x * aShadow.w * 2.0, aShadow.y + 0.015, aShadow.z + position.z * aShadow.w * 2.0);
  vWorld = w;
  gl_Position = projectionMatrix * viewMatrix * vec4(w, 1.0);
}
`;

const shadowFragment = /* glsl */ `
uniform vec3 uFogCentre;
uniform vec2 uFogRange;
varying vec2 vUv;
varying float vAlpha;
varying vec3 vWorld;
void main() {
  float d = length(vUv);
  float a = (1.0 - smoothstep(0.55, 1.0, d)) * vAlpha;
  float fog = smoothstep(uFogRange.x, uFogRange.y, length(vWorld.xz - uFogCentre.xz));
  a *= 1.0 - fog;
  if (a < 0.01) discard;
  gl_FragColor = vec4(0.0, 0.0, 0.0, a);
}
`;

export interface EntityMaterials {
  model: THREE.ShaderMaterial;
  modelTranslucent: THREE.ShaderMaterial;
  tile: THREE.ShaderMaterial;
  shadow: THREE.ShaderMaterial;
  /** Uniforms shared by every variant (bones, textures, time). */
  shared: {
    uBones: THREE.IUniform<THREE.Texture | null>;
    uTex: THREE.IUniform<THREE.Texture | null>;
    uItems: THREE.IUniform<THREE.Texture | null>;
    uBlocks: THREE.IUniform<THREE.Texture | null>;
    uBlocksGrid: THREE.IUniform<THREE.Vector2>;
    uTime: THREE.IUniform<number>;
  };
  dispose(): void;
}

export function createEntityMaterials(world: WorldUniforms): EntityMaterials {
  const shared = {
    uBones: { value: null as THREE.Texture | null },
    uTex: { value: null as THREE.Texture | null },
    uItems: { value: null as THREE.Texture | null },
    uBlocks: { value: null as THREE.Texture | null },
    uBlocksGrid: { value: new THREE.Vector2(16, 16) },
    uTime: { value: 0 },
  };
  const uniforms = (alpha: number) => ({ ...world, ...shared, uAlphaTest: { value: alpha } }) as unknown as Record<string, THREE.IUniform>;
  const model = new THREE.ShaderMaterial({ vertexShader: vertex, fragmentShader: fragment, uniforms: uniforms(0.1), defines: { MODEL: 1 } });
  const modelTranslucent = new THREE.ShaderMaterial({
    vertexShader: vertex,
    fragmentShader: fragment,
    uniforms: uniforms(0.02),
    defines: { MODEL: 1 },
    transparent: true,
    depthWrite: false,
  });
  const tile = new THREE.ShaderMaterial({ vertexShader: vertex, fragmentShader: fragment, uniforms: uniforms(0.1), defines: { TILE: 1 } });
  const shadow = new THREE.ShaderMaterial({
    vertexShader: shadowVertex,
    fragmentShader: shadowFragment,
    uniforms: { uFogCentre: world.uFogCentre, uFogRange: world.uFogRange },
    transparent: true,
    depthWrite: false,
    polygonOffset: true,
    polygonOffsetFactor: -2,
    polygonOffsetUnits: -2,
  });
  return {
    model,
    modelTranslucent,
    tile,
    shadow,
    shared,
    dispose() {
      for (const m of [model, modelTranslucent, tile, shadow]) m.dispose();
    },
  };
}

/** A sheet texture as the entity shader samples it: nearest, no mipmaps, no flip (v grows downwards like vanilla UVs). */
export function sheetTexture(img: ImageBitmap | HTMLCanvasElement | HTMLImageElement): THREE.Texture {
  const t = new THREE.Texture(img as unknown as HTMLImageElement);
  t.magFilter = THREE.NearestFilter;
  t.minFilter = THREE.NearestFilter;
  t.generateMipmaps = false;
  t.flipY = false;
  t.colorSpace = THREE.NoColorSpace; // raw texels, like the section atlas (the shaders write gl_FragColor directly)
  t.needsUpdate = true;
  return t;
}
