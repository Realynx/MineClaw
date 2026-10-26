namespace MineClaw.Protocol;

/// <summary>A wire-format violation. After one of these the stream position is unknown and the connection must be dropped.</summary>
public class ProtocolException : IOException
{
    public ProtocolException(string message) : base(message) { }
    public ProtocolException(string message, Exception inner) : base(message, inner) { }
}
