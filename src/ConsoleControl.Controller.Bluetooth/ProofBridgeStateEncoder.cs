using System.Buffers.Binary;
using ConsoleControl.Core;

namespace ConsoleControl.Controller.Bluetooth;

public static class ProofBridgeStateEncoder
{
    public const int EncodedLength = 8;

    public static void Encode(ControllerState state, Span<byte> destination)
    {
        if (destination.Length < EncodedLength)
        {
            throw new ArgumentException(
                $"The bridge state buffer must contain at least {EncodedLength} bytes.",
                nameof(destination));
        }

        ushort buttons = 0;
        MapButton(state.Buttons, GameButtons.Y, 0x0001, ref buttons);
        MapButton(state.Buttons, GameButtons.B, 0x0002, ref buttons);
        MapButton(state.Buttons, GameButtons.A, 0x0004, ref buttons);
        MapButton(state.Buttons, GameButtons.X, 0x0008, ref buttons);
        MapButton(state.Buttons, GameButtons.LeftShoulder, 0x0010, ref buttons);
        MapButton(state.Buttons, GameButtons.RightShoulder, 0x0020, ref buttons);
        MapButton(state.Buttons, GameButtons.LeftTrigger, 0x0040, ref buttons);
        MapButton(state.Buttons, GameButtons.RightTrigger, 0x0080, ref buttons);
        MapButton(state.Buttons, GameButtons.Minus, 0x0100, ref buttons);
        MapButton(state.Buttons, GameButtons.Plus, 0x0200, ref buttons);
        MapButton(state.Buttons, GameButtons.LeftStick, 0x0400, ref buttons);
        MapButton(state.Buttons, GameButtons.RightStick, 0x0800, ref buttons);
        MapButton(state.Buttons, GameButtons.Home, 0x1000, ref buttons);
        MapButton(state.Buttons, GameButtons.Capture, 0x2000, ref buttons);

        BinaryPrimitives.WriteUInt16LittleEndian(destination, buttons);
        destination[2] = (byte)state.DPad;
        destination[3] = state.LeftStick.X;
        destination[4] = state.LeftStick.Y;
        destination[5] = state.RightStick.X;
        destination[6] = state.RightStick.Y;
        destination[7] = 0;
    }

    private static void MapButton(
        GameButtons pressed,
        GameButtons canonical,
        ushort bridge,
        ref ushort encoded)
    {
        if ((pressed & canonical) != 0)
        {
            encoded |= bridge;
        }
    }
}
