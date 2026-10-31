using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: AssemblyTitle("Yakuza 3 Recaps Installer")]
[assembly: AssemblyDescription("Installs or removes the restored Yakuza 1 and 2 recaps in Yakuza 3 Remastered.")]
[assembly: AssemblyCompany("Egld")]
[assembly: AssemblyProduct("Yakuza 3 Recaps Restored")]
[assembly: AssemblyCopyright("Copyright (c) 2026 Egld. MIT License.")]
[assembly: AssemblyVersion("1.0.1.0")]
[assembly: AssemblyFileVersion("1.0.1.0")]
[assembly: AssemblyInformationalVersion("1.0.1")]

namespace Y3Recaps
{
    class Region
    {
        public uint Rva; public byte[] Before; public byte[] After;
        public Region(uint rva, string before, string after) { Rva = rva; Before = Hex(before); After = Hex(after); }
        static byte[] Hex(string h)
        {
            byte[] b = new byte[h.Length / 2];
            for (int i = 0; i < b.Length; i++) b[i] = Convert.ToByte(h.Substring(2 * i, 2), 16);
            return b;
        }
    }

    class UserError : Exception { public UserError(string m) : base(m) { } }

    enum ExeState { Original, Patched, Unsupported }

    delegate void LogFn(string line);
    delegate void ProgressFn(long done, long total);

    class Plan
    {
        public bool Install;
        public string Game;
        public string NothingToDo;
        public ExeState Exe;
        public List<string> Scenarios = new List<string>();
        public List<int> Videos = new List<int>();
        public List<string> BackupFiles = new List<string>();
        public List<string> Lines = new List<string>();
        public long Bytes;
    }

    static class Core
    {
        public static readonly string[] Languages = { "en", "ja", "ko", "zh" };
        public static readonly string ModDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "mod");
        const string BackupName = "recaps_backup";

        static bool IsPe64(byte[] d)
        {
            if (d.Length < 0x400 || d[0] != 'M' || d[1] != 'Z') return false;
            int pe = BitConverter.ToInt32(d, 0x3c);
            return pe > 0 && pe + 0x108 < d.Length && BitConverter.ToUInt32(d, pe) == 0x4550 && BitConverter.ToUInt16(d, pe + 24) == 0x20b;
        }

        static int Sections(byte[] d, out int count)
        {
            int pe = BitConverter.ToInt32(d, 0x3c);
            count = BitConverter.ToUInt16(d, pe + 6);
            return pe + 24 + BitConverter.ToUInt16(d, pe + 20);
        }

        static int RvaToOffset(byte[] d, uint rva)
        {
            int n, t = Sections(d, out n);
            for (int i = 0; i < n; i++)
            {
                int h = t + 40 * i;
                if (h + 40 > d.Length) break;
                uint va = BitConverter.ToUInt32(d, h + 12), raw = BitConverter.ToUInt32(d, h + 16), ptr = BitConverter.ToUInt32(d, h + 20);
                if (rva >= va && rva - va < raw) return (int)(ptr + (rva - va));
            }
            return -1;
        }

        static int TextHeader(byte[] d)
        {
            int n, t = Sections(d, out n);
            for (int i = 0; i < n; i++)
                if (Encoding.ASCII.GetString(d, t + 40 * i, 8).TrimEnd('\0') == ".text") return t + 40 * i;
            return -1;
        }

        static uint PeChecksum(byte[] d)
        {
            int at = BitConverter.ToInt32(d, 0x3c) + 24 + 64;
            ulong sum = 0;
            for (int i = 0; i < d.Length; i += 2)
            {
                if (i == at || i == at + 2) continue;
                sum += (uint)(d[i] | (i + 1 < d.Length ? d[i + 1] << 8 : 0));
                sum = (sum & 0xffff) + (sum >> 16);
            }
            sum = (sum & 0xffff) + (sum >> 16);
            return (uint)(sum + (ulong)d.Length);
        }

        static bool Holds(byte[] d, Region r, byte[] bytes)
        {
            int o = RvaToOffset(d, r.Rva);
            if (o < 0 || RvaToOffset(d, r.Rva + (uint)bytes.Length - 1) != o + bytes.Length - 1) return false;
            for (int i = 0; i < bytes.Length; i++) if (d[o + i] != bytes[i]) return false;
            return true;
        }

