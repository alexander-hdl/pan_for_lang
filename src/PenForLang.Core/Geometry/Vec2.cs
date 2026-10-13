namespace PenForLang.Core.Geometry;

/// <summary>
/// Двумерный вектор или точка в <c>double</c>. Единицы задаёт вызывающий код (px поверхности, x-height линейки, мм).
/// Все операции чистые и детерминированные (только арифметика IEEE 754 и <see cref="Math.Sqrt(double)"/>).
/// </summary>
/// <param name="X">Координата x (вдоль строки).</param>
/// <param name="Y">Координата y.</param>
public readonly record struct Vec2(double X, double Y)
{
    /// <summary>Нулевой вектор.</summary>
    public static Vec2 Zero => new(0.0, 0.0);

    /// <summary>Квадрат длины вектора.</summary>
    public double LengthSquared => (X * X) + (Y * Y);

    /// <summary>Длина вектора.</summary>
    public double Length => Math.Sqrt(LengthSquared);

    /// <summary>Сумма двух векторов.</summary>
    public static Vec2 operator +(Vec2 a, Vec2 b) => new(a.X + b.X, a.Y + b.Y);

    /// <summary>Разность двух векторов.</summary>
    public static Vec2 operator -(Vec2 a, Vec2 b) => new(a.X - b.X, a.Y - b.Y);

    /// <summary>Противоположный вектор.</summary>
    public static Vec2 operator -(Vec2 a) => new(-a.X, -a.Y);

    /// <summary>Умножение на скаляр.</summary>
    public static Vec2 operator *(Vec2 a, double s) => new(a.X * s, a.Y * s);

    /// <summary>Умножение на скаляр.</summary>
    public static Vec2 operator *(double s, Vec2 a) => new(a.X * s, a.Y * s);

    /// <summary>Деление на скаляр.</summary>
    public static Vec2 operator /(Vec2 a, double s) => new(a.X / s, a.Y / s);

    /// <summary>Скалярное произведение.</summary>
    public static double Dot(Vec2 a, Vec2 b) => (a.X * b.X) + (a.Y * b.Y);

    /// <summary>Псевдоскалярное (z-компонента векторного) произведение: <c>a.X*b.Y - a.Y*b.X</c>.</summary>
    public static double Cross(Vec2 a, Vec2 b) => (a.X * b.Y) - (a.Y * b.X);

    /// <summary>Квадрат расстояния между точками.</summary>
    public static double DistanceSquared(Vec2 a, Vec2 b) => (a - b).LengthSquared;

    /// <summary>Расстояние между точками.</summary>
    public static double Distance(Vec2 a, Vec2 b) => (a - b).Length;

    /// <summary>Линейная интерполяция: <c>a</c> при <c>t = 0</c>, <c>b</c> при <c>t = 1</c>.</summary>
    public static Vec2 Lerp(Vec2 a, Vec2 b, double t) => new(a.X + ((b.X - a.X) * t), a.Y + ((b.Y - a.Y) * t));

    /// <summary>Единичный вектор того же направления; для нулевого вектора возвращает <see cref="Zero"/>.</summary>
    public Vec2 Normalized()
    {
        double len = Length;
        return len > 0.0 ? new Vec2(X / len, Y / len) : Zero;
    }

    /// <summary>Поворот на +90° (против часовой при оси y вверх): <c>(-Y, X)</c>.</summary>
    public Vec2 Perpendicular() => new(-Y, X);

    /// <summary>Угол вектора в радианах, <c>atan2(Y, X)</c>, в диапазоне (−π, π].</summary>
    public double Angle() => Math.Atan2(Y, X);
}
