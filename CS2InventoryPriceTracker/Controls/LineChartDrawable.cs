using System.Globalization;

namespace CS2InventoryPriceTracker.Controls;

/// <summary>One point of a line chart.</summary>
public record ChartPoint(DateTime Date, double Value);

/// <summary>
/// A simple line chart (values over days) drawn with MAUI graphics, so no chart library is needed.
/// Shows the highest and lowest value on the left and the first and last date at the bottom.
/// </summary>
public class LineChartDrawable : IDrawable
{
    private static readonly CultureInfo Portugal = CultureInfo.GetCultureInfo("pt-PT");

    public IReadOnlyList<ChartPoint> Points { get; set; } = [];

    public Color LineColor { get; set; } = Color.FromArgb("#F08A1C");

    public Color TextColor { get; set; } = Colors.Gray;

    public Color GridColor { get; set; } = Color.FromArgb("#33888888");

    public string EmptyText { get; set; } = "No data yet";

    public void Draw(ICanvas canvas, RectF rect)
    {
        canvas.FontSize = 11;
        canvas.FontColor = TextColor;

        if (Points.Count == 0)
        {
            canvas.DrawString(EmptyText, rect.X, rect.Y, rect.Width, rect.Height, HorizontalAlignment.Center, VerticalAlignment.Center);
            return;
        }

        const float left = 64, bottom = 22, top = 8, right = 8;
        var plot = new RectF(rect.X + left, rect.Y + top, rect.Width - left - right, rect.Height - top - bottom);

        var min = Points.Min(p => p.Value);
        var max = Points.Max(p => p.Value);
        if (Math.Abs(max - min) < 0.005)
        {
            // Flat line: give it some room so it sits in the middle.
            min -= Math.Max(1, max * 0.05);
            max += Math.Max(1, max * 0.05);
        }

        var firstDay = Points[0].Date;
        var days = Math.Max(1, (Points[^1].Date - firstDay).TotalDays);

        PointF ToScreen(ChartPoint p) => new(
            plot.Left + (float)((p.Date - firstDay).TotalDays / days) * plot.Width,
            plot.Bottom - (float)((p.Value - min) / (max - min)) * plot.Height);

        // Grid lines and value labels.
        canvas.StrokeColor = GridColor;
        canvas.StrokeSize = 1;
        for (var i = 0; i <= 2; i++)
        {
            var y = plot.Bottom - plot.Height * i / 2f;
            canvas.DrawLine(plot.Left, y, plot.Right, y);
            var value = min + (max - min) * i / 2;
            canvas.DrawString(Format(value), rect.X, y - 8, left - 6, 16,
                HorizontalAlignment.Right, VerticalAlignment.Center);
        }

        // Date labels.
        canvas.DrawString(Points[0].Date.ToString("dd/MM"), plot.Left, plot.Bottom + 4, plot.Width, bottom - 4,
            HorizontalAlignment.Left, VerticalAlignment.Top);
        if (Points.Count > 1)
            canvas.DrawString(Points[^1].Date.ToString("dd/MM"), plot.Left, plot.Bottom + 4, plot.Width, bottom - 4,
                HorizontalAlignment.Right, VerticalAlignment.Top);

        // The line, or a single dot when there is only one day.
        canvas.StrokeColor = LineColor;
        canvas.FillColor = LineColor;
        canvas.StrokeSize = 2.5f;
        canvas.StrokeLineJoin = LineJoin.Round;
        if (Points.Count == 1)
        {
            var only = ToScreen(Points[0]);
            canvas.FillCircle(plot.Left + plot.Width / 2, only.Y, 4);
            return;
        }

        var path = new PathF();
        path.MoveTo(ToScreen(Points[0]));
        foreach (var point in Points.Skip(1))
            path.LineTo(ToScreen(point));
        canvas.DrawPath(path);

        var last = ToScreen(Points[^1]);
        canvas.FillCircle(last.X, last.Y, 4);
    }

    private static string Format(double value) => value >= 10000
        ? (value / 1000).ToString("#,##0.#", Portugal) + "k €"
        : value.ToString("#,##0", Portugal) + " €";
}