        public static ExeState GetExeState(byte[] d)
        {
            if (!IsPe64(d) || TextHeader(d) < 0) return ExeState.Unsupported;
            bool original = true, patched = true;
            foreach (Region r in Data.Regions)
            {
                original &= Holds(d, r, r.Before);
                patched &= Holds(d, r, r.After);
            }
            if (original) return ExeState.Original;
            if (patched) return ExeState.Patched;
            return ExeState.Unsupported;
        }

        public static byte[] PatchExe(byte[] d)
        {
            byte[] p = (byte[])d.Clone();
            foreach (Region r in Data.Regions) Buffer.BlockCopy(r.After, 0, p, RvaToOffset(p, r.Rva), r.After.Length);
            int h = TextHeader(p);
            if (BitConverter.ToUInt32(p, h + 8) < Data.TextVirtualSize) BitConverter.GetBytes(Data.TextVirtualSize).CopyTo(p, h + 8);
            BitConverter.GetBytes(PeChecksum(p)).CopyTo(p, BitConverter.ToInt32(p, 0x3c) + 24 + 64);
            return p;
        }

        static string HexOf(byte[] h)
        {
            StringBuilder sb = new StringBuilder();
            foreach (byte b in h) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }

        public static string Sha256(byte[] d) { using (SHA256 s = SHA256.Create()) return HexOf(s.ComputeHash(d)); }

