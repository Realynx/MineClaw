// The section shader: atlas cell + cell-local UV → texel (wrapping for procedural liquids; tile-entity quads address a
// w×h-cell sheet given in the tile attribute's 4th component), vanilla light levels resolved per
// fragment from the baked sky/block light and the time of day (World.k as a uniform), ambient occlusion, remembered-column
// dimming and distance fog around a centre (the camera, or the iso target).
// Cut-aways are per-view uniforms, never geometry: the x-ray cone (ViewCut.Hides per fragment) and the roof cut (cells above a
// height within a radius of the followed bot) discard whole cells. Where that opens a solid, the cap pass (the same section
// geometry drawn back-face-only after the front faces, depth LESS) paints the inside hatched (cool tint + diagonal stripes,
// like the C# renderer's cut faces), so a cut never reads as a hole or a real wall.

import * as THREE from 'three';
import { starfieldGlsl } from './starfield';

const vertex = /* glsl */ `
attribute vec4 aTile;
attribute vec4 aColor;
attribute vec4 aLight;
attribute vec4 aCell;
varying vec2 vUv;
flat varying vec4 vTile;
varying vec4 vColor;
varying vec3 vLight;
flat varying float vFlags;
varying vec3 vWorld;
flat varying vec3 vCell;
void main() {
  vec4 w = modelMatrix * vec4(position, 1.0);
  vWorld = w.xyz;
  vCell = (modelMatrix * vec4(aCell.xyz + 0.5, 1.0)).xyz;
  vUv = uv;
  vTile = aTile;
  vColor = aColor;
  vLight = vec3(aLight.x * 15.0, aLight.y * 15.0, aLight.z);
  vFlags = floor(aLight.w * 255.0 + 0.5);
  gl_Position = projectionMatrix * viewMatrix * w;
}
`;

