using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia.OpenGL;
using MineClaw.Render;

namespace MineClaw.MemoryExplorer.LiveHive.Gpu;

/// <summary>
/// The GL side of the GPU free camera (GL thread only): section VBOs uploaded from the <see cref="SectionStore"/> queue within a
/// per-frame budget, one shared quad index buffer, the atlas texture, and the passes — sky (fullscreen triangle), solid sections
/// front to back (alpha-tested cutouts), entities, overlay lines (visible, then hidden parts blended), translucent sections and
/// shadows back to front — into an own FBO with depth, blitted to the control's framebuffer. Works on GLES 3.0 (ANGLE) and
/// desktop GL 3.3 core.
/// </summary>
internal sealed unsafe class GlFreecamRenderer : IDisposable
{
    private GlInterface _gl = null!;
    private GlExtra _x = null!;
    private int _world, _sky, _vao, _ebo, _eboQuads, _tex, _fbo, _color, _depth, _fbw, _fbh, _dynSolid, _dynTrans, _lines, _rain;
    private int _uVP, _uOrigin, _uTex, _uGrid, _uRem, _uStale, _uStaleColour, _uFogColour, _uFog, _uAlphaTest, _uOpaque, _uXA, _uXB, _uXP;
    private int _sF, _sR, _sU, _sTan, _sTop, _sHorizon, _sVoid;
    private int _lineCount;
    private readonly Stack<int> _freeVbos = new();
    public bool Textured { get; private set; }
    /// <summary>The atlas the texture was built from (a different one means a rebuild).</summary>
    public TextureAtlas? Atlas { get; private set; }
    private int _gridRows = GpuAtlas.Rows;
    /// <summary>Set to save the next frame as a PNG (profiling / checks).</summary>
    public string? ShotPath { get; set; }
    public string Info { get; private set; } = "";

    public void Init(GlInterface gl, GlVersion ver, TextureAtlas? atlas)
    {
        _gl = gl;
        _x = new GlExtra(gl);
        bool es = ver.Type == GlProfileType.OpenGLES;
        if (es ? ver.Major < 3 : ver.Major * 10 + ver.Minor < 33) throw new NotSupportedException($"needs GLES 3.0 or GL 3.3, have {ver}");
        string head = es ? "#version 300 es\nprecision highp float;\nprecision highp int;\n" : "#version 330 core\n";
        _world = Program(head + WorldVs, head + WorldFs, ["aPos", "aUv", "aCell", "aColor"]);
        _sky = Program(head + SkyVs, head + SkyFs, []);
        _uVP = U(_world, "uVP"); _uOrigin = U(_world, "uOrigin"); _uTex = U(_world, "uTex"); _uGrid = U(_world, "uGrid"); _uRem = U(_world, "uRem");
        _uStale = U(_world, "uStale"); _uStaleColour = U(_world, "uStaleColour"); _uFogColour = U(_world, "uFogColour"); _uFog = U(_world, "uFog");
        _uAlphaTest = U(_world, "uAlphaTest"); _uOpaque = U(_world, "uOpaque");
        _uXA = U(_world, "uXA"); _uXB = U(_world, "uXB"); _uXP = U(_world, "uXP");
        _sF = U(_sky, "uF"); _sR = U(_sky, "uR"); _sU = U(_sky, "uU"); _sTan = U(_sky, "uTan"); _sTop = U(_sky, "uTop"); _sHorizon = U(_sky, "uHorizon"); _sVoid = U(_sky, "uVoid");
        _vao = gl.GenVertexArray();
        gl.BindVertexArray(_vao);
        _ebo = gl.GenBuffer();
        Indices(16384);
        _dynSolid = gl.GenBuffer(); _dynTrans = gl.GenBuffer(); _lines = gl.GenBuffer(); _rain = gl.GenBuffer();
        var (w, h, px) = GpuAtlas.Build(atlas);
        _gridRows = GpuAtlas.RowsFor(atlas);
        Textured = atlas != null;
        Atlas = atlas;
        _tex = gl.GenTexture();
        gl.BindTexture(Gl.TEXTURE_2D, _tex);
        fixed (byte* p = px) gl.TexImage2D(Gl.TEXTURE_2D, 0, Gl.RGBA8, w, h, 0, Gl.RGBA, Gl.UNSIGNED_BYTE, (nint)p);
        gl.TexParameteri(Gl.TEXTURE_2D, Gl.TEXTURE_MIN_FILTER, Gl.NEAREST);
        gl.TexParameteri(Gl.TEXTURE_2D, Gl.TEXTURE_MAG_FILTER, Gl.NEAREST);
        gl.TexParameteri(Gl.TEXTURE_2D, Gl.TEXTURE_WRAP_S, Gl.CLAMP_TO_EDGE);
        gl.TexParameteri(Gl.TEXTURE_2D, Gl.TEXTURE_WRAP_T, Gl.CLAMP_TO_EDGE);
        Info = $"{ver} {gl.GetString(0x1F01)}"; // GL_RENDERER
    }

