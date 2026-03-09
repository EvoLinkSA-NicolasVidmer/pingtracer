using System;
using System.Drawing;
using System.Windows.Forms;

namespace PingTracer
{
	/// <summary>
	/// TabControl subclass with owner-drawn close buttons on host tabs (index > 0)
	/// and drag-to-reorder support. The Overview tab at index 0 has no close button
	/// and cannot be dragged or become a drag target.
	/// </summary>
	public class DraggableTabControl : TabControl
	{
		/// <summary>
		/// Fires with the tab index when the close button on a host tab is clicked.
		/// </summary>
		public event EventHandler<int> TabCloseRequested;

		/// <summary>
		/// Fires after a drag-drop reorder of tabs completes.
		/// </summary>
		public event EventHandler TabsReordered;

		private TabPage draggedTab;
		private Point dragStartPoint;

		public DraggableTabControl()
		{
			DrawMode = TabDrawMode.OwnerDrawFixed;
			Padding = new Point(12, 4);
			AllowDrop = true;
			SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);

			DrawItem += DrawTabWithCloseButton;
			MouseDown += HandleTabMouseDown;
			MouseMove += HandleTabMouseMove;
			DragOver += HandleDragOver;
			DragDrop += HandleDragDrop;
		}

		private void DrawTabWithCloseButton(object sender, DrawItemEventArgs e)
		{
			Rectangle tabRect = GetTabRect(e.Index);
			bool isSelected = (SelectedIndex == e.Index);

			// Fill background
			using (Brush bgBrush = new SolidBrush(isSelected ? SystemColors.Control : SystemColors.ControlLight))
			{
				e.Graphics.FillRectangle(bgBrush, tabRect);
			}

			if (e.Index == 0)
			{
				// Overview tab: text centered, no close button
				TextRenderer.DrawText(e.Graphics, TabPages[e.Index].Text, Font, tabRect,
					SystemColors.ControlText,
					TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter);
			}
			else
			{
				// Host tabs: text left-aligned with room for close button, then draw "x"
				Rectangle textRect = new Rectangle(tabRect.X + 4, tabRect.Y, tabRect.Width - 22, tabRect.Height);
				TextRenderer.DrawText(e.Graphics, TabPages[e.Index].Text, Font, textRect,
					SystemColors.ControlText,
					TextFormatFlags.VerticalCenter | TextFormatFlags.Left);

				// Draw close "x" button
				using (Font closeFont = new Font(Font.FontFamily, 7f, FontStyle.Bold))
				{
					e.Graphics.DrawString("x", closeFont, Brushes.Gray,
						tabRect.Right - 18, tabRect.Y + 4);
				}
			}
		}

		private void HandleTabMouseDown(object sender, MouseEventArgs e)
		{
			for (int i = 0; i < TabCount; i++)
			{
				Rectangle tabRect = GetTabRect(i);
				if (!tabRect.Contains(e.Location))
					continue;

				if (i == 0)
					return; // Overview tab: not closeable, not draggable

				// Check if click is on close button
				Rectangle closeRect = new Rectangle(tabRect.Right - 18, tabRect.Y + 4, 14, 14);
				if (closeRect.Contains(e.Location))
				{
					TabCloseRequested?.Invoke(this, i);
					return;
				}

				// Start potential drag
				draggedTab = TabPages[i];
				dragStartPoint = e.Location;
				return;
			}
		}

		private void HandleTabMouseMove(object sender, MouseEventArgs e)
		{
			if (draggedTab != null && e.Button == MouseButtons.Left)
			{
				// Check minimum drag distance to avoid eating close-button clicks
				int dx = Math.Abs(e.Location.X - dragStartPoint.X);
				int dy = Math.Abs(e.Location.Y - dragStartPoint.Y);
				if (dx > 5 || dy > 5)
				{
					DoDragDrop(draggedTab, DragDropEffects.Move);
				}
			}
		}

		private void HandleDragOver(object sender, DragEventArgs drgevent)
		{
			TabPage dragged = drgevent.Data.GetData(typeof(TabPage)) as TabPage;
			if (dragged == null)
			{
				drgevent.Effect = DragDropEffects.None;
				return;
			}

			Point clientPoint = PointToClient(new Point(drgevent.X, drgevent.Y));
			TabPage hoverTab = GetTabPageAtPoint(clientPoint);
			if (hoverTab != null && TabPages.IndexOf(hoverTab) != 0)
			{
				drgevent.Effect = DragDropEffects.Move;
			}
			else
			{
				drgevent.Effect = DragDropEffects.None;
			}
		}

		private void HandleDragDrop(object sender, DragEventArgs drgevent)
		{
			TabPage dragged = drgevent.Data.GetData(typeof(TabPage)) as TabPage;
			if (dragged == null)
			{
				draggedTab = null;
				return;
			}

			Point clientPoint = PointToClient(new Point(drgevent.X, drgevent.Y));
			TabPage target = GetTabPageAtPoint(clientPoint);
			if (target == null)
			{
				draggedTab = null;
				return;
			}

			int draggedIndex = TabPages.IndexOf(dragged);
			int targetIndex = TabPages.IndexOf(target);

			if (draggedIndex == targetIndex || targetIndex == 0)
			{
				draggedTab = null;
				return;
			}

			TabPages.Remove(dragged);
			TabPages.Insert(targetIndex, dragged);
			SelectedTab = dragged;

			TabsReordered?.Invoke(this, EventArgs.Empty);
			draggedTab = null;
		}

		private TabPage GetTabPageAtPoint(Point pt)
		{
			for (int i = 0; i < TabCount; i++)
			{
				if (GetTabRect(i).Contains(pt))
					return TabPages[i];
			}
			return null;
		}
	}
}