        static string Sha256(string path, ProgressFn progress, ref long done, long total)
        {
            using (SHA256 s = SHA256.Create())
            using (FileStream f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20))
            {
                byte[] buf = new byte[1 << 20];
                int n;
                while ((n = f.Read(buf, 0, buf.Length)) > 0)
                {
                    s.TransformBlock(buf, 0, n, null, 0);
                    done += n;
                    if (progress != null) progress(done, total);
                }
                s.TransformFinalBlock(buf, 0, 0);
                return HexOf(s.Hash);
            }
        }

        static string Sha256(string path) { long d = 0; return Sha256(path, null, ref d, 0); }

        static bool FileIs(string path, long size, string sha)
        {
            return File.Exists(path) && new FileInfo(path).Length == size && Sha256(path) == sha;
        }

        static void CopyVerified(string src, string dst, string sha, ProgressFn progress, ref long done, long total)
        {
            string tmp = dst + ".copying";
            using (SHA256 s = SHA256.Create())
            {
                using (FileStream i = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20))
                using (FileStream o = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
                {
                    byte[] buf = new byte[1 << 20];
                    int n;
                    while ((n = i.Read(buf, 0, buf.Length)) > 0)
                    {
                        o.Write(buf, 0, n);
                        s.TransformBlock(buf, 0, n, null, 0);
                        done += n;
                        if (progress != null) progress(done, total);
                    }
                    s.TransformFinalBlock(buf, 0, 0);
                }
                if (HexOf(s.Hash) != sha) { File.Delete(tmp); throw new UserError("A copied file did not match: " + dst + ". Run the installer again."); }
            }
            if (File.Exists(dst)) File.Delete(dst);
            File.Move(tmp, dst);
        }

        static void CopyVerified(string src, string dst, string sha) { long d = 0; CopyVerified(src, dst, sha, null, ref d, 0); }

        static void WriteVerified(byte[] data, string dst)
        {
            string tmp = dst + ".copying";
            File.WriteAllBytes(tmp, data);
            if (Sha256(File.ReadAllBytes(tmp)) != Sha256(data)) { File.Delete(tmp); throw new UserError("Writing " + dst + " failed. Nothing was changed in that file."); }
            if (File.Exists(dst)) File.Delete(dst);
            File.Move(tmp, dst);
        }

        static string ScenarioPath(string game, string lang) { return Path.Combine(game, "data", "scenario_" + lang, "scenario.bin"); }
        static string VideoPath(string game, int i) { return Path.Combine(game, "data", "mvusm", Data.VideoNames[i]); }
        static string ModVideo(int i) { return Path.Combine(ModDir, "data", "mvusm", Data.VideoNames[i]); }
        static string ModScenario { get { return Path.Combine(ModDir, "data", "scenario", "scenario.bin"); } }

        public static bool IsGameFolder(string game)
        {
            return !string.IsNullOrEmpty(game) && File.Exists(Path.Combine(game, "Yakuza3.exe")) && Directory.Exists(Path.Combine(game, "data"));
        }

        static void CheckGame(string game)
        {
            if (!IsGameFolder(game)) throw new UserError("Yakuza3.exe and its data folder were not found in this folder.");
            if (Process.GetProcessesByName("Yakuza3").Length > 0) throw new UserError("Yakuza 3 is running: quit the game, then try again.");
        }

        public static Plan PlanInstall(string game, LogFn log, ProgressFn progress)
        {
            CheckGame(game);
            Plan p = new Plan(); p.Install = true; p.Game = game;
            if (!Directory.Exists(ModDir))
                throw new UserError("The mod files were not found next to the installer.\n\n" +
                    "Extract the whole archive first (right-click > Extract All), then run the installer from the extracted folder.");
            log("Checking the mod files...");
            long total = Data.ScenarioSize, done = 0;
            foreach (long s in Data.VideoSizes) total += s;
            List<string> files = new List<string>(); List<long> sizes = new List<long>(); List<string> shas = new List<string>();
            files.Add(ModScenario); sizes.Add(Data.ScenarioSize); shas.Add(Data.ScenarioMod);
            for (int i = 0; i < Data.VideoNames.Length; i++) { files.Add(ModVideo(i)); sizes.Add(Data.VideoSizes[i]); shas.Add(Data.VideoShas[i]); }
            for (int i = 0; i < files.Count; i++)
            {
                if (!File.Exists(files[i]) || new FileInfo(files[i]).Length != sizes[i] || Sha256(files[i], progress, ref done, total) != shas[i])
                    throw new UserError("A mod file is missing or damaged:\n" + files[i].Substring(ModDir.Length - 3) +
                        "\n\nExtract the whole archive again (or download it again if it is damaged), then try again.");
            }

            log("Checking the game...");
            p.Exe = GetExeState(File.ReadAllBytes(Path.Combine(game, "Yakuza3.exe")));
            if (p.Exe == ExeState.Unsupported)
                throw new UserError("This Yakuza3.exe cannot be patched: its code does not match the game version this mod was made for " +
                    "(different build, or already changed by another mod).");
            foreach (string l in Languages)
            {
                string s = ScenarioPath(game, l);
                if (!File.Exists(s)) continue;
                string h = Sha256(s);
                if (h == Data.ScenarioOriginal) p.Scenarios.Add(l);
                else if (h != Data.ScenarioMod)
                    throw new UserError("data\\scenario_" + l + "\\scenario.bin is not the version this mod expects (it may have been changed by another mod).");
            }
            if (!File.Exists(ScenarioPath(game, "en")) && p.Scenarios.Count == 0)
                throw new UserError("data\\scenario_en\\scenario.bin was not found in this folder.");
            for (int i = 0; i < Data.VideoNames.Length; i++)
                if (!FileIs(VideoPath(game, i), Data.VideoSizes[i], Data.VideoShas[i])) { p.Videos.Add(i); p.Bytes += Data.VideoSizes[i]; }

            if (p.Exe == ExeState.Patched && p.Scenarios.Count == 0 && p.Videos.Count == 0) { p.NothingToDo = "The recaps are already installed."; return p; }
            long exeSize = new FileInfo(Path.Combine(game, "Yakuza3.exe")).Length;
            p.Bytes += p.Scenarios.Count * (Data.ScenarioSize + new FileInfo(ScenarioPath(game, p.Scenarios.Count > 0 ? p.Scenarios[0] : "en")).Length) +
                (p.Exe == ExeState.Original ? 2 * exeSize : 0);
            try
            {
                long free = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(game))).AvailableFreeSpace;
                if (free < p.Bytes + (64L << 20))
                    throw new UserError(string.Format(CultureInfo.InvariantCulture, "Not enough free space: about {0:0.0} GB is needed on this drive.", (p.Bytes + (64L << 20)) / 1e9));
            }
            catch (ArgumentException) { }

            if (p.Videos.Count > 0) p.Lines.Add(string.Format(CultureInfo.InvariantCulture, "- add {0} videos to data\\mvusm ({1:0.0} GB)", p.Videos.Count, p.Bytes / 1e9));
            if (p.Scenarios.Count > 0) p.Lines.Add(string.Format(CultureInfo.InvariantCulture, "- update data\\scenario_xx\\scenario.bin ({0} languages)", p.Scenarios.Count));
            if (p.Exe == ExeState.Original) p.Lines.Add("- patch Yakuza3.exe (" + Data.PatchedBytes + " bytes)");
            p.Lines.Add("");
            p.Lines.Add("Original files are backed up first in:\n" + Path.Combine(game, BackupName));
            return p;
        }

        static void ApplyInstall(Plan p, LogFn log, ProgressFn progress)
        {
            string game = p.Game, backup = Path.Combine(game, BackupName), exe = Path.Combine(game, "Yakuza3.exe");
            Directory.CreateDirectory(backup);
            byte[] exeData = File.ReadAllBytes(exe);
            if (GetExeState(exeData) != p.Exe) throw new UserError("Yakuza3.exe changed since the check. Nothing was changed.");
            if (p.Exe == ExeState.Original)
            {
                string b = Path.Combine(backup, "Yakuza3.exe"), sha = Sha256(exeData);
                if (!(File.Exists(b) && Sha256(b) == sha)) CopyVerified(exe, b, sha);
            }
            foreach (string l in p.Scenarios)
            {
                string b = Path.Combine(backup, "scenario_" + l + ".bin");
                if (!(File.Exists(b) && Sha256(b) == Data.ScenarioOriginal)) CopyVerified(ScenarioPath(game, l), b, Data.ScenarioOriginal);
            }
            log("Original files backed up.");
            long done = 0;
            foreach (int i in p.Videos)
            {
                CopyVerified(ModVideo(i), VideoPath(game, i), Data.VideoShas[i], progress, ref done, p.Bytes);
                log("Video added: " + Data.VideoNames[i]);
            }
            foreach (string l in p.Scenarios) CopyVerified(ModScenario, ScenarioPath(game, l), Data.ScenarioMod);
            if (p.Scenarios.Count > 0) log("Scenario updated (" + p.Scenarios.Count + " languages).");
            if (p.Exe == ExeState.Original)
            {
                byte[] patched = PatchExe(exeData);
                if (GetExeState(patched) != ExeState.Patched) throw new UserError("The patched Yakuza3.exe did not check out. It was not changed.");
                if (Sha256(exeData) == Data.ExeSteamOriginal && Sha256(patched) != Data.ExeSteamPatched)
                    throw new UserError("The patched Yakuza3.exe did not check out. It was not changed.");
                WriteVerified(patched, exe);
                log("Yakuza3.exe patched.");
            }
            progress(1, 1);
        }

        static string[] BackupNames()
        {
            List<string> n = new List<string>(); n.Add("Yakuza3.exe");
            foreach (string l in Languages) n.Add("scenario_" + l + ".bin");
            return n.ToArray();
        }

        public static Plan PlanUninstall(string game, LogFn log)
        {
            CheckGame(game);
            Plan p = new Plan(); p.Install = false; p.Game = game;
            string backup = Path.Combine(game, BackupName);
            log("Checking the game...");
            byte[] exe = File.ReadAllBytes(Path.Combine(game, "Yakuza3.exe"));
            p.Exe = GetExeState(exe);
            if (p.Exe == ExeState.Patched)
            {
                string b = Path.Combine(backup, "Yakuza3.exe");
                byte[] orig = File.Exists(b) ? File.ReadAllBytes(b) : null;
                bool same = orig != null && GetExeState(orig) == ExeState.Original && Sha256(PatchExe(orig)) == Sha256(exe);
                if (!same) throw new UserError("The backup of Yakuza3.exe (in " + BackupName + ") is missing or does not belong to this copy of the game.\n\n" +
                    "To get the original back, restore Yakuza3.exe from your game files (Steam: Properties > Installed Files > Verify integrity).");
            }
            foreach (string l in Languages)
            {
                string s = ScenarioPath(game, l);
                if (File.Exists(s) && Sha256(s) == Data.ScenarioMod)
                {
                    string b = Path.Combine(backup, "scenario_" + l + ".bin");
                    if (!(File.Exists(b) && Sha256(b) == Data.ScenarioOriginal))
                        throw new UserError("The backup of scenario_" + l + ".bin (in " + BackupName + ") is missing.\n\n" +
                            "To get the original back, restore it from your game files (Steam: Properties > Installed Files > Verify integrity).");
                    p.Scenarios.Add(l);
                }
            }
            for (int i = 0; i < Data.VideoNames.Length; i++) if (File.Exists(VideoPath(game, i))) p.Videos.Add(i);
            foreach (string n in BackupNames()) if (File.Exists(Path.Combine(backup, n))) p.BackupFiles.Add(n);

            if (p.Exe != ExeState.Patched && p.Scenarios.Count == 0 && p.Videos.Count == 0 && p.BackupFiles.Count == 0)
            { p.NothingToDo = "The recaps are not installed."; return p; }
            if (p.Exe == ExeState.Patched) p.Lines.Add("- restore the original Yakuza3.exe");
            if (p.Scenarios.Count > 0) p.Lines.Add(string.Format(CultureInfo.InvariantCulture, "- restore the original data\\scenario_xx\\scenario.bin ({0} languages)", p.Scenarios.Count));
            if (p.Videos.Count > 0) p.Lines.Add(string.Format(CultureInfo.InvariantCulture, "- remove the {0} recap videos from data\\mvusm", p.Videos.Count));
            if (p.BackupFiles.Count > 0) p.Lines.Add("- delete the backup folder " + BackupName);
            return p;
        }

        static void ApplyUninstall(Plan p, LogFn log, ProgressFn progress)
        {
            string game = p.Game, backup = Path.Combine(game, BackupName);
            if (p.Exe == ExeState.Patched)
            {
                string b = Path.Combine(backup, "Yakuza3.exe");
                CopyVerified(b, Path.Combine(game, "Yakuza3.exe"), Sha256(b));
                log("Yakuza3.exe restored.");
            }
            foreach (string l in p.Scenarios) CopyVerified(Path.Combine(backup, "scenario_" + l + ".bin"), ScenarioPath(game, l), Data.ScenarioOriginal);
            if (p.Scenarios.Count > 0) log("Scenario restored (" + p.Scenarios.Count + " languages).");
            foreach (int i in p.Videos) File.Delete(VideoPath(game, i));
            if (p.Videos.Count > 0) log("Videos removed.");
            foreach (string n in p.BackupFiles) File.Delete(Path.Combine(backup, n));
            if (Directory.Exists(backup) && Directory.GetFileSystemEntries(backup).Length == 0) Directory.Delete(backup);
            if (p.BackupFiles.Count > 0) log("Backup folder removed.");
            progress(1, 1);
        }

        public static void Apply(Plan p, LogFn log, ProgressFn progress)
        {
            CheckGame(p.Game);
            if (p.Install) ApplyInstall(p, log, progress); else ApplyUninstall(p, log, progress);
        }

        public static string FindSteamGame()
        {
            List<string> libs = new List<string>();
            try
            {
                foreach (object v in new object[] {
                    Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null),
                    Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath", null) })
                    if (v is string) libs.Add((string)v);
                foreach (string root in libs.ToArray())
                {
                    string vdf = Path.Combine(root, "steamapps", "libraryfolders.vdf");
                    if (!File.Exists(vdf)) continue;
                    foreach (Match m in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"([^\"]+)\""))
                        libs.Add(m.Groups[1].Value.Replace(@"\\", @"\"));
                }
                foreach (string lib in libs)
                {
                    string g = Path.GetFullPath(Path.Combine(lib, "steamapps", "common", "Yakuza 3"));
                    if (IsGameFolder(g)) return RealCase(g);
                }
            }
            catch (Exception) { }
            return "";
        }

        static string RealCase(string path)
        {
            try
            {
                string root = Path.GetPathRoot(path).ToUpperInvariant(), cur = root;
                foreach (string part in path.Substring(root.Length).Split(new[] { '\\' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    string[] found = Directory.GetFileSystemEntries(cur, part);
                    cur = found.Length == 1 ? found[0] : Path.Combine(cur, part);
                }
                return cur;
            }
            catch (Exception) { return path; }
        }

        public static string Describe(string game, out Color color)
        {
            color = Color.Firebrick;
            if (string.IsNullOrWhiteSpace(game)) { color = SystemColors.GrayText; return "Choose your Yakuza 3 Remastered folder (the one that contains Yakuza3.exe)."; }
            try
            {
                if (!IsGameFolder(game)) return "Yakuza3.exe was not found in this folder.";
                ExeState s = GetExeState(File.ReadAllBytes(Path.Combine(game, "Yakuza3.exe")));
                if (s == ExeState.Original) { color = Color.ForestGreen; return "Game found. Ready to install."; }
                if (s == ExeState.Patched) { color = Color.ForestGreen; return "The recaps are installed in this copy of the game."; }
                return "This Yakuza3.exe cannot be patched (different build, or already changed by another mod).";
            }
            catch (Exception e) { return "This folder cannot be read: " + e.Message; }
        }
    }

    class MainForm : Form
    {
        TextBox folder, logBox;
        Button browse, install, uninstall, close;
        Label status;
        ProgressBar bar;
        bool busy;
        System.Windows.Forms.Timer typing = new System.Windows.Forms.Timer();

        public MainForm(string initial)
        {
            Text = "Yakuza 3 Remastered - Yakuza 1 & 2 recaps";
            Font = new Font("Segoe UI", 9f);
            AutoScaleMode = AutoScaleMode.Font;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(600, 440);
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch (Exception) { }

            Controls.Add(new Label { Text = "The Story from Yakuza 1 & 2", UseMnemonic = false, Font = new Font("Segoe UI", 14f, FontStyle.Bold), Location = new Point(14, 12), AutoSize = true });
            Controls.Add(new Label
            {
                Text = "Brings back the PS3 recaps of Yakuza and Yakuza 2 in the title menu of Yakuza 3 Remastered. " +
                       "Original files are backed up, and Uninstall puts them back.",
                Location = new Point(16, 48), Size = new Size(568, 36)
            });
            Controls.Add(new Label { Text = "Game folder (the one that contains Yakuza3.exe):", Location = new Point(16, 96), AutoSize = true });
            folder = new TextBox { Location = new Point(16, 116), Size = new Size(478, 23), Text = initial };
            browse = new Button { Text = "Browse...", Location = new Point(502, 115), Size = new Size(82, 26) };
            status = new Label { Location = new Point(16, 148), Size = new Size(568, 34) };
            bar = new ProgressBar { Location = new Point(16, 186), Size = new Size(568, 16), Maximum = 1000 };
            logBox = new TextBox { Location = new Point(16, 210), Size = new Size(568, 172), Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BackColor = SystemColors.Window };
            install = new Button { Text = "Install", Location = new Point(16, 396), Size = new Size(120, 30) };
            uninstall = new Button { Text = "Uninstall", Location = new Point(144, 396), Size = new Size(120, 30) };
            close = new Button { Text = "Close", Location = new Point(484, 396), Size = new Size(100, 30) };
            Controls.AddRange(new Control[] { folder, browse, status, bar, logBox, install, uninstall, close });

            browse.Click += delegate { Browse(); };
            install.Click += delegate { Run(true); };
            uninstall.Click += delegate { Run(false); };
            close.Click += delegate { Close(); };
            typing.Interval = 400;
            typing.Tick += delegate { typing.Stop(); UpdateStatus(); };
            folder.TextChanged += delegate { typing.Stop(); typing.Start(); };
            FormClosing += delegate(object s, FormClosingEventArgs e)
            {
                if (busy) { e.Cancel = true; MessageBox.Show(this, "Please wait until the current operation is finished.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information); }
            };
            AcceptButton = install;
            ActiveControl = install;
            logBox.Text = "Click Install to add the recaps to the game, or Uninstall to remove them." + Environment.NewLine +
                          "Steam copies are found automatically; for any other copy, use Browse." + Environment.NewLine;
            UpdateStatus();
        }

        string Game { get { string g = folder.Text.Trim().Trim('"'); return g.Length > 0 ? g.TrimEnd('\\', '/') : g; } }

        void Browse()
        {
            using (OpenFileDialog d = new OpenFileDialog())
            {
                d.Title = "Select Yakuza3.exe in your Yakuza 3 Remastered folder";
                d.Filter = "Yakuza3.exe|Yakuza3.exe|Programs (*.exe)|*.exe";
                if (Directory.Exists(Game)) d.InitialDirectory = Game;
                if (d.ShowDialog(this) == DialogResult.OK) folder.Text = Path.GetDirectoryName(d.FileName);
            }
        }

        void UpdateStatus()
        {
            Color c;
            status.Text = Core.Describe(Game, out c);
            status.ForeColor = c;
            bool ok = Core.IsGameFolder(Game);
            install.Enabled = uninstall.Enabled = ok && !busy;
            folder.Enabled = browse.Enabled = !busy;
        }

        void Log(string line)
        {
            if (InvokeRequired) { BeginInvoke((MethodInvoker)delegate { Log(line); }); return; }
            logBox.AppendText(line + Environment.NewLine);
        }

        void Progress(long done, long total)
        {
            if (InvokeRequired) { BeginInvoke((MethodInvoker)delegate { Progress(done, total); }); return; }
            bar.Value = total <= 0 ? 0 : (int)Math.Min(1000, done * 1000 / total);
        }

        void SetBusy(bool b) { busy = b; UseWaitCursor = b; UpdateStatus(); }

        void Background(ThreadStart work)
        {
            Thread t = new Thread(delegate ()
            {
                try { work(); }
                catch (Exception e) { BeginInvoke((MethodInvoker)delegate { Failed(e); }); }
            });
            t.IsBackground = true;
            t.Start();
        }

        void Run(bool installing)
        {
            string game = Game;
            logBox.Clear(); bar.Value = 0;
            SetBusy(true);
            Background(delegate
            {
                Plan p = installing ? Core.PlanInstall(game, Log, Progress) : Core.PlanUninstall(game, Log);
                BeginInvoke((MethodInvoker)delegate { Confirm(p); });
            });
        }

        void Confirm(Plan p)
        {
            bar.Value = 0;
            if (p.NothingToDo != null) { Log(p.NothingToDo); SetBusy(false); return; }
            string question = (p.Install ? "The installer will:" : "The uninstaller will:") + "\n\n" + string.Join("\n", p.Lines.ToArray()) + "\n\nContinue?";
            if (MessageBox.Show(this, question, Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            {
                Log("Cancelled. Nothing was changed."); SetBusy(false); return;
            }
            Background(delegate
            {
                Core.Apply(p, Log, Progress);
                BeginInvoke((MethodInvoker)delegate
                {
                    string done = p.Install ? "Installation complete. Start the game: the two recaps are in the title menu."
                                            : "Uninstall complete: the game files are back to their original state.";
                    Log(""); Log(done); SetBusy(false);
                    MessageBox.Show(this, done, Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                });
            });
        }

        void Failed(Exception e)
        {
            SetBusy(false);
            if (e is UnauthorizedAccessException)
            {
                Log("Windows denied access to the game folder. Nothing else was changed.");
                MessageBox.Show(this, "Windows denied access to the game folder.\n\nClose the installer, then right-click it and choose \"Run as administrator\".",
                    Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            string msg = e is UserError ? e.Message : "Something went wrong: " + e.Message + "\n\nThe game stays playable; run Install or Uninstall again.";
            Log(msg.Replace("\n\n", " ").Replace("\n", " "));
            Log("Nothing else was changed.");
            MessageBox.Show(this, msg, Text, MessageBoxButtons.OK, e is UserError ? MessageBoxIcon.Warning : MessageBoxIcon.Error);
        }
    }

    static class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
#if TESTCLI
            if (args.Length == 3)
            {
                StreamWriter w = new StreamWriter(args[2], false, new UTF8Encoding(false));
                LogFn log = delegate (string s) { w.WriteLine(s); w.Flush(); };
                try
                {
                    if (args[0] == "state") { Color c; log(Core.Describe(args[1], out c)); return 0; }
                    Plan p = args[0] == "install" ? Core.PlanInstall(args[1], log, null) : Core.PlanUninstall(args[1], log);
                    if (p.NothingToDo != null) { log(p.NothingToDo); return 0; }
                    foreach (string l in p.Lines) log(l);
                    Core.Apply(p, log, delegate (long a, long b) { });
                    log("DONE");
                    return 0;
                }
                catch (Exception e) { log("ERROR " + e.GetType().Name + ": " + e.Message); return 1; }
                finally { w.Close(); }
            }
#endif
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm(Core.FindSteamGame()));
            return 0;
        }
    }
}
