using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace YamaEditor.Controls
{
    /// <summary>
    /// WinUI 3 网格分割条（GridSplitter）。
    /// WinUI 3 未内置 GridSplitter（且 Border 为 sealed，无法直接派生），
    /// 此控件用一个内部 Border 作为可视条，并提供等价的拖拽调整行为：
    ///   - 横置（ActualWidth &gt; ActualHeight）：上下拖动调整相邻行高。
    ///   - 竖置（ActualHeight &gt; ActualWidth）：左右拖动调整相邻列宽。
    /// 拖拽会把相邻的行/列定义转为绝对像素长度（首次拖动后固定，符合常见编辑器行为）。
    /// </summary>
    public sealed class GridSplitter : ContentControl
    {
        private readonly Border _bar;
        private bool _isDragging;
        private Windows.Foundation.Point _dragStart;

        public GridSplitter()
        {
            _bar = new Border();
            _bar.HorizontalAlignment = HorizontalAlignment.Stretch;
            _bar.VerticalAlignment = VerticalAlignment.Stretch;
            HorizontalContentAlignment = HorizontalAlignment.Stretch;
            VerticalContentAlignment = VerticalAlignment.Stretch;
            Content = _bar;

            // 命中区域不小于 3px，便于抓取
            MinWidth = 3;
            MinHeight = 3;
            PointerPressed += OnPointerPressed;
            PointerMoved += OnPointerMoved;
            PointerReleased += OnPointerReleased;
            PointerCaptureLost += OnPointerCaptureLost;
            PointerCanceled += OnPointerCanceled;
        }

        /// <summary>分割条背景（转发给内部 Border）。</summary>
        public Brush? Background
        {
            get => _bar.Background;
            set => _bar.Background = value;
        }

        /// <summary>横向分割条：调节行高。</summary>
        private bool IsHorizontal => ActualHeight < ActualWidth;

        private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
        {
            if (Parent is not Grid grid) return;
            _dragStart = e.GetCurrentPoint(grid).Position;
            _isDragging = true;
            CapturePointer(e.Pointer);
            e.Handled = true;
        }

        private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (!_isDragging || Parent is not Grid grid) return;
            double delta = IsHorizontal
                ? e.GetCurrentPoint(grid).Position.Y - _dragStart.Y
                : e.GetCurrentPoint(grid).Position.X - _dragStart.X;
            ApplyDelta(grid, delta);
            e.Handled = true;
        }

        private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
        {
            _isDragging = false;
            ReleasePointerCapture(e.Pointer);
        }

        private void OnPointerCanceled(object sender, PointerRoutedEventArgs e)
        {
            _isDragging = false;
            ReleasePointerCapture(e.Pointer);
        }

        private void OnPointerCaptureLost(object sender, PointerRoutedEventArgs e)
        {
            _isDragging = false;
        }

        private void ApplyDelta(Grid grid, double delta)
        {
            if (IsHorizontal)
            {
                int row = Grid.GetRow(this);
                if (row < 0 || row >= grid.RowDefinitions.Count) return;
                ResizeRow(grid, row - 1, delta);
                ResizeRow(grid, row + 1, -delta);
            }
            else
            {
                int col = Grid.GetColumn(this);
                if (col < 0 || col >= grid.ColumnDefinitions.Count) return;
                ResizeColumn(grid, col - 1, delta);
                ResizeColumn(grid, col + 1, -delta);
            }
        }

        private static void ResizeRow(Grid grid, int index, double delta)
        {
            if (index < 0 || index >= grid.RowDefinitions.Count) return;
            var def = grid.RowDefinitions[index];
            double value = Math.Max(def.MinHeight, def.ActualHeight + delta);
            def.Height = new GridLength(value);
        }

        private static void ResizeColumn(Grid grid, int index, double delta)
        {
            if (index < 0 || index >= grid.ColumnDefinitions.Count) return;
            var def = grid.ColumnDefinitions[index];
            double value = Math.Max(def.MinWidth, def.ActualWidth + delta);
            def.Width = new GridLength(value);
        }
    }
}