    private int U(int prog, string name) => _gl.GetUniformLocationString(prog, name);

    private int Program(string vs, string fs, string[] attribs)
    {
        int v = _gl.CreateShader(Gl.VERTEX_SHADER), f = _gl.CreateShader(Gl.FRAGMENT_SHADER);
        if (_gl.CompileShaderAndGetError(v, vs) is { Length: > 0 } ev) throw new InvalidOperationException("vertex shader: " + ev);
        if (_gl.CompileShaderAndGetError(f, fs) is { Length: > 0 } ef) throw new InvalidOperationException("fragment shader: " + ef);
        int p = _gl.CreateProgram();
        _gl.AttachShader(p, v);
        _gl.AttachShader(p, f);
        for (int i = 0; i < attribs.Length; i++) _gl.BindAttribLocationString(p, i, attribs[i]);
        if (_gl.LinkProgramAndGetError(p) is { Length: > 0 } el) throw new InvalidOperationException("link: " + el);
        _gl.DeleteShader(v);
        _gl.DeleteShader(f);
        return p;
    }

    /// <summary>Grows the shared quad index buffer (0,1,2, 0,2,3 per quad) to at least <paramref name="quads"/>.</summary>
    private void Indices(int quads)
    {
        if (quads <= _eboQuads) return;
        int n = Math.Max(quads, _eboQuads * 2);
        var idx = new uint[n * 6];
        for (uint q = 0, i = 0; q < n; q++)
        {
            uint b = q * 4;
            idx[i++] = b; idx[i++] = b + 1; idx[i++] = b + 2; idx[i++] = b; idx[i++] = b + 2; idx[i++] = b + 3;
        }
        _gl.BindBuffer(Gl.ELEMENT_ARRAY_BUFFER, _ebo);
        fixed (uint* p = idx) _gl.BufferData(Gl.ELEMENT_ARRAY_BUFFER, idx.Length * 4, (nint)p, Gl.STATIC_DRAW);
        _eboQuads = n;
    }

    private int Vbo() => _freeVbos.Count > 0 ? _freeVbos.Pop() : _gl.GenBuffer();
    private void Free(ref int vbo) { if (vbo != 0) _freeVbos.Push(vbo); vbo = 0; }

    private void Upload(ref int vbo, GpuVertex[]? data, int count, int usage)
    {
        if (count == 0 || data == null) { Free(ref vbo); return; }
        if (vbo == 0) vbo = Vbo();
        _gl.BindBuffer(Gl.ARRAY_BUFFER, vbo);
        fixed (GpuVertex* p = data) _gl.BufferData(Gl.ARRAY_BUFFER, count * GpuVertex.Size, (nint)p, usage);
        Indices(count / 4);
    }

    /// <summary>Applies queued meshes until the budget runs out (time or count). Returns how many were applied.</summary>
    public int Ingest(SectionStore store, FrameScene scene, double budgetMs, int maxUploads)
    {
        long t0 = Stopwatch.GetTimestamp();
        int n = 0;
        _gl.BindVertexArray(_vao);
        while (n < maxUploads && (Stopwatch.GetTimestamp() - t0) * FreecamStats.TickMs < budgetMs && store.Ready.TryDequeue(out var up))
        {
            try { Apply(up, scene); }
            finally { up.Release(); }
            n++;
        }
        if (n > 0) FreecamStats.Shared.UploadMs.Add((Stopwatch.GetTimestamp() - t0) * FreecamStats.TickMs);
        Interlocked.Exchange(ref FreecamStats.Shared.SectionsLive, scene.Sections.Count);
        return n;
    }

