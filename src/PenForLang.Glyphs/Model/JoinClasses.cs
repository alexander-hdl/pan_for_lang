using System.Diagnostics.CodeAnalysis;

namespace PenForLang.Glyphs;

/// <summary>Класс входа в букву (начальное движение; METHODOLOGY.md §3.1).</summary>
public enum EntryClass
{
    /// <summary><c>E-UP</c>: подъём от базовой линии до x-height (ZB undercurve).</summary>
    Up,

    /// <summary><c>E-UP-TALL</c>: подъём к линии асцендеров — петля или ствол.</summary>
    UpTall,

    /// <summary><c>E-C</c>: «перекат» к верхней правой точке и дуга против часовой (ZB downcurve).</summary>
    C,

    /// <summary><c>E-HUMP</c>: горб — дуга вверх и вправо от базовой линии (ZB overcurve).</summary>
    Hump,
}

/// <summary>Класс выхода из буквы (как буква передаёт движение следующей; METHODOLOGY.md §3.1).</summary>
public enum ExitClass
{
    /// <summary><c>X-BASE</c>: выход от базовой линии вверх-вправо (undercurve).</summary>
    Base,

    /// <summary><c>X-HIGH</c>: выход на уровне x-height (checkstroke).</summary>
    High,

    /// <summary><c>X-DESC</c>: выход из нижней петли вверх (overcurve).</summary>
    Desc,

    /// <summary><c>X-NONE</c>: разрыв, соединения нет.</summary>
    None,
}

/// <summary>Преобразование классов входа и выхода в имена формата (<c>E-UP</c>, <c>X-BASE</c>, ...) и обратно.</summary>
public static class JoinClasses
{
    /// <summary>Имя класса входа в JSON (<c>E-UP</c>, <c>E-UP-TALL</c>, <c>E-C</c>, <c>E-HUMP</c>).</summary>
    /// <param name="value">Класс входа.</param>
    public static string ToWireName(this EntryClass value) => value switch
    {
        EntryClass.Up => "E-UP",
        EntryClass.UpTall => "E-UP-TALL",
        EntryClass.C => "E-C",
        EntryClass.Hump => "E-HUMP",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown entry class."),
    };

    /// <summary>Имя класса выхода в JSON (<c>X-BASE</c>, <c>X-HIGH</c>, <c>X-DESC</c>, <c>X-NONE</c>).</summary>
    /// <param name="value">Класс выхода.</param>
    public static string ToWireName(this ExitClass value) => value switch
    {
        ExitClass.Base => "X-BASE",
        ExitClass.High => "X-HIGH",
        ExitClass.Desc => "X-DESC",
        ExitClass.None => "X-NONE",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown exit class."),
    };

    /// <summary>Разбирает имя класса входа; <c>false</c>, если имя неизвестно.</summary>
    /// <param name="text">Имя, например <c>E-C</c>.</param>
    /// <param name="value">Результат.</param>
    public static bool TryParseEntry([NotNullWhen(true)] string? text, out EntryClass value)
    {
        switch (text)
        {
            case "E-UP":
                value = EntryClass.Up;
                return true;
            case "E-UP-TALL":
                value = EntryClass.UpTall;
                return true;
            case "E-C":
                value = EntryClass.C;
                return true;
            case "E-HUMP":
                value = EntryClass.Hump;
                return true;
            default:
                value = default;
                return false;
        }
    }

    /// <summary>Разбирает имя класса выхода; <c>false</c>, если имя неизвестно.</summary>
    /// <param name="text">Имя, например <c>X-BASE</c>.</param>
    /// <param name="value">Результат.</param>
    public static bool TryParseExit([NotNullWhen(true)] string? text, out ExitClass value)
    {
        switch (text)
        {
            case "X-BASE":
                value = ExitClass.Base;
                return true;
            case "X-HIGH":
                value = ExitClass.High;
                return true;
            case "X-DESC":
                value = ExitClass.Desc;
                return true;
            case "X-NONE":
                value = ExitClass.None;
                return true;
            default:
                value = default;
                return false;
        }
    }
}
