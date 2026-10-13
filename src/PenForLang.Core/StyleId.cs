using System.Diagnostics.CodeAnalysis;

namespace PenForLang.Core;

/// <summary>
/// Идентификатор стиля письма (например <c>zaner-bloser</c>, <c>ru-propis</c>): непустая строка до 64 символов из
/// строчных латинских букв, цифр, <c>-</c> и <c>_</c>, начинается с буквы или цифры. Значение по умолчанию (<c>default</c>)
/// — «нет стиля»: <see cref="Value"/> пусто, <see cref="IsDefault"/> истинно.
/// </summary>
public readonly record struct StyleId
{
    /// <summary>Максимальная длина идентификатора.</summary>
    public const int MaxLength = 64;

    private readonly string? _value;

    /// <summary>Создаёт идентификатор с проверкой.</summary>
    /// <exception cref="ArgumentException">Значение не соответствует правилам.</exception>
    public StyleId(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (!IsValid(value))
        {
            throw new ArgumentException($"Invalid style id '{value}': expected 1..{MaxLength} chars of [a-z0-9_-], starting with a letter or digit.", nameof(value));
        }

        _value = value;
    }

    /// <summary>Текст идентификатора; для <c>default</c> — пустая строка.</summary>
    public string Value => _value ?? string.Empty;

    /// <summary>Это значение по умолчанию (стиль не задан).</summary>
    public bool IsDefault => _value is null;

    /// <summary>Разбирает идентификатор; <c>false</c> для некорректной строки.</summary>
    public static bool TryParse([NotNullWhen(true)] string? text, out StyleId id)
    {
        if (text is not null && IsValid(text))
        {
            id = new StyleId(text);
            return true;
        }

        id = default;
        return false;
    }

    /// <inheritdoc />
    public override string ToString() => Value;

    private static bool IsValid(string text)
    {
        if (text.Length is 0 or > MaxLength)
        {
            return false;
        }

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            bool alnum = c is (>= 'a' and <= 'z') or (>= '0' and <= '9');
            if (!alnum && (i == 0 || c is not ('-' or '_')))
            {
                return false;
            }
        }

        return true;
    }
}
