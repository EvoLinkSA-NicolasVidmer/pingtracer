using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Net;
using System.Threading;
using System.Windows.Forms;

namespace PingTracer
{
	/// <summary>
	/// Encapsulates all per-host ping state. Each host being monitored gets its own
	/// HostPingSession instance containing graphs, targets, counters, and UI elements.
	/// </summary>
	public class HostPingSession : IDisposable
	{
		#region Properties

		/// <summary>
		/// The original host string entered by the user.
		/// </summary>
		public string Host { get; private set; }

		/// <summary>
		/// The resolved IP address string, set after DNS resolution.
		/// </summary>
		public string ResolvedAddress { get; set; }

		/// <summary>
		/// The tab page for this host session.
		/// </summary>
		public TabPage TabPage { get; private set; }

		/// <summary>
		/// The panel inside the tab page that holds ping graph controls.
		/// </summary>
		public Panel GraphPanel { get; private set; }

		/// <summary>
		/// Per-host graph collection, keyed by graph sorting order.
		/// (Was static MainForm.pingGraphs)
		/// </summary>
		public SortedList<int, PingGraphControl> PingGraphs { get; private set; }

		/// <summary>
		/// Per-host ping targets, keyed by graph sorting order.
		/// (Was static MainForm.pingTargets)
		/// </summary>
		public SortedList<int, IPAddress> PingTargets { get; private set; }

		/// <summary>
		/// Per-host success tracking for each target.
		/// (Was static MainForm.pingTargetHasAtLeastOneSuccess)
		/// </summary>
		public SortedList<int, bool> PingTargetHasAtLeastOneSuccess { get; private set; }

		/// <summary>
		/// Whether dead hosts have been cleared for this session.
		/// </summary>
		public bool ClearedDeadHosts { get; set; }

		/// <summary>
		/// The background worker for this session's ping loop.
		/// Public so MainForm (Plan 02) can assign and manage per-session BackgroundWorkers.
		/// </summary>
		public BackgroundWorker Worker { get; set; }

		#endregion

		#region Private Fields

		private int graphSortingCounter;
		private long successfulPings;
		private long failedPings;
		private long destinationFailedPings;
		private Settings settings;
		private bool disposed;

		#endregion

		#region Events

		/// <summary>
		/// Raised when the session needs to log something.
		/// </summary>
		public event Action<string> LogEntry;

		#endregion

		#region Constructor

		/// <summary>
		/// Creates a new HostPingSession for the specified host.
		/// </summary>
		/// <param name="host">The host string (IP or hostname).</param>
		/// <param name="settings">Reference to shared application settings.</param>
		public HostPingSession(string host, Settings settings)
		{
			this.Host = host;
			this.settings = settings;
			this.ClearedDeadHosts = false;
			this.graphSortingCounter = 0;
			this.successfulPings = 0;
			this.failedPings = 0;
			this.destinationFailedPings = 0;

			PingGraphs = new SortedList<int, PingGraphControl>();
			PingTargets = new SortedList<int, IPAddress>();
			PingTargetHasAtLeastOneSuccess = new SortedList<int, bool>();

			TabPage = new TabPage(host);
			GraphPanel = new Panel
			{
				Dock = DockStyle.Fill,
				BackColor = SystemColors.Window
			};
			TabPage.Controls.Add(GraphPanel);

			GraphPanel.Resize += GraphPanel_Resize;
		}

		#endregion

		#region Public Methods

		/// <summary>
		/// Adds a ping target to this session, creating a PingGraphControl for it.
		/// Mirrors the current MainForm.AddPingTarget logic but operates on this session's collections.
		/// </summary>
		/// <param name="ipAddress">The IP address of the ping target.</param>
		/// <param name="name">The display name or hostname.</param>
		/// <param name="reverseDnsLookup">Whether to perform reverse DNS lookup.</param>
		/// <param name="configureGraph">Callback so MainForm can apply current UI settings (thresholds, scaling, etc.).</param>
		public void AddPingTarget(IPAddress ipAddress, string name, bool reverseDnsLookup, Action<PingGraphControl> configureGraph)
		{
			if (ipAddress == null)
				return;

			try
			{
				if (GraphPanel.InvokeRequired)
				{
					GraphPanel.Invoke((Action<IPAddress, string, bool, Action<PingGraphControl>>)AddPingTarget,
						ipAddress, name, reverseDnsLookup, configureGraph);
				}
				else
				{
					int id = graphSortingCounter++;
					PingGraphControl graph = new PingGraphControl(this.settings, ipAddress, name, reverseDnsLookup);

					PingTargets.Add(id, ipAddress);
					PingGraphs.Add(id, graph);
					PingTargetHasAtLeastOneSuccess.Add(id, false);

					configureGraph?.Invoke(graph);

					GraphPanel.Controls.Add(graph);
					ResetGraphTimestamps();
					GraphPanel_Resize(null, null);
				}
			}
			catch (Exception ex)
			{
				if (!(ex.InnerException is ThreadAbortException))
					RaiseLogEntry(ex.ToString());
			}
		}

