using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace PingTracer
{
	/// <summary>
	/// UserControl that displays a summary table of all monitored hosts.
	/// Shows host name, status, average latency, packet loss, and sparkline trend.
	/// Refreshes every 1 second via Timer. Row clicks fire HostNavigationRequested.
	/// </summary>
	public class OverviewPanel : UserControl
	{
		private DataGridView dataGridView;
		private Timer refreshTimer;
		private Label lblEmpty;
		private List<HostPingSession> sessions = new List<HostPingSession>();

		/// <summary>
		/// Event for MainForm to handle row-click navigation.
		/// The int parameter is the tab index to navigate to.
		/// </summary>
		public event EventHandler<int> HostNavigationRequested;

		public OverviewPanel()
		{
			this.Dock = DockStyle.Fill;
			InitializeDataGridView();
			InitializeEmptyLabel();
			InitializeTimer();
		}

		private void InitializeDataGridView()
		{
			dataGridView = new DataGridView();
			dataGridView.Dock = DockStyle.Fill;
			dataGridView.ReadOnly = true;
			dataGridView.AllowUserToAddRows = false;
			dataGridView.AllowUserToDeleteRows = false;
			dataGridView.AllowUserToResizeRows = false;
			dataGridView.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
			dataGridView.MultiSelect = false;
			dataGridView.RowHeadersVisible = false;
			dataGridView.BackgroundColor = SystemColors.Window;
			dataGridView.BorderStyle = BorderStyle.None;
			dataGridView.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
			dataGridView.RowTemplate.Height = 40;  // Tall enough for sparklines
			dataGridView.DefaultCellStyle.Font = new Font(FontFamily.GenericSansSerif, 9f);
			dataGridView.ColumnHeadersDefaultCellStyle.Font = new Font(FontFamily.GenericSansSerif, 9f, FontStyle.Bold);

			// Columns
			var colHost = new DataGridViewTextBoxColumn { Name = "Host", HeaderText = "Host", FillWeight = 30 };
			var colStatus = new DataGridViewTextBoxColumn { Name = "Status", HeaderText = "Status", FillWeight = 15 };
			var colAvgLatency = new DataGridViewTextBoxColumn { Name = "AvgLatency", HeaderText = "Avg Latency", FillWeight = 15 };
			var colPacketLoss = new DataGridViewTextBoxColumn { Name = "PacketLoss", HeaderText = "Pkt Loss", FillWeight = 15 };
			var colSparkline = new SparklineColumn { Name = "Sparkline", HeaderText = "Trend", FillWeight = 25 };

			dataGridView.Columns.AddRange(new DataGridViewColumn[] { colHost, colStatus, colAvgLatency, colPacketLoss, colSparkline });

			dataGridView.CellClick += DataGridView_CellClick;

			this.Controls.Add(dataGridView);
		}

		private void InitializeEmptyLabel()
		{
			lblEmpty = new Label();
			lblEmpty.Text = "Start pinging to see overview data";
			lblEmpty.ForeColor = SystemColors.GrayText;
			lblEmpty.Font = new Font(FontFamily.GenericSansSerif, 11f);
			lblEmpty.AutoSize = false;
			lblEmpty.Dock = DockStyle.Fill;
			lblEmpty.TextAlign = ContentAlignment.MiddleCenter;
			lblEmpty.Visible = true;
			this.Controls.Add(lblEmpty);
			lblEmpty.BringToFront();
		}

		private void InitializeTimer()
		{
			refreshTimer = new Timer();
			refreshTimer.Interval = 1000;
			refreshTimer.Tick += RefreshTimer_Tick;
		}

		/// <summary>
		/// Called by MainForm when sessions are created (start pinging).
		/// </summary>
		public void UpdateSessions(List<HostPingSession> activeSessions)
		{
			sessions = activeSessions;
			dataGridView.Rows.Clear();

			if (sessions.Count == 0)
			{
				lblEmpty.Visible = true;
				refreshTimer.Stop();
				return;
			}

			lblEmpty.Visible = false;

			// Create one row per session
			foreach (var session in sessions)
			{
				int rowIdx = dataGridView.Rows.Add();
				dataGridView.Rows[rowIdx].Cells["Host"].Value = session.Host;
				dataGridView.Rows[rowIdx].Cells["Status"].Value = "Starting...";
				dataGridView.Rows[rowIdx].Cells["AvgLatency"].Value = "-";
				dataGridView.Rows[rowIdx].Cells["PacketLoss"].Value = "-";
				dataGridView.Rows[rowIdx].Cells["Sparkline"].Value = new short[0];
			}

			refreshTimer.Start();
		}

		/// <summary>
		/// Called by MainForm when pinging stops or sessions cleared.
		/// </summary>
		public void ClearSessions()
		{
			refreshTimer.Stop();
			sessions = new List<HostPingSession>();
			dataGridView.Rows.Clear();
			lblEmpty.Visible = true;
		}

		/// <summary>
		/// Called by MainForm when a single session is removed (tab closed).
		/// Removes the row for that session.
		/// </summary>
		public void RemoveSession(HostPingSession session)
		{
			int idx = sessions.IndexOf(session);
			if (idx >= 0 && idx < dataGridView.Rows.Count)
			{
				dataGridView.Rows.RemoveAt(idx);
			}
			// sessions list is managed by MainForm (activeSessions reference)
		}

		private void RefreshTimer_Tick(object sender, EventArgs e)
		{
			if (sessions.Count == 0) return;

			// Ensure row count matches sessions (may have changed from tab close)
			while (dataGridView.Rows.Count > sessions.Count)
				dataGridView.Rows.RemoveAt(dataGridView.Rows.Count - 1);

			for (int i = 0; i < sessions.Count && i < dataGridView.Rows.Count; i++)
			{
				var session = sessions[i];
				var row = dataGridView.Rows[i];

				// Host name (update in case ResolvedAddress is now available)
				string hostDisplay = session.Host;
				if (!string.IsNullOrEmpty(session.ResolvedAddress) && session.ResolvedAddress != session.Host)
					hostDisplay = session.Host + " (" + session.ResolvedAddress + ")";
				row.Cells["Host"].Value = hostDisplay;

				// Ping counts
				long success = session.GetSuccessfulPings();
				long failed = session.GetFailedPings();
				long total = success + failed;

				// Status
				string status = "Idle";
				if (session.Worker != null && session.Worker.IsBusy)
					status = failed == 0 ? "OK" : "Degraded";
				row.Cells["Status"].Value = status;

				// Color the status cell
				if (status == "OK")
					row.Cells["Status"].Style.ForeColor = Color.FromArgb(0, 128, 0);
				else if (status == "Degraded")
					row.Cells["Status"].Style.ForeColor = Color.FromArgb(200, 128, 0);

				// Packet loss
				double lossPercent = total > 0 ? (failed / (double)total) * 100 : 0;
				row.Cells["PacketLoss"].Value = total > 0 ? lossPercent.ToString("0.0") + "%" : "-";

				if (lossPercent > 5)
					row.Cells["PacketLoss"].Style.ForeColor = Color.Red;
				else if (lossPercent > 0)
					row.Cells["PacketLoss"].Style.ForeColor = Color.FromArgb(200, 128, 0);
				else
					row.Cells["PacketLoss"].Style.ForeColor = Color.FromArgb(0, 128, 0);

				// Average latency (from recent pings)
				short[] recent = session.GetRecentPingTimes(100);
				var successes = recent.Where(t => t > 0);
				if (successes.Any())
				{
					double avgLatency = successes.Average(t => (double)t);
					row.Cells["AvgLatency"].Value = avgLatency.ToString("0") + " ms";
				}
				else
				{
					row.Cells["AvgLatency"].Value = total > 0 ? "Timeout" : "-";
				}

				// Sparkline data
				row.Cells["Sparkline"].Value = recent;
			}

			dataGridView.Invalidate();
		}

		private void DataGridView_CellClick(object sender, DataGridViewCellEventArgs e)
		{
			if (e.RowIndex < 0 || e.RowIndex >= sessions.Count) return;
			// Row index maps to session index; host tab is at tabIndex = rowIndex + 1 (Overview is index 0)
			HostNavigationRequested?.Invoke(this, e.RowIndex + 1);
		}

		protected override void Dispose(bool disposing)
		{
			if (disposing)
			{
				refreshTimer?.Stop();
				refreshTimer?.Dispose();
			}
			base.Dispose(disposing);
		}
	}
}
