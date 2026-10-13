namespace PenForLang.Ink;

/// <summary>Соответствие перечислений текстовым значениям формата penlog.</summary>
internal static class PenLogNames
{
    public static string ToText(PenSourceKind value) => value switch
    {
        PenSourceKind.Win32Pointer => "wm_pointer",
        PenSourceKind.WpfStylus => "wpf_stylus",
        PenSourceKind.Replay => "replay",
        PenSourceKind.Mouse => "mouse",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown pen source kind."),
    };

    public static bool TryParse(string text, out PenSourceKind value)
    {
        (bool ok, value) = text switch
        {
            "wm_pointer" => (true, PenSourceKind.Win32Pointer),
            "wpf_stylus" => (true, PenSourceKind.WpfStylus),
            "replay" => (true, PenSourceKind.Replay),
            "mouse" => (true, PenSourceKind.Mouse),
            _ => (false, default(PenSourceKind)),
        };
        return ok;
    }

    public static string ToText(PenTimestampKind value) => value switch
    {
        PenTimestampKind.Qpc => "qpc",
        PenTimestampKind.Interpolated => "interpolated",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown timestamp kind."),
    };

    public static bool TryParse(string text, out PenTimestampKind value)
    {
        (bool ok, value) = text switch
        {
            "qpc" => (true, PenTimestampKind.Qpc),
            "interpolated" => (true, PenTimestampKind.Interpolated),
            _ => (false, default(PenTimestampKind)),
        };
        return ok;
    }

    public static string ToText(PxPerMmSource value) => value switch
    {
        PxPerMmSource.Default => "default",
        PxPerMmSource.Edid => "edid",
        PxPerMmSource.Calibrated => "calibrated",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown pxPerMm source."),
    };

    public static bool TryParse(string text, out PxPerMmSource value)
    {
        (bool ok, value) = text switch
        {
            "default" => (true, PxPerMmSource.Default),
            "edid" => (true, PxPerMmSource.Edid),
            "calibrated" => (true, PxPerMmSource.Calibrated),
            _ => (false, default(PxPerMmSource)),
        };
        return ok;
    }

    public static string ToText(PenSubjectKind value) => value switch
    {
        PenSubjectKind.Adult => "adult",
        PenSubjectKind.Child => "child",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown subject kind."),
    };

    public static bool TryParse(string text, out PenSubjectKind value)
    {
        (bool ok, value) = text switch
        {
            "adult" => (true, PenSubjectKind.Adult),
            "child" => (true, PenSubjectKind.Child),
            _ => (false, default(PenSubjectKind)),
        };
        return ok;
    }

    public static string ToText(PenPhase value) => value switch
    {
        PenPhase.Hover => "hover",
        PenPhase.Down => "down",
        PenPhase.Move => "move",
        PenPhase.Up => "up",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown pen phase."),
    };

    public static bool TryParse(string text, out PenPhase value)
    {
        (bool ok, value) = text switch
        {
            "hover" => (true, PenPhase.Hover),
            "down" => (true, PenPhase.Down),
            "move" => (true, PenPhase.Move),
            "up" => (true, PenPhase.Up),
            _ => (false, default(PenPhase)),
        };
        return ok;
    }
}
