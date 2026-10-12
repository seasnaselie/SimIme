using System;
using System.Drawing;
using System.Windows.Forms;

namespace SimIme.App
{
    // 微软拼音候选窗外观：白底 / 1px 浅灰边框 / 两行（拼音行 + 候选行）/ 绝不空白
    // 仍然永不抢焦点：WS_EX_NOACTIVATE|WS_EX_TOOLWINDOW|WS_EX_TOPMOST + ShowWithoutActivation
    internal sealed class FloatingBar : Form
    {
        internal static readonly Color BackgroundColor = Color.FromArgb(0xFF, 0xFF, 0xFF);
        internal static readonly Color BorderColor = Color.FromArgb(0xC8, 0xC8, 0xC8);
        internal static readonly Color TextColor = Color.FromArgb(0x00, 0x00, 0x00);
        internal static readonly Color MutedColor = Color.FromArgb(0x80, 0x80, 0x80);
        internal static readonly Color ComposingColor = Color.FromArgb(0x1A, 0x6B, 0xC8);
        internal static readonly Color HighlightColor = Color.FromArgb(0xCC, 0xE4, 0xFF);

        private readonly BehaviorConfig _behavior;
        private readonly Font _font;
        private readonly Font _fontBold;
        private BarStatus _status = new BarStatus();

        internal FloatingBar(BehaviorConfig behavior)
        {
            _behavior = behavior;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            BackColor = BackgroundColor;
            ForeColor = TextColor;
            Text = "SimIme.FloatingBar";     // 标题仅用于 --bartest 截图定位（无边框，不显示）
            DoubleBuffered = true;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            _font = new Font("Microsoft YaHei UI", 9f, FontStyle.Regular, GraphicsUnit.Point);
            _fontBold = new Font("Microsoft YaHei UI", 9f, FontStyle.Bold, GraphicsUnit.Point);
            ClientSize = new Size(220, 48);
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW | Native.WS_EX_TOPMOST;
                return cp;
            }
        }

        protected override bool ShowWithoutActivation
        {
            get { return true; }
        }

        internal void EnsureHandle()
        {
            IntPtr h = Handle;
            if (h != IntPtr.Zero) Native.ForceToolWindowStyle(h);
        }

