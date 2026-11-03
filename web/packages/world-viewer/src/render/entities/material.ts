// The entity shaders: GPU skinning by bone matrices fetched from one float texture (row = entity, 64 slots × 3 texels per
// row), vanilla model lighting (ambient 0.4 + two fixed lights), the world light (sky light minus the time-of-day
// subtraction, block light, the same curve as the section shader), fog, the hurt flash (red overlay) and a white flash
// (creeper fuse, primed TNT). One shader source, three variants:
//   MODEL  box models, texture = the entity sheet (per-instance rectangle), part layer masking
//   TILE   dropped/held items and block cubes: sprites from the items/blocks/entity sheets, cubes from the blocks grid
//   SHADOW soft round blobs under entities (procedural, no texture)
//   GHOST  (MODEL + GHOST) the occluded-body silhouette: the same instances again, only those flagged GHOST_FLAG, drawn
//          after the world and before the entities with depth GREATER and no depth write, so it paints exactly the parts
//          of the body that terrain (leaves included) hides: a flat tint with a bright rim, never over a visible part.
//   GLINT  (MODEL or TILE + GLINT) the enchantment glint: the same instances again, only those flagged GLINT_FLAG, drawn
//          over themselves (depth LEQUAL, no depth write) with vanilla's two scrolling passes of enchanted_item_glint.png
//          added on top (GL_SRC_COLOR, GL_ONE): armour like LayerArmorBase.renderEnchantedGlint (colour 0.38, 0.19, 0.608;
//          texture scale 1/3, turned 30° and −30°, sliding 0.02 and 0.08 per tick), items like RenderItem.renderEffect
//          (colour 0x8040CC; atlas uv × 8, turned −50° and 10°, a full slide every 3 and 4.873 s). One extra draw per drawn
//          geometry that has a glinting instance this view; no texture fetch beyond the base alpha and the two glint taps.

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
#ifdef GHOST
varying vec3 vN;
flat varying vec4 vGhost;
#endif

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
#ifdef GHOST
  if (!flag(aColor.w, 256.0)) { gl_Position = vec4(2.0, 2.0, 2.0, 1.0); return; }
  // Strength (bits 2048 × 0..15) and tint: the selected bot, another bot, a player.
  float strength = mod(floor(aColor.w / 2048.0), 16.0) / 15.0;
  vec3 tint = flag(aColor.w, 512.0) ? vec3(0.35, 1.0, 0.8) : flag(aColor.w, 1024.0) ? vec3(1.0, 0.82, 0.4) : vec3(0.55, 0.8, 1.0);
  vGhost = vec4(strength, tint);
#endif
#ifdef GLINT
  if (!flag(aColor.w, 32768.0)) { gl_Position = vec4(2.0, 2.0, 2.0, 1.0); return; }
#endif
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
#ifdef GHOST
  vN = n;
#endif
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
#ifdef GLINT
uniform sampler2D uGlint;
vec2 turn(vec2 p, float deg) {
  float a = radians(deg), c = cos(a), s = sin(a);
  return vec2(c * p.x - s * p.y, s * p.x + c * p.y);
}
#endif
varying vec2 vUv;
varying vec3 vLit;
varying vec3 vWorld;
varying vec4 vFx;
flat varying float vFlags;
flat varying vec4 vTex;
#ifdef GHOST
varying vec3 vN;
flat varying vec4 vGhost;
#endif
void main() {
  vec4 c = vec4(1.0);
  vec2 luv = clamp(vUv, 0.0, 1.0);
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
    vec2 suv = flag(vFlags, 128.0) ? fract(vUv + vec2(uTime * 0.02, uTime * 0.01)) : luv;
    vec2 uv = vTex.xy + suv * vTex.zw;
#ifdef TILE
    // Atlas: bits 4 and 8 of the flags (0 items, 1 blocks, 2 entity sheet).
    float atlas = mod(floor(vFlags / 4.0), 4.0);
    c = atlas < 0.5 ? texture2D(uItems, uv) : atlas < 1.5 ? texture2D(uBlocks, uv) : texture2D(uTex, uv);
#else
    c = texture2D(uTex, uv);
#endif
  }
  if (c.a < uAlphaTest) discard;
#ifdef GLINT
  float gfog = 1.0 - smoothstep(uFogRange.x, uFogRange.y, length(vWorld.xz - uFogCentre.xz));
#ifdef TILE
  // RenderItem.renderEffect: the atlas coordinates × 8; a 16 px item is 1/64 of vanilla's 1024 px atlas, so one item spans
  // an eighth of the glint texture.
  vec3 col = vec3(0.502, 0.251, 0.8);
  vec2 iuv = luv * 0.125;
  vec3 g1 = texture2D(uGlint, turn(iuv, -50.0) + vec2(fract(uTime / 3.0), 0.0)).rgb * col;
  vec3 g2 = texture2D(uGlint, turn(iuv, 10.0) - vec2(fract(uTime / 4.873), 0.0)).rgb * col;
#else
  // LayerArmorBase.renderEnchantedGlint over the armour texture's coordinates, f = ticks.
  vec3 col = vec3(0.38, 0.19, 0.608);
  float f = uTime * 20.0;
  vec3 g1 = texture2D(uGlint, turn(luv + vec2(0.0, f * 0.02), 30.0) / 3.0).rgb * col;
  vec3 g2 = texture2D(uGlint, turn(luv + vec2(0.0, f * 0.08), -30.0) / 3.0).rgb * col;
