// Rain streaks or snowflakes around the camera (world-anchored so they don't slide when the camera moves), chosen by the
// biome under the view (Weather.At: cold biomes snow, dry ones stay clear), and thunder flashes.

import * as THREE from 'three';

const R = 24, H = 32, N = 2400;

const vertex = /* glsl */ `
attribute float aEnd;
uniform vec3 uCam;
uniform float uTime;
uniform float uSnow;
varying float vEnd;
void main() {
  vec3 p = position;
  vec2 xz = uCam.xz + mod(p.xz - uCam.xz, ${2 * R}.0) - ${R}.0;
  float r = fract(p.x * 13.1 + p.z * 7.3);
  float fall = mix(18.0 + r * 6.0, 1.6 + r * 1.4, uSnow);
  float len = mix(0.7, 0.1, uSnow);
  float sway = uSnow * sin(uTime * 1.3 + p.x * 3.1) * 0.6;
  float y = uCam.y - ${H / 2}.0 + mod(p.y - uTime * fall - uCam.y, ${H}.0) - aEnd * len;
  vEnd = aEnd;
  gl_Position = projectionMatrix * viewMatrix * vec4(xz.x + sway + aEnd * mix(0.05, 0.08, uSnow), y, xz.y + sway * 0.5, 1.0);
}
`;

const fragment = /* glsl */ `
uniform float uRain;
uniform float uSnow;
uniform vec3 uColour;
varying float vEnd;
void main() {
  vec3 c = mix(uColour, vec3(1.0), uSnow);
  gl_FragColor = vec4(c, uRain * mix(0.55 - 0.35 * vEnd, 0.9, uSnow));
}
`;

export class WeatherLayer {
  readonly object: THREE.LineSegments;
  private readonly mat: THREE.ShaderMaterial;
  private flash = 0;
  private nextBolt = 0;

  constructor(private readonly random: () => number = Math.random) {
    const pos = new Float32Array(N * 2 * 3);
    const end = new Float32Array(N * 2);
    for (let i = 0; i < N; i++) {
      const x = random() * 2 * R, y = random() * H, z = random() * 2 * R;
      pos.set([x, y, z, x, y, z], i * 6);
      end[i * 2 + 1] = 1;
    }
    const g = new THREE.BufferGeometry();
    g.setAttribute('position', new THREE.BufferAttribute(pos, 3));
    g.setAttribute('aEnd', new THREE.BufferAttribute(end, 1));
    g.boundingSphere = new THREE.Sphere(new THREE.Vector3(), 1e9);
    this.mat = new THREE.ShaderMaterial({
      vertexShader: vertex,
      fragmentShader: fragment,
      transparent: true,
      depthWrite: false,
      uniforms: { uCam: { value: new THREE.Vector3() }, uTime: { value: 0 }, uRain: { value: 0 }, uSnow: { value: 0 }, uColour: { value: new THREE.Color(0.7, 0.75, 0.85) } },
    });
    this.object = new THREE.LineSegments(g, this.mat);
    this.object.frustumCulled = false;
    this.object.renderOrder = 8;
    this.object.name = 'rain';
  }

  /** Returns the flash brightness to add to the world this frame. */
  update(cam: THREE.Vector3, timeS: number, rain: number, thunder: number, enabled: boolean, precip: 'rain' | 'snow' | 'none' = 'rain'): number {
    const r = enabled && precip !== 'none' ? Math.max(0, Math.min(1, rain)) : 0;
    this.mat.uniforms.uSnow.value = precip === 'snow' ? 1 : 0;
    this.object.visible = r > 0.01;
    this.mat.uniforms.uCam.value.copy(cam);
    this.mat.uniforms.uTime.value = timeS;
    this.mat.uniforms.uRain.value = r;
    const t = enabled ? Math.max(0, Math.min(1, thunder)) * r : 0;
    if (t > 0.2 && timeS > this.nextBolt) {
      this.flash = 0.35;
      this.nextBolt = timeS + 6 + this.random() * 20 * (1.2 - t);
    }
    this.flash = Math.max(0, this.flash - 0.02);
    return this.flash;
  }

  dispose(): void {
    this.object.geometry.dispose();
    this.mat.dispose();
  }
}
