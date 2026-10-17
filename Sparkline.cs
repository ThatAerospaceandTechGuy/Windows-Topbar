using System;
using System.Windows;
using System.Windows.Media;

namespace TopBar
{
    /// <summary>
    /// Rolling 0..100 graph. Samples live in a ring buffer and render as one
    /// filled polyline per frame (no child elements, no animations) — cheap
    /// enough to run dozens of instances at 1 Hz.
    /// </summary>
    public sealed class Sparkline : FrameworkElement
    {
        private readonly double[] _buf;
        private int _count, _head;
        private readonly Pen _line;
        private readonly Brush _fill;
        private readonly Pen? _grid;

        public Sparkline(int capacity, Brush line, Brush fill, Brush? grid = null)
        {
            _buf = new double[Math.Max(2, capacity)];
            _line = new Pen(line, 1.2);
            _line.Freeze();
            _fill = fill;
            if (grid != null)
            {
                _grid = new Pen(grid, 1);
                _grid.Freeze();
            }
        }

        public void Add(double pct)
        {
            _buf[_head] = double.IsFinite(pct) ? Math.Clamp(pct, 0, 100) : 0;
            _head = (_head + 1) % _buf.Length;
            if (_count < _buf.Length) _count++;
            InvalidateVisual();
        }

        public void Reset()
        {
            _count = 0;
            _head = 0;
            InvalidateVisual();
        }

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth, h = ActualHeight;
            if (w <= 1 || h <= 1) return;

            if (_grid != null)
                for (int i = 1; i <= 3; i++)
                {
                    double y = h * i / 4.0;
                    dc.DrawLine(_grid, new Point(0, y), new Point(w, y));
                }

            if (_count < 2) return;

            int cap = _buf.Length;
            double step = w / (cap - 1);
            double xFirst = w - (_count - 1) * step;

            double X(int i) => xFirst + i * step;
            double Y(int i) => h - 1.5 - (_buf[(_head - _count + i + cap * 2) % cap] / 100.0) * (h - 3);

            var geo = new StreamGeometry();
            using (var ctx = geo.Open())
            {
                ctx.BeginFigure(new Point(X(0), Y(0)), isFilled: true, isClosed: false);
                for (int i = 1; i < _count; i++)
                    ctx.LineTo(new Point(X(i), Y(i)), false, false);
                ctx.LineTo(new Point(X(_count - 1), h), false, false);
                ctx.LineTo(new Point(X(0), h), true, false);
            }
            geo.Freeze();
            dc.DrawGeometry(_fill, _line, geo);
        }
    }
}
