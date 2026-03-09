using System;
using System.Drawing;
using System.Windows.Forms;

namespace PingTracer
{
	/// <summary>
	/// TabControl subclass with drag-to-reorder support. The Overview tab at index 0
	/// cannot be dragged or become a drag target.
	/// </summary>
	public class DraggableTabControl : TabControl
	{
		/// <summary>
		/// Fires after a drag-drop reorder of tabs completes.
		/// </summary>
		public event EventHandler TabsReordered;

		private TabPage draggedTab;
		private Point dragStartPoint;

		public DraggableTabControl()
		{
			AllowDrop = true;
			SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);

			MouseDown += HandleTabMouseDown;
			MouseMove += HandleTabMouseMove;
			DragOver += HandleDragOver;
			DragDrop += HandleDragDrop;
		}

		private void HandleTabMouseDown(object sender, MouseEventArgs e)
		{
			for (int i = 0; i < TabCount; i++)
			{
				Rectangle tabRect = GetTabRect(i);
				if (!tabRect.Contains(e.Location))
					continue;

				if (i == 0)
					return; // Overview tab: not draggable

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