        // 只用 SetWindowPos(SWP_NOACTIVATE) 显示：绝不 SetForegroundWindow，绝不 Activate
        internal void ShowAt(Point anchor, BarStatus status)
        {
            try
            {
                _status = status == null ? new BarStatus() : status;
                LayoutForContent();
                Point target = ComputeLocation(anchor);
                Native.SetWindowPos(Handle, Native.HWND_TOPMOST, target.X, target.Y, Width, Height,
                    Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
                Invalidate();
            }
            catch (Exception ex)
            {
                Log.Error("显示悬浮条失败", ex);
            }
        }

        internal void HideBar()
        {
            try
            {
                if (IsHandleCreated) Native.ShowWindow(Handle, Native.SW_HIDE);
            }
            catch (Exception ex)
            {
                Log.Error("隐藏悬浮条失败", ex);
            }
        }

        internal string DescribeLook()
        {
            return string.Format("尺寸={0}x{1} 底色=#{2:X2}{3:X2}{4:X2} 边框=#{5:X2}{6:X2}{7:X2} 字体={8} {9}pt 行数=2",
                Width, Height,
                BackgroundColor.R, BackgroundColor.G, BackgroundColor.B,
                BorderColor.R, BorderColor.G, BorderColor.B,
                _font.Name, _font.SizeInPoints);
        }

        private float Scale()
        {
            try
            {
                using (Graphics g = CreateGraphics())
                {
                    float s = g.DpiY / 96f;
                    if (s < 1f || s > 4f) return 1f;
                    return s;
                }
            }
            catch
            {
                return 1f;
            }
        }

        // 紧凑两行：高 = 上下内边距 + 2 行；宽随内容 200~420
        private void LayoutForContent()
        {
            try
            {
                float scale = Scale();
                int pad = (int)Math.Round(6 * scale);
                int lineH = (int)Math.Round(18 * scale);
                int minW = (int)Math.Round(200 * scale);
                int maxW = (int)Math.Round(560 * scale);   // 词组候选一行可能 6~9 个，宽度上限放宽

                int width;
                using (Graphics g = CreateGraphics())
                {
                    string line1 = (_status.Trail ?? "") + (_status.Composing ?? "") + (_status.Suffix ?? "");
                    int w1 = (int)Math.Ceiling(g.MeasureString(line1, _fontBold).Width);
                    // 候选行可能整行加粗（选词高亮），按最宽的粗体量，并留一点余量，避免末位候选被裁
                    int w2 = (int)Math.Ceiling(g.MeasureString(_status.Candidates ?? "", _fontBold).Width) + 10;
                    int w3 = (int)Math.Ceiling(g.MeasureString(_status.Progress ?? "", _font).Width);
                    int w4 = (int)Math.Ceiling(g.MeasureString(_status.Foot ?? "", _font).Width);
                    width = Math.Max(Math.Max(w1, w2), Math.Max(w3, w4)) + pad * 2 + 6;
                }
                if (width < minW) width = minW;
                if (width > maxW) width = maxW;
                ClientSize = new Size(width, pad * 2 + lineH * 2);
            }
            catch (Exception ex)
            {
                Log.Error("计算悬浮条尺寸失败", ex);
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            try
            {
                PaintCore(e.Graphics);
            }
            catch (Exception ex)
            {
                Log.Error("绘制悬浮条失败", ex);
                try { e.Graphics.Clear(BackgroundColor); } catch { }
            }
        }

        private void PaintCore(Graphics g)
        {
            g.Clear(BackgroundColor);
            using (Pen border = new Pen(BorderColor))
            {
                g.DrawRectangle(border, 0, 0, Width - 1, Height - 1);
            }

            float scale = Scale();
            int pad = (int)Math.Round(6 * scale);
            int lineH = (int)Math.Round(18 * scale);
            int y1 = pad;
            int y2 = pad + lineH;

            string trail = _status.Trail ?? "";
            string composing = _status.Composing ?? "";
            string candidates = _status.Candidates ?? "";
            string progress = _status.Progress ?? "";
            string foot = _status.Foot ?? "";

            // 绝不空白：四段全空时也要给一句兜底
            if (trail.Length == 0 && composing.Length == 0 && candidates.Length == 0 && progress.Length == 0 && foot.Length == 0)
            {
                composing = "等待…";
            }

            // ---- 上行：灰色拼音前缀 + 黑体当前音节（下划线） ----
            float x = pad;
            if (trail.Length > 0)
            {
                using (SolidBrush b = new SolidBrush(MutedColor)) g.DrawString(trail, _font, b, x, y1);
                x += g.MeasureString(trail, _font).Width;
            }
            if (composing.Length > 0)
            {
                SizeF sz = g.MeasureString(composing, _fontBold);
                using (SolidBrush b = new SolidBrush(ComposingColor)) g.DrawString(composing, _fontBold, b, x, y1);
                using (Pen underline = new Pen(ComposingColor))
                {
                    float uy = y1 + sz.Height - 3 * scale;
                    g.DrawLine(underline, x, uy, x + sz.Width, uy);
                }
                x += sz.Width;
            }
            string suffix = _status.Suffix ?? "";
            if (suffix.Length > 0)
            {
                using (SolidBrush b = new SolidBrush(MutedColor)) g.DrawString(suffix, _font, b, x, y1);
                x += g.MeasureString(suffix, _font).Width;
            }
            if (progress.Length > 0)
            {
                SizeF psz = g.MeasureString(progress, _font);
                using (SolidBrush b = new SolidBrush(MutedColor))
                    g.DrawString(progress, _font, b, Width - pad - psz.Width, y1);
            }

            // ---- 下行：候选（当前字高亮浅蓝底）；没有候选就显示状态说明，保证有内容 ----
            if (candidates.Length > 0)
            {
                DrawCandidates(g, candidates, pad, y2, scale);
            }
            else if (foot.Length > 0)
            {
                using (SolidBrush b = new SolidBrush(MutedColor)) g.DrawString(foot, _font, b, pad, y2);
            }

            if (candidates.Length > 0 && foot.Length > 0)
            {
                SizeF fsz = g.MeasureString(foot, _font);
                using (SolidBrush b = new SolidBrush(MutedColor))
                    g.DrawString(foot, _font, b, Width - pad - fsz.Width, y2);
            }
        }

        private void DrawCandidates(Graphics g, string candidates, int pad, int y, float scale)
        {
            string highlight = _status.Highlight ?? "";
            string selected = _status.SelectedCandidate ?? "";   // 正在"选词"的那一个：浅蓝底 + 加粗
            string[] parts = candidates.Split(' ');
            float x = pad;
            using (SolidBrush normal = new SolidBrush(TextColor))
            using (SolidBrush marked = new SolidBrush(TextColor))
            using (SolidBrush back = new SolidBrush(HighlightColor))
            {
                for (int i = 0; i < parts.Length; i++)
                {
                    string token = parts[i];
                    if (token.Length == 0) continue;
                    bool isSelected = selected.Length > 0 && token.EndsWith(selected, StringComparison.Ordinal);
                    bool hit = isSelected || (highlight.Length > 0 && token.IndexOf(highlight, StringComparison.Ordinal) >= 0);
                    Font font = hit ? _fontBold : _font;
                    SizeF sz = g.MeasureString(token, font);
                    if (hit)
                    {
                        float padX = (isSelected ? 3f : 2f) * scale;
                        g.FillRectangle(back, x - padX, y - 1 * scale, sz.Width + padX * 2, sz.Height + 1 * scale);
                        g.DrawString(token, font, marked, x, y);
                    }
                    else
                    {
                        g.DrawString(token, font, normal, x, y);
                    }
                    x += sz.Width + 6 * scale;
                }
            }
        }

        private Point ComputeLocation(Point anchor)
        {
            Rectangle screen = Screen.FromPoint(anchor).WorkingArea;
            int x = anchor.X + _behavior.BarOffsetX;
            int y = anchor.Y + _behavior.BarOffsetY;
            if (y + Height > screen.Bottom) y = anchor.Y - Height - 4;
            if (y < screen.Top) y = screen.Top + 4;
            if (x + Width > screen.Right) x = screen.Right - Width - 4;
            if (x < screen.Left) x = screen.Left + 4;
            return new Point(x, y);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
        }
    }
}
