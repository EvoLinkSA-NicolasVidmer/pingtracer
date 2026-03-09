using PingTracer.Tracer;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace PingTracer
{
	public partial class MainForm : Form
	{
		public bool isRunning { get; private set; } = false;
		public bool graphsMaximized { get; private set; } = false;
		private volatile int pingDelay = 1000;

		//private const string dateFormatString = "yyyy'-'MM'-'dd hh':'mm':'ss':'fff tt";
		private const string fileNameFriendlyDateFormatString = "yyyy'-'MM'-'dd HH'-'mm'-'ss";

		/// <summary>
		/// TabControl that holds one tab per host session, with close buttons and drag-reorder.
		/// </summary>
		private DraggableTabControl tabControlHosts;

		/// <summary>
		/// Active ping sessions, one per host.
		/// </summary>
		private List<HostPingSession> activeSessions = new List<HostPingSession>();

		/// <summary>
		/// Overview dashboard panel showing summary of all hosts.
		/// </summary>
		private OverviewPanel overviewPanel;

		/// <summary>
		/// A hidden panel that will hold the graphs once clicked.
		/// </summary>
		Form panelForm = new Form();

		public Settings settings = new Settings();

		DateTime suppressHostSettingsSaveUntil = DateTime.MinValue;
		private string[] args;
		/// <summary>
		/// Event raised when pinging begins.  See <see cref="isRunning"/>.
		/// </summary>
		public event EventHandler StartedPinging = delegate { };
		/// <summary>
		/// Event raised when pinging stops.  See <see cref="isRunning"/>.
		/// </summary>
		public event EventHandler StoppedPinging = delegate { };
		/// <summary>
		/// Event raised when the selected Host field or Display Name field or Prefer IPv4 value changed.  See <see cref="txtHost"/> and <see cref="txtDisplayName"/> and <see cref="cbPreferIpv4"/>.
		/// </summary>
		public event EventHandler SelectedHostChanged = delegate { };
		/// <summary>
		/// Event raised when the graphs are maximized or restored to the regular window.  See <see cref="graphsMaximized"/>.
		/// </summary>
		public event EventHandler MaximizeGraphsChanged = delegate { };

		private bool _logFailures = false;
		/// <summary>
		/// Gets or sets a value indicating whether failures should be logged for the current UI state.
		/// </summary>
		public bool LogFailures
		{
			get
			{
				return _logFailures;
			}
			set
			{
				_logFailures = value;
				SetLogFailures(value);
			}
		}
		private void SetLogFailures(bool value)
		{
			if (this.InvokeRequired)
				this.Invoke((Action<bool>)SetLogFailures, value);
			else
				cbLogFailures.Checked = value;
		}

		private bool _logSuccesses = false;
		/// <summary>
		/// Gets or sets a value indicating whether failures should be logged for the current UI state.
		/// </summary>
		public bool LogSuccesses
		{
			get
			{
				return _logSuccesses;
			}
			set
			{
				_logFailures = value;
				SetLogSuccesses(value);
			}
		}
		private void SetLogSuccesses(bool value)
		{
			if (this.InvokeRequired)
				this.Invoke((Action<bool>)SetLogSuccesses, value);
			else
				cbLogSuccesses.Checked = value;
		}
		/// <summary>
		/// Assigned during MainForm construction, this field remembers the default window size.
		/// </summary>
		private readonly Size defaultWindowSize;
		/// <summary>
		/// Calls <see cref="_rememberCurrentPosition"/> throttled.
		/// </summary>
		private Action RememberCurrentPositionThrottled;

		public MainForm(string[] args)
		{
			this.args = args;
			RememberCurrentPositionThrottled = Throttle.Create(_rememberCurrentPosition, 250, ex => MessageBox.Show(ex.ToString()));

			InitializeComponent();

			// Create DraggableTabControl programmatically, replacing panel_Graphs in splitContainer1.Panel2
			tabControlHosts = new DraggableTabControl();
			tabControlHosts.Dock = DockStyle.Fill;
			tabControlHosts.ShowToolTips = true;
			tabControlHosts.SelectedIndexChanged += tabControlHosts_SelectedIndexChanged;
			tabControlHosts.TabsReordered += TabControl_TabsReordered;
			splitContainer1.Panel2.Controls.Remove(panel_Graphs);
			splitContainer1.Panel2.Controls.Add(tabControlHosts);

			overviewPanel = new OverviewPanel();
			overviewPanel.HostNavigationRequested += (s, tabIndex) =>
			{
				if (tabIndex > 0 && tabIndex < tabControlHosts.TabPages.Count)
					tabControlHosts.SelectedIndex = tabIndex;
			};

			defaultWindowSize = this.Size;
		}

		private void MainForm_Load(object sender, EventArgs e)
		{
			this.Text += " " + System.Reflection.Assembly.GetExecutingAssembly().GetName().Version.ToString();
			panelForm.Text = this.Text;
			panelForm.Icon = this.Icon;
			panelForm.FormClosing += panelForm_FormClosing;
			selectPingsPerSecond.SelectedIndex = 0;
			settings.Load();
			StartupOptions options = new StartupOptions(args);
			lock (settings.hostHistory)
			{
				HostSettings item = null;
				if (options.StartupHostName != null)
				{
					// Attempt to find the given hostname from the startup options.
					item = settings.hostHistory.FirstOrDefault(h => h.displayName == options.StartupHostName && _hostMatchesOptions(h, options));
					if (item == null)
						item = settings.hostHistory.FirstOrDefault(h => h.host == options.StartupHostName && _hostMatchesOptions(h, options));

					if (item == null)
					{
						// Create a new profile.
						// Base it on a displayName-only match.
						if (item == null)
							item = settings.hostHistory.FirstOrDefault(h => h.displayName == options.StartupHostName);
						// Base it on a host-only match.
						if (item == null)
							item = settings.hostHistory.FirstOrDefault(h => h.host == options.StartupHostName);
						// Base it on the most recently accessed configuration
						if (item == null)
							item = settings.hostHistory.FirstOrDefault();
						if (item != null)
							LoadProfileIntoUI(item);

						HostSettings newHS = NewHostSettingsFromUi();
						newHS.host = options.StartupHostName;
						newHS.displayName = "";
						if (options.PreferIPv6 != BoolOverride.Inherit)
							newHS.preferIpv4 = options.PreferIPv6 == BoolOverride.False;
						if (options.TraceRoute != BoolOverride.Inherit)
							newHS.doTraceRoute = options.TraceRoute == BoolOverride.True;
						settings.hostHistory.Add(newHS);
						item = newHS;
					}
				}
				if (item == null)
					item = settings.hostHistory.FirstOrDefault();
				if (item != null)
					LoadProfileIntoUI(item);
				else
				{
					this.ScalingMethod = GraphScalingMethod.Classic;
				}
			}
			selectPingsPerSecond_SelectedIndexChanged(null, null);
			AddKeyDownHandler(this);
			AddClickHandler(this);

			if (options.WindowLocation != null)
			{
				WindowParams wp = options.WindowLocation;

				Size s = this.Size;
				if (wp.W > 0)
					s.Width = wp.W + (settings.osWindowLeftMargin + settings.osWindowRightMargin);
				if (wp.H > 0)
					s.Height = wp.H + (settings.osWindowTopMargin + settings.osWindowBottomMargin);

				this.Location = new Point(wp.X - settings.osWindowLeftMargin, wp.Y - settings.osWindowTopMargin);
				this.Size = s;
			}
			else if (settings.lastWindowParams != null)
			{
				WindowParams wp = settings.lastWindowParams;

				Size s = this.Size;
				if (wp.W > 0)
					s.Width = wp.W;
				if (wp.H > 0)
					s.Height = wp.H;

				this.Location = new Point(wp.X, wp.Y);
				this.Size = s;
			}

			this.MoveOnscreenIfOffscreen();

			if (options.StartPinging)
				btnStart_Click(this, new EventArgs());

			if (options.MaximizeGraphs)
			{
				this.Hide(); // Hide before end of OnLoad to help reduce visual glitching.
				this.BeginInvoke((Action)(() =>
				{
					this.Show();
					SetGraphsMaximizedState(true);
				}));
			}

			this.Move += MainForm_MoveOrResize;
			this.Resize += MainForm_MoveOrResize;
		}

		private bool _hostMatchesOptions(HostSettings h, StartupOptions options)
		{
			return (options.PreferIPv6 == BoolOverride.Inherit || h.preferIpv4 == (options.PreferIPv6 == BoolOverride.False))
				&& (options.TraceRoute == BoolOverride.Inherit || h.doTraceRoute == (options.TraceRoute == BoolOverride.True));
		}

		/// <summary>
		/// Adds the <see cref="HandleKeyDown"/> handler to the KeyDown event of this control and most child controls.
		/// </summary>
		/// <param name="parent"></param>
		private void AddKeyDownHandler(Control parent)
		{
			if (parent.GetType() == typeof(NumericUpDown)
				|| (parent.GetType() == typeof(TextBox) && !((TextBox)parent).ReadOnly)
				)
			{
				return;
			}
			parent.KeyDown += new KeyEventHandler(HandleKeyDown);
			foreach (Control c in parent.Controls)
				AddKeyDownHandler(c);
		}
		/// <summary>
		/// Adds a basic "click to focus" handler to this control and most child controls. Non-input control types (such as Form or GroupBox) normally lack this functionality, so calling this makes it possible to unfocus input controls by clicking outside of them.
		/// </summary>
		/// <param name="parent"></param>
		private void AddClickHandler(Control parent)
		{
			if (parent.GetType() == typeof(NumericUpDown)
				|| (parent.GetType() == typeof(TextBox) && !((TextBox)parent).ReadOnly)
				)
			{
				return;
			}
			parent.Click += (sender, e) =>
			{
				((Control)sender).Focus();
			};
			foreach (Control c in parent.Controls)
				AddClickHandler(c);
		}
		/// <summary>
		/// 
		/// </summary>
		/// <param name="address"></param>
		/// <param name="preferIpv4"></param>
		/// <param name="hostName">This gets assigned a copy of [address] if DNS was queried to get the IP address.  Null if the IP was simply parsed from [address].</param>
		/// <returns></returns>
		/// <exception cref="Exception"></exception>

		private IPAddress StringToIp(string address, bool preferIpv4, out string hostName)
		{
			hostName = null;
			// Parse IP
			try
			{
				if (IPAddress.TryParse(address, out IPAddress tmp))
					return tmp;
			}
			catch (FormatException)
			{
			}

			// Try to resolve host name
			try
			{
				hostName = address;
				IPHostEntry iphe = Dns.GetHostEntry(address);
				if (preferIpv4)
				{
					IPAddress addr = iphe.AddressList.FirstOrDefault(ip => ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
					if (addr != null)
						return addr;
				}
				else
				{
					IPAddress addr = iphe.AddressList.FirstOrDefault(ip => ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6);
					if (addr != null)
						return addr;
				}
				if (iphe.AddressList.Length > 0)
					return iphe.AddressList[0];
			}
			catch (Exception e)
			{
				throw new Exception("Unable to resolve '" + address + "'", e);
			}

			// Fail
			throw new Exception("Unable to resolve '" + address + "'");
		}

		private void SessionWorker_DoWork(HostPingSession session, BackgroundWorker self)
		{
			bool traceRoute = false;
			bool reverseDnsLookup = false;
			bool preferIpv4 = true;

			// Read UI state (must use Invoke since we're on a background thread)
			this.Invoke((Action)(() =>
			{
				btnStart.Enabled = true;
				traceRoute = cbTraceroute.Checked;
				reverseDnsLookup = cbReverseDNS.Checked;
				preferIpv4 = cbPreferIpv4.Checked;
			}));

			IPAddress target = null;
			try
			{
				target = StringToIp(session.Host, preferIpv4, out string hostName);
				session.ResolvedAddress = target.ToString();

				CreateLogEntry("[" + session.Host + "] (" + GetTimestamp(DateTime.Now) + "): Initializing pings to " + session.Host);

				if (traceRoute)
				{
					CreateLogEntry("[" + session.Host + "] Tracing route ...");
					foreach (TracertEntry entry in Tracert.Trace(target, 64, 5000))
					{
						if (self.CancellationPending)
							break;
						CreateLogEntry("[" + session.Host + "] " + entry.ToString());
						session.AddPingTarget(entry.Address, null, reverseDnsLookup, ConfigureGraphFromUI);
					}
				}
				else
				{
					session.AddPingTarget(target, hostName, reverseDnsLookup, ConfigureGraphFromUI);
				}

				CreateLogEntry("[" + session.Host + "] Now beginning pings");
				Stopwatch sw = null;
				byte[] buffer = new byte[0];

				long numberOfPingLoopIterations = 0;
				DateTime tenPingsAt = DateTime.MinValue;
				while (!self.CancellationPending)
				{
					try
					{
						if (!session.ClearedDeadHosts && tenPingsAt != DateTime.MinValue && tenPingsAt.AddSeconds(10) < DateTime.Now)
						{
							if (session.PingTargets.Count > 1)
							{
								IList<int> pingTargetIds = session.PingTargets.Keys;
								foreach (int pingTargetId in pingTargetIds)
								{
									if (!session.PingTargetHasAtLeastOneSuccess[pingTargetId])
									{
										// This ping target has not yet had a successful response. Assume it never will, and delete it.
										this.Invoke((Action)(() =>
										{
											session.PingTargets.Remove(pingTargetId);
											session.GraphPanel.Controls.Remove(session.PingGraphs[pingTargetId]);
											RemoveEventHandlers(session.PingGraphs[pingTargetId]);
											session.PingGraphs.Remove(pingTargetId);
											if (session.PingGraphs.Count == 0)
											{
												Label lblNoGraphsRemain = new Label();
												lblNoGraphsRemain.Text = "All graphs were removed because" + Environment.NewLine + "none of the hosts responded to pings.";
												session.GraphPanel.Controls.Add(lblNoGraphsRemain);
											}
											session.ResetGraphTimestamps();
										}));
									}
								}
								this.Invoke((Action)(() =>
								{
									session.GraphPanel_Resize(null, null);
								}));
							}
							session.ClearedDeadHosts = true;
						}
						while (!self.CancellationPending && pingDelay <= 0)
							Thread.Sleep(100);
						if (!self.CancellationPending)
						{
							int msToWait = sw == null ? 0 : (int)(pingDelay - sw.ElapsedMilliseconds);
							while (!self.CancellationPending && msToWait > 0)
							{
								Thread.Sleep(Math.Min(msToWait, 100));
								msToWait = sw == null ? 0 : (int)(pingDelay - sw.ElapsedMilliseconds);
							}
							if (!self.CancellationPending)
							{
								if (sw == null)
									sw = Stopwatch.StartNew();
								else
									sw.Restart();
								DateTime lastPingAt = DateTime.Now;
								// We can't re-use the same Ping instance because it is only capable of one ping at a time.
								foreach (KeyValuePair<int, IPAddress> targetMapping in session.PingTargets)
								{
									PingGraphControl graph = session.PingGraphs[targetMapping.Key];
									long offset = graph.ClearNextOffset();
									Ping pinger = PingInstancePool.Get();
									pinger.PingCompleted += pinger_PingCompleted;
									pinger.SendAsync(targetMapping.Value, 5000, buffer, new object[] { lastPingAt, offset, graph, targetMapping.Key, targetMapping.Value, pinger, session });
								}
							}
						}
					}
					catch (ThreadAbortException ex)
					{
						throw ex;
					}
					catch (Exception)
					{
					}
					numberOfPingLoopIterations++;
					if (numberOfPingLoopIterations == 10)
						tenPingsAt = DateTime.Now;
				}
			}
			catch (Exception ex)
			{
				if (!(ex.InnerException is ThreadAbortException))
				{
					CreateLogEntry("[" + session.Host + "] Unable to resolve: " + ex.Message);
					// Update the tab title to show error state
					try
					{
						if (session.TabPage.InvokeRequired)
							session.TabPage.Invoke((Action)(() =>
							{
								session.TabPage.Text = session.Host + " (error)";
								session.TabPage.ToolTipText = ex.Message;
							}));
						else
						{
							session.TabPage.Text = session.Host + " (error)";
							session.TabPage.ToolTipText = ex.Message;
						}
					}
					catch (Exception) { }
				}
			}
			finally
			{
				CreateLogEntry("[" + session.Host + "] (" + GetTimestamp(DateTime.Now) + "): Shutting down pings to " + session.Host);
			}
		}

		private void SessionWorker_Completed(HostPingSession session)
		{
			// Check if all workers are done; if so, re-enable Start button
			bool allDone = true;
			foreach (var s in activeSessions)
			{
				if (s.Worker != null && s.Worker.IsBusy)
				{
					allDone = false;
					break;
				}
			}
			if (allDone && isRunning)
			{
				try
				{
					btnStart_Click(btnStart, new EventArgs());
				}
				catch (Exception) { }
			}
		}

		/// <summary>
		/// Applies current UI settings to a PingGraphControl and wires event handlers.
		/// </summary>
		private void ConfigureGraphFromUI(PingGraphControl graph)
		{
			graph.AlwaysShowServerNames = cbAlwaysShowServerNames.Checked;
			graph.Threshold_Bad = (int)nudBadThreshold.Value;
			graph.Threshold_Worse = (int)nudWorseThreshold.Value;
			graph.upperLimit = (int)nudUpLimit.Value;
			graph.lowerLimit = (int)nudLowLimit.Value;
			graph.ScalingMethod = ScalingMethod;
			graph.ShowLastPing = cbLastPing.Checked;
			graph.ShowAverage = cbAverage.Checked;
			graph.ShowJitter = cbJitter.Checked;
			graph.ShowMinMax = cbMinMax.Checked;
			graph.ShowPacketLoss = cbPacketLoss.Checked;
			graph.DrawLimitText = cbDrawLimits.Checked;
			AddEventHandlers(graph);
		}

		void pinger_PingCompleted(object sender, PingCompletedEventArgs e)
		{
			try
			{
				object[] args = (object[])e.UserState;
				DateTime time = (DateTime)args[0];
				long pingNum = (long)args[1];

				PingGraphControl graph = (PingGraphControl)args[2];
				int pingTargetId = (int)args[3]; // Do not assume the pingTargets or pingGraphs containers will have this key!
				IPAddress remoteHost = (IPAddress)args[4];
				Ping pinger = (Ping)args[5];
				HostPingSession session = (HostPingSession)args[6];
				pinger.PingCompleted -= pinger_PingCompleted;
				PingInstancePool.Recycle(pinger);
				if (e.Cancelled)
				{
					graph.AddPingLogToSpecificOffset(pingNum, new PingLog(time, 0, IPStatus.Unknown));
					session.IncrementFailed();
					return;
				}
				graph.AddPingLogToSpecificOffset(pingNum, new PingLog(time, (short)e.Reply.RoundtripTime, e.Reply.Status));
				if (e.Reply.Status != IPStatus.Success)
				{
					session.IncrementFailed();
					if (session.ClearedDeadHosts && LogFailures && session.PingTargets.ContainsKey(pingTargetId))
						CreateLogEntry("[" + session.Host + "] " + GetTimestamp(time) + ", " + remoteHost.ToString() + ": " + e.Reply.Status.ToString());
				}
				else
				{
					if (!session.ClearedDeadHosts)
					{
						session.PingTargetHasAtLeastOneSuccess[pingTargetId] = true;
					}
					session.IncrementSuccessful();
					if (LogSuccesses && session.PingTargets.ContainsKey(pingTargetId))
						CreateLogEntry("[" + session.Host + "] " + GetTimestamp(time) + ", " + remoteHost.ToString() + ": " + e.Reply.Status.ToString() + " in " + e.Reply.RoundtripTime + "ms");
				}
			}
			finally
			{
				// Show active tab's counts
				var activeSession = GetActiveSession();
				if (activeSession != null)
					UpdatePingCounts(activeSession.GetSuccessfulPings(), activeSession.GetFailedPings());
			}
		}
		/// <summary>
		/// Returns the currently active HostPingSession (the one whose tab is selected), or null.
		/// </summary>
		private HostPingSession GetActiveSession()
		{
			if (tabControlHosts == null || tabControlHosts.SelectedIndex < 1 || (tabControlHosts.SelectedIndex - 1) >= activeSessions.Count)
				return null;
			return activeSessions[tabControlHosts.SelectedIndex - 1];
		}
		private void AddEventHandlers(PingGraphControl graph)
		{
			graph.MouseDown += panel_Graphs_MouseDown;
			graph.MouseMove += panel_Graphs_MouseMove;
			graph.MouseLeave += panel_Graphs_MouseLeave;
			graph.MouseUp += panel_Graphs_MouseUp;
			graph.KeyDown += HandleKeyDown;
		}
		private void RemoveEventHandlers(PingGraphControl graph)
		{
			graph.MouseDown -= panel_Graphs_MouseDown;
			graph.MouseMove -= panel_Graphs_MouseMove;
			graph.MouseLeave -= panel_Graphs_MouseLeave;
			graph.MouseUp -= panel_Graphs_MouseUp;
			graph.KeyDown -= HandleKeyDown;
		}

		private void CreateLogEntry(string str)
		{
			try
			{
				if (txtOut.InvokeRequired)
					txtOut.Invoke(new Action<string>(CreateLogEntry), str);
				else
				{
					if (txtOut.TextLength > 32000)
						txtOut.Text = txtOut.Text.Substring(txtOut.TextLength - 22000); // Keep txtOut from growing out of control.
					txtOut.AppendText(Environment.NewLine + str);
					if (settings.logTextOutputToFile)
						File.AppendAllText("PingTracer_Output.txt", str + Environment.NewLine);
				}
			}
			catch (Exception)
			{
			}
		}

		private void UpdatePingCounts(long successful, long failed)
		{
			try
			{
				if (lblSuccessful.InvokeRequired)
					lblSuccessful.Invoke(new Action<long, long>(UpdatePingCounts), successful, failed);
				else
				{
					lblSuccessful.Text = successful.ToString();
					lblFailed.Text = failed.ToString();
				}
			}
			catch (Exception)
			{
			}
		}

		private void tabControlHosts_SelectedIndexChanged(object sender, EventArgs e)
		{
			var session = GetActiveSession();
			if (session != null)
				UpdatePingCounts(session.GetSuccessfulPings(), session.GetFailedPings());
			else
				UpdatePingCounts(0, 0);
		}

		private void TabControl_TabsReordered(object sender, EventArgs e)
		{
			// Rebuild activeSessions order to match TabPages order (skip index 0 = Overview)
			var reordered = new List<HostPingSession>();
			for (int i = 1; i < tabControlHosts.TabPages.Count; i++)
			{
				TabPage tp = tabControlHosts.TabPages[i];
				HostPingSession match = activeSessions.FirstOrDefault(s => s.TabPage == tp);
				if (match != null)
					reordered.Add(match);
			}
			activeSessions = reordered;
		}

		private void MainForm_FormClosing(object sender, FormClosingEventArgs e)
		{
			cla_form?.Close();
			if (isRunning)
			{
				SaveProfileFromUI();
				btnStart_Click(btnStart, new EventArgs());
			}
			overviewPanel.ClearSessions();
			// Dispose all sessions
			foreach (var session in activeSessions)
				session.Dispose();
			activeSessions.Clear();
		}

		void panelForm_FormClosing(object sender, FormClosingEventArgs e)
		{
			try
			{
				this.Close();
			}
			catch (Exception) { }
		}

		private void panel_Graphs_Resize(object sender, EventArgs e)
		{
			// Delegate to active session's GraphPanel resize
			var session = GetActiveSession();
			if (session != null)
				session.GraphPanel_Resize(sender, e);
		}

		#region Form input changed/clicked events

		private void lblHost_Click(object sender, EventArgs e)
		{
			LoadHostHistory();
			contextMenuStripHostHistory.Show(Cursor.Position);
		}

		private void rsitem_Click(object sender, EventArgs e)
		{
			if (isRunning)
			{
				MessageBox.Show("Cannot load a stored host while pings are running." + Environment.NewLine + "Please stop the pings first.");
				return;
			}

			ToolStripItem tsi = (ToolStripItem)sender;
			HostSettings p = (HostSettings)tsi.Tag;
			LoadProfileIntoUI(p);
		}

		private void mi_snapshotGraphs_Click(object sender, EventArgs e)
		{
			var session = GetActiveSession();
			if (session == null || session.ResolvedAddress == null)
			{
				MessageBox.Show("Unable to save a snapshot of the graphs at this time.");
				return;
			}
			string address = session.Host;
			using (Bitmap bmp = new Bitmap(session.GraphPanel.Width, session.GraphPanel.Height))
			{
				session.GraphPanel.DrawToBitmap(bmp, new Rectangle(0, 0, bmp.Width, bmp.Height));
				bmp.Save("PingTracer " + address + " " + DateTime.Now.ToString(fileNameFriendlyDateFormatString) + ".png", System.Drawing.Imaging.ImageFormat.Png);
			}
		}

		private void btnStart_Click(object sender, EventArgs e)
		{
			if (btnStart.InvokeRequired)
			{
				btnStart.BeginInvoke((Action<object, EventArgs>)btnStart_Click, sender, e);
				return;
			}
			SaveProfileFromUI();
			if (isRunning)
			{
				isRunning = false;
				btnStart.Text = "Click to Start";
				btnStart.BackColor = Color.FromArgb(255, 128, 128);
				// Cancel all session workers
				foreach (var session in activeSessions)
				{
					if (session.Worker != null && session.Worker.IsBusy)
						session.Worker.CancelAsync();
				}
				overviewPanel.ClearSessions();
				txtHost.Enabled = true;
				cbTraceroute.Enabled = true;
				cbReverseDNS.Enabled = true;
				StoppedPinging.Invoke(sender, e);
			}
			else
			{
				// Parse hosts from input
				string[] hosts = txtHost.Text.Split(new char[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries)
					.Select(h => h.Trim())
					.Where(h => !string.IsNullOrWhiteSpace(h))
					.Distinct(StringComparer.OrdinalIgnoreCase)
					.ToArray();

				if (hosts.Length == 0)
				{
					CreateLogEntry("No hosts specified");
					return;
				}

				// Dispose old sessions and clear tabs
				foreach (var oldSession in activeSessions)
					oldSession.Dispose();
				activeSessions.Clear();
				tabControlHosts.TabPages.Clear();

				// Insert Overview tab at index 0 (always present, not closeable)
				TabPage overviewTab = new TabPage("Overview");
				overviewPanel.Dock = DockStyle.Fill;
				overviewTab.Controls.Add(overviewPanel);
				tabControlHosts.TabPages.Add(overviewTab);

				isRunning = true;
				btnStart.Text = "Click to Stop";
				btnStart.BackColor = Color.FromArgb(128, 255, 128);

				// Create one session per host
				foreach (string host in hosts)
				{
					HostPingSession session = new HostPingSession(host, settings);
					session.LogEntry += (msg) => CreateLogEntry("[" + session.Host + "] " + msg);
					tabControlHosts.TabPages.Add(session.TabPage);
					activeSessions.Add(session);
				}

				// Start a BackgroundWorker for each session
				foreach (var session in activeSessions)
				{
					BackgroundWorker worker = new BackgroundWorker();
					worker.WorkerSupportsCancellation = true;
					worker.DoWork += (s, args) => SessionWorker_DoWork(session, (BackgroundWorker)s);
					worker.RunWorkerCompleted += (s, args) => SessionWorker_Completed(session);
					session.Worker = worker;
					worker.RunWorkerAsync();
				}

				overviewPanel.UpdateSessions(activeSessions);
				tabControlHosts.SelectedIndex = 0;

				txtHost.Enabled = false;
				cbTraceroute.Enabled = false;
				cbReverseDNS.Enabled = false;
				StartedPinging.Invoke(sender, e);
			}
		}

		private void nudPingsPerSecond_ValueChanged(object sender, EventArgs e)
		{
			SaveProfileIfProfileAlreadyExists();
			if (nudPingsPerSecond.Value == 0)
				pingDelay = 0;
			else if (selectPingsPerSecond.SelectedIndex == 0)
				pingDelay = Math.Max(100, (int)(1000 / nudPingsPerSecond.Value));
			else
				pingDelay = Math.Max(100, (int)(1000 * nudPingsPerSecond.Value));
		}

		private void selectPingsPerSecond_SelectedIndexChanged(object sender, EventArgs e)
		{
			if (selectPingsPerSecond.SelectedIndex == 0)
				nudPingsPerSecond.Maximum = 10;
			else
				nudPingsPerSecond.Maximum = 600;
			nudPingsPerSecond_ValueChanged(sender, e);
		}

		private void cbAlwaysShowServerNames_CheckedChanged(object sender, EventArgs e)
		{
			SaveProfileIfProfileAlreadyExists();
			try
			{
				foreach (var session in activeSessions)
					foreach (PingGraphControl graph in session.PingGraphs.Values)
					{
						graph.AlwaysShowServerNames = cbAlwaysShowServerNames.Checked;
						graph.Invalidate();
					}
			}
			catch (Exception)
			{
			}
		}

		private void nudBadThreshold_ValueChanged(object sender, EventArgs e)
		{
			SaveProfileIfProfileAlreadyExists();
			if (nudWorseThreshold.Value < nudBadThreshold.Value)
				nudWorseThreshold.Value = nudBadThreshold.Value;
			try
			{
				foreach (var session in activeSessions)
					foreach (PingGraphControl graph in session.PingGraphs.Values)
					{
						graph.Threshold_Bad = (int)nudBadThreshold.Value;
						graph.Invalidate();
					}
			}
			catch (Exception)
			{
			}
		}

		private void nudWorseThreshold_ValueChanged(object sender, EventArgs e)
		{
			SaveProfileIfProfileAlreadyExists();
			if (nudBadThreshold.Value > nudWorseThreshold.Value)
				nudBadThreshold.Value = nudWorseThreshold.Value;
			try
			{
				foreach (var session in activeSessions)
					foreach (PingGraphControl graph in session.PingGraphs.Values)
					{
						graph.Threshold_Worse = (int)nudWorseThreshold.Value;
						graph.Invalidate();
					}
			}
			catch (Exception)
			{
			}
		}

		private void nudUpLimit_ValueChanged(object sender, EventArgs e)
		{
			if (nudUpLimit.Value <= nudLowLimit.Value)
				nudLowLimit.Value = nudUpLimit.Value - 1;
			SaveProfileIfProfileAlreadyExists();
			try
			{
				foreach (var session in activeSessions)
					foreach (PingGraphControl graph in session.PingGraphs.Values)
					{
						graph.upperLimit = (int)nudUpLimit.Value;
						graph.Invalidate();
					}
			}
			catch (Exception)
			{
			}
		}

		private void nudLowLimit_ValueChanged(object sender, EventArgs e)
		{
			if (nudLowLimit.Value >= nudUpLimit.Value)
				nudUpLimit.Value = nudLowLimit.Value + 1;
			SaveProfileIfProfileAlreadyExists();
			try
			{
				foreach (var session in activeSessions)
					foreach (PingGraphControl graph in session.PingGraphs.Values)
					{
						graph.lowerLimit = (int)nudLowLimit.Value;
						graph.Invalidate();
					}
			}
			catch (Exception)
			{
			}
		}

		private void cbLastPing_CheckedChanged(object sender, EventArgs e)
		{
			SaveProfileIfProfileAlreadyExists();
			try
			{
				foreach (var session in activeSessions)
					foreach (PingGraphControl graph in session.PingGraphs.Values)
					{
						graph.ShowLastPing = cbLastPing.Checked;
						graph.Invalidate();
					}
			}
			catch (Exception)
			{
			}
		}

		private void cbAverage_CheckedChanged(object sender, EventArgs e)
		{
			SaveProfileIfProfileAlreadyExists();
			try
			{
				foreach (var session in activeSessions)
					foreach (PingGraphControl graph in session.PingGraphs.Values)
					{
						graph.ShowAverage = cbAverage.Checked;
						graph.Invalidate();
					}
			}
			catch (Exception)
			{
			}
		}
		private void cbJitter_CheckedChanged(object sender, EventArgs e)
		{
			SaveProfileIfProfileAlreadyExists();
			try
			{
				foreach (var session in activeSessions)
					foreach (PingGraphControl graph in session.PingGraphs.Values)
					{
						graph.ShowJitter = cbJitter.Checked;
						graph.Invalidate();
					}
			}
			catch (Exception)
			{
			}
		}
		private void cbMinMax_CheckedChanged(object sender, EventArgs e)
		{
			SaveProfileIfProfileAlreadyExists();
			try
			{
				foreach (var session in activeSessions)
					foreach (PingGraphControl graph in session.PingGraphs.Values)
					{
						graph.ShowMinMax = cbMinMax.Checked;
						graph.Invalidate();
					}
			}
			catch (Exception)
			{
			}
		}

		private void cbPacketLoss_CheckedChanged(object sender, EventArgs e)
		{
			SaveProfileIfProfileAlreadyExists();
			try
			{
				foreach (var session in activeSessions)
					foreach (PingGraphControl graph in session.PingGraphs.Values)
					{
						graph.ShowPacketLoss = cbPacketLoss.Checked;
						graph.Invalidate();
					}
			}
			catch (Exception)
			{
			}
		}

		private void cbDrawLimits_CheckedChanged(object sender, EventArgs e)
		{
			SaveProfileIfProfileAlreadyExists();
			try
			{
				foreach (var session in activeSessions)
					foreach (PingGraphControl graph in session.PingGraphs.Values)
					{
						graph.DrawLimitText = cbDrawLimits.Checked;
						graph.Invalidate();
					}
			}
			catch (Exception)
			{
			}
		}

		private void cbTraceroute_CheckedChanged(object sender, EventArgs e)
		{
			SaveProfileIfProfileAlreadyExists();
			SelectedHostChanged.Invoke(sender, e);
		}

		private void cbReverseDNS_CheckedChanged(object sender, EventArgs e)
		{
			SaveProfileIfProfileAlreadyExists();
		}

		private void txtDisplayName_TextChanged(object sender, EventArgs e)
		{
			SaveProfileIfProfileAlreadyExists();
			SelectedHostChanged.Invoke(sender, e);
		}

		private void cbPreferIpv4_CheckedChanged(object sender, EventArgs e)
		{
			SaveProfileIfProfileAlreadyExists();
			SelectedHostChanged.Invoke(sender, e);
		}

		private void cbLogFailures_CheckedChanged(object sender, EventArgs e)
		{
			_logFailures = cbLogFailures.Checked;
			SaveProfileIfProfileAlreadyExists();
		}

		private void cbLogSuccesses_CheckedChanged(object sender, EventArgs e)
		{
			_logSuccesses = cbLogSuccesses.Checked;
			SaveProfileIfProfileAlreadyExists();
		}

		private void txtHost_TextChanged(object sender, EventArgs e)
		{
			// This txtHost_TextChanged event handler was added on 2023-08-02, so it did not call SaveProfileIfProfileAlreadyExists(); like most other event handlers.
			SelectedHostChanged.Invoke(sender, e);
		}
		#endregion

		#region Mouse graph events

		Point pGraphMouseDownAt = new Point();
		Point pGraphMouseLastSeenAt = new Point();
		bool mouseIsDownOnGraph = false;
		bool mouseMayBeClickingGraph = false;
		DateTime lastAllGraphsRedrawTime = DateTime.MinValue;
		private void panel_Graphs_MouseDown(object sender, MouseEventArgs e)
		{
			mouseIsDownOnGraph = true;
			mouseMayBeClickingGraph = true;
			pGraphMouseLastSeenAt = pGraphMouseDownAt = e.Location;
		}
		private void refreshGraphs()
		{
			if (settings.fastRefreshScrollingGraphs || DateTime.Now > lastAllGraphsRedrawTime.AddSeconds(1))
			{
				bool aGraphIsInvalidated = false;
				foreach (var session in activeSessions)
					foreach (PingGraphControl graph in session.PingGraphs.Values)
						if (graph.IsInvalidatedSync)
						{
							aGraphIsInvalidated = true;
							break;
						}
				if (!aGraphIsInvalidated)
				{
					Console.WriteLine("Invalidating All");
					foreach (var session in activeSessions)
						foreach (PingGraphControl graph in session.PingGraphs.Values)
							graph.InvalidateSync();
					lastAllGraphsRedrawTime = DateTime.Now;
				}
			}
		}
		private void panel_Graphs_MouseMove(object sender, MouseEventArgs e)
		{
			bool mouseWasTeleported = false;
			if (mouseIsDownOnGraph)
			{
				if (Math.Abs(pGraphMouseDownAt.X - e.Location.X) >= 5
					|| Math.Abs(pGraphMouseDownAt.Y - e.Location.Y) >= 5)
				{
					mouseMayBeClickingGraph = false;
				}

				if (!mouseMayBeClickingGraph)
				{
					int dx = e.Location.X - pGraphMouseLastSeenAt.X;
					var activeSession = GetActiveSession();
					if (dx != 0 && settings.graphScrollMultiplier != 0 && activeSession != null && activeSession.PingGraphs.Count > 0)
					{
						int newScrollXOffset = activeSession.PingGraphs.Values[0].ScrollXOffset + (dx * settings.graphScrollMultiplier);

						foreach (PingGraphControl graph in activeSession.PingGraphs.Values)
							graph.ScrollXOffset = newScrollXOffset;

						refreshGraphs();

						#region while scrolling graph: teleport mouse when reaching end of graph to enable scrolling infinitely without having to click again
						this.Cursor = new Cursor(Cursor.Current.Handle);
						int offset = 9; //won't work with maximized window otherwise
						if (Cursor.Position.X >= Bounds.Right - offset) //mouse moving to the right
						{
							//teleport mouse to the left
							Cursor.Position = new Point(Bounds.Left + offset, Cursor.Position.Y);
							mouseWasTeleported = true;
						}
						else if (Cursor.Position.X <= Bounds.Left + offset //cursor moving to the left
							&& activeSession.PingGraphs.Values[0].ScrollXOffset != 0) //usability: only teleport mouse if graphs have data to the right
						{
							//teleport mouse to the right
							Cursor.Position = new Point(Bounds.Right - offset, Cursor.Position.Y);
							mouseWasTeleported = true;
						}
						#endregion
					}
				}
			}
			pGraphMouseLastSeenAt = mouseWasTeleported ? PointToClient(Cursor.Position) : e.Location;
		}
		private void panel_Graphs_MouseLeave(object sender, EventArgs e)
		{
			mouseMayBeClickingGraph = mouseIsDownOnGraph = false;
		}
		private void panel_Graphs_MouseUp(object sender, MouseEventArgs e)
		{
			if (mouseIsDownOnGraph
				&& mouseMayBeClickingGraph
				&& (Math.Abs(pGraphMouseDownAt.X - e.Location.X) < 5
					&& Math.Abs(pGraphMouseDownAt.Y - e.Location.Y) < 5))
			{
				panel_Graphs_Click(sender, e);
			}
			pGraphMouseLastSeenAt = e.Location;
			mouseMayBeClickingGraph = mouseIsDownOnGraph = false;
		}
		private void HandleKeyDown(object sender, KeyEventArgs e)
		{
			var activeSession = GetActiveSession();
			if (activeSession == null)
				return;
			switch (e.KeyData)
			{
				case Keys.Home: //Pos1
				case Keys.D9:
					foreach (PingGraphControl graph in activeSession.PingGraphs.Values)
						graph.ScrollXOffset = graph.cachedPings - graph.Width - (settings.delayMostRecentPing ? 1 : 0);
					e.Handled = true;
					break;
				case Keys.End:
				case Keys.D0:
					foreach (PingGraphControl graph in activeSession.PingGraphs.Values)
						graph.ScrollXOffset = 0;
					e.Handled = true;
					break;
				case Keys.PageUp:
				case Keys.OemMinus:
					foreach (PingGraphControl graph in activeSession.PingGraphs.Values)
						graph.ScrollXOffset += graph.Width;
					e.Handled = true;
					break;
				case Keys.PageDown:
				case Keys.Oemplus:
					foreach (PingGraphControl graph in activeSession.PingGraphs.Values)
						graph.ScrollXOffset -= graph.Width;
					e.Handled = true;
					break;
				default:
					return;
			}
			refreshGraphs();
		}
		private void panel_Graphs_Click(object sender, EventArgs e)
		{
			SetGraphsMaximizedState(!graphsMaximized);
		}
		private void SetGraphsMaximizedState(bool maximize)
		{
			if (maximize)
			{
				var activeSession = GetActiveSession();
				if (activeSession == null) return;
				graphsMaximized = true;
				panelForm.FormBorderStyle = System.Windows.Forms.FormBorderStyle.None;
				panelForm.Controls.Add(activeSession.GraphPanel);
				activeSession.GraphPanel.Dock = DockStyle.Fill;
				panelForm.Show();
				panelForm.SetBounds(this.Left + settings.osWindowLeftMargin, this.Top + settings.osWindowTopMargin, this.Width - (settings.osWindowLeftMargin + settings.osWindowRightMargin), this.Height - settings.osWindowBottomMargin);
				this.Hide();
				MaximizeGraphsChanged.Invoke(this, EventArgs.Empty);
			}
			else
			{
				graphsMaximized = false;
				var activeSession = GetActiveSession();
				if (activeSession != null)
				{
					activeSession.TabPage.Controls.Add(activeSession.GraphPanel);
					activeSession.GraphPanel.Dock = DockStyle.Fill;
				}
				this.Show();
				panelForm.Hide();
				MaximizeGraphsChanged.Invoke(this, EventArgs.Empty);
			}
		}
		#endregion

		#region Host History

		private void LoadHostHistory()
		{
			contextMenuStripHostHistory.Items.Clear();
			bool first = true;
			lock (settings.hostHistory)
			{
				foreach (HostSettings p in settings.hostHistory)
				{
					ToolStripItem item = new ToolStripMenuItem();
					//Name that will appear on the menu
					if (string.IsNullOrWhiteSpace(p.displayName))
						item.Text = (p.preferIpv4 ? "" : "[ipv6] ") + p.host;
					else
						item.Text = (p.preferIpv4 ? "" : "[ipv6] ") + p.displayName + " [" + p.host + "]";
					item.Tag = p;
					item.Click += new EventHandler(rsitem_Click);

					if (first)
						item.Font = new Font(item.Font, FontStyle.Bold);
					first = false;

					//Add the submenu to the parent menu
					contextMenuStripHostHistory.Items.Add(item);
				}
			}
		}

		private void LoadProfileIntoUI(HostSettings hs)
		{
			suppressHostSettingsSaveUntil = DateTime.Now.AddMilliseconds(100);

			txtHost.Text = hs.host;
			txtDisplayName.Text = hs.displayName;
			selectPingsPerSecond.SelectedIndex = hs.pingsPerSecond ? 0 : 1;
			nudPingsPerSecond.Value = hs.rate;
			cbTraceroute.Checked = hs.doTraceRoute;
			cbReverseDNS.Checked = hs.reverseDnsLookup;
			cbAlwaysShowServerNames.Checked = hs.drawServerNames;
			cbLastPing.Checked = hs.drawLastPing;
			cbAverage.Checked = hs.drawAverage;
			cbJitter.Checked = hs.drawJitter;
			cbMinMax.Checked = hs.drawMinMax;
			cbPacketLoss.Checked = hs.drawPacketLoss;
			cbDrawLimits.Checked = hs.drawLimitText;
			nudBadThreshold.Value = hs.badThreshold;
			nudWorseThreshold.Value = hs.worseThreshold;
			nudUpLimit.Value = hs.upperLimit;
			nudLowLimit.Value = hs.lowerLimit;
			ScalingMethod = (GraphScalingMethod)hs.ScalingMethodID;
			cbPreferIpv4.Checked = hs.preferIpv4;
			LogFailures = hs.logFailures;
			LogSuccesses = hs.logSuccesses;


			lock (settings.hostHistory)
			{
				for (int i = 1; i < settings.hostHistory.Count; i++)
					if (settings.hostHistory[i].host == hs.host && settings.hostHistory[i].preferIpv4 == hs.preferIpv4)
					{
						HostSettings justLoaded = settings.hostHistory[i];
						settings.hostHistory.RemoveAt(i);
						settings.hostHistory.Insert(0, justLoaded);
						break;
					}
			}
		}

		private void SaveProfileIfProfileAlreadyExists()
		{
			lock (settings.hostHistory)
			{
				bool hostExists = false;
				foreach (HostSettings p in settings.hostHistory)
					if (p.host == txtHost.Text && p.preferIpv4 == cbPreferIpv4.Checked)
					{
						hostExists = true;
						break;
					}
				if (hostExists)
					SaveProfileFromUI();
			}
		}
		private void DeleteCurrentProfile()
		{
			lock (settings.hostHistory)
			{
				bool hostExisted = false;
				for (int i = 0; i < settings.hostHistory.Count; i++)
					if (settings.hostHistory[i].host == txtHost.Text && settings.hostHistory[i].preferIpv4 == cbPreferIpv4.Checked)
					{
						hostExisted = true;
						settings.hostHistory.RemoveAt(i);
						settings.Save();
						break;
					}
				if (hostExisted)
				{
					if (settings.hostHistory.Count > 0)
						LoadProfileIntoUI(settings.hostHistory[0]);
				}
			}
		}
		private HostSettings NewHostSettingsFromUi()
		{
			HostSettings p = new HostSettings();
			p.host = txtHost.Text;
			p.displayName = txtDisplayName.Text;
			p.rate = (int)nudPingsPerSecond.Value;
			p.pingsPerSecond = selectPingsPerSecond.SelectedIndex == 0;
			p.doTraceRoute = cbTraceroute.Checked;
			p.reverseDnsLookup = cbReverseDNS.Checked;
			p.drawServerNames = cbAlwaysShowServerNames.Checked;
			p.drawLastPing = cbLastPing.Checked;
			p.drawAverage = cbAverage.Checked;
			p.drawJitter = cbJitter.Checked;
			p.drawMinMax = cbMinMax.Checked;
			p.drawPacketLoss = cbPacketLoss.Checked;
			p.drawLimitText = cbDrawLimits.Checked;
			p.badThreshold = (int)nudBadThreshold.Value;
			p.worseThreshold = (int)nudWorseThreshold.Value;
			p.upperLimit = (int)nudUpLimit.Value;
			p.lowerLimit = (int)nudLowLimit.Value;
			p.ScalingMethodID = (int)ScalingMethod;
			p.preferIpv4 = cbPreferIpv4.Checked;
			p.logFailures = LogFailures;
			p.logSuccesses = LogSuccesses;
			return p;
		}
		/// <summary>
		/// Adds the current profile to the profile list and saves it to disk. Only if the host field is defined.
		/// </summary>
		private void SaveProfileFromUI()
		{
			if (DateTime.Now < suppressHostSettingsSaveUntil)
				return;
			HostSettings p = NewHostSettingsFromUi();
			if (!string.IsNullOrWhiteSpace(p.host))
			{
				lock (settings.hostHistory)
				{
					if (settings.hostHistory.Count == 0)
						settings.hostHistory.Add(p);
					else
					{
						for (int i = 0; i < settings.hostHistory.Count; i++)
							if (settings.hostHistory[i].host == p.host && settings.hostHistory[i].preferIpv4 == p.preferIpv4)
							{
								settings.hostHistory.RemoveAt(i);
								break;
							}
						settings.hostHistory.Insert(0, p);
					}
					settings.Save();
				}
			}
		}

		private HostSettings FindHostSettings(string host)
		{
			lock (settings.hostHistory)
			{
				// Match by host string and current IPv4 preference
				HostSettings match = settings.hostHistory.FirstOrDefault(
					h => h.host == host && h.preferIpv4 == cbPreferIpv4.Checked);
				if (match == null)
				{
					// Match by host string only (different IPv4 pref)
					match = settings.hostHistory.FirstOrDefault(h => h.host == host);
				}
				if (match == null)
				{
					// No history for this host -- create from current UI defaults
					match = NewHostSettingsFromUi();
					match.host = host;
				}
				return match;
			}
		}

		#endregion

		private void mi_Exit_Click(object sender, EventArgs e)
		{
			this.Close();
		}

		OptionsForm optionsForm = null;
		private void mi_Options_Click(object sender, EventArgs e)
		{
			if (optionsForm != null)
			{
				optionsForm.Close();
				optionsForm.Dispose();
			}
			optionsForm = new OptionsForm(this);
			optionsForm.Show();
		}

		private void mi_deleteHost_Click(object sender, EventArgs e)
		{
			DeleteCurrentProfile();
		}
		private string GetTimestamp(DateTime time)
		{
			if (!string.IsNullOrWhiteSpace(settings.customTimeStr))
			{
				try
				{
					return time.ToString(settings.customTimeStr);
				}
				catch { }
			}
			return time.ToString();
		}

		private void menuItem_OpenSettingsFolder_Click(object sender, EventArgs e)
		{
			settings.OpenSettingsFolder();
		}

		CommandLineArgsForm cla_form;
		private void menuItem_CommandLineArgs_Click(object sender, EventArgs e)
		{
			if (cla_form == null)
			{
				cla_form = new CommandLineArgsForm(this);
				cla_form.FormClosed += (sender2, e2) => { cla_form = null; };
				cla_form.Show();
			}
			else
				cla_form.BringToFront();
		}

		private void MainForm_Click(object sender, EventArgs e)
		{
			//this.Focus();
		}


		private void MainForm_MoveOrResize(object sender, EventArgs e)
		{
			RememberCurrentPositionThrottled();
		}

		/// <summary>
		/// Do not call this directly.  Instead, call <see cref="RememberCurrentPositionThrottled"/>.
		/// </summary>
		private void _rememberCurrentPosition()
		{
			if (this.InvokeRequired)
				this.Invoke((Action)_rememberCurrentPosition);
			else
			{
				lock (settings.hostHistory)
				{
					settings.lastWindowParams = new WindowParams(this.Location.X, this.Location.Y, this.Size.Width, this.Size.Height);
					settings.Save();
				}
				lblFailed.Text = (int.Parse(lblFailed.Text) + 1).ToString();
			}
		}

		private void menuItem_resetWindowSize_Click(object sender, EventArgs e)
		{
			this.Size = defaultWindowSize;
		}

		private void SetScaleLimitFieldsEnabledState()
		{
			if (ScalingMethod == GraphScalingMethod.Classic || ScalingMethod == GraphScalingMethod.Zoom || ScalingMethod == GraphScalingMethod.Fixed)
			{
				nudUpLimit.Enabled = nudLowLimit.Enabled = true;
			}
			else
			{
				nudUpLimit.Enabled = nudLowLimit.Enabled = false;
			}
		}
		/// <summary>
		/// <para>Gets or sets the ID of the graph scaling method currently selected in the GUI.</para>
		/// <para>IDs currently correspond exactly with the dropdown list item index:</para>
		/// <para>0: Classic</para>
		/// <para>1: Zoom</para>
		/// <para>2: Zoom Unlimited</para>
		/// <para>3: Fixed</para>
		/// <para>This implementation is subject to change in the future.</para>
		/// </summary>
		public GraphScalingMethod ScalingMethod
		{
			get
			{
				return (GraphScalingMethod)cbScalingMethod.SelectedIndex;
			}
			set
			{
				cbScalingMethod.SelectedIndex = (int)value;
			}
		}

		private void cbScalingMethod_SelectedIndexChanged(object sender, EventArgs e)
		{
			SetScaleLimitFieldsEnabledState();
			SaveProfileIfProfileAlreadyExists();
			try
			{
				foreach (var session in activeSessions)
					foreach (PingGraphControl graph in session.PingGraphs.Values)
					{
						graph.ScalingMethod = ScalingMethod;
						graph.Invalidate();
					}
			}
			catch (Exception)
			{
			}
		}
	}
}