const fragment = /* glsl */ `
uniform sampler2D uAtlas;
uniform vec2 uGrid;
uniform float uInset;
uniform float uTextured;
uniform float uSkySub;
uniform float uBase;
uniform float uMinBright;
uniform float uStale;
uniform float uFlash;
uniform vec3 uFogColor;
uniform vec3 uFogCentre;
uniform vec2 uFogRange;
uniform int uCones;
uniform vec4 uConeU[2];
uniform float uConeR;
uniform float uFloorY;
uniform vec4 uRoof;
uniform float uAlphaTest;
uniform float uCap;
${starfieldGlsl()}
varying vec2 vUv;
flat varying vec4 vTile;
varying vec4 vColor;
varying vec3 vLight;
flat varying float vFlags;
varying vec3 vWorld;
flat varying vec3 vCell;

bool flag(float f, float bit) { return mod(floor(f / bit), 2.0) >= 1.0; }

float bright(float level) {
  float f1 = 1.0 - clamp(level, 0.0, 15.0) / 15.0;
  return (1.0 - f1) / (f1 * 3.0 + 1.0) * (1.0 - uBase) + uBase;
}

bool hidden(vec3 c) {
  if (uRoof.w > 0.0 && c.y >= uRoof.y && length(c.xz - uRoof.xz) <= uRoof.w) return true;
  if (uCones == 0 || c.y < uFloorY) return false;
  vec3 q = c - cameraPosition;
  float q2 = dot(q, q);
  for (int i = 0; i < 2; i++) {
    if (i >= uCones) break;
    vec3 u = uConeU[i].xyz;
    float d = uConeU[i].w;
    float t = dot(q, u);
    if (t <= 0.0 || t >= d) continue;
    float r = uConeR * t / d + 0.87;
    if (q2 - t * t <= r * r) return true;
  }
  return false;
}

#ifdef CAP
uniform mat4 uProj;
// Where the ray from the camera leaves the cut-away volume (and so enters the solid behind it), or -1.
float cutExit(vec3 o, vec3 d, float tBack) {
  float best = -1.0;
  // Cone toward each subject: the solid starts at the subject's plane (cells beyond it are not cut) or the floor.
  for (int i = 0; i < 2; i++) {
    if (i >= uCones) break;
    vec3 u = uConeU[i].xyz;
    float D = uConeU[i].w;
    float du = dot(d, u);
    if (du > 1e-4) {
      float t = D / du;
      // Only rays inside this cone were cut (outside it, e.g. a camera buried in rock, the back face itself is shown).
      vec3 q = d * t;
      float along = dot(q, u);
      float r = uConeR * along / D + 0.87;
      if (dot(q, q) - along * along > r * r) continue;
      if (d.y < -1e-4) t = min(t, (uFloorY - o.y) / d.y);
      if (t > 0.0 && t < tBack) best = best < 0.0 ? t : min(best, t);
    }
  }
  // Roof: the cut volume is y >= roof.y within roof.w of (x, z); leave it through its floor or its side.
  if (uRoof.w > 0.0) {
    float t = 1e9;
    if (d.y < -1e-4) {
      float tp = (uRoof.y - o.y) / d.y;
      vec2 q = o.xz + d.xz * tp - uRoof.xz;
      if (tp > 0.0 && dot(q, q) <= uRoof.w * uRoof.w) t = tp;
    }
    vec2 oc = o.xz - uRoof.xz;
    float a = dot(d.xz, d.xz), b = dot(oc, d.xz), c = dot(oc, oc) - uRoof.w * uRoof.w;
    float disc = b * b - a * c;
    if (a > 1e-6 && disc >= 0.0) {
      float t2 = (-b + sqrt(disc)) / a;
      if (t2 > 0.0 && o.y + d.y * t2 >= uRoof.y) t = min(t, t2);
    }
    if (t < tBack) best = best < 0.0 ? t : min(best, t);
  }
  return best;
}
#endif

vec4 texel() {
  float tf = vTile.z;
  if (uTextured < 0.5 || !flag(tf, 1.0)) return vec4(1.0);
  vec2 span = vTile.w > 0.5 ? vec2(floor(vTile.w / 256.0), mod(vTile.w, 256.0)) : vec2(1.0);
  vec2 luv = flag(tf, 2.0) ? fract(vUv) : vUv;
  luv = clamp(luv, vec2(uInset), span - vec2(uInset));
  return texture2D(uAtlas, (vTile.xy + luv) / uGrid);
}

void main() {
  if (hidden(floor(vCell) + 0.5)) discard;
#ifdef CAP
  // [blocks] cut-out texels open no cut: the back of a plant, rail or pane is not a wall.
  if (texel().a < 0.5) discard;
  // The true section wall: this back face shows that the ray runs inside a solid the cut opened; draw the hatched wall
  // where the ray leaves the cut volume (the cone's subject plane / floor, the roof cut's floor or side), not here.
  vec3 ray = vWorld - cameraPosition;
  float tBack = length(ray);
  vec3 dir = ray / tBack;
  float t = cutExit(cameraPosition, dir, tBack);
  vec3 p = t > 0.0 ? cameraPosition + dir * t : vWorld;
  vec4 clip = uProj * viewMatrix * vec4(p, 1.0);
  gl_FragDepth = t > 0.0 ? clamp(clip.z / clip.w * 0.5 + 0.5, 0.0, 1.0) : gl_FragCoord.z;
  float s = step(0.5, fract((p.x + p.y + p.z) * 2.0));
  vec3 cap = mix(vec3(0.36, 0.42, 0.5), vec3(0.43, 0.5, 0.6), s);
  // No analytic exit (the camera itself is buried): the back face is the wall; never fog it into the sky colour.
  float f = t > 0.0 ? smoothstep(uFogRange.x, uFogRange.y, length(p.xz - uFogCentre.xz)) : 0.0;
  gl_FragColor = vec4(mix(cap, uFogColor, f), 1.0);
  return;
#endif
  if (uTextured > 0.5 && flag(vTile.z, 4.0)) {
    // [blocks] end portal / gateway: the layered starfield, full bright.
    float sf = smoothstep(uFogRange.x, uFogRange.y, length(vWorld.xz - uFogCentre.xz));
    gl_FragColor = vec4(mix(starfield() + vec3(uFlash), uFogColor, sf), 1.0);
    return;
  }
  vec4 c = vColor * texel();
  if (flag(vTile.z, 8.0)) c.a = vColor.a; // [blocks] fast leaves: the texel's alpha is ignored
  if (c.a < uAlphaTest) discard;
  bool emissive = flag(vFlags, 1.0);
  float b = emissive ? 1.0 : bright(max(vLight.y, vLight.x - uSkySub));
  if (!emissive && flag(vFlags, 2.0)) b = 0.75;
  b = max(b, uMinBright) * (emissive ? 1.0 : vLight.z);
  vec3 rgb = c.rgb;
  if (flag(vFlags, 8.0)) {
    float s = step(0.5, fract((vWorld.x + vWorld.y + vWorld.z) * 2.0));
    rgb = mix(rgb, vec3(0.55, 0.7, 0.95), 0.3 + 0.2 * s);
    b = max(b, 0.8);
  }
  rgb *= b;
  if (uStale > 0.5 && flag(vFlags, 4.0)) rgb = mix(rgb, vec3(dot(rgb, vec3(0.3, 0.59, 0.11))), 0.45) * 0.8;
  rgb += vec3(uFlash);
  float fog = smoothstep(uFogRange.x, uFogRange.y, length(vWorld.xz - uFogCentre.xz));
  gl_FragColor = vec4(mix(rgb, uFogColor, fog), c.a);
}
`;