    /// <summary>One queued change: drop everything, drop a column, drop an emptied section, or upload a section's meshes.</summary>
    private void Apply(MeshUpload up, FrameScene scene)
    {
        if (up.DropAll)
        {
            foreach (var s in scene.Sections.Values) FreeSection(s);
            scene.Sections.Clear();
        }
        else if (up.DropColumn)
        {
            for (int sy = 0; sy < SectionStore.Sections; sy++)
                if (scene.Sections.Remove(new SectionKey(up.Key.Cx, sy, up.Key.Cz), out var s)) FreeSection(s);
        }
        else if (up.SolidCount + up.TranslucentCount == 0)
        {
            if (scene.Sections.Remove(up.Key, out var s)) FreeSection(s);
        }
        else UploadSection(up, scene);
    }

    private void FreeSection(GpuSection s)
    {
        Free(ref s.SolidVbo);
        Free(ref s.TransVbo);
    }

    private void UploadSection(MeshUpload up, FrameScene scene)
    {
        if (!scene.Sections.TryGetValue(up.Key, out var s)) scene.Sections[up.Key] = s = new GpuSection { Key = up.Key };
        Upload(ref s.SolidVbo, up.Solid, up.SolidCount, Gl.STATIC_DRAW);
        Upload(ref s.TransVbo, up.Translucent, up.TranslucentCount, Gl.STATIC_DRAW);
        s.SolidCount = up.SolidCount;
        s.TransCount = up.TranslucentCount;
    }

    private void Target(int w, int h)
    {
        if (_fbo != 0 && w == _fbw && h == _fbh) return;
        if (_fbo != 0) { _gl.DeleteFramebuffer(_fbo); _gl.DeleteRenderbuffer(_color); _gl.DeleteRenderbuffer(_depth); }
        _fbo = _gl.GenFramebuffer();
        _gl.BindFramebuffer(Gl.FRAMEBUFFER, _fbo);
        _color = _gl.GenRenderbuffer();
        _gl.BindRenderbuffer(Gl.RENDERBUFFER, _color);
        _gl.RenderbufferStorage(Gl.RENDERBUFFER, Gl.RGBA8, w, h);
        _gl.FramebufferRenderbuffer(Gl.FRAMEBUFFER, Gl.COLOR_ATTACHMENT0, Gl.RENDERBUFFER, _color);
        _depth = _gl.GenRenderbuffer();
        _gl.BindRenderbuffer(Gl.RENDERBUFFER, _depth);
        _gl.RenderbufferStorage(Gl.RENDERBUFFER, Gl.DEPTH_COMPONENT24, w, h);
        _gl.FramebufferRenderbuffer(Gl.FRAMEBUFFER, Gl.DEPTH_ATTACHMENT, Gl.RENDERBUFFER, _depth);
        if (_gl.CheckFramebufferStatus(Gl.FRAMEBUFFER) != Gl.FRAMEBUFFER_COMPLETE) throw new InvalidOperationException("framebuffer incomplete");
        (_fbw, _fbh) = (w, h);
    }

    private void Attribs(int vbo)
    {
        _gl.BindBuffer(Gl.ARRAY_BUFFER, vbo);
        _gl.VertexAttribPointer(0, 3, Gl.FLOAT, 0, GpuVertex.Size, 0);
        _gl.VertexAttribPointer(1, 2, Gl.FLOAT, 0, GpuVertex.Size, 12);
        _gl.VertexAttribPointer(2, 4, Gl.UNSIGNED_BYTE, 1, GpuVertex.Size, 20);
        _gl.VertexAttribPointer(3, 4, Gl.UNSIGNED_BYTE, 1, GpuVertex.Size, 24);
    }

    private void Dyn(int vbo, VertexList list)
    {
        if (list.Count == 0) return;
        _gl.BindBuffer(Gl.ARRAY_BUFFER, vbo);
        fixed (GpuVertex* p = list.V) _gl.BufferData(Gl.ARRAY_BUFFER, list.Count * GpuVertex.Size, (nint)p, Gl.STREAM_DRAW);
        Indices(list.Count / 4);
    }

    private static (float, float, float) Rgb(uint c) => ((c >> 16 & 0xff) / 255f, (c >> 8 & 0xff) / 255f, (c & 0xff) / 255f);