#endif
  // GL_SRC_COLOR, GL_ONE: each pass adds its colour squared.
  gl_FragColor = vec4((g1 * g1 + g2 * g2) * gfog, 1.0);
  return;
#endif
#ifdef GHOST
  // Flat tint, a little of the skin's shading so limbs stay readable, and a bright rim (an outline at any size).
  float rim = 1.0 - abs(dot(normalize(vN), normalize(cameraPosition - vWorld)));
  rim = rim * rim;
  float lum = dot(c.rgb, vec3(0.3, 0.59, 0.11));
  vec3 g = vGhost.yzw * (0.55 + 0.45 * lum) + vec3(rim * 0.45);
  gl_FragColor = vec4(g, vGhost.x * (0.5 + 0.45 * rim));
  return;
#endif
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
  /** The occluded-body silhouette pass (see the header). */
  ghost: THREE.ShaderMaterial;
  /** The enchantment glint over models (armour) and over tiles (items, cubes) (see the header). */
  glintModel: THREE.ShaderMaterial;
  glintTile: THREE.ShaderMaterial;
  /** Uniforms shared by every variant (bones, textures, time). */
  shared: {
    uBones: THREE.IUniform<THREE.Texture | null>;
    uTex: THREE.IUniform<THREE.Texture | null>;
    uItems: THREE.IUniform<THREE.Texture | null>;
    uBlocks: THREE.IUniform<THREE.Texture | null>;
    uBlocksGrid: THREE.IUniform<THREE.Vector2>;
    uTime: THREE.IUniform<number>;
    /** enchanted_item_glint.png (repeating, linear like its .mcmeta blur); null = no glint drawn. */
    uGlint: THREE.IUniform<THREE.Texture | null>;
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
    uGlint: { value: null as THREE.Texture | null },
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
  const ghost = createGhostMaterial(uniforms(0.1));
  const glintModel = createGlintMaterial(uniforms(0.1), 'MODEL');
  const glintTile = createGlintMaterial(uniforms(0.1), 'TILE');
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
    ghost,
    glintModel,
    glintTile,
    shared,
    dispose() {
      for (const m of [model, modelTranslucent, tile, shadow, ghost, glintModel, glintTile]) m.dispose();
    },
  };
}

/** Instance flag bits of the silhouette pass (aColor.w): drawn as a ghost, the selected bot, a player (not a bot); the
 * strength 0..15 sits in the four bits from GHOST_LEVEL up. */
export const GHOST_FLAG = 256, GHOST_SELECTED = 512, GHOST_PLAYER = 1024, GHOST_LEVEL = 2048;
/** Instance flag bit of the glint pass (above the ghost strength bits). */
export const GLINT_FLAG = 32768;

/** The glint pass's render state: over the instance's own surface (depth LEQUAL, nudged toward the camera, no depth write),
 * added on top (the shader squares each pass's colour: vanilla's GL_SRC_COLOR, GL_ONE). Drawn after the opaque entities. */
export function createGlintMaterial(uniforms: Record<string, THREE.IUniform>, variant: 'MODEL' | 'TILE'): THREE.ShaderMaterial {
  return new THREE.ShaderMaterial({
    vertexShader: vertex,
    fragmentShader: fragment,
    uniforms,
    defines: { [variant]: 1, GLINT: 1 },
    transparent: true,
    blending: THREE.CustomBlending,
    blendSrc: THREE.OneFactor,
    blendDst: THREE.OneFactor,
    depthWrite: false,
    depthFunc: THREE.LessEqualDepth,
    polygonOffset: true,
    polygonOffsetFactor: -1,
    polygonOffsetUnits: -1,
  });
}

/** The glint texture as the glint pass samples it: repeating both ways, linear (its .mcmeta asks for blur). */
export function glintTexture(img: ImageBitmap | HTMLCanvasElement | HTMLImageElement): THREE.Texture {
  const t = new THREE.Texture(img as unknown as HTMLImageElement);
  t.wrapS = t.wrapT = THREE.RepeatWrapping;
  t.magFilter = THREE.LinearFilter;
  t.minFilter = THREE.LinearFilter;
  t.generateMipmaps = false;
  t.flipY = false;
  t.colorSpace = THREE.NoColorSpace;
  t.needsUpdate = true;
  return t;
}

/** The ghost pass's render state: only where the body is behind what is already drawn (GREATER), blended over it without
 * touching depth, pulled a little toward the camera so a body merely touching a block (feet on the ground, a shoulder in a
 * doorway) never shimmers. Not `transparent`: it must run in the opaque list, after the world (sections render order 0,
 * cut cap 1) and before the entities themselves (render order 2), or it would also ghost a body's far side behind its own
 * front and anything behind another mob. */
export function createGhostMaterial(uniforms: Record<string, THREE.IUniform>): THREE.ShaderMaterial {
  return new THREE.ShaderMaterial({
    vertexShader: vertex,
    fragmentShader: fragment,
    uniforms,
    defines: { MODEL: 1, GHOST: 1 },
    transparent: false,
    blending: THREE.CustomBlending,
    blendSrc: THREE.SrcAlphaFactor,
    blendDst: THREE.OneMinusSrcAlphaFactor,
    depthWrite: false,
    depthFunc: THREE.GreaterDepth,
    polygonOffset: true,
    polygonOffsetFactor: -1,
    polygonOffsetUnits: -4,
  });
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
