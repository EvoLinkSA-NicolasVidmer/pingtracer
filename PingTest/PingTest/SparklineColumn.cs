using System.Windows.Forms;

namespace PingTracer
{
	/// <summary>
	/// Custom DataGridViewColumn that uses SparklineCell as its cell template.
	/// Used in the OverviewPanel's DataGridView to display mini latency trend graphs.
	/// </summary>
	public class SparklineColumn : DataGridViewColumn
	{
		public SparklineColumn() : base(new SparklineCell())
		{
			this.ValueType = typeof(short[]);
			this.CellTemplate = new SparklineCell();
		}
	}
}
