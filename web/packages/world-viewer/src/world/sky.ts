// Day/night and weather maths, ported from src/MineClaw.Render/Lighting.cs and Weather.cs (vanilla b1.8 / 1.12.2).

/** WorldProvider celestial angle (0..1) of a world time. */
export function celestialAngle(time: number): number {
  const j = ((Math.floor(time) % 24000) + 24000) % 24000;
  let f1 = j / 24000 - 0.25;
  if (f1 < 0) f1 += 1;
  if (f1 > 1) f1 -= 1;
  const f2 = f1;
  f1 = 1 - (Math.cos(f1 * Math.PI) + 1) / 2;
  return f2 + (f1 - f2) / 3;
}

/** World.k under weather: sky light taken off (0 at a clear noon, 11 at midnight; rain 3, a storm 5 at noon). */
export function skylightSubtracted(time: number, rain = 0, thunder = 0): number {
  let k = 1 - (Math.cos(celestialAngle(time) * Math.PI * 2) * 2 + 0.5);
  k = Math.max(0, Math.min(1, k));
  k = 1 - k;
  const r = Math.max(0, Math.min(1, rain));
  k *= 1 - (r * 5) / 16;
  k *= 1 - (Math.max(0, Math.min(1, thunder)) * r * 5) / 16;
  k = 1 - k;
  return Math.floor(k * 11);
}

/** The continuous version (for smooth shader transitions). */
export function skylightSubtractedSmooth(time: number, rain = 0, thunder = 0): number {
  let k = 1 - (Math.cos(celestialAngle(time) * Math.PI * 2) * 2 + 0.5);
  k = 1 - Math.max(0, Math.min(1, k));
  const r = Math.max(0, Math.min(1, rain));
  k *= (1 - (r * 5) / 16) * (1 - (Math.max(0, Math.min(1, thunder)) * r * 5) / 16);
  return (1 - k) * 11;
}

/** Brightness (0..1) of light level 0..15 (WorldProvider brightness table; base 0.1 in the Nether). */
export function brightness(level: number, nether = false): number {
  const b = nether ? 0.1 : 0;
  const f1 = 1 - Math.max(0, Math.min(15, level)) / 15;
  return ((1 - f1) / (f1 * 3 + 1)) * (1 - b) + b;
}

/** Daylight factor 0..1 for the sky colour. */
export function daylight(time: number): number {
  return Math.max(0, Math.min(1, Math.cos(celestialAngle(time) * Math.PI * 2) * 2 + 0.5));
}

type Rgb = [number, number, number];

function grey(c: Rgb, amount: number, k: number): Rgb {
  if (amount <= 0) return c;
  const g = (c[0] * 0.3 + c[1] * 0.59 + c[2] * 0.11) * k;
  return [c[0] * (1 - amount) + g * amount, c[1] * (1 - amount) + g * amount, c[2] * (1 - amount) + g * amount];
}

/** Sky colour (0..1 rgb) at a time under weather (Weather.Sky): blue by day, deep blue at night, grey in rain. */
export function skyColour(time: number | null, rain: number, thunder: number, nether: boolean): Rgb {
  if (nether) return [0.2, 0.03, 0.03];
  const d = time == null ? 1 : daylight(time);
  const day: Rgb = [0.47, 0.65, 1.0];
  const night: Rgb = [0.02, 0.03, 0.08];
  let c: Rgb = [night[0] + (day[0] - night[0]) * d, night[1] + (day[1] - night[1]) * d, night[2] + (day[2] - night[2]) * d];
  const r = Math.max(0, Math.min(1, rain));
  const t = Math.max(0, Math.min(1, thunder)) * r;
  c = grey(c, r * 0.75, 0.6);
  c = grey(c, t * 0.75, 0.2);
  return c;
}

/** Fog / horizon colour (Weather.Fog over a lighter sky). */
export function fogColour(time: number | null, rain: number, thunder: number, nether: boolean): Rgb {
  if (nether) return [0.2, 0.03, 0.03];
  const d = time == null ? 1 : daylight(time);
  const base: Rgb = [0.75 * (d * 0.94 + 0.06), 0.85 * (d * 0.94 + 0.06), 1.0 * (d * 0.91 + 0.09)];
  const r = Math.max(0, Math.min(1, rain));
  const t = Math.max(0, Math.min(1, thunder)) * r;
  const kr = (1 - r * 0.5) * (1 - t * 0.5), kb = (1 - r * 0.4) * (1 - t * 0.5);
  return [base[0] * kr, base[1] * kr, base[2] * kb];
}