    public int Draw(FrameScene sc, int fb, int w, int h)
    {
        BeginFrame(w, h);
        var (top, horizon, voidC) = sc.Sky; // day/night: the CPU eye's sky at the world time
        int draws = DrawSky(sc, top, horizon, voidC);
        draws += DrawSolids(sc, horizon);
        draws += DrawBodies(sc);
        draws += DrawLines(sc);
        draws += DrawTranslucent(sc);
        draws += DrawPrecipitation(sc);
        _gl.DepthMask(1);
        _gl.Disable(Gl.BLEND);
        _gl.Disable(Gl.DEPTH_TEST);
        if (ShotPath is { } shot)
        {
            ShotPath = null;
            SaveShot(shot, w, h);
        }
        Present(fb, w, h);
        return draws;
    }

    /// <summary>Our framebuffer at the frame size, cleared, with the default state.</summary>
    private void BeginFrame(int w, int h)
    {
        var gl = _gl;
        Target(w, h);
        gl.BindFramebuffer(Gl.FRAMEBUFFER, _fbo);
        gl.Viewport(0, 0, w, h);
        gl.Disable(Gl.SCISSOR_TEST);
        gl.Disable(Gl.STENCIL_TEST);
        gl.Disable(Gl.CULL_FACE);
        gl.DepthMask(1);
        _x.ColorMask(true, true, true, true);
        gl.ClearColor(0, 0, 0, 1);
        gl.ClearDepth(1);
        gl.Clear(Gl.COLOR_BUFFER_BIT | Gl.DEPTH_BUFFER_BIT);
        gl.BindVertexArray(_vao);
    }

    private int DrawSky(FrameScene sc, uint top, uint horizon, uint voidC)
    {
        var gl = _gl;
        gl.Disable(Gl.DEPTH_TEST);
        gl.Disable(Gl.BLEND);
        DisableAttribs();
        gl.UseProgram(_sky);
        _x.Uniform3f(_sF, sc.Forward.X, sc.Forward.Y, sc.Forward.Z);
        _x.Uniform3f(_sR, sc.Right.X, sc.Right.Y, sc.Right.Z);
        _x.Uniform3f(_sU, sc.Up.X, sc.Up.Y, sc.Up.Z);
        _x.Uniform3f(_sTan, sc.TanX, sc.TanY, 0);
        var (r, g, b) = Rgb(top); _x.Uniform3f(_sTop, r, g, b);
        (r, g, b) = Rgb(horizon); _x.Uniform3f(_sHorizon, r, g, b);
        (r, g, b) = Rgb(voidC); _x.Uniform3f(_sVoid, r, g, b);
        gl.DrawArrays(Gl.TRIANGLES, 0, 3);
        return 1;
    }

    /// <summary>The world program (atlas, stale tint, fog in the horizon colour) and the opaque sections, x-rayed.</summary>
    private int DrawSolids(FrameScene sc, uint horizon)
    {
        var gl = _gl;
        EnableAttribs();
        gl.UseProgram(_world);
        var vp = sc.ViewProj;
        gl.UniformMatrix4fv(_uVP, 1, false, &vp);
        gl.ActiveTexture(Gl.TEXTURE0);
        gl.BindTexture(Gl.TEXTURE_2D, _tex);
        gl.Uniform1i(_uTex, 0);
        _x.Uniform3f(_uGrid, GpuAtlas.Cols, _gridRows, 0);
        var (r, g, b) = Rgb(OverlayPainter.StaleColour); _x.Uniform3f(_uStaleColour, r, g, b);
        (r, g, b) = Rgb(horizon); _x.Uniform3f(_uFogColour, r, g, b);
        _x.Uniform3f(_uFog, sc.MaxDistance * 0.6f, sc.MaxDistance, 0);
        gl.Enable(Gl.DEPTH_TEST);
        gl.DepthFunc(Gl.LEQUAL);
        gl.Uniform1f(_uAlphaTest, 0.5f);
        gl.Uniform1f(_uOpaque, 1);
        XRay(sc, true);
        gl.BindBuffer(Gl.ELEMENT_ARRAY_BUFFER, _ebo);
        int draws = 0;
        foreach (var s in sc.SolidDraw)
        {
            Section(sc, s);
            Attribs(s.SolidVbo);
            gl.DrawElements(Gl.TRIANGLES, s.SolidCount / 4 * 6, Gl.UNSIGNED_INT, 0);
            draws++;
        }
        return draws;
    }

