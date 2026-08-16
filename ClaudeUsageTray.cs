// ClaudeUsageTray - Claude の使用量をデスクトップに常駐表示するウィジェット (Windows)
// Claude Code のログイン情報（~/.claude/.credentials.json）を読み、
// 使用量エンドポイントから 5時間枠・週間枠の残量を取得してトレイとウィジェットに表示する。
//
// 注意: 本ツールは非公式です。利用している使用量 API は公開ドキュメントに記載が無く、
//       予告なく変更・停止される可能性があります。
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace ClaudeUsageTray
{
    static class Program
    {
        public static string AppDir
        {
            get { return Path.GetDirectoryName(Application.ExecutablePath); }
        }

        // 環境変数 CLAUDEUSAGETRAY_DEBUG=1 のときだけ poll-log.txt に通信ログを残す
        public static bool DebugLog
        {
            get { return Environment.GetEnvironmentVariable("CLAUDEUSAGETRAY_DEBUG") == "1"; }
        }

        [STAThread]
        static void Main()
        {
            bool created;
            using (new Mutex(true, "Global\\ClaudeUsageTrayMutex", out created))
            {
                if (!created) return;
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                // API は TLS1.2 必須
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
                try
                {
                    Application.Run(new TrayContext());
                }
                catch (Exception ex)
                {
                    Log(ex.ToString());
                    throw;
                }
            }
        }

        public static void Log(string msg)
        {
            try
            {
                File.AppendAllText(Path.Combine(AppDir, "tray-error.log"),
                    "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "] " + msg + "\r\n");
            }
            catch { }
        }
    }

    // ウィジェットに表示する1行分（5時間枠 / 週間枠 / モデル別枠）
    class UsageRow
    {
        public string Label;
        public int Remain;    // 残量 %
        public string Reset;  // リセット時刻の表示用文字列
    }

    static class UiUtil
    {
        // 残量に応じた色。50%以上=緑、20%以上=橙、それ未満=赤
        public static Color RemainColor(int remain)
        {
            if (remain >= 50) return Color.FromArgb(34, 197, 94);
            if (remain >= 20) return Color.FromArgb(245, 158, 11);
            return Color.FromArgb(239, 68, 68);
        }
    }

    class WidgetForm : Form
    {
        [DllImport("user32.dll")] static extern bool ReleaseCapture();
        [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wp, IntPtr lp);

        const int RowH = 27;
        const int PadTop = 8;
        const int WidgetW = 240;

        public bool IsError;
        public string ErrorMessage = "";
        public bool IsStale;          // 取得失敗中だが前回値を表示している状態
        public string StaleSince = "";
        public bool AlwaysOnTop = true;
        public List<UsageRow> Rows = new List<UsageRow>();

        readonly string posPath = Path.Combine(Program.AppDir, "widget-position.json");

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= 0x80 | 0x8000000;  // WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE
                return cp;
            }
        }

        public WidgetForm()
        {
            Text = "Claude Usage";
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            ClientSize = new Size(WidgetW, 62);
            BackColor = Color.FromArgb(28, 28, 34);
            Opacity = 0.92;
            DoubleBuffered = true;

            LoadPosition();
            TopMost = AlwaysOnTop;

            // 余白のドラッグで移動
            MouseDown += delegate(object s, MouseEventArgs e)
            {
                if (e.Button != MouseButtons.Left) return;
                ReleaseCapture();
                SendMessage(Handle, 0xA1 /*WM_NCLBUTTONDOWN*/, (IntPtr)2 /*HTCAPTION*/, IntPtr.Zero);
            };
            ResizeEnd += delegate { SavePosition(); };
        }

        void LoadPosition()
        {
            var wa = Screen.PrimaryScreen.WorkingArea;
            var loc = new Point(wa.Right - Width, wa.Bottom - Height);
            try
            {
                if (File.Exists(posPath))
                {
                    var ser = new JavaScriptSerializer();
                    var d = (Dictionary<string, object>)ser.DeserializeObject(File.ReadAllText(posPath));
                    var saved = new Point(Convert.ToInt32(d["X"]), Convert.ToInt32(d["Y"]));
                    // 画面構成が変わって画面外になっていた場合は既定位置に戻す
                    foreach (var sc in Screen.AllScreens)
                    {
                        if (sc.WorkingArea.Contains(saved)) { loc = saved; break; }
                    }
                    if (d.ContainsKey("TopMost")) AlwaysOnTop = Convert.ToBoolean(d["TopMost"]);
                }
            }
            catch { }
            Location = loc;
        }

        public void SavePosition()
        {
            try
            {
                File.WriteAllText(posPath, string.Format(
                    "{{\"X\":{0},\"Y\":{1},\"TopMost\":{2}}}",
                    Location.X, Location.Y, AlwaysOnTop ? "true" : "false"));
            }
            catch { }
        }

        // 行数に合わせて高さを変える。下端を固定したいので上に伸ばす
        public void FitToRowCount(int count)
        {
            int h = PadTop + RowH * Math.Max(1, count);
            if (ClientSize.Height == h) return;
            int diff = h - ClientSize.Height;
            ClientSize = new Size(WidgetW, h);
            Top -= diff;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

            using (var font = new Font("Segoe UI", 9f, FontStyle.Bold))
            using (var fontSmall = new Font("Segoe UI", 7.5f))
            using (var fg = new SolidBrush(Color.FromArgb(235, 235, 240)))
            using (var dim = new SolidBrush(Color.FromArgb(150, 150, 158)))
            using (var track = new SolidBrush(Color.FromArgb(60, 60, 70)))
            using (var border = new Pen(Color.FromArgb(70, 70, 82)))
            {
                g.DrawRectangle(border, 0, 0, ClientSize.Width - 1, ClientSize.Height - 1);

                if (IsError)
                {
                    g.DrawString("取得エラー", font, dim, 12f, 10f);
                    g.DrawString(ErrorMessage, fontSmall, dim,
                        new RectangleF(12f, 30f, ClientSize.Width - 24, ClientSize.Height - 34));
                    return;
                }

                // 前回値を表示中は右上に警告マークと最終取得時刻を出す
                if (IsStale)
                {
                    using (var warn = new SolidBrush(Color.FromArgb(245, 158, 11)))
                    {
                        g.DrawString("!", font, warn, ClientSize.Width - 16, 1f);
                        g.DrawString(StaleSince, fontSmall, warn, ClientSize.Width - 52, 3f);
                    }
                }

                int y = PadTop;
                foreach (var row in Rows)
                {
                    g.DrawString(row.Label, font, fg, 8f, y);
                    g.FillRectangle(track, 52, y + 3, 80, 11);
                    int w = Math.Max(1, (int)Math.Round(80.0 * row.Remain / 100.0));
                    using (var bar = new SolidBrush(UiUtil.RemainColor(row.Remain)))
                        g.FillRectangle(bar, 52, y + 3, w, 11);
                    g.DrawString("残" + row.Remain + "%", font, fg, 136f, y);
                    g.DrawString(row.Reset, fontSmall, dim, 182f, y + 2);
                    y += RowH;
                }
            }
        }
    }

    class TrayContext : ApplicationContext
    {
        [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr handle);

        const int PollMs = 120000;      // 通常のポーリング間隔（2分）
        const int MaxBackoffMs = 300000; // エラー時の最大間隔（5分）
        const string UsageUrl = "https://api.anthropic.com/api/oauth/usage";

        readonly NotifyIcon notify;
        readonly WidgetForm widget;
        readonly System.Windows.Forms.Timer timer;
        readonly ToolTip widgetTip = new ToolTip();
        readonly string credPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude\\.credentials.json");
        readonly string pollLogPath = Path.Combine(Program.AppDir, "poll-log.txt");

        string lastDetail = "未取得";
        string lastGoodDetail = "";
        string lastRawBody = "";
        DateTime lastSuccess = DateTime.MinValue;
        IntPtr prevHicon = IntPtr.Zero;

        public TrayContext()
        {
            widget = new WidgetForm();

            var menu = new ContextMenuStrip();
            menu.Items.Add("ウィジェットを表示/隠す", null, delegate
            {
                if (widget.Visible) widget.Hide(); else widget.Show();
            });

            var miTopMost = new ToolStripMenuItem("常に前面に表示");
            miTopMost.Checked = widget.AlwaysOnTop;
            miTopMost.CheckOnClick = true;
            miTopMost.CheckedChanged += delegate
            {
                widget.AlwaysOnTop = miTopMost.Checked;
                widget.TopMost = miTopMost.Checked;
                widget.SavePosition();
            };
            menu.Items.Add(miTopMost);

            menu.Items.Add("詳細を表示", null, delegate
            {
                notify.ShowBalloonTip(8000, "Claude 使用状況", lastDetail, ToolTipIcon.Info);
            });
            menu.Items.Add("今すぐ更新", null, delegate { UpdateStatus(); });
            menu.Items.Add("使用状況ページを開く", null, delegate
            {
                try { Process.Start("https://claude.ai/settings/usage"); } catch { }
            });
            menu.Items.Add("ログイン (Claude Code を起動)", null, delegate
            {
                try { Process.Start("cmd.exe", "/k claude"); } catch { }
            });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("終了", null, delegate
            {
                widget.SavePosition();
                notify.Visible = false;
                notify.Dispose();
                ExitThread();
            });

            notify = new NotifyIcon();
            notify.Visible = true;
            notify.ContextMenuStrip = menu;
            notify.MouseClick += delegate(object s, MouseEventArgs e)
            {
                if (e.Button != MouseButtons.Left) return;
                UpdateStatus();
                notify.ShowBalloonTip(8000, "Claude 使用状況", lastDetail, ToolTipIcon.Info);
            };

            widget.ContextMenuStrip = menu;
            widget.MouseDoubleClick += delegate { UpdateStatus(); };

            timer = new System.Windows.Forms.Timer();
            timer.Interval = PollMs;
            timer.Tick += delegate { UpdateStatus(); };

            UpdateStatus();
            widget.Show();
            timer.Start();
        }

        void PollLog(string line)
        {
            if (!Program.DebugLog) return;
            try
            {
                var fi = new FileInfo(pollLogPath);
                if (fi.Exists && fi.Length > 204800) fi.Delete();  // 200KB を超えたら作り直す
                File.AppendAllText(pollLogPath,
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + line + "\r\n");
            }
            catch { }
        }

        // ISO8601 を「今日ならHH:mm、それ以外はM/d HH:mm」に整形
        static string FormatReset(string iso)
        {
            if (string.IsNullOrEmpty(iso)) return "—";
            try
            {
                var t = DateTimeOffset.Parse(iso).ToLocalTime();
                return t.Date == DateTime.Today ? t.ToString("HH:mm") : t.ToString("M/d HH:mm");
            }
            catch { return "—"; }
        }

        static string GetStr(Dictionary<string, object> d, string key)
        {
            object v;
            return (d != null && d.TryGetValue(key, out v)) ? v as string : null;
        }

        // Claude Code のログイン情報を使って使用量を取得する
        Dictionary<string, object> FetchUsage()
        {
            var ser = new JavaScriptSerializer();
            var cred = (Dictionary<string, object>)ser.DeserializeObject(File.ReadAllText(credPath));
            if (!cred.ContainsKey("claudeAiOauth"))
                throw new Exception("credentials.json に claudeAiOauth がありません");
            var oauth = (Dictionary<string, object>)cred["claudeAiOauth"];
            var token = oauth["accessToken"] as string;
            if (string.IsNullOrEmpty(token))
                throw new Exception("credentials.json に accessToken がありません");

            var req = (HttpWebRequest)WebRequest.Create(UsageUrl);
            req.Method = "GET";
            req.Timeout = 15000;
            req.UserAgent = "ClaudeUsageTray/1.0";
            req.Headers["Authorization"] = "Bearer " + token;
            req.Headers["anthropic-beta"] = "oauth-2025-04-20";
            using (var res = (HttpWebResponse)req.GetResponse())
            using (var r = new StreamReader(res.GetResponseStream()))
            {
                lastRawBody = r.ReadToEnd();
                return (Dictionary<string, object>)ser.DeserializeObject(lastRawBody);
            }
        }

        void UpdateStatus()
        {
            try
            {
                var json = FetchUsage();

                // 5時間枠
                var five = (Dictionary<string, object>)json["five_hour"];
                int fiveRemain = Math.Max(0, 100 - (int)Math.Round(Convert.ToDouble(five["utilization"])));
                string fiveReset = FormatReset(GetStr(five, "resets_at"));

                // 週間枠は limits 配列に入る。scope.model があればモデル別、無ければ全体
                var overall = new List<Dictionary<string, object>>();
                var perModel = new List<Dictionary<string, object>>();
                var limits = json.ContainsKey("limits") ? json["limits"] as object[] : null;
                if (limits != null)
                {
                    foreach (var o in limits)
                    {
                        var lim = o as Dictionary<string, object>;
                        if (lim == null || (string)lim["group"] != "weekly") continue;
                        var scope = lim.ContainsKey("scope") ? lim["scope"] as Dictionary<string, object> : null;
                        var model = (scope != null && scope.ContainsKey("model"))
                            ? scope["model"] as Dictionary<string, object> : null;
                        if (model != null) perModel.Add(lim); else overall.Add(lim);
                    }
                }

                var rows = new List<UsageRow>();
                rows.Add(new UsageRow { Label = "5h", Remain = fiveRemain, Reset = fiveReset });

                int weekWorst = 100;
                if (overall.Count > 0 || perModel.Count > 0)
                {
                    foreach (var lim in overall)
                    {
                        int remain = Math.Max(0, 100 - Convert.ToInt32(lim["percent"]));
                        rows.Add(new UsageRow { Label = "週", Remain = remain, Reset = FormatReset(GetStr(lim, "resets_at")) });
                        weekWorst = Math.Min(weekWorst, remain);
                    }
                    foreach (var lim in perModel)
                    {
                        var scope = (Dictionary<string, object>)lim["scope"];
                        var model = (Dictionary<string, object>)scope["model"];
                        string label = GetStr(model, "display_name") ?? "モデル別";
                        int remain = Math.Max(0, 100 - Convert.ToInt32(lim["percent"]));
                        rows.Add(new UsageRow { Label = label, Remain = remain, Reset = FormatReset(GetStr(lim, "resets_at")) });
                        weekWorst = Math.Min(weekWorst, remain);
                    }
                }
                else
                {
                    // limits が無い旧レスポンス向けのフォールバック
                    var seven = (Dictionary<string, object>)json["seven_day"];
                    int remain = Math.Max(0, 100 - (int)Math.Round(Convert.ToDouble(seven["utilization"])));
                    rows.Add(new UsageRow { Label = "週", Remain = remain, Reset = FormatReset(GetStr(seven, "resets_at")) });
                    weekWorst = remain;
                }

                widget.IsError = false;
                widget.IsStale = false;
                widget.Rows = rows;
                widget.FitToRowCount(rows.Count);
                PollLog("OK 5h=" + fiveRemain + "% weekWorst=" + weekWorst + "%");
                SetTrayIcon(fiveRemain, weekWorst, false);

                SetTrayText("Claude 5h: 残" + fiveRemain + "% (" + fiveReset + ")\n週: 残" + weekWorst + "%");

                var lines = new List<string>();
                foreach (var row in rows)
                {
                    string name = row.Label == "5h" ? "5時間枠"
                        : row.Label == "週" ? "週間 (全体)"
                        : "週間 [" + row.Label + "]";
                    lines.Add(name + " : 残り " + row.Remain + "% (リセット " + row.Reset + ")");
                }
                lines.Add("更新: " + DateTime.Now.ToString("HH:mm:ss"));
                lastGoodDetail = string.Join("\n", lines.ToArray());
                lastDetail = lastGoodDetail;
                lastSuccess = DateTime.Now;
                timer.Interval = PollMs;
            }
            catch (Exception ex)
            {
                int status = 0;
                var web = ex as WebException;
                if (web != null && web.Response is HttpWebResponse)
                    status = (int)((HttpWebResponse)web.Response).StatusCode;

                string msg = status == 429
                    ? "APIレート制限中。間隔を空けて自動再試行します"
                    : (status == 401 || status == 403)
                        ? "認証エラー。右クリック →「ログイン (Claude Code を起動)」で再ログインしてください"
                        : "取得エラー: " + ex.Message;

                // 失敗中は間隔を倍にして API を叩きすぎないようにする
                timer.Interval = Math.Min(timer.Interval * 2, MaxBackoffMs);
                PollLog("ERR status=" + status + " " + ex.Message.Replace("\r", " ").Replace("\n", " "));
                if (status == 0 && web == null && lastRawBody.Length > 0)
                    PollLog("BODY " + (lastRawBody.Length > 2000 ? lastRawBody.Substring(0, 2000) + "..." : lastRawBody));

                if (widget.Rows.Count > 0)
                {
                    // 一度でも取得できていれば前回値を残して警告表示にとどめる
                    widget.IsStale = true;
                    widget.StaleSince = lastSuccess.ToString("HH:mm");
                    lastDetail = "[" + msg + "]\n最終取得 " + lastSuccess.ToString("HH:mm:ss") + "\n" + lastGoodDetail;
                    SetTrayText("Claude: " + msg);
                }
                else
                {
                    widget.IsError = true;
                    widget.ErrorMessage = msg;
                    SetTrayIcon(0, 0, true);
                    notify.Text = "Claude 使用量: 取得エラー";
                    lastDetail = msg;
                }
            }

            widget.Invalidate();
            widgetTip.SetToolTip(widget, lastDetail);
            if (widget.Visible && widget.AlwaysOnTop) widget.TopMost = true;
        }

        // NotifyIcon.Text は 63 文字までしか設定できない
        void SetTrayText(string text)
        {
            notify.Text = text.Length > 63 ? text.Substring(0, 63) : text;
        }

        // 5時間枠の残量を数字で、週間枠の残量を下部のバーで描いたアイコンを作る
        void SetTrayIcon(int fiveRemain, int weekRemain, bool isError)
        {
            using (var bmp = new Bitmap(32, 32))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                    g.Clear(Color.Transparent);
                    using (var center = new StringFormat())
                    {
                        center.Alignment = StringAlignment.Center;
                        center.LineAlignment = StringAlignment.Center;

                        if (isError)
                        {
                            using (var f = new Font("Segoe UI", 18f, FontStyle.Bold, GraphicsUnit.Pixel))
                            using (var b = new SolidBrush(Color.Gray))
                                g.DrawString("!", f, b, new RectangleF(0, 0, 32, 32), center);
                        }
                        else
                        {
                            string s = fiveRemain.ToString();
                            using (var f = new Font("Segoe UI", s.Length >= 3 ? 13 : 19, FontStyle.Bold, GraphicsUnit.Pixel))
                            using (var b = new SolidBrush(UiUtil.RemainColor(fiveRemain)))
                                g.DrawString(s, f, b, new RectangleF(0, -2, 32, 26), center);

                            using (var track = new SolidBrush(Color.FromArgb(80, 128, 128, 128)))
                                g.FillRectangle(track, 1, 25, 30, 6);
                            int w = Math.Max(1, (int)Math.Round(30.0 * weekRemain / 100.0));
                            using (var bar = new SolidBrush(UiUtil.RemainColor(weekRemain)))
                                g.FillRectangle(bar, 1, 25, w, 6);
                        }
                    }
                }
                IntPtr h = bmp.GetHicon();
                notify.Icon = Icon.FromHandle(h);
                if (prevHicon != IntPtr.Zero) DestroyIcon(prevHicon);
                prevHicon = h;
            }
        }
    }
}