		/// <summary>
		/// Sets ShowTimestamps on the last graph only.
		/// Same logic as current MainForm.ResetGraphTimestamps() but uses this session's PingGraphs.
		/// </summary>
		public void ResetGraphTimestamps()
		{
			IList<PingGraphControl> all = PingGraphs.Values;
			foreach (PingGraphControl g in all)
				g.ShowTimestamps = false;

			PingGraphControl lastGraph = all.Count > 0 ? all[all.Count - 1] : null;
			if (lastGraph != null)
				lastGraph.ShowTimestamps = true;
		}

		/// <summary>
		/// Lays out graphs vertically in the panel.
		/// Same logic as current MainForm.panel_Graphs_Resize() but uses this session's PingGraphs and GraphPanel.
		/// </summary>
		public void GraphPanel_Resize(object sender, EventArgs e)
		{
			if (PingGraphs.Count == 0)
				return;
			IList<int> keys = PingGraphs.Keys;
			int width = GraphPanel.Width;
			int timestampsHeight = PingGraphs[keys[0]].TimestampsHeight;
			int height = GraphPanel.Height - timestampsHeight;
			int outerHeight = height / PingGraphs.Count;
			int innerHeight = outerHeight - 1;
			for (int i = 0; i < keys.Count; i++)
			{
				PingGraphControl graph = PingGraphs[keys[i]];
				if (i == keys.Count - 1)
				{
					int leftoverSpace = (height - (outerHeight * keys.Count)) + timestampsHeight;
					innerHeight += leftoverSpace + 1;
				}
				graph.SetBounds(0, i * outerHeight, width, innerHeight);
			}
		}

		/// <summary>
		/// Returns the current count of successful pings (thread-safe).
		/// </summary>
		public long GetSuccessfulPings()
		{
			return Interlocked.Read(ref successfulPings);
		}

		/// <summary>
		/// Returns the current count of failed pings (thread-safe).
		/// </summary>
		public long GetFailedPings()
		{
			return Interlocked.Read(ref failedPings);
		}

		/// <summary>
		/// Returns recent ping times from the destination (last hop) graph.
		/// Delegates to PingGraphControl.GetRecentPingTimes on the last graph in the sorted list.
		/// </summary>
		/// <param name="count">Number of recent pings to retrieve.</param>
		public short[] GetRecentPingTimes(int count)
		{
			if (PingGraphs.Count == 0)
				return new short[0];
			PingGraphControl destGraph = PingGraphs.Values[PingGraphs.Count - 1];
			return destGraph.GetRecentPingTimes(count);
		}

		/// <summary>
		/// Increments the successful ping counter (thread-safe).
		/// </summary>
		public void IncrementSuccessful()
		{
			Interlocked.Increment(ref successfulPings);
		}

		/// <summary>
		/// Increments the failed ping counter (thread-safe).
		/// </summary>
		public void IncrementFailed()
		{
			Interlocked.Increment(ref failedPings);
		}

		/// <summary>
		/// Increments the destination-only failed ping counter (thread-safe).
		/// Only called when the last hop (destination) ping fails.
		/// </summary>
		public void IncrementDestinationFailed()
		{
			Interlocked.Increment(ref destinationFailedPings);
		}

		/// <summary>
		/// Returns the current count of destination-only failed pings (thread-safe).
		/// </summary>
		public long GetDestinationFailedPings()
		{
			return Interlocked.Read(ref destinationFailedPings);
		}

		/// <summary>
		/// Resets both ping counters to zero (thread-safe).
		/// </summary>
		public void ResetCounts()
		{
			Interlocked.Exchange(ref successfulPings, 0);
			Interlocked.Exchange(ref failedPings, 0);
			Interlocked.Exchange(ref destinationFailedPings, 0);
		}

		/// <summary>
		/// Returns the next graph sorting ID and increments the counter.
		/// </summary>
		public int NextGraphId()
		{
			return graphSortingCounter++;
		}

		/// <summary>
		/// Raises the LogEntry event with the specified message.
		/// </summary>
		/// <param name="message">The log message.</param>
		public void RaiseLogEntry(string message)
		{
			LogEntry?.Invoke(message);
		}

		#endregion

		#region IDisposable

		/// <summary>
		/// Disposes of session resources: cancels the worker, removes event handlers,
		/// clears the graph panel, and disposes the tab page.
		/// </summary>
		public void Dispose()
		{
			Dispose(true);
			GC.SuppressFinalize(this);
		}

		protected virtual void Dispose(bool disposing)
		{
			if (disposed)
				return;

			if (disposing)
			{
				// Cancel worker if running
				if (Worker != null)
				{
					if (Worker.IsBusy)
						Worker.CancelAsync();
					Worker.Dispose();
					Worker = null;
				}

				// Remove resize handler
				if (GraphPanel != null)
					GraphPanel.Resize -= GraphPanel_Resize;

				// Clear graph controls
				if (GraphPanel != null)
					GraphPanel.Controls.Clear();

				// Dispose the tab page (also disposes child controls including GraphPanel)
				if (TabPage != null)
				{
					TabPage.Dispose();
					TabPage = null;
				}

				PingGraphs?.Clear();
				PingTargets?.Clear();
				PingTargetHasAtLeastOneSuccess?.Clear();
			}

			disposed = true;
		}

		#endregion
	}
}
