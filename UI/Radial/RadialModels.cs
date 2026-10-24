// ============================================================================
// RadialFlow - Módulo 2 (parte 1): Modelo + Matemática da roda
// Sem dependência de janela: tudo aqui é puro e testável.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;

namespace RadialFlow.UI;

/// <summary>Uma fatia da roda, podendo conter sub-fatias (submenu por permanência/hover).</summary>
/// <param name="Label">Texto curto ou rótulo do formato (ex.: "WEBP", "Minimizar").</param>
/// <param name="Glyph">Caractere da fonte Segoe Fluent Icons / MDL2 (ex.: "\uE921").</param>
/// <param name="Execute">Ação executada ao soltar o botão sobre a fatia.</param>
/// <param name="SubSlices">Opções de segundo nível exibidas ao pairar sobre a fatia.</param>
/// <param name="Description">Descrição amigável exibida no hub central.</param>
public sealed record RadialSlice(
    string Label,
    string Glyph,
    Action? Execute = null,
    IReadOnlyList<RadialSlice>? SubSlices = null,
    string? Description = null);

public static class RadialGeometry
{
    public const int MinSlices = 2;
    public const int MaxSlices = 8;

    /// <summary>
    /// Ângulo (graus) do centro da fatia. Convenção: fatia 0 no TOPO,
    /// crescendo no sentido horário (eixo Y da tela aponta para baixo).
    /// </summary>
    public static double CenterAngle(int index, int count) => -90.0 + index * (360.0 / count);

    /// <summary>Ponto em coordenadas polares (ângulo em graus, 0° = direita).</summary>
    public static Point Polar(double cx, double cy, double radius, double angleDeg)
    {
        double rad = angleDeg * Math.PI / 180.0;
        return new Point(cx + radius * Math.Cos(rad), cy + radius * Math.Sin(rad));
    }

    /// <summary>
    /// Descobre a fatia sob o cursor. (dx, dy) = cursor - centro da roda,
    /// na MESMA unidade de innerRadius. Retorna -1 se estiver no furo central.
    /// Não há raio externo: arrastar além da roda mantém a fatia selecionada.
    /// </summary>
    public static int HitTest(double dx, double dy, double innerRadius, int count)
    {
        if (dx * dx + dy * dy < innerRadius * innerRadius) return -1;

        double step = 360.0 / count;
        double deg = Math.Atan2(dy, dx) * 180.0 / Math.PI;   // -180..180, 0 = direita

        // Desloca para que a fatia 0 comece em 0 (ela cobre topo ± step/2).
        double rel = (deg + 90.0 + step / 2.0) % 360.0;
        if (rel < 0) rel += 360.0;

        return Math.Min((int)(rel / step), count - 1);
    }

    /// <summary>Monta o "pedaço de donut" de uma fatia (geometria congelada).</summary>
    public static Geometry BuildSlice(int index, int count, double cx, double cy,
                                      double inner, double outer, double gapDeg)
    {
        double step = 360.0 / count;
        double mid = CenterAngle(index, count);
        double a0 = mid - step / 2.0 + gapDeg / 2.0;
        double a1 = mid + step / 2.0 - gapDeg / 2.0;
        bool large = (a1 - a0) > 180.0;

        var outerStart = Polar(cx, cy, outer, a0);
        var outerEnd   = Polar(cx, cy, outer, a1);
        var innerEnd   = Polar(cx, cy, inner, a1);
        var innerStart = Polar(cx, cy, inner, a0);

        var figure = new PathFigure { StartPoint = outerStart, IsClosed = true, IsFilled = true };
        figure.Segments.Add(new ArcSegment(outerEnd, new Size(outer, outer), 0, large, SweepDirection.Clockwise, true));
        figure.Segments.Add(new LineSegment(innerEnd, true));
        figure.Segments.Add(new ArcSegment(innerStart, new Size(inner, inner), 0, large, SweepDirection.Counterclockwise, true));

        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        geometry.Freeze();
        return geometry;
    }