export interface ChunkUniforms {
  [k: string]: THREE.IUniform;
  uAtlas: THREE.IUniform<THREE.Texture | null>;
  uGrid: THREE.IUniform<THREE.Vector2>;
  uInset: THREE.IUniform<number>;
  uTextured: THREE.IUniform<number>;
  uSkySub: THREE.IUniform<number>;
  uBase: THREE.IUniform<number>;
  uMinBright: THREE.IUniform<number>;
  uStale: THREE.IUniform<number>;
  uFlash: THREE.IUniform<number>;
  uFogColor: THREE.IUniform<THREE.Color>;
  uFogCentre: THREE.IUniform<THREE.Vector3>;
  uFogRange: THREE.IUniform<THREE.Vector2>;
  uCones: THREE.IUniform<number>;
  uConeU: THREE.IUniform<THREE.Vector4[]>;
  uConeR: THREE.IUniform<number>;
  uFloorY: THREE.IUniform<number>;
  /** Roof cut: centre x, cut y, centre z, radius (0 = off). */
  uRoof: THREE.IUniform<THREE.Vector4>;
  uAlphaTest: THREE.IUniform<number>;
  uCap: THREE.IUniform<number>;
  /** The view's projection matrix (the cap pass projects the cut wall itself). */
  uProj: THREE.IUniform<THREE.Matrix4>;
  /** [blocks] Seconds (animated block shaders: the end starfield), the view's size in device pixels, the starfield's sheets
   * (column, row, width, height in cells) and layer count. */
  uTime: THREE.IUniform<number>;
  uViewport: THREE.IUniform<THREE.Vector2>;
  uStarSky: THREE.IUniform<THREE.Vector4>;
  uStarPortal: THREE.IUniform<THREE.Vector4>;
  uStarLayers: THREE.IUniform<number>;
}

/** Uniforms shared by both passes (one object: updating it updates both materials). */
export function createChunkUniforms(): ChunkUniforms {
  return {
    uAtlas: { value: null },
    uGrid: { value: new THREE.Vector2(16, 1) },
    uInset: { value: 0.5 / 16 },
    uTextured: { value: 0 },
    uSkySub: { value: 0 },
    uBase: { value: 0 },
    uMinBright: { value: 0.12 },
    uStale: { value: 1 },
    uFlash: { value: 0 },
    uFogColor: { value: new THREE.Color(0.75, 0.85, 1) },
    uFogCentre: { value: new THREE.Vector3() },
    uFogRange: { value: new THREE.Vector2(96, 128) },
    uCones: { value: 0 },
    uConeU: { value: [new THREE.Vector4(), new THREE.Vector4()] },
    uConeR: { value: 3 },
    uFloorY: { value: 0 },
    uRoof: { value: new THREE.Vector4(0, 0, 0, 0) },
    uAlphaTest: { value: 0.5 },
    uCap: { value: 0 },
    uProj: { value: new THREE.Matrix4() },
    uTime: { value: 0 },
    uViewport: { value: new THREE.Vector2(1, 1) },
    uStarSky: { value: new THREE.Vector4(0, 0, 1, 1) },
    uStarPortal: { value: new THREE.Vector4(0, 0, 1, 1) },
    uStarLayers: { value: 16 },
  };
}

export interface ChunkMaterials {
  solid: THREE.ShaderMaterial;
  translucent: THREE.ShaderMaterial;
  /** Back faces of the solid geometry, drawn only while a cut is active (`visible` per view). */
  cap: THREE.ShaderMaterial;
}

export function createChunkMaterials(shared: ChunkUniforms): ChunkMaterials {
  const solid = new THREE.ShaderMaterial({ vertexShader: vertex, fragmentShader: fragment, uniforms: shared, side: THREE.FrontSide });
  // The translucent pass shares every uniform but its alpha test (blended water and glass keep faint texels).
  const translucent = new THREE.ShaderMaterial({
    vertexShader: vertex,
    fragmentShader: fragment,
    uniforms: { ...shared, uAlphaTest: { value: 0.02 } },
    side: THREE.FrontSide,
    transparent: true,
    depthWrite: false,
  });
  const cap = new THREE.ShaderMaterial({
    vertexShader: vertex,
    fragmentShader: fragment,
    uniforms: { ...shared, uCap: { value: 1 } },
    defines: { CAP: 1 },
    side: THREE.BackSide,
    depthFunc: THREE.LessDepth,
  });
  cap.visible = false;
  return { solid, translucent, cap };
}
