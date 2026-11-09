/** Adaptive quality must never wait for the GPU on the input/render thread. */
interface TimerExtension { TIME_ELAPSED_EXT: number; GPU_DISJOINT_EXT: number }

export class FrameTiming {
  private readonly gl: WebGL2RenderingContext | null;
  private readonly ext: TimerExtension | null;
  private query: WebGLQuery | null = null;
  private pending: { query: WebGLQuery; cpu: number; at: number } | null = null;
  private start = 0;
  private active = false;

  constructor(gl: WebGLRenderingContext | WebGL2RenderingContext, private readonly clock = () => performance.now()) {
    this.gl = 'createQuery' in gl && typeof gl.createQuery === 'function' ? gl : null;
    this.ext = this.gl?.getExtension('EXT_disjoint_timer_query_webgl2') as TimerExtension | null ?? null;
  }

  begin(): boolean {
    if (this.active || this.pending) return false;
    this.start = this.clock();
    this.active = true;
    this.query = this.ext ? this.gl?.createQuery() ?? null : null;
    if (this.query && this.ext) this.gl?.beginQuery(this.ext.TIME_ELAPSED_EXT, this.query);
    return true;
  }

  end(now: number): number | null {
    if (!this.active) return null;
    this.active = false;
    const cpu = Math.max(0, this.clock() - this.start);
    if (!this.query || !this.ext) return cpu; // unsupported GPU timing: CPU cost, never synchronous readback
    this.gl?.endQuery(this.ext.TIME_ELAPSED_EXT);
    this.pending = { query: this.query, cpu, at: now };
    this.query = null;
    return null;
  }

  poll(now: number): number | null {
    const p = this.pending;
    const gl = this.gl;
    if (!p || !this.ext || !gl) return null;
    const ready = gl.getQueryParameter(p.query, gl.QUERY_RESULT_AVAILABLE) === true;
    if (!ready && now - p.at < 2000) return null;
    // Query results are only read when ready. Context loss, disjoint timing or timeout fall back to the CPU sample.
    const disjoint = gl.getParameter(this.ext.GPU_DISJOINT_EXT) === true;
    const ns = ready && !disjoint ? Number(gl.getQueryParameter(p.query, gl.QUERY_RESULT)) : 0;
    gl.deleteQuery(p.query);
    this.pending = null;
    return Math.max(p.cpu, Number.isFinite(ns) ? ns / 1e6 : 0);
  }

  dispose(): void {
    if (this.query) this.gl?.deleteQuery(this.query);
    if (this.pending) this.gl?.deleteQuery(this.pending.query);
    this.query = null; this.pending = null;
  }
}