    /// <summary>
    /// Fatia de anel com CANTOS ARREDONDADOS e fresta de LARGURA CONSTANTE (em pixels) entre as vizinhas.
    /// (Com fresta angular, como em <see cref="BuildSlice"/>, a abertura cresce com o raio.)
    /// </summary>
    /// <param name="gapPx">Largura da fresta entre fatias vizinhas.</param>
    /// <param name="cornerRadius">Raio dos cantos; limitado para caber na espessura do anel e no arco interno.</param>
    public static Geometry BuildRoundedSlice(int index, int count, double cx, double cy,
                                             double inner, double outer, double gapPx, double cornerRadius)
    {
        double step = 360.0 / count;
        double mid = CenterAngle(index, count);
        double startBoundary = mid - step / 2.0;   // limite com a fatia anterior
        double endBoundary = mid + step / 2.0;     // limite com a próxima

        // Cada borda reta é paralela ao raio do limite, deslocada meia fresta para DENTRO da fatia.
        // Em um círculo de raio R esse deslocamento equivale a asin(d / R) graus.
        double half = gapPx / 2.0;
        double outerShift = ToDegrees(Math.Asin(Math.Min(half / outer, 1)));
        double innerShift = ToDegrees(Math.Asin(Math.Min(half / inner, 1)));

        Point outerStart = Polar(cx, cy, outer, startBoundary + outerShift);
        Point outerEnd   = Polar(cx, cy, outer, endBoundary - outerShift);
        Point innerStart = Polar(cx, cy, inner, startBoundary + innerShift);
        Point innerEnd   = Polar(cx, cy, inner, endBoundary - innerShift);

        double innerSweep = (endBoundary - innerShift) - (startBoundary + innerShift);
        double maxByThickness = (outer - inner) / 2.0 - 0.5;
        double maxByInnerArc = inner * ToRadians(innerSweep) / 2.0 * 0.9;
        double r = Math.Clamp(cornerRadius, 0, Math.Max(0, Math.Min(maxByThickness, maxByInnerArc)));

        double rOuter = ToDegrees(r / outer);   // recuo ao longo do círculo externo
        double rInner = ToDegrees(r / inner);   // recuo ao longo do círculo interno

        // Ponto a "distance" de "from" em direção a "to" (recuo ao longo da borda reta).
        static Point Toward(Point from, Point to, double distance)
        {
            Vector v = to - from;
            double length = v.Length;
            return length < 1e-9 ? from : from + v * (distance / length);
        }

        var figure = new PathFigure
        {
            StartPoint = Polar(cx, cy, outer, startBoundary + outerShift + rOuter),
            IsClosed = true,
            IsFilled = true
        };

        double outerArcSweep = (endBoundary - outerShift - rOuter) - (startBoundary + outerShift + rOuter);
        figure.Segments.Add(new ArcSegment(Polar(cx, cy, outer, endBoundary - outerShift - rOuter),
            new Size(outer, outer), 0, outerArcSweep > 180.0, SweepDirection.Clockwise, true));
        figure.Segments.Add(new QuadraticBezierSegment(outerEnd, Toward(outerEnd, innerEnd, r), true));
        figure.Segments.Add(new LineSegment(Toward(innerEnd, outerEnd, r), true));
        figure.Segments.Add(new QuadraticBezierSegment(innerEnd, Polar(cx, cy, inner, endBoundary - innerShift - rInner), true));
        figure.Segments.Add(new ArcSegment(Polar(cx, cy, inner, startBoundary + innerShift + rInner),
            new Size(inner, inner), 0, outerArcSweep > 180.0, SweepDirection.Counterclockwise, true));
        figure.Segments.Add(new QuadraticBezierSegment(innerStart, Toward(innerStart, outerStart, r), true));
        figure.Segments.Add(new LineSegment(Toward(outerStart, innerStart, r), true));
        figure.Segments.Add(new QuadraticBezierSegment(outerStart, figure.StartPoint, true));

        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        geometry.Freeze();
        return geometry;
    }

    private static double ToDegrees(double radians) => radians * 180.0 / Math.PI;
    private static double ToRadians(double degrees) => degrees * Math.PI / 180.0;
}