    /// <summary>Entities (camera-relative already): never x-rayed (the bodies are what the cut is for).</summary>
    private int DrawBodies(FrameScene sc)
    {
        var gl = _gl;
        XRay(sc, false);
        gl.Uniform1f(_uRem, 0); gl.Uniform1f(_uStale, 0); _x.Uniform3f(_uOrigin, 0, 0, 0);
        if (sc.DynSolid.Count == 0) return 0;
        Dyn(_dynSolid, sc.DynSolid);
        gl.BindBuffer(Gl.ELEMENT_ARRAY_BUFFER, _ebo);
        Attribs(_dynSolid);
        gl.DrawElements(Gl.TRIANGLES, sc.DynSolid.Count / 4 * 6, Gl.UNSIGNED_INT, 0);
        return 1;
    }

    /// <summary>Overlay lines: visible parts opaque, hidden parts blended with their own alpha (plans 0.25, dangers 0.45).</summary>
    private int DrawLines(FrameScene sc)
    {
        var gl = _gl;
        if (sc.LinesChanged) UploadLines(sc);
        if (_lineCount <= 0) return 0;
        _x.Uniform3f(_uOrigin, (float)(sc.LinesOrigin.X - sc.Pose.X), (float)(sc.LinesOrigin.Y - sc.Pose.Y), (float)(sc.LinesOrigin.Z - sc.Pose.Z));
        Attribs(_lines);
        gl.Uniform1f(_uAlphaTest, 0);
        gl.DrawArrays(Gl.LINES, 0, _lineCount);
        gl.DepthFunc(Gl.GREATER);
        gl.DepthMask(0);
        gl.Enable(Gl.BLEND);
        _x.BlendFuncSeparate(Gl.SRC_ALPHA, Gl.ONE_MINUS_SRC_ALPHA, 0, 1);
        gl.Uniform1f(_uOpaque, 0);
        gl.Uniform1f(_uAlphaTest, 0.01f);
        gl.DrawArrays(Gl.LINES, 0, _lineCount);
        gl.DepthFunc(Gl.LEQUAL);
        return 2;
    }

    private void UploadLines(FrameScene sc)
    {
        sc.LinesChanged = false;
        _lineCount = sc.Lines.Count;
        if (_lineCount <= 0) return;
        _gl.BindBuffer(Gl.ARRAY_BUFFER, _lines);
        fixed (GpuVertex* p = sc.Lines.V) _gl.BufferData(Gl.ARRAY_BUFFER, _lineCount * GpuVertex.Size, (nint)p, Gl.STATIC_DRAW);
    }

    /// <summary>Translucent: water/ice/portal sections far to near, then shadows; no depth writes.</summary>
    private int DrawTranslucent(FrameScene sc)
    {
        var gl = _gl;
        gl.Enable(Gl.BLEND);
        _x.BlendFuncSeparate(Gl.SRC_ALPHA, Gl.ONE_MINUS_SRC_ALPHA, 0, 1);
        gl.DepthMask(0);
        gl.Uniform1f(_uOpaque, 0);
        gl.Uniform1f(_uAlphaTest, 0.004f);
        gl.BindBuffer(Gl.ELEMENT_ARRAY_BUFFER, _ebo);
        XRay(sc, true);
        int draws = 0;
        foreach (var s in sc.TransDraw)
        {
            Section(sc, s);
            Attribs(s.TransVbo);
            gl.DrawElements(Gl.TRIANGLES, s.TransCount / 4 * 6, Gl.UNSIGNED_INT, 0);
            draws++;
        }
        XRay(sc, false);
        if (sc.DynTrans.Count == 0) return draws;
        gl.Uniform1f(_uRem, 0); gl.Uniform1f(_uStale, 0); _x.Uniform3f(_uOrigin, 0, 0, 0);
        Dyn(_dynTrans, sc.DynTrans);
        gl.BindBuffer(Gl.ELEMENT_ARRAY_BUFFER, _ebo);
        Attribs(_dynTrans);
        gl.DrawElements(Gl.TRIANGLES, sc.DynTrans.Count / 4 * 6, Gl.UNSIGNED_INT, 0);
        return draws + 1;
    }

