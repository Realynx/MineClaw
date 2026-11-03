// The end portal / end gateway surface (TileEntityEndPortalRenderer, our own GLSL): up to 16 layers, each the end_portal star
// texture projected in screen space (the client's eye-linear texgen through the projection), scaled, turned by a fixed angle
// per layer and scrolled with time, added up in the colours the client draws from java.util.Random(31100); layer 0 is the
// end_sky texture at 0.15 grey. Section quads with the TEX_STARFIELD flag take this colour instead of their texel.

/** java.util.Random's 48-bit LCG: the first `n` nextFloat() values from `seed`. */
export function javaRandomFloats(seed: number, n: number): number[] {
  const mask = (1n << 48n) - 1n;
  let s = (BigInt(seed) ^ 0x5deece66dn) & mask;
  const out: number[] = [];
  for (let i = 0; i < n; i++) {
    s = (s * 0x5deece66dn + 0xbn) & mask;
    out.push(Number(s >> 24n) / (1 << 24));
  }
  return out;
}

/** The colour of each layer (r, g, b), as the client computes them every frame from Random(31100). */
export function starLayerColours(): Array<[number, number, number]> {
  const f = javaRandomFloats(31100, 48);
  const out: Array<[number, number, number]> = [];
  for (let j = 0; j < 16; j++) {
    const f2 = j === 0 ? 0.15 : 2 / (18 - j);
    const r = (f[j * 3] * 0.5 + 0.1) * f2, g = (f[j * 3 + 1] * 0.5 + 0.4) * f2, b = (f[j * 3 + 2] * 0.5 + 0.5) * f2;
    out.push(j === 0 ? [f2, f2, f2] : [r, g, b]);
  }
  return out;
}

const glslNum = (v: number) => (Number.isInteger(v) ? `${v}.0` : v.toFixed(6));

/** GLSL: `vec3 starfield()` — needs uAtlas, uGrid, uInset, uTime, uViewport, uStarSky, uStarPortal, uStarLayers. */
export function starfieldGlsl(): string {
  const cols = starLayerColours();
  return /* glsl */ `
uniform float uTime;
uniform vec2 uViewport;
uniform vec4 uStarSky;
uniform vec4 uStarPortal;
uniform float uStarLayers;
const vec3 STAR_COLOURS[16] = vec3[16](${cols.map((c) => `vec3(${c.map(glslNum).join(', ')})`).join(', ')});
vec3 sheetSample(vec4 sheet, vec2 st) {
  vec2 luv = clamp(fract(st) * sheet.zw, vec2(uInset), sheet.zw - vec2(uInset));
  return texture2D(uAtlas, (sheet.xy + luv) / uGrid).rgb;
}
vec3 starfield() {
  vec2 ndc = gl_FragCoord.xy / uViewport * 2.0 - 1.0;
  float t = mod(uTime, 800.0) / 800.0;
  vec3 col = vec3(0.0);
  for (int j = 0; j < 16; j++) {
    if (float(j) >= uStarLayers) break;
    float f3 = float(j + 1);
    float a = radians(mod((f3 * f3 * 4321.0 + f3 * 9.0) * 2.0, 360.0));
    float k = 4.5 - f3 / 4.0;
    vec2 p = ndc * k;
    p = vec2(p.x * cos(a) - p.y * sin(a), p.x * sin(a) + p.y * cos(a));
    p += vec2(17.0 / f3, (2.0 + f3 / 1.5) * t);
    vec2 st = 0.5 + 0.5 * p;
    if (j == 0) col = sheetSample(uStarSky, st) * STAR_COLOURS[0];
    else col += sheetSample(uStarPortal, st) * STAR_COLOURS[j];
  }
  return col;
}
`;
}
