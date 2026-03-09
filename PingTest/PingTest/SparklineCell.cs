using System;
using System.Drawing;
using System.Windows.Forms;

namespace PingTracer
{
	/// <summary>
	/// Custom DataGridViewImageCell that renders a mini latency sparkline graph.
	/// Paints recent ping times as a line chart within the cell bounds.
	/// </summary>
	public class SparklineCell : DataGridViewImageCell
	{
		public SparklineCell()
		{
			this.ValueType = typeof(short[]);
		}

		protected override void Paint(Graphics graphics, Rectangle clipBounds, Rectangle cellBounds,
			int rowIndex, DataGridViewElementStates cellState, object value, object formattedValue,
			string errorText, DataGridViewCellStyle cellStyle,
			DataGridViewAdvancedBorderStyle advancedBorderStyle, DataGridViewPaintParts paintParts)
		{
			// Fill background with black (matching PingGraphControl theme)
			using (var brush = new SolidBrush(Color.FromArgb(0, 0, 0)))
				graphics.FillRectangle(brush, cellBounds);

			// Paint border
			PaintBorder(graphics, clipBounds, cellBounds, cellStyle, advancedBorderStyle);

			short[] data = value as short[];
			if (data == null || data.Length < 2) return;

			// Find max for scaling (ignore failures = -1)
			int maxVal = 1;
			for (int i = 0; i < data.Length; i++)
				if (data[i] > maxVal) maxVal = data[i];

			float xStep = (float)(cellBounds.Width - 4) / (data.Length - 1);
			float yScale = (float)(cellBounds.Height - 6) / maxVal;
			int padX = cellBounds.X + 2;
			int padBottom = cellBounds.Bottom - 3;

			using (var penOk = new Pen(Color.FromArgb(64, 128, 64), 1))     // Green for success
			using (var penFail = new Pen(Color.FromArgb(255, 0, 0), 1))     // Red for failure
			{
				for (int i = 1; i < data.Length; i++)
				{
					short prev = data[i - 1];
					short curr = data[i];
					// Skip segments involving zero (no data)
					if (prev == 0 || curr == 0) continue;

					bool hasFail = prev < 0 || curr < 0;
					Pen pen = hasFail ? penFail : penOk;

					float x1 = padX + (i - 1) * xStep;
					float y1 = prev < 0 ? cellBounds.Y + 2 : padBottom - Math.Min(prev, maxVal) * yScale;
					float x2 = padX + i * xStep;
					float y2 = curr < 0 ? cellBounds.Y + 2 : padBottom - Math.Min(curr, maxVal) * yScale;

					graphics.DrawLine(pen, x1, y1, x2, y2);
				}
			}
		}
	}
}
