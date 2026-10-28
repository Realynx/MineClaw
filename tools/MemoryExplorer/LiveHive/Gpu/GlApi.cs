using Avalonia.OpenGL;

namespace MineClaw.MemoryExplorer.LiveHive.Gpu;

/// <summary>GL constants used by the free camera (GLES 3.0 / GL 3.3 core subset).</summary>
internal static class Gl
{
    public const int ARRAY_BUFFER = 0x8892, ELEMENT_ARRAY_BUFFER = 0x8893, STATIC_DRAW = 0x88E4, DYNAMIC_DRAW = 0x88E8, STREAM_DRAW = 0x88E0;
    public const int TRIANGLES = 4, LINES = 1, FLOAT = 0x1406, UNSIGNED_BYTE = 0x1401, UNSIGNED_INT = 0x1405;
    public const int DEPTH_TEST = 0x0B71, BLEND = 0x0BE2, CULL_FACE = 0x0B44, SCISSOR_TEST = 0x0C11, STENCIL_TEST = 0x0B90;
    public const int SRC_ALPHA = 0x0302, ONE_MINUS_SRC_ALPHA = 0x0303;
    public const int TEXTURE_2D = 0x0DE1, TEXTURE0 = 0x84C0, RGBA = 0x1908, RGBA8 = 0x8058;
    public const int TEXTURE_MIN_FILTER = 0x2801, TEXTURE_MAG_FILTER = 0x2800, NEAREST = 0x2600, TEXTURE_WRAP_S = 0x2802, TEXTURE_WRAP_T = 0x2803, CLAMP_TO_EDGE = 0x812F;
    public const int COLOR_BUFFER_BIT = 0x4000, DEPTH_BUFFER_BIT = 0x0100;
    public const int LEQUAL = 0x0203, LESS = 0x0201, GREATER = 0x0204, ALWAYS = 0x0207;
    public const int VERTEX_SHADER = 0x8B31, FRAGMENT_SHADER = 0x8B30;
    public const int FRAMEBUFFER = 0x8D40, READ_FRAMEBUFFER = 0x8CA8, DRAW_FRAMEBUFFER = 0x8CA9, RENDERBUFFER = 0x8D41;
    public const int COLOR_ATTACHMENT0 = 0x8CE0, DEPTH_ATTACHMENT = 0x8D00, DEPTH_COMPONENT24 = 0x81A6, DEPTH_COMPONENT16 = 0x81A5;
    public const int FRAMEBUFFER_COMPLETE = 0x8CD5;
    public const int FRAMEBUFFER_BINDING = 0x8CA6;
}

/// <summary>The few GL entry points <see cref="GlInterface"/> does not wrap, loaded through its GetProcAddress.</summary>
internal sealed unsafe class GlExtra
{
    private readonly delegate* unmanaged<int, float, float, float, void> _uniform3f;
    private readonly delegate* unmanaged<int, float, float, float, float, void> _uniform4f;
    private readonly delegate* unmanaged<int, int, void> _blendFunc;
    private readonly delegate* unmanaged<int, int, int, int, void> _blendFuncSeparate;
    private readonly delegate* unmanaged<int, nint, nint, void*, void> _bufferSubData;
    private readonly delegate* unmanaged<byte, byte, byte, byte, void> _colorMask;
    private readonly delegate* unmanaged<int, void> _disableAttrib;
    private readonly delegate* unmanaged<int, int, int, int, int, int, void*, void> _readPixels;

    public GlExtra(GlInterface gl)
    {
        nint P(string n) => gl.GetProcAddress(n) is var p && p != 0 ? p : throw new InvalidOperationException($"GL entry point {n} missing");
        _uniform3f = (delegate* unmanaged<int, float, float, float, void>)P("glUniform3f");
        _uniform4f = (delegate* unmanaged<int, float, float, float, float, void>)P("glUniform4f");
        _blendFunc = (delegate* unmanaged<int, int, void>)P("glBlendFunc");
        _blendFuncSeparate = (delegate* unmanaged<int, int, int, int, void>)P("glBlendFuncSeparate");
        _bufferSubData = (delegate* unmanaged<int, nint, nint, void*, void>)P("glBufferSubData");
        _colorMask = (delegate* unmanaged<byte, byte, byte, byte, void>)P("glColorMask");
        _disableAttrib = (delegate* unmanaged<int, void>)P("glDisableVertexAttribArray");
        _readPixels = (delegate* unmanaged<int, int, int, int, int, int, void*, void>)P("glReadPixels");
    }

    public void Uniform3f(int loc, float x, float y, float z) => _uniform3f(loc, x, y, z);
    public void Uniform4f(int loc, float x, float y, float z, float w) => _uniform4f(loc, x, y, z, w);
    public void BlendFunc(int s, int d) => _blendFunc(s, d);
    public void BlendFuncSeparate(int sc, int dc, int sa, int da) => _blendFuncSeparate(sc, dc, sa, da);
    public void BufferSubData(int target, nint offset, nint size, void* data) => _bufferSubData(target, offset, size, data);
    public void DisableVertexAttribArray(int i) => _disableAttrib(i);
    public void ReadPixels(int x, int y, int w, int h, int format, int type, void* data) => _readPixels(x, y, w, h, format, type, data);
    public void ColorMask(bool r, bool g, bool b, bool a) => _colorMask((byte)(r ? 1 : 0), (byte)(g ? 1 : 0), (byte)(b ? 1 : 0), (byte)(a ? 1 : 0));
}
