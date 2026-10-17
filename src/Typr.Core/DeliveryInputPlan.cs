namespace Typr.Core;

public enum InputStrokeKind { Unicode, VirtualKey }
public readonly record struct InputStroke(InputStrokeKind Kind, ushort Code, bool KeyUp);

/// <summary>Platform-independent SendInput expansion. The Windows adapter must use
/// 40-byte INPUT records on x64 and honor all key-up records, including on failure.</summary>
public sealed record DeliveryInputPlan(string Text, IReadOnlyList<InputStroke> Strokes)
{
    public const ushort Shift = 0x10;
    public const ushort Enter = 0x0D;

    public static DeliveryInputPlan Create(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n')
            .Replace('\u2028', '\n').Replace('\u2029', '\n').TrimEnd('\n');
        var strokes = new List<InputStroke>(normalized.Length * 2);
        foreach (char unit in normalized)
        {
            if (unit == '\n')
            {
                strokes.Add(new(InputStrokeKind.VirtualKey, Shift, false));
                strokes.Add(new(InputStrokeKind.VirtualKey, Enter, false));
                strokes.Add(new(InputStrokeKind.VirtualKey, Enter, true));
                strokes.Add(new(InputStrokeKind.VirtualKey, Shift, true));
            }
            else
            {
                // KEYEVENTF_UNICODE consumes UTF-16 code units, not Unicode scalar values.
                strokes.Add(new(InputStrokeKind.Unicode, unit, false));
                strokes.Add(new(InputStrokeKind.Unicode, unit, true));
            }
        }
        return new(normalized, strokes.ToArray());
    }
}