    /// <summary>Rain/snow streaks: depth-tested lines, blended, no depth writes (a roof or a tunnel keeps them out).</summary>
    private int DrawPrecipitation(FrameScene sc)
    {
        if (sc.Precipitation.Count == 0) return 0;
        var gl = _gl;
        gl.Uniform1f(_uRem, 0); gl.Uniform1f(_uStale, 0); _x.Uniform3f(_uOrigin, 0, 0, 0);
        gl.BindBuffer(Gl.ARRAY_BUFFER, _rain);
        fixed (GpuVertex* p = sc.Precipitation.V) gl.BufferData(Gl.ARRAY_BUFFER, sc.Precipitation.Count * GpuVertex.Size, (nint)p, Gl.STREAM_DRAW);
        Attribs(_rain);
        gl.Uniform1f(_uAlphaTest, 0.01f);
        gl.DrawArrays(Gl.LINES, 0, sc.Precipitation.Count);
        return 1;
    }

    /// <summary>Reads our framebuffer back (bottom-up RGBA) and writes it as a PNG; a failed shot is skipped.</summary>
    private void SaveShot(string shot, int w, int h)
    {
        try
        {
            var buf = new byte[w * h * 4];
            fixed (byte* p = buf) _x.ReadPixels(0, 0, w, h, Gl.RGBA, Gl.UNSIGNED_BYTE, p);
            var px = new uint[w * h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int i = ((h - 1 - y) * w + x) * 4;
                    px[y * w + x] = (uint)(buf[i] << 16 | buf[i + 1] << 8 | buf[i + 2]);
                }
            File.WriteAllBytes(shot, Png.Encode(px, w, h));
        }
        catch (Exception) { }
    }

    /// <summary>Present: our FBO → the control's.</summary>
    private void Present(int fb, int w, int h)
    {
        var gl = _gl;
        gl.BindFramebuffer(Gl.READ_FRAMEBUFFER, _fbo);
        gl.BindFramebuffer(Gl.DRAW_FRAMEBUFFER, fb);
        gl.BlitFramebuffer(0, 0, w, h, 0, 0, w, h, Gl.COLOR_BUFFER_BIT, Gl.NEAREST);
        gl.BindFramebuffer(Gl.FRAMEBUFFER, fb);
        gl.BindVertexArray(0);
    }

    /// <summary>The x-ray cones (<see cref="FrameScene.XRaySubjects"/>) on for world geometry, off for bodies, drops and lines.</summary>
    private void XRay(FrameScene sc, bool on)
    {
        int n = on ? Math.Min(2, sc.XRaySubjects.Count) : 0;
        var a = n > 0 ? sc.XRaySubjects[0] : default;
        var b = n > 1 ? sc.XRaySubjects[1] : a;
        _x.Uniform3f(_uXA, a.X, a.Y, a.Z);
        _x.Uniform3f(_uXB, b.X, b.Y, b.Z);
        _x.Uniform3f(_uXP, (float)PerspectivePanel.XRayRadius, sc.XRayFloor, n);
    }

    private void Section(FrameScene sc, GpuSection s)
    {
        var k = s.Key;
        _x.Uniform3f(_uOrigin, (float)(k.Cx * 16 - sc.Pose.X), (float)(k.Sy * 16 - sc.Pose.Y), (float)(k.Cz * 16 - sc.Pose.Z));
        _gl.Uniform1f(_uRem, s.Rem);
        _gl.Uniform1f(_uStale, s.Stale);
    }

    private void EnableAttribs() { for (int i = 0; i < 4; i++) _gl.EnableVertexAttribArray(i); }

    private void DisableAttribs() { for (int i = 0; i < 4; i++) _x.DisableVertexAttribArray(i); } // the sky uses gl_VertexID only

    public void Release(FrameScene scene)
    {
        foreach (var s in scene.Sections.Values) { Free(ref s.SolidVbo); Free(ref s.TransVbo); }
        scene.Sections.Clear();
    }

    public void Dispose()
    {
        if (_gl == null) return;
        while (_freeVbos.Count > 0) _gl.DeleteBuffer(_freeVbos.Pop());
        foreach (var b in new[] { _ebo, _dynSolid, _dynTrans, _lines, _rain }) if (b != 0) _gl.DeleteBuffer(b);
        if (_tex != 0) _gl.DeleteTexture(_tex);
        if (_fbo != 0) { _gl.DeleteFramebuffer(_fbo); _gl.DeleteRenderbuffer(_color); _gl.DeleteRenderbuffer(_depth); }
        if (_vao != 0) _gl.DeleteVertexArray(_vao);
        if (_world != 0) _gl.DeleteProgram(_world);
        if (_sky != 0) _gl.DeleteProgram(_sky);
    }

