// FlipPix uninstaller - a tiny, self-contained WinForms app (deliberately retro
// Windows 98 look to match Install-FlipPix). Compiled against the .NET Framework
// (csc.exe) so the resulting Uninstall-FlipPix.exe is a few KB and runs on any
// Windows without installing a runtime. See scripts\build-uninstaller.ps1.
//
// What it removes (the "traces" left by Install-FlipPix.bat / the PS wizard):
//   * the install folder (default %LOCALAPPDATA%\Programs\FlipPix, or wherever
//     this exe lives if it was installed alongside FlipPix.UI.exe)
//   * the Desktop shortcut  <Desktop>\FlipPix.lnk
//   * the Start Menu group  <Programs>\FlipPix\
//   * optionally  %AppData%\FlipPix  (settings.json, logs, queue, prompts, cache)
//   * optionally  the generated media folders Pictures\flippix-images and
//     Videos\flippix-vids (OFF by default - that's the user's own output)
//
// Because the exe usually sits *inside* the folder it must delete, it can't remove
// that folder while running. So it does everything else first, then copies itself
// to %TEMP% and relaunches with --finalize "<dir>"; the temp copy deletes the
// install folder and then deletes itself.

using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace FlipPixUninstaller
{
    internal static class Program
    {
        // --- Win98 palette + fonts ------------------------------------------
        static readonly Color ClSilver = Color.FromArgb(192, 192, 192);
        static readonly Color ClNavy1 = Color.FromArgb(0, 0, 128);
        static readonly Color ClNavy2 = Color.FromArgb(0, 0, 40);
        static readonly Font Fnt = new Font("MS Sans Serif", 8.25f);
        static readonly Font FntBold = new Font("MS Sans Serif", 8.25f, FontStyle.Bold);
        static readonly Font FntTitle = new Font("MS Sans Serif", 14f, FontStyle.Bold);

        static string _installDir;
        static Icon _icon;
        static Bitmap _bannerBmp;

        [STAThread]
        static int Main(string[] args)
        {
            // --finalize "<dir>": relocated temp copy whose only job is to delete
            // the now-unlocked install folder and then remove itself.
            for (int i = 0; i < args.Length; i++)
            {
                if (string.Equals(args[i], "--finalize", StringComparison.OrdinalIgnoreCase)
                    && i + 1 < args.Length)
                {
                    return Finalize(args[i + 1]);
                }
            }

            // NOTE: do NOT EnableVisualStyles - keep the classic Win9x 3-D look.
            Application.SetCompatibleTextRenderingDefault(false);

            TryLoadIcon();
            _installDir = GuessInstallDir();

            using (var w = new Wizard())
            {
                Application.Run(w);
            }
            return 0;
        }

        // -------------------------------------------------------------------
        // discovery
        // -------------------------------------------------------------------
        static string GuessInstallDir()
        {
            // If we live next to FlipPix.UI.exe, that's the install folder.
            string here = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');
            if (File.Exists(Path.Combine(here, "FlipPix.UI.exe")))
                return here;

            // Otherwise fall back to the PS wizard's default location.
            string def = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Programs", "FlipPix");
            return def;
        }

        static void TryLoadIcon()
        {
            try
            {
                _icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
                if (_icon != null) _bannerBmp = new Icon(_icon, 48, 48).ToBitmap();
            }
            catch { }
        }

        // -------------------------------------------------------------------
        // the wizard form
        // -------------------------------------------------------------------
        private sealed class Wizard : Form
        {
            int _step;
            readonly Panel _pgWelcome, _pgOpts, _pgRun, _pgDone;
            readonly Button _btnBack, _btnNext, _btnCancel;
            TextBox _txtDir;
            CheckBox _chkAppData, _chkMedia;
            Label _lblStatus, _dBody;
            ProgressBar _pb;
            ListBox _lstLog;

            public Wizard()
            {
                Text = "FlipPix Uninstall";
                ClientSize = new Size(497, 360);
                FormBorderStyle = FormBorderStyle.FixedDialog;
                MaximizeBox = false;
                MinimizeBox = false;
                StartPosition = FormStartPosition.CenterScreen;
                BackColor = ClSilver;
                Font = Fnt;
                if (_icon != null) Icon = _icon;

                _pgWelcome = BuildWelcome();
                _pgOpts = BuildOptions();
                _pgRun = BuildRun();
                _pgDone = BuildDone();
                Controls.Add(_pgWelcome);
                Controls.Add(_pgOpts);
                Controls.Add(_pgRun);
                Controls.Add(_pgDone);

                var bar = new Panel { Location = new Point(0, 311), Size = new Size(497, 49) };
                bar.Paint += (s, e) => ControlPaint.DrawBorder3D(
                    e.Graphics, 0, 0, bar.Width, 2, Border3DStyle.Etched);
                _btnBack = new Button { Text = "< Back", Size = new Size(75, 23), Location = new Point(252, 13) };
                _btnNext = new Button { Text = "Next >", Size = new Size(75, 23), Location = new Point(327, 13) };
                _btnCancel = new Button { Text = "Cancel", Size = new Size(75, 23), Location = new Point(412, 13) };
                _btnBack.Click += (s, e) => { if (_step == 1) ShowStep(0); };
                _btnNext.Click += OnNext;
                _btnCancel.Click += (s, e) =>
                {
                    if (MessageBox.Show("Cancel FlipPix Uninstall?", "FlipPix Uninstall",
                        MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                        Close();
                };
                bar.Controls.Add(_btnBack);
                bar.Controls.Add(_btnNext);
                bar.Controls.Add(_btnCancel);
                Controls.Add(bar);

                ShowStep(0);
            }

            // --- pages ------------------------------------------------------
            Panel NewBanner()
            {
                var b = new Panel { Location = new Point(0, 0), Size = new Size(164, 311) };
                b.Paint += (s, e) =>
                {
                    var r = b.ClientRectangle;
                    using (var g = new LinearGradientBrush(r, ClNavy1, ClNavy2, 90f))
                        e.Graphics.FillRectangle(g, r);
                    if (_bannerBmp != null) e.Graphics.DrawImage(_bannerBmp, 22, 24, 48, 48);
                    e.Graphics.DrawString("FlipPix", FntTitle, Brushes.White, 18, 82);
                    using (var sub = new Font("MS Sans Serif", 8.25f))
                    {
                        e.Graphics.DrawString("AI image & video\r\nstudio", sub, Brushes.Gainsboro, 20, 112);
                        e.Graphics.DrawString("Uninstall", sub, Brushes.Gainsboro, 20, 280);
                    }
                };
                return b;
            }

            Panel NewHeader(string title, string desc)
            {
                var h = new Panel { Location = new Point(0, 0), Size = new Size(497, 59), BackColor = Color.White };
                h.Controls.Add(new Label
                {
                    Text = title, Font = FntBold, BackColor = Color.White,
                    Location = new Point(18, 10), AutoSize = true
                });
                h.Controls.Add(new Label
                {
                    Text = desc, BackColor = Color.White,
                    Location = new Point(32, 30), Size = new Size(440, 26)
                });
                if (_bannerBmp != null)
                {
                    h.Controls.Add(new PictureBox
                    {
                        Image = _bannerBmp, SizeMode = PictureBoxSizeMode.Zoom,
                        Location = new Point(437, 6), Size = new Size(48, 48), BackColor = Color.White
                    });
                }
                h.Paint += (s, e) => ControlPaint.DrawBorder3D(
                    e.Graphics, 0, h.Height - 2, h.Width, 2, Border3DStyle.Etched);
                return h;
            }

            static Label Lbl(string text, int x, int y, int w, int ht)
            {
                return new Label { Text = text, Location = new Point(x, y), Size = new Size(w, ht) };
            }

            Panel BuildWelcome()
            {
                var p = new Panel { Location = new Point(0, 0), Size = new Size(497, 311) };
                p.Controls.Add(NewBanner());
                var t = Lbl("Uninstall FlipPix", 180, 24, 300, 40);
                t.Font = FntBold;
                var body = Lbl(
                    "This will remove FlipPix from your computer.\r\n\r\n" +
                    "Setup will delete the FlipPix program files and its desktop and Start " +
                    "Menu shortcuts. On the next page you can also choose to remove your " +
                    "FlipPix settings and logs.\r\n\r\n" +
                    "Close FlipPix before continuing.\r\n\r\n" +
                    "Click Next to continue, or Cancel to exit.",
                    180, 70, 300, 230);
                p.Controls.Add(t);
                p.Controls.Add(body);
                return p;
            }

            Panel BuildOptions()
            {
                var p = new Panel { Location = new Point(0, 0), Size = new Size(497, 311) };
                p.Controls.Add(NewHeader("Choose what to remove", "Confirm the FlipPix folder and any extra cleanup."));

                p.Controls.Add(Lbl("FlipPix is installed in this folder:", 18, 76, 400, 16));
                _txtDir = new TextBox { Location = new Point(18, 94), Size = new Size(380, 20), Text = _installDir };
                var browse = new Button { Text = "Browse...", Location = new Point(404, 93), Size = new Size(75, 23) };
                browse.Click += (s, e) =>
                {
                    using (var dlg = new FolderBrowserDialog { Description = "Select the FlipPix install folder", SelectedPath = _txtDir.Text })
                        if (dlg.ShowDialog() == DialogResult.OK) _txtDir.Text = dlg.SelectedPath;
                };

                _chkAppData = new CheckBox
                {
                    Text = "Also remove my FlipPix settings and logs (%AppData%\\FlipPix)",
                    Checked = true, Location = new Point(18, 132), Size = new Size(460, 20)
                };
                _chkMedia = new CheckBox
                {
                    Text = "Also delete generated media (Pictures\\flippix-images, Videos\\flippix-vids)",
                    Checked = false, Location = new Point(18, 156), Size = new Size(460, 20)
                };
                var warn = Lbl(
                    "Leaving the boxes above unchecked keeps your settings and any images/videos " +
                    "FlipPix generated. This uninstaller does not touch ComfyUI.",
                    18, 188, 461, 60);

                p.Controls.Add(_txtDir);
                p.Controls.Add(browse);
                p.Controls.Add(_chkAppData);
                p.Controls.Add(_chkMedia);
                p.Controls.Add(warn);
                return p;
            }

            Panel BuildRun()
            {
                var p = new Panel { Location = new Point(0, 0), Size = new Size(497, 311) };
                p.Controls.Add(NewHeader("Uninstalling", "Please wait while FlipPix is removed."));
                _lblStatus = Lbl("Preparing...", 18, 80, 461, 16);
                _pb = new ProgressBar { Location = new Point(18, 100), Size = new Size(461, 22), Minimum = 0, Maximum = 100 };
                _lstLog = new ListBox { Location = new Point(18, 134), Size = new Size(461, 160), BackColor = Color.White };
                p.Controls.Add(_lblStatus);
                p.Controls.Add(_pb);
                p.Controls.Add(_lstLog);
                return p;
            }

            Panel BuildDone()
            {
                var p = new Panel { Location = new Point(0, 0), Size = new Size(497, 311) };
                p.Controls.Add(NewBanner());
                var t = Lbl("FlipPix has been removed", 180, 24, 300, 40);
                t.Font = FntBold;
                _dBody = Lbl("Setup has finished removing FlipPix from your computer.", 180, 74, 300, 80);
                p.Controls.Add(t);
                p.Controls.Add(_dBody);
                p.Controls.Add(Lbl("Click Finish to exit.", 180, 270, 300, 30));
                return p;
            }

            // --- navigation -------------------------------------------------
            void ShowStep(int i)
            {
                _step = i;
                _pgWelcome.Visible = i == 0;
                _pgOpts.Visible = i == 1;
                _pgRun.Visible = i == 2;
                _pgDone.Visible = i == 3;
                switch (i)
                {
                    case 0: _btnBack.Enabled = false; _btnNext.Enabled = true; _btnNext.Text = "Next >"; _btnCancel.Visible = true; _btnCancel.Enabled = true; break;
                    case 1: _btnBack.Enabled = true; _btnNext.Enabled = true; _btnNext.Text = "Uninstall"; _btnCancel.Visible = true; _btnCancel.Enabled = true; break;
                    case 2: _btnBack.Enabled = false; _btnNext.Enabled = false; _btnNext.Text = "Next >"; _btnCancel.Enabled = false; break;
                    case 3: _btnBack.Enabled = false; _btnNext.Enabled = true; _btnNext.Text = "Finish"; _btnCancel.Visible = false; break;
                }
            }

            void OnNext(object sender, EventArgs e)
            {
                switch (_step)
                {
                    case 0: ShowStep(1); break;
                    case 1:
                        if (string.IsNullOrWhiteSpace(_txtDir.Text))
                        {
                            MessageBox.Show("Please choose the FlipPix install folder.", "FlipPix Uninstall",
                                MessageBoxButtons.OK, MessageBoxIcon.Warning);
                            return;
                        }
                        ShowStep(2);
                        Run();
                        break;
                    case 3: Close(); break;
                }
            }

            // --- logging helpers --------------------------------------------
            void Log(string m)
            {
                _lstLog.Items.Add(m);
                _lstLog.TopIndex = _lstLog.Items.Count - 1;
                Application.DoEvents();
            }
            void Status(string m, int pct)
            {
                _lblStatus.Text = m;
                _pb.Value = Math.Max(0, Math.Min(100, pct));
                Application.DoEvents();
            }

            // --- the actual uninstall work ----------------------------------
            void Run()
            {
                try
                {
                    string dir = _txtDir.Text.Trim().TrimEnd('\\');

                    Status("Closing FlipPix...", 5);
                    KillFlipPix(Log);

                    Status("Removing shortcuts...", 25);
                    RemoveShortcuts(Log);

                    if (_chkAppData.Checked)
                    {
                        Status("Removing settings and logs...", 45);
                        string appData = Path.Combine(
                            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FlipPix");
                        TryDeleteDir(appData, Log);
                    }

                    if (_chkMedia.Checked)
                    {
                        Status("Removing generated media...", 60);
                        TryDeleteDir(Path.Combine(
                            Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "flippix-images"), Log);
                        TryDeleteDir(Path.Combine(
                            Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "flippix-vids"), Log);
                    }

                    Status("Removing program files...", 80);
                    bool selfInside = IsSelfInside(dir);

                    if (!Directory.Exists(dir))
                    {
                        Log("Install folder not found (already removed): " + dir);
                    }
                    else if (selfInside)
                    {
                        // Can't delete our own folder while running - hand off to a
                        // temp copy that finishes the job after we exit, then close
                        // immediately so this exe releases its lock on the folder.
                        // The relocated copy shows the final confirmation message.
                        Log("Scheduling removal of: " + dir);
                        RelaunchToFinalize(dir);
                        Status("Done.", 100);
                        Application.Exit();
                        return;
                    }
                    else
                    {
                        TryDeleteDir(dir, Log);
                    }

                    Status("Done.", 100);
                    Log("FlipPix has been uninstalled.");
                    ShowStep(3);
                }
                catch (Exception ex)
                {
                    Log("ERROR: " + ex.Message);
                    MessageBox.Show(ex.Message, "FlipPix Uninstall - Error",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                    _btnBack.Enabled = true;
                    _btnCancel.Enabled = true;
                    ShowStep(1);
                }
            }

            bool IsSelfInside(string dir)
            {
                try
                {
                    string self = Path.GetDirectoryName(Application.ExecutablePath).TrimEnd('\\');
                    string target = Path.GetFullPath(dir).TrimEnd('\\');
                    return self.StartsWith(target, StringComparison.OrdinalIgnoreCase);
                }
                catch { return false; }
            }

            void RelaunchToFinalize(string dir)
            {
                string temp = Path.Combine(Path.GetTempPath(),
                    "FlipPix-Uninstall-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".exe");
                File.Copy(Application.ExecutablePath, temp, true);
                Process.Start(new ProcessStartInfo
                {
                    FileName = temp,
                    Arguments = "--finalize \"" + dir + "\"",
                    UseShellExecute = false
                });
            }
        }

        // -------------------------------------------------------------------
        // shared file/process helpers (used by UI and the --finalize path)
        // -------------------------------------------------------------------
        static void KillFlipPix(Action<string> log)
        {
            foreach (var name in new[] { "FlipPix.UI", "FlipPix" })
            {
                foreach (var p in Process.GetProcessesByName(name))
                {
                    try { p.Kill(); p.WaitForExit(4000); if (log != null) log("Closed " + name + " (pid " + p.Id + ")."); }
                    catch { }
                }
            }
        }

        static void RemoveShortcuts(Action<string> log)
        {
            string desktop = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "FlipPix.lnk");
            if (File.Exists(desktop)) { TryDeleteFile(desktop, log); }

            // Per-user and (best-effort) all-users Start Menu group.
            foreach (var programs in new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.Programs),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms)
            })
            {
                string group = Path.Combine(programs, "FlipPix");
                if (Directory.Exists(group)) TryDeleteDir(group, log);
                string lnk = Path.Combine(programs, "FlipPix.lnk");
                if (File.Exists(lnk)) TryDeleteFile(lnk, log);
            }
        }

        static void TryDeleteFile(string path, Action<string> log)
        {
            try { File.Delete(path); if (log != null) log("Removed " + path); }
            catch (Exception ex) { if (log != null) log("Could not remove " + path + ": " + ex.Message); }
        }

        static void TryDeleteDir(string path, Action<string> log)
        {
            try
            {
                if (Directory.Exists(path)) { Directory.Delete(path, true); if (log != null) log("Removed " + path); }
            }
            catch (Exception ex) { if (log != null) log("Could not remove " + path + ": " + ex.Message); }
        }

        // -------------------------------------------------------------------
        // --finalize: temp copy deletes the (now-unlocked) install folder,
        // then deletes itself.
        // -------------------------------------------------------------------
        static int Finalize(string dir)
        {
            // Give the parent process a moment to exit and release the folder.
            for (int attempt = 0; attempt < 30; attempt++)
            {
                try
                {
                    if (Directory.Exists(dir)) Directory.Delete(dir, true);
                    break;
                }
                catch { Thread.Sleep(500); }
            }

            bool gone = !Directory.Exists(dir);
            MessageBox.Show(
                gone ? "FlipPix has been removed from your computer."
                     : "FlipPix was removed, but some files in\r\n" + dir + "\r\ncould not be deleted (they may be in use). You can delete that folder manually.",
                "FlipPix Uninstall",
                MessageBoxButtons.OK,
                gone ? MessageBoxIcon.Information : MessageBoxIcon.Warning);

            SelfDelete();
            return 0;
        }

        // Delete our own temp exe after we exit (a detached cmd waits, then deletes).
        static void SelfDelete()
        {
            try
            {
                string self = Application.ExecutablePath;
                Process.Start(new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = "/c ping 127.0.0.1 -n 2 >nul & del /f /q \"" + self + "\"",
                    WindowStyle = ProcessWindowStyle.Hidden,
                    CreateNoWindow = true,
                    UseShellExecute = false
                });
            }
            catch { }
        }
    }
}
