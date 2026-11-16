namespace RemoteLAN.Protocol.Messages;

public sealed class SendCtrlAltDelMessage
{
    private static readonly byte[] EmptyPayload = [];

    public byte[] Serialize() => EmptyPayload;

    public static SendCtrlAltDelMessage Deserialize(byte[]? data = null)
    {
        return new SendCtrlAltDelMessage();
    }
}

/// <summary>Asks the agent to draw its mouse cursor into the streamed frames, or to stop.</summary>
public sealed class SetCursorCaptureMessage
{
    public bool Enabled { get; set; }

    public byte[] Serialize() => [(byte)(Enabled ? 1 : 0)];

    public static SetCursorCaptureMessage Deserialize(byte[]? data)
    {
        return new SetCursorCaptureMessage { Enabled = data is { Length: > 0 } && data[0] != 0 };
    }
}

public enum PowerActionType : byte
{
    Lock = 1,
    Sleep = 2,
    Restart = 3,
    Shutdown = 4
}

public sealed class PowerActionMessage
{
    public PowerActionType Action { get; set; }

    public byte[] Serialize()
    {
        return [(byte)Action];
    }

    public static PowerActionMessage Deserialize(byte[] data)
    {
        if (data == null || data.Length == 0)
        {
            return new PowerActionMessage { Action = PowerActionType.Lock };
        }
        return new PowerActionMessage { Action = (PowerActionType)data[0] };
    }
}