    // ------------------------------------------------------------------ shaders

    private const string WorldVs = """
        in vec3 aPos; in vec2 aUv; in vec4 aCell; in vec4 aColor;
        uniform mat4 uVP; uniform vec3 uOrigin;
        out vec2 vUv; flat out vec2 vCell; flat out int vFlags; out float vLight; out vec4 vColor; out vec3 vRel;
        void main() {
            vec3 p = aPos + uOrigin;
            vRel = p;
            gl_Position = uVP * vec4(p, 1.0);
            vUv = aUv;
            vCell = floor(aCell.xy * 255.0 + 0.5);
            vLight = aCell.z;
            vFlags = int(aCell.w * 255.0 + 0.5);
            vColor = aColor;
        }
        """;

    private const string WorldFs = """
        in vec2 vUv; flat in vec2 vCell; flat in int vFlags; in float vLight; in vec4 vColor; in vec3 vRel;
        uniform sampler2D uTex; uniform vec3 uGrid; uniform float uRem; uniform float uStale; uniform vec3 uStaleColour;
        uniform vec3 uFogColour; uniform vec3 uFog; uniform float uAlphaTest; uniform float uOpaque;
        uniform vec3 uXA; uniform vec3 uXB; uniform vec3 uXP;
        out vec4 oColor;
        // The x-ray cone (ViewCut): apex at the camera (the origin here), a disk of uXP.x blocks at the subject, never below uXP.y.
        bool cutBy(vec3 s) {
            float d = length(s);
            if (d < 1.5) return false;
            vec3 u = s / d;
            float t = dot(vRel, u);
            if (t <= 0.0 || t >= d) return false;
            float r = uXP.x * t / d + 0.3;
            vec3 q = vRel - u * t;
            return dot(q, q) <= r * r;
        }
        void main() {
            if (uXP.z > 0.5 && vRel.y >= uXP.y - 0.001 && (cutBy(uXA) || (uXP.z > 1.5 && cutBy(uXB)))) discard;
            vec4 c = vColor;
            if ((vFlags & 1) != 0) {
                vec2 uv = (vFlags & 2) != 0 ? fract(vUv) : vUv;
                uv = (vFlags & 4) != 0 ? clamp(uv, vec2(0.002), vec2(3.998, 1.998)) : clamp(uv, 0.002, 0.998);
                c *= texture(uTex, (vCell + uv) / uGrid.xy);
            }
            if (c.a < uAlphaTest) discard;
            float k = vLight;
            if (uRem > 0.5) k = max(k, 0.45) * 0.8;
            vec3 rgb = c.rgb * k;
            rgb = mix(rgb, uStaleColour, uStale);
            float f = clamp((length(vRel) - uFog.x) / max(1.0, uFog.y - uFog.x), 0.0, 0.85);
            rgb = mix(rgb, uFogColour, f);
            oColor = vec4(rgb, uOpaque > 0.5 ? 1.0 : c.a);
        }
        """;

    private const string SkyVs = """
        out vec2 vNdc;
        void main() {
            vec2 p = vec2(gl_VertexID == 1 ? 3.0 : -1.0, gl_VertexID == 2 ? 3.0 : -1.0);
            vNdc = p;
            gl_Position = vec4(p, 0.0, 1.0);
        }
        """;

    private const string SkyFs = """
        in vec2 vNdc;
        uniform vec3 uF; uniform vec3 uR; uniform vec3 uU; uniform vec3 uTan; uniform vec3 uTop; uniform vec3 uHorizon; uniform vec3 uVoid;
        out vec4 oColor;
        void main() {
            vec3 d = normalize(uF + uR * vNdc.x * uTan.x + uU * vNdc.y * uTan.y);
            vec3 c = d.y >= 0.0 ? mix(uHorizon, uTop, min(1.0, d.y * 1.6)) : mix(uHorizon * 0.55, uVoid, min(1.0, -d.y * 2.2));
            oColor = vec4(c, 1.0);
        }
        """;
}
