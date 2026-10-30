using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Windows.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using TDPdf.Services;
using TDPdf.Diagnostics;

namespace TDPdf
{
    public partial class App : Application
    {
        // ============================================================
        // Paths
        // ============================================================

        private static readonly string AppName   = "TDPdf";
        private static readonly string ExeName   = "TDPdf.exe";

        private enum InstallScope { PerUser, PerMachine }

        // Possible install locations. Resolved lazily — never assume only one
        // exists. An EXE running as SYSTEM (Intune install behavior=System)
        // installs to PerMachine; an end user double-clicking the EXE falls
        // back to PerUser. Uninstall detects which one is actually installed
        // from the registry markers so the elevated UAC uninstall (which runs
        // as the user, not SYSTEM) still removes a PerMachine install.

        private static readonly string PerMachineInstallDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), AppName);
        private static readonly string PerMachineInstallExe = Path.Combine(PerMachineInstallDir, ExeName);
        private static readonly string PerMachineStartMenuDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), AppName);

        private static readonly string PerUserInstallDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs", AppName);
        private static readonly string PerUserInstallExe = Path.Combine(PerUserInstallDir, ExeName);
        private static readonly string PerUserStartMenuDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Programs), AppName);

        private static readonly string DesktopLnk = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), $"{AppName}.lnk");

        // Install destination for a *new* install. SYSTEM (Intune) → PerMachine,
        // otherwise PerUser (matches the legacy interactive double-click flow).
        private static InstallScope NewInstallScope =>
            IsSystemContextSafe() ? InstallScope.PerMachine : InstallScope.PerUser;

        // The install lock's scope (Services/InstallerExitCodes): one machine-wide install at a time,
        // and one per user for per-user installs, which never share files with anyone else's.
        private static string InstallLockKey() => InstallLockKey(NewInstallScope);

        private static string InstallLockKey(InstallScope scope)
        {
            if (scope == InstallScope.PerMachine) return "Machine";
            string? sid = null;
            try { sid = WindowsIdentity.GetCurrent().User?.Value; } catch { /* fall back below */ }
            return "User-" + (sid ?? Environment.UserName);
        }

        private static bool IsSystemContextSafe()
        {
            try { return WindowsIdentity.GetCurrent().IsSystem; }
            catch { return false; }
        }

        private static string InstallDirFor(InstallScope s) =>
            s == InstallScope.PerMachine ? PerMachineInstallDir : PerUserInstallDir;
        private static string InstallExeFor(InstallScope s) =>
            s == InstallScope.PerMachine ? PerMachineInstallExe : PerUserInstallExe;
        private static string StartMenuDirFor(InstallScope s) =>
            s == InstallScope.PerMachine ? PerMachineStartMenuDir : PerUserStartMenuDir;
        private static string StartMenuLnkFor(InstallScope s) =>
            Path.Combine(StartMenuDirFor(s), $"{AppName}.lnk");
        private static RegistryKey HiveFor(InstallScope s) =>
            s == InstallScope.PerMachine ? Registry.LocalMachine : Registry.CurrentUser;

        // Discover an existing install by checking registry markers. Used by
        // Uninstall and the portable badge so they don't depend on the current
        // process's user context (PerMachine uninstall fires under UAC as the
        // user, not SYSTEM — `IsSystemContextSafe()` would lie there).
        /// <summary>
        /// Full path of the installed EXE for whichever scope this machine actually has, or
        /// <c>null</c> when TDPdf is not installed (a portable run). #132/#134: the update check
        /// compares the version of the file sitting there against the process that is running, to
        /// notice an update that landed underneath a running instance.
        /// </summary>
        internal static string? InstalledExePath =>
            DetectInstalledScope() is { } scope ? InstallExeFor(scope) : null;

        private static InstallScope? DetectInstalledScope()
        {
            using (var k = Registry.LocalMachine.OpenSubKey(@"Software\TDPdf"))
            {
                if (k?.GetValue("Installed") is int hklm && hklm == 1)
                    return InstallScope.PerMachine;
            }
            using (var k = Registry.CurrentUser.OpenSubKey(@"Software\TDPdf"))
            {
                if (k?.GetValue("Installed") is int hkcu && hkcu == 1)
                    return InstallScope.PerUser;
            }
            return null;
        }

        // ============================================================
        // Shell interop
        // ============================================================

        [DllImport("shell32.dll")]
        private static extern void SHChangeNotify(uint wEventId, uint uFlags, IntPtr dwItem1, IntPtr dwItem2);
        private const uint SHCNE_ASSOCCHANGED = 0x08000000;
        private const uint SHCNF_IDLIST       = 0x0000;

        // ============================================================
        // Startup
        // ============================================================

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // #168: install the font resolver before ANYTHING can create an XFont — PdfSharpCore
            // caches the resolver on the first font operation and then refuses to swap it
            // ("Must not change font resolver after is was once used"). Every XFont in the app is
            // reached from here: the annotation burn (MainWindow), the searchable-PDF text layer
            // (Ocr.cs), and the headless CLI below. The stock resolver sees only *.ttf, so without
            // this the save path cannot embed the .ttc families every CJK script relies on.
            // Indexing is lazy, so this costs nothing on the headless install/uninstall paths.
            Services.TdpFontResolver.Install();

            // Handle install/uninstall flags BEFORE wiring up UI / theme / crash plumbing.
            //
            // Intune (install behavior=System) runs us as LocalSystem in session 0 where
            // there is no display, no message pump owner, and no user profile in the usual
            // sense. Touching ThemeManager (pack-URI ResourceDictionary load, SystemEvents
            // hook) or installing a crash dialog handler in that context adds failure
            // surface for zero gain — the install/uninstall paths are headless by design.
            //
            // `/install` and `/uninstall` accept an optional `/silent` second arg used by
            // the Intune Win32 app install/uninstall commands and by the QuietUninstallString
            // in the Add/Remove Programs entry.
            // Install is headless either way — see DoInstall; only uninstall genuinely branches
            // on it. A bare `/S`, `/silent`, `/quiet`, `/verysilent` or `--silent` is ALSO
            // accepted as "install silently", for the unattended installers and store
            // certification harnesses that expect one of those spellings rather than this app's own.
            //
            // `/clear-telemetry` is the admin/SYSTEM-only device opt-out. It outranks every
            // configuration source, including the managed Intune policy, so a machine can be
            // taken out of reporting without waiting for a profile change (see
            // Diagnostics/TelemetryConfig.cs). Its `/set-telemetry` counterpart was removed in
            // 1.24.0.0 along with Application Insights: the OTLP collector is provisioned by
            // policy, so there is no longer a secret for an operator to pipe in by hand.
            if (e.Args.Length > 0)
            {
                bool silent = e.Args.Length > 1 &&
                              string.Equals(e.Args[1], "/silent", StringComparison.OrdinalIgnoreCase);

                // Unattended-install aliases. TDPdf's own switch is `/install /silent`, which
                // nobody outside this repo would guess, and Microsoft Store certification runs the
                // installer with NO UI permitted — a reviewer reaches for whichever spelling the
                // ecosystem taught them (`/S` is NSIS, `/quiet` is MSI, `/verysilent` is Inno).
                // Accepting the common ones costs nothing and is the difference between a working
                // submission and a rejected one.
                //
                // This also fixes a real bug in passing: a bare `/silent` previously fell through
                // to the document-open path, so TDPdf tried to open a file literally named
                // "/silent". Only an argument list of exactly one is treated as an alias, so
                // `/install /silent` and a genuine file path are both unaffected.
                bool bareSilentInstall = e.Args.Length == 1 && IsSilentInstallAlias(e.Args[0]);

                if (bareSilentInstall ||
                    string.Equals(e.Args[0], "/install", StringComparison.OrdinalIgnoreCase))
                {
                    if (bareSilentInstall) silent = true;
                    InstallLog.WriteHeader("INSTALL", e.Args);
                    // SYSTEM-context install is the right moment to retire a legacy
                    // telemetry.dat, since this is the one path that reliably runs elevated.
                    TelemetryStore.RemoveLegacyStore();
                    Telemetry.Initialize(AppVersionString());
                    Telemetry.TrackEvent("Install.Start", InstallProps(silent));
                    try
                    {
                        using (InstallerExitCodes.Acquire(InstallLockKey()))
                            DoInstall(wantDesktop: false);
                        InstallLog.Write("INSTALL OK");
                        Telemetry.TrackEvent("Install.Success", InstallProps(silent));
                        Telemetry.Flush();
                        Shutdown(0);
                    }
                    catch (Exception ex)
                    {
                        int code = InstallerExitCodes.FromException(ex);
                        InstallLog.WriteError($"INSTALL FAILED (exit {code})", ex);
                        if (code == InstallerExitCodes.Busy)
                            Telemetry.TrackEvent("Install.Busy", InstallProps(silent));
                        else
                            Telemetry.TrackCrash(ex, "Install", recoverable: false);
                        Telemetry.Flush();
                        Shutdown(code);
                    }
                    return;
                }

                if (string.Equals(e.Args[0], "/uninstall", StringComparison.OrdinalIgnoreCase))
                {
                    InstallLog.WriteHeader("UNINSTALL", e.Args);
                    Telemetry.Initialize(AppVersionString());
                    Telemetry.TrackEvent("Uninstall.Start", InstallProps(silent));
                    try
                    {
                        bool done;
                        using (InstallerExitCodes.Acquire(InstallLockKey()))
                            done = Uninstall(silent: silent);
                        InstallLog.Write(done ? "UNINSTALL OK" : "UNINSTALL NOT DONE");
                        Telemetry.TrackEvent("Uninstall.Success", InstallProps(silent));
                        Telemetry.Flush();
                        Shutdown(0);
                    }
                    catch (Exception ex)
                    {
                        int code = InstallerExitCodes.FromException(ex);
                        InstallLog.WriteError($"UNINSTALL FAILED (exit {code})", ex);
                        if (code == InstallerExitCodes.Busy)
                            Telemetry.TrackEvent("Uninstall.Busy", InstallProps(silent));
                        else
                            Telemetry.TrackCrash(ex, "Uninstall", recoverable: false);
                        Telemetry.Flush();
                        Shutdown(code);
                    }
                    return;
                }

                if (string.Equals(e.Args[0], "/clear-telemetry", StringComparison.OrdinalIgnoreCase))
                {
                    InstallLog.WriteHeader("CLEAR-TELEMETRY", e.Args);
                    try
                    {
                        // Clear() also removes any legacy telemetry.dat left by a pre-1.24.0.0
                        // install. That file held a DPAPI-encrypted Application Insights
                        // connection string which nothing reads any more; leaving a dead secret
                        // on disk across the fleet is not a state worth preserving.
                        TelemetryStore.Clear();
                        TelemetryStore.MarkDisabled();
                        InstallLog.Write("CLEAR-TELEMETRY OK");
                        Shutdown(0);
                    }
                    catch (Exception ex)
                    {
                        InstallLog.WriteError("CLEAR-TELEMETRY FAILED", ex);
                        Shutdown(1);
                    }
                    return;
                }
            }

            // Headless CLI commands (see Cli.cs; --batch-resave in BatchMode.cs). Checked
            // AFTER the install/telemetry flags above but BEFORE the single-instance mutex
            // and any window, so a --command runs headless, works while a GUI instance is
            // open, never forwards to it, and returns a process exit code. A launch with no
            // recognized --command (including the file-association "TDPdf.exe file.pdf"
            // open) falls through to the normal GUI below.
            if (TDPdf.MainWindow.TryRunCli(e.Args, out int cliExit))
            {
                Shutdown(cliExit);
                return;
            }

            // Interactive launch. Heal a corrupt user.config BEFORE any
            // Settings.Default access (the single-instance check, theme
            // load, and MainWindow field initializers all read settings).
            // A truncated user.config — from a hard shutdown, a full disk,
            // or a roaming-profile sync conflict — otherwise throws an
            // uncaught ConfigurationErrorsException and the app dies before
            // any window or crash handler exists.
            EnsureSettingsHealthy();

            // Wire crash handling FIRST so failures anywhere below (theme
            // load, single-instance pipe, window construction) are caught,
            // reported to telemetry, and recovered where possible instead
            // of taking down the process silently.
            DispatcherUnhandledException += OnDispatcherUnhandledException;
            AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
            TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

            // Retire any telemetry.dat left behind by a pre-1.24.0.0 install. It holds a
            // DPAPI-encrypted Application Insights connection string that nothing reads since
            // that destination was removed, so this deletes a dead secret from every machine
            // that upgrades rather than leaving it sitting in %ProgramData% indefinitely.
            TelemetryStore.RemoveLegacyStore();

            // Opt-in telemetry (see Diagnostics/Telemetry.cs). No-op unless an OTLP collector
            // is provisioned. Initialized up front so a crash during the rest of startup is
            // captured.
            string appVersion = AppVersionString();
            Telemetry.Initialize(appVersion);

            if (s_settingsRecovered)
            {
                // Signal: a user.config was corrupt and got reset. Valuable
                // for spotting profile-sync or shutdown-integrity issues.
                Telemetry.TrackEvent("Settings.Recovered");
            }

            // Single-instance: when enabled (the default), a second launch — for
            // example double-clicking another PDF in Explorer — forwards its file
            // path to the already-running window (opened there as a new tab)
            // instead of spawning a second window. Best-effort: any failure falls
            // back to the normal one-window-per-process behavior.
            //
            // --new-window (MainWindow's tab tear-off / "Move to New Window") always skips this
            // block: the whole point of that gesture is a genuinely separate window, so folding it
            // back into whichever window happens to hold the mutex would silently undo the user's
            // drag. MainWindow's own startup path strips the flag back off before looking at args[1]
            // for the file to open.
            bool isTearOffLaunch = e.Args.Length > 0 &&
                string.Equals(e.Args[0], "--new-window", StringComparison.OrdinalIgnoreCase);
            if (!isTearOffLaunch && SafeSetting(() => TDPdf.Properties.Settings.Default.SingleInstanceTabs, true))
            {
                try
                {
                    _singleInstanceMutex = new Mutex(true, SingleInstanceMutexName, out bool createdNew);
                    if (!createdNew)
                    {
                        string payload = (e.Args.Length > 0 && File.Exists(e.Args[0]))
                            ? e.Args[0]
                            : string.Empty;
                        if (TrySendToRunningInstance(payload))
                        {
                            Shutdown(0);
                            return;
                        }
                        // Couldn't reach the running instance — continue and open
                        // a normal window so the user is never left with nothing.
                    }
                    else
                    {
                        StartSingleInstanceServer();
                    }
                }
                catch
                {
                    // Ignore and degrade to multi-window behavior.
                }
            }

            ThemeManager.Initialize(ParseThemeSetting(SafeSetting(() => TDPdf.Properties.Settings.Default.Theme, "")));

            string installScope = DetectInstalledScope() switch
            {
                InstallScope.PerMachine => "PerMachine",
                InstallScope.PerUser    => "PerUser",
                _                        => "Portable",
            };
            Telemetry.TrackEvent("App.Startup", new Dictionary<string, string>
            {
                ["AppVersion"]        = appVersion,
                ["InstallScope"]      = installScope,
                ["OSVersion"]         = Environment.OSVersion.Version.ToString(),
                ["CLRVersion"]        = Environment.Version.ToString(),
                ["Is64BitProcess"]    = Environment.Is64BitProcess ? "true" : "false",
                ["ProcessorCount"]    = Environment.ProcessorCount.ToString(),
                ["SettingsRecovered"] = s_settingsRecovered ? "true" : "false",
            });

            ShutdownMode = ShutdownMode.OnLastWindowClose;

            // Immediate startup feedback. MainWindow has a heavy first render
            // (large XAML, FindName resolution, tab strip / context menu build,
            // grain texture, signature load, then the async document open), so a
            // bare double-click looks unresponsive for a beat. The splash runs on
            // its own UI thread, so it stays painted and animated even while this
            // thread is blocked constructing the window below; it is dismissed the
            // instant the real window has rendered. Best-effort: a splash failure
            // must never break launch. Reached only on the genuine interactive
            // path — the /install, /uninstall, telemetry, and single-instance
            // forward branches above have all returned before here.
            StartupSplash? splash = null;
            try { splash = StartupSplash.Show(); } catch { /* splash is best-effort */ }

            var window = new MainWindow();
            MainWindow = window;
            if (splash is { } shownSplash)
                window.ContentRendered += (_, _) => shownSplash.Close();
            window.Show();
        }

        // ============================================================
        // Settings self-heal
        // ============================================================

        private static bool s_settingsRecovered;

        /// <summary>
        /// Proactively parse the per-user settings file and, if it is
        /// corrupt, delete it so defaults regenerate. Turns a fatal
        /// <see cref="System.Configuration.ConfigurationErrorsException"/>
        /// at first <c>Settings.Default</c> access into a transparent
        /// self-heal. Must run before any other settings read.
        /// </summary>
        private static void EnsureSettingsHealthy()
        {
            try
            {
                // Force ApplicationSettingsBase to parse user.config now.
                _ = TDPdf.Properties.Settings.Default.Theme;
            }
            catch (System.Configuration.ConfigurationErrorsException ex)
            {
                s_settingsRecovered = true;
                InstallLog.WriteError("SETTINGS CORRUPT - resetting user.config", ex);
                DeleteCorruptUserConfig(ex);
                try { TDPdf.Properties.Settings.Default.Reload(); }
                catch { /* in-memory defaults will be used for this session */ }
            }
            catch
            {
                // Any other failure: ignore. Downstream reads fall back to
                // defaults or are individually guarded.
            }
        }

        /// <summary>
        /// Reads one Settings.Default property, falling back to <paramref name="fallback"/> on a
        /// <see cref="System.Configuration.ConfigurationErrorsException"/>. The recovery above
        /// only proves the FIRST settings access after a corrupt user.config works again —
        /// ConfigurationManager's underlying config-system init state is cached process-wide once
        /// PrepareConfigSystem fails, so <c>Reload()</c> does not reliably un-poison it, and every
        /// later property read can keep throwing the identical exception for the rest of the
        /// process even though the corrupt file is already gone. A crash observed in the field
        /// (2026-09-02, DEREKHOMEVM) hit exactly this: EnsureSettingsHealthy healed the first
        /// Theme read and logged Settings.Recovered, then the very next Settings.Default access —
        /// SingleInstanceTabs, below — threw the same ConfigurationErrorsException uncaught and
        /// took the process down before any window existed.
        /// </summary>
        private static T SafeSetting<T>(Func<T> getter, T fallback)
        {
            try { return getter(); }
            catch (System.Configuration.ConfigurationErrorsException) { return fallback; }
        }

        private static void DeleteCorruptUserConfig(System.Configuration.ConfigurationErrorsException ex)
        {
            foreach (string file in EnumerateConfigFiles(ex))
            {
                try
                {
                    if (File.Exists(file))
                    {
                        File.Delete(file);
                        InstallLog.Write($"SETTINGS reset: deleted {file}");
                    }
                }
                catch { /* best-effort */ }
            }
        }

        private static IEnumerable<string> EnumerateConfigFiles(System.Configuration.ConfigurationErrorsException root)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // The thrown exception (and its inner chain) names the offending
            // file in Filename. Collect every distinct path we can find.
            for (Exception? cur = root; cur is not null; cur = cur.InnerException)
            {
                if (cur is System.Configuration.ConfigurationErrorsException cee &&
                    !string.IsNullOrEmpty(cee.Filename) && seen.Add(cee.Filename))
                {
                    yield return cee.Filename;
                }
            }

            // Fallback: ask the config system directly for the per-user path.
            string? viaApi;
            try
            {
                viaApi = System.Configuration.ConfigurationManager
                    .OpenExeConfiguration(System.Configuration.ConfigurationUserLevel.PerUserRoamingAndLocal)
                    .FilePath;
            }
            catch (System.Configuration.ConfigurationErrorsException cee)
            {
                viaApi = cee.Filename;
            }
            catch
            {
                viaApi = null;
            }

            if (!string.IsNullOrEmpty(viaApi) && seen.Add(viaApi))
                yield return viaApi;
        }

        // ============================================================
        // Single instance (open subsequent PDFs as tabs in one window)
        // ============================================================

        // Named from the user SID plus the session id (InstanceIdentity): the pipe namespace is
        // machine-wide, so a user-name key let a second RDP session of the same account forward
        // its files to the first session's window, and let another account pre-create the pipe
        // and receive our paths. CurrentUserOnly on both ends closes the second hole. A running
        // TDPdf older than this uses the old names, so a new launch will not find it and opens
        // its own window instead.
        private static readonly string SingleInstanceMutexName =
            "Local\\TDPdf.SingleInstance.Mutex." + InstanceIdentity.Suffix;
        private static readonly string SingleInstancePipeName =
            "TDPdf.SingleInstance.Pipe." + InstanceIdentity.Suffix;
        private Mutex? _singleInstanceMutex;

        // Sends a file path (or empty string = "just focus") to the running
        // instance over a named pipe. Returns false if no instance answered.
        private static bool TrySendToRunningInstance(string payload)
        {
            try
            {
                using var client = new NamedPipeClientStream(".", SingleInstancePipeName, PipeDirection.Out,
                    PipeOptions.CurrentUserOnly);
                client.Connect(3000);
                using var writer = new StreamWriter(client) { AutoFlush = true };
                writer.WriteLine(payload);
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Gives up the single-instance mutex early, so a process this one is about to start can
        /// take it. Only for a hand-over immediately before shutdown; see InstallAndRelaunchCore.
        /// </summary>
        private void ReleaseSingleInstance()
        {
            try { _singleInstanceMutex?.Dispose(); } catch { }
            _singleInstanceMutex = null;
        }

        private void StartSingleInstanceServer()
        {
            var thread = new Thread(SingleInstanceServerLoop)
            {
                IsBackground = true,
                Name = "TDPdf-SingleInstance"
            };
            thread.Start();
        }

        private void SingleInstanceServerLoop()
        {
            while (true)
            {
                try
                {
                    using var server = new NamedPipeServerStream(
                        SingleInstancePipeName,
                        PipeDirection.In,
                        1,
                        PipeTransmissionMode.Byte,
                        PipeOptions.CurrentUserOnly);
                    server.WaitForConnection();
                    using var reader = new StreamReader(server);
                    string? line = reader.ReadLine();
                    Dispatcher.Invoke(() =>
                    {
                        if (Current?.MainWindow is MainWindow mw)
                            mw.OpenPathFromAnotherInstance(line);
                    });
                }
                catch
                {
                    // Pipe broke, or the app is shutting down. Pause briefly and
                    // retry; bail out if the application is gone.
                    if (Current is null) break;
                    try { Thread.Sleep(200); } catch { }
                }
            }
        }

        private static string AppVersionString() =>
            Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0.0";

        private static Dictionary<string, string> InstallProps(bool silent) => new()
        {
            ["Silent"]          = silent ? "true" : "false",
            ["IsSystemContext"] = IsSystemContextSafe() ? "true" : "false",
            ["OSVersion"]       = Environment.OSVersion.Version.ToString(),
            ["AppVersion"]      = AppVersionString(),
        };


        private static Theme ParseThemeSetting(string? value)
        {
            return Enum.TryParse(value, ignoreCase: true, out Theme theme) ? theme : Theme.System;
        }


        protected override void OnExit(ExitEventArgs e)
        {
            // Order matters. TrackSessionEnd marks this run as having finished under its own
            // power — a session with an App.Startup and no App.SessionEnd did not, which is what
            // makes crash-free session rate measurable. Then Flush, then Shutdown: buffered OTLP
            // LOG records are drained by Shutdown's dispose, not by Flush, so exiting after Flush
            // alone would discard the very event just recorded.
            Telemetry.TrackSessionEnd();
            Telemetry.Flush();
            Telemetry.Shutdown();
            ThemeManager.Cleanup();
            try { _singleInstanceMutex?.Dispose(); } catch { }
            base.OnExit(e);
        }

        // ============================================================
        // Crash handling
        // ============================================================

        private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            try
            {
                var report = CrashReporter.Report(e.Exception, "UI thread");

                if (report.Recoverable)
                {
                    // #115: mark it handled and unwind FIRST, then ask. CrashDialog is modal, and a
                    // modal window opened from the throwing stack pushes a dispatcher frame that
                    // re-enters whatever was in flight — for a throw inside ContextLayoutManager
                    // that means re-entering layout, which threw again, tripped ShowCrash's
                    // reentrancy guard into returning "don't continue", and turned a recoverable
                    // exception into process death. Background priority runs after the current
                    // operation has fully unwound.
                    e.Handled = true;
                    _ = Dispatcher.BeginInvoke(DispatcherPriority.Background,
                        new Action(() => PromptAfterRecoveredCrash(report)));
                    return;
                }

                CrashDialog.ShowCrash(report);
                e.Handled = false;
                // App is going down — give the in-memory telemetry
                // channel a bounded window to ship the crash event.
                Telemetry.Flush();
                Shutdown(1);
            }
            catch
            {
                // Crash reporting itself must never throw. Recover the UI
                // thread rather than entering an exception loop.
                e.Handled = true;
            }
        }

        /// <summary>
        /// Shows the crash dialog for an already-recovered exception, off the stack that threw it.
        /// The app is alive and usable at this point; the dialog only offers the user the chance to
        /// close it down anyway.
        /// </summary>
        private void PromptAfterRecoveredCrash(CrashReport report)
        {
            try
            {
                if (CrashDialog.ShowCrash(report)) return;
                Telemetry.Flush();
                Shutdown(1);
            }
            catch { /* never throw from the crash path */ }
        }

        private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            try
            {
                var exception = e.ExceptionObject as Exception
                    ?? new InvalidOperationException("A non-Exception object was thrown.");
                var report = CrashReporter.Report(exception, "AppDomain unhandled exception");
                CrashDialog.ShowCrash(report);
            }
            catch { /* never throw from the last-chance handler */ }
            finally
            {
                // A domain-level unhandled exception terminates the process;
                // flush before the runtime tears us down.
                Telemetry.Flush();
            }
        }

        private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
        {
            try
            {
                var report = CrashReporter.Report(e.Exception, "Unobserved task exception");
                CrashDialog.ShowCrash(report);
                e.SetObserved();
            }
            catch
            {
                // Observe it anyway so an unobserved-task escalation can't
                // tear the process down over a reporting failure.
                try { e.SetObserved(); } catch { }
            }
        }

        // ============================================================
        // Public surface used by MainWindow (portable badge / install)
        // ============================================================

        /// <summary>
        /// True when running from outside any installed location (i.e. portable mode).
        /// </summary>
        internal static bool IsPortable()
        {
            string currentExe = Process.GetCurrentProcess().MainModule!.FileName;
            return !string.Equals(currentExe, PerMachineInstallExe, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(currentExe, PerUserInstallExe,    StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// What a portable copy finds already installed: which scope (from the registry markers, the
        /// same ones Uninstall trusts) and the installed exe's FileVersion — <c>null</c> when the exe
        /// is missing or unreadable, which the badge treats as a repair.
        /// </summary>
        internal static (ExistingInstall Existing, Version? Version) DetectExistingInstall()
        {
            InstallScope? scope;
            try { scope = DetectInstalledScope(); }
            catch (Exception ex)
            {
                InstallLog.WriteError("Existing-install detection failed (treated as not installed)", ex);
                return (ExistingInstall.None, null);
            }
            if (scope is null) return (ExistingInstall.None, null);

            var existing = scope == InstallScope.PerMachine ? ExistingInstall.PerMachine : ExistingInstall.PerUser;
            Version? version = null;
            try
            {
                string exe = InstallExeFor(scope.Value);
                if (File.Exists(exe))
                {
                    var fvi = FileVersionInfo.GetVersionInfo(exe);
                    version = new Version(fvi.FileMajorPart, fvi.FileMinorPart, fvi.FileBuildPart, fvi.FilePrivatePart);
                }
            }
            catch (Exception ex)
            {
                InstallLog.WriteError("Installed FileVersion lookup failed (non-fatal)", ex);
            }
            return (existing, version);
        }

        /// <summary>
        /// Installs TDPdf, offers to set as default PDF handler, then relaunches
        /// from the installed location. On failure, says so and leaves this copy running.
        /// </summary>
        internal static void InstallAndRelaunch(string? fileToOpen, bool wantDesktop) =>
            _ = InstallAndRelaunchCore(fileToOpen, wantDesktop, NewInstallScope, offerDefaultHandler: true);

        /// <summary>
        /// Upgrade / reinstall / downgrade from a portable copy: replaces the EXISTING per-user
        /// install in place and relaunches from it. The target is the detected scope, never
        /// <see cref="NewInstallScope"/> — and never a machine-wide install, which belongs to the
        /// organisation (Intune) and which a user-context process could not write to anyway.
        /// Returns false when nothing was relaunched (refused, or the install failed and said so).
        /// </summary>
        internal static bool ReinstallAndRelaunch(string? fileToOpen)
        {
            if (DetectInstalledScope() != InstallScope.PerUser)
            {
                InstallLog.Write("Reinstall refused: no per-user install to replace");
                return false;
            }
            // Keep the user's shortcut choice: a desktop shortcut they deleted is not recreated.
            return InstallAndRelaunchCore(fileToOpen, wantDesktop: File.Exists(DesktopLnk),
                InstallScope.PerUser, offerDefaultHandler: false);
        }

        private static bool InstallAndRelaunchCore(
            string? fileToOpen, bool wantDesktop, InstallScope scope, bool offerDefaultHandler)
        {
            try
            {
                InstallLog.WriteHeader("INSTALL (interactive)", Array.Empty<string>());
                // Same lock as /install, so this cannot race an Intune install of the same scope
                // over the same files; a held lock surfaces as the failure dialog below.
                using (InstallerExitCodes.Acquire(InstallLockKey(scope)))
                    DoInstall(wantDesktop, scope);
            }
            catch (Exception ex)
            {
                InstallLog.WriteError("Interactive install failed", ex);
                TdpDialog.Show(Current?.MainWindow, $"Installation failed:\n{ex.Message}", AppName,
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }

            if (offerDefaultHandler && !IsDefaultPdfHandler())
            {
                var res = TdpDialog.Show(null,
                    "Would you like to set TDPdf as your default PDF viewer?\n\n" +
                    "Opens Windows Settings → Default Apps.",
                    AppName, MessageBoxButton.YesNo);
                if (res == MessageBoxResult.Yes)
                    Process.Start(new ProcessStartInfo("ms-settings:defaultapps")
                        { UseShellExecute = true });
            }

            // Hand the single-instance role over BEFORE starting the installed copy. This process
            // still holds the mutex until OnExit, so the new one would see it taken, forward its
            // file down the pipe to THIS window - which is on its way out - and exit itself,
            // leaving the user with no window at all. Released here, the new copy owns the mutex
            // and starts its own pipe server (retrying until this one's listener is gone).
            (Current as App)?.ReleaseSingleInstance();

            var psi = new ProcessStartInfo(InstallExeFor(scope));
            if (fileToOpen != null)
                psi.Arguments = $"\"{fileToOpen}\"";
            Process.Start(psi);
            Application.Current.Shutdown();
            return true;
        }

        // ============================================================
        // Registry helpers
        // ============================================================

        private static bool IsDefaultPdfHandler()
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\Shell\Associations\FileAssociations\.pdf\UserChoice");
            return key?.GetValue("ProgId") is string progId &&
                   progId.Equals("TDPdf.pdf", StringComparison.OrdinalIgnoreCase);
        }

        // ============================================================
        // Installation
        // ============================================================

        /// <summary>
        /// Copies the running EXE onto <paramref name="dest"/>, surviving a destination that is
        /// momentarily locked.
        /// </summary>
        /// <remarks>
        /// #134: a plain <c>File.Copy(overwrite: true)</c> here crashed the installer on five
        /// occasions across four consecutive releases, always the same
        /// <see cref="IOException"/> — "the process cannot access the file" — out of
        /// <c>DoInstall</c> on a first-run path, where the user sees the app die on launch rather
        /// than install. The lock is transient every time (a previous TDPdf finishing its exit, a
        /// shell preview handler, an AV scan), so two cheap things clear it:
        /// <list type="number">
        /// <item>a short backoff and retry, which covers a process on its way out; and</item>
        /// <item>renaming the destination aside first — Windows permits renaming a file that is
        /// open and even one that is executing, where it refuses to overwrite it.</item>
        /// </list>
        /// The displaced file is deleted on the next install if it is no longer held, so a machine
        /// cannot accumulate them. If every attempt fails the original exception is rethrown
        /// untouched, preserving the "exit non-zero and let Intune retry" contract that the caller
        /// depends on.
        /// </remarks>
        private static void CopyExeOverLock(string src, string dest)
        {
            const int attempts = 4;

            // Clear anything a PREVIOUS install displaced, now that whatever held it has probably
            // gone. Best-effort by definition: the instance that was running then may still be.
            foreach (string stale in DisplacedNamesFor(dest))
            {
                try { if (File.Exists(stale)) File.Delete(stale); }
                catch { /* still held; DisplacedNamesFor will simply pick another name below */ }
            }

            // Where the ORIGINAL exe was parked, if it was. Later displacements can only move a
            // partial copy of ours, so only this first one is worth restoring.
            string? parkedOriginal = null;

            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    File.Copy(src, dest, overwrite: true);
                    if (attempt > 1) InstallLog.Write($"Copy succeeded on attempt {attempt}.");
                    return;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    if (attempt >= attempts)
                    {
                        InstallLog.WriteError($"Copy failed after {attempts} attempts", ex);
                        // Never leave the install without an exe: the registry, ProgID and marker
                        // all still point at it. Put the displaced original back.
                        if (parkedOriginal is not null) RestoreDisplaced(parkedOriginal, dest);
                        throw;
                    }
                    InstallLog.Write($"Copy attempt {attempt} failed ({ex.GetType().Name}: {ex.Message}); retrying.");

                    // From the second attempt, stop fighting the lock and move it out of the way.
                    // Windows refuses to overwrite a file that is open or executing but is perfectly
                    // happy to RENAME one, which is what makes updating a running TDPdf possible at
                    // all: the live process keeps running from the renamed image, the new build goes
                    // into place, and the next launch is the new build.
                    if (attempt >= 2)
                    {
                        string? parked = TryDisplace(dest);
                        parkedOriginal ??= parked;
                    }

                    Thread.Sleep(250 * attempt);
                }
            }
        }

        /// <summary>Candidate parking names for a destination we cannot overwrite.</summary>
        /// <remarks>
        /// More than one, because the obvious single name is not enough: a person who has had two
        /// updates land without restarting is holding <c>.old</c> already, and reusing it would fail
        /// exactly when the retry matters most.
        /// </remarks>
        private static IEnumerable<string> DisplacedNamesFor(string dest)
        {
            yield return dest + ".old";
            for (int i = 1; i <= 4; i++) yield return $"{dest}.old{i}";
        }

        /// <summary>Moves a locked destination aside; returns where it went, or null.</summary>
        private static string? TryDisplace(string dest)
        {
            if (!File.Exists(dest)) return null;
            foreach (string parked in DisplacedNamesFor(dest))
            {
                try
                {
                    if (File.Exists(parked)) continue;   // held by an older still-running instance
                    File.Move(dest, parked);
                    InstallLog.Write($"Displaced locked destination to {parked}.");
                    return parked;
                }
                catch (Exception ex)
                {
                    InstallLog.Write($"Could not displace to {parked}: {ex.Message}");
                }
            }
            InstallLog.Write("Could not displace the locked destination under any name.");
            return null;
        }

        /// <summary>Undoes <see cref="TryDisplace"/> after every copy attempt failed.</summary>
        private static void RestoreDisplaced(string parked, string dest)
        {
            try
            {
                // Anything at dest now is a partial copy of ours, not the original.
                if (File.Exists(dest)) File.Delete(dest);
                File.Move(parked, dest);
                InstallLog.Write($"Restored the original exe from {parked}.");
            }
            catch (Exception ex)
            {
                InstallLog.WriteError($"Could not restore the original exe from {parked}; it is still there", ex);
            }
        }

        /// <summary>
        /// The spellings accepted as "install silently, no UI", in addition to
        /// <c>/install /silent</c>. See the call site for why these exist.
        /// </summary>
        private static bool IsSilentInstallAlias(string arg) =>
            arg is not null &&
            (arg.Equals("/S", StringComparison.OrdinalIgnoreCase) ||
             arg.Equals("/silent", StringComparison.OrdinalIgnoreCase) ||
             arg.Equals("/quiet", StringComparison.OrdinalIgnoreCase) ||
             arg.Equals("/verysilent", StringComparison.OrdinalIgnoreCase) ||
             arg.Equals("--silent", StringComparison.OrdinalIgnoreCase));

        /// <summary>
        /// Installs TDPdf. <b>Always headless</b> — there is no interactive variant of this, and
        /// no argument that makes it show UI.
        /// </summary>
        /// <remarks>
        /// This deliberately takes no <c>silent</c> parameter. It used to, and never read it,
        /// which made the install path look conditionally quiet when it never was. The
        /// distinction is real for <see cref="Uninstall"/>, which genuinely does branch on it,
        /// so the asymmetry is worth stating rather than papering over with a dead parameter.
        ///
        /// Anything interactive belongs to the CALLER — the portable badge's Install / Upgrade /
        /// Reinstall prompts live in MainWindow.Install_Click, not here.
        /// </remarks>
        private static void DoInstall(bool wantDesktop, InstallScope? into = null)
        {
            // `into` is set only by a portable copy replacing an install it found (see
            // ReinstallAndRelaunch); every other path installs where a NEW install belongs.
            var scope = into ?? NewInstallScope;
            string installDir   = InstallDirFor(scope);
            string installExe   = InstallExeFor(scope);
            string startMenuDir = StartMenuDirFor(scope);
            string startMenuLnk = StartMenuLnkFor(scope);
            var hive = HiveFor(scope);
            string src = Process.GetCurrentProcess().MainModule!.FileName;

            InstallLog.Write($"DoInstall scope={scope} src={src} dest={installExe} hive={hive.Name}");

            // Copy EXE to install location. If the destination is locked — a previous TDPdf still
            // exiting, an Explorer preview handler, an AV scanner mid-scan — File.Copy throws
            // IOException and the caller maps it to a non-zero exit code so Intune retries on the
            // next sync. That contract is preserved; CopyExeOverLock just stops the common,
            // transient case from getting that far. See #134.
            Directory.CreateDirectory(installDir);
            CopyExeOverLock(src, installExe);

            // Post-copy verification. Without this, a partial / silently-failed
            // copy could leave us with no file on disk and the caller would
            // still see "success" — exactly the Intune "installed but not
            // detected" footgun we are trying to fix.
            var fi = new FileInfo(installExe);
            if (!fi.Exists || fi.Length == 0)
                throw new IOException($"Post-copy verification failed: {installExe} missing or empty.");

            // Extract the stock PDF icon next to the EXE. This is what the
            // .pdf file-type association (DefaultIcon) points at, so PDF files
            // show the generic PDF logo while the EXE / taskbar keeps the
            // company logo as the primary indicator. Best-effort: a failure
            // here just means the association falls back to the EXE icon.
            try
            {
                ExtractPdfFileIcon(installDir);
            }
            catch (Exception ex)
            {
                InstallLog.WriteError("PDF file-type icon extraction failed (non-fatal)", ex);
            }

            try
            {
                var fvi = System.Diagnostics.FileVersionInfo.GetVersionInfo(installExe);
                InstallLog.Write($"Copied {fi.Length} bytes; FileVersion={fvi.FileVersion} ProductVersion={fvi.ProductVersion}");
            }
            catch (Exception ex)
            {
                InstallLog.WriteError("FileVersionInfo lookup failed (non-fatal)", ex);
            }

            // Shortcuts — best-effort. A failed shortcut should not fail the
            // entire install (and CreateShortcut already swallows internally),
            // but we still log it.
            try
            {
                Directory.CreateDirectory(startMenuDir);
                CreateShortcut(startMenuLnk, installExe);
                if (wantDesktop)
                    CreateShortcut(DesktopLnk, installExe);
            }
            catch (Exception ex)
            {
                InstallLog.WriteError("Shortcut creation failed (non-fatal)", ex);
            }

            // Add/Remove Programs entry. Use the full 4-part version so
            // revision-only bumps (e.g. 1.0.0.3) are visible.
            string version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "";
            using (var key = hive.CreateSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Uninstall\TDPdf"))
            {
                key.SetValue("DisplayName",          AppName);
                key.SetValue("DisplayVersion",       version);
                key.SetValue("Publisher",            "The Doodle Project");
                key.SetValue("InstallLocation",      installDir);
                key.SetValue("DisplayIcon",          $"{installExe},0");
                key.SetValue("UninstallString",      $"\"{installExe}\" /uninstall");
                key.SetValue("QuietUninstallString", $"\"{installExe}\" /uninstall /silent");
                key.SetValue("NoModify",             1);
                key.SetValue("NoRepair",             1);
                // Microsoft Store certification queries the Add/Remove Programs entry and checks
                // that the installer's details are actually there — a missing or obviously wrong
                // size is a documented failure. EstimatedSize is in KB and is a DWORD; Explorer
                // renders it as the "Size" column. InstallDate is yyyyMMdd, which is the only
                // format the Settings app parses.
                key.SetValue("EstimatedSize",        (int)Math.Max(1, fi.Length / 1024), RegistryValueKind.DWord);
                key.SetValue("InstallDate",          DateTime.Now.ToString("yyyyMMdd"));
                key.SetValue("URLInfoAbout",         "https://github.com/doodlemania2/TDPdf");
                key.SetValue("HelpLink",             "https://github.com/doodlemania2/TDPdf/issues");
            }
            InstallLog.Write("Wrote Add/Remove Programs entry");

            // Register as PDF file handler in the same hive. This creates
            // `Software\TDPdf\Capabilities` under the scope hive.
            RegisterFileHandler(scope);
            InstallLog.Write("Registered PDF file handler");

            // Installed marker — written LAST so the Intune registry detection
            // rule (`HKLM\Software\TDPdf` value `Version` >= release version)
            // only flips to "installed" once all required steps above have
            // succeeded. A failure earlier in DoInstall must not leave a
            // partial install that detects as complete.
            using (var key = hive.CreateSubKey(@"Software\TDPdf"))
            {
                key.SetValue("Installed",    1);
                key.SetValue("InstallPath",  installExe);
                key.SetValue("Version",      version);
            }
            InstallLog.Write($"Wrote install marker: HKLM-or-HKCU\\Software\\TDPdf Version={version}");
        }

        private static void RegisterFileHandler(InstallScope scope)
        {
            // HKLM\Software\Classes is visible to all users; HKCU\Software\Classes
            // is per-user. Stay consistent with the rest of the install.
            string installExe = InstallExeFor(scope);
            var hive = HiveFor(scope);

            using (var k = hive.CreateSubKey(@"Software\Classes\TDPdf.pdf"))
            {
                k.SetValue("", "PDF Document");

                // Mark the "open" verb as safe for the Windows Attachment
                // Manager. Without this, opening a .pdf that carries a
                // Mark-of-the-Web zone tag — e.g. an attachment double-clicked
                // from Outlook (which drops the file in a temp folder tagged
                // with the Internet/Restricted zone) or any downloaded file —
                // raises the "Open File - Security Warning" prompt before the
                // handler launches. FTA_OpenIsSafe (0x00010000) on the ProgID's
                // EditFlags tells the shell this file type is safe to open with
                // this handler, suppressing the warning. (FTA_OpenIsSafe.)
                k.SetValue("EditFlags", unchecked((int)0x00010000), RegistryValueKind.DWord);
            }

            // File-type icon shown on .pdf files. Prefer the generic stock PDF
            // icon extracted alongside the EXE so PDF files don't show the
            // company logo; fall back to the EXE's own icon if the extracted
            // file is missing for any reason.
            string pdfIcon = PdfFileIconPathFor(scope);
            string defaultIcon = File.Exists(pdfIcon) ? $"{pdfIcon},0" : $"{installExe},0";
            using (var k = hive.CreateSubKey(
                @"Software\Classes\TDPdf.pdf\DefaultIcon"))
                k.SetValue("", defaultIcon);

            using (var k = hive.CreateSubKey(
                @"Software\Classes\TDPdf.pdf\shell\open\command"))
                k.SetValue("", $"\"{installExe}\" \"%1\"");

            // Associate .pdf extension — adds TDPdf to the "Open with" list
            using (var k = hive.CreateSubKey(
                @"Software\Classes\.pdf\OpenWithProgids"))
                k.SetValue("TDPdf.pdf", new byte[0], RegistryValueKind.None);

            // RegisteredApplications capability (used by Default Programs UI)
            using (var k = hive.CreateSubKey(
                @"Software\TDPdf\Capabilities"))
            {
                k.SetValue("ApplicationName",        AppName);
                k.SetValue("ApplicationDescription", "Lightweight PDF viewer and editor");
            }
            using (var k = hive.CreateSubKey(
                @"Software\TDPdf\Capabilities\FileAssociations"))
                k.SetValue(".pdf", "TDPdf.pdf");

            using (var k = hive.CreateSubKey(@"Software\RegisteredApplications"))
                k.SetValue(AppName, @"Software\TDPdf\Capabilities");

            // Tell the shell file associations have changed
            SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, IntPtr.Zero, IntPtr.Zero);
        }

        // Full path to the stock PDF file-type icon for a given install scope.
        private static string PdfFileIconPathFor(InstallScope scope) =>
            Path.Combine(InstallDirFor(scope), "pdf-file.ico");

        // Write the embedded stock PDF icon out to the install directory so the
        // .pdf file-type association can point at it. The icon ships inside the
        // single-file EXE as a WPF resource (Resources\pdf-file.ico).
        private static void ExtractPdfFileIcon(string installDir)
        {
            var uri = new Uri("pack://application:,,,/Resources/pdf-file.ico", UriKind.Absolute);
            var info = Application.GetResourceStream(uri)
                       ?? throw new FileNotFoundException("Embedded resource Resources/pdf-file.ico not found.");

            string dest = Path.Combine(installDir, "pdf-file.ico");
            using var src = info.Stream;
            using var dst = File.Create(dest);
            src.CopyTo(dst);
            InstallLog.Write($"Extracted PDF file-type icon to {dest}");
        }

        // Best-effort, but no longer silent: a shortcut that did not appear is logged, so a
        // "where is my shortcut" report can be answered from install.log.
        private static void CreateShortcut(string lnkPath, string targetPath)
        {
            try
            {
                var shellType = Type.GetTypeFromProgID("WScript.Shell");
                if (shellType is null)
                {
                    InstallLog.Write($"Shortcut not created ({lnkPath}): WScript.Shell is unavailable");
                    return;
                }
                dynamic shell    = Activator.CreateInstance(shellType)!;
                dynamic shortcut = shell.CreateShortcut(lnkPath);
                shortcut.TargetPath       = targetPath;
                shortcut.WorkingDirectory = Path.GetDirectoryName(targetPath);
                shortcut.Save();
                InstallLog.Write(File.Exists(lnkPath)
                    ? $"Created shortcut {lnkPath}"
                    : $"Shortcut not created ({lnkPath}): Save reported no error but the file is missing");
            }
            catch (Exception ex)
            {
                InstallLog.WriteError($"Shortcut not created ({lnkPath})", ex);
            }
        }

        /// <summary>The target of an existing shortcut, or null when it cannot be read.</summary>
        private static string? ShortcutTarget(string lnkPath)
        {
            try
            {
                if (!File.Exists(lnkPath)) return null;
                var shellType = Type.GetTypeFromProgID("WScript.Shell");
                if (shellType is null) return null;
                dynamic shell    = Activator.CreateInstance(shellType)!;
                dynamic shortcut = shell.CreateShortcut(lnkPath);   // opens, does not overwrite
                return shortcut.TargetPath as string;
            }
            catch { return null; }
        }

        // ============================================================
        // Running-instance check for uninstall
        // ============================================================

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint access, bool inherit, int processId);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder exeName, ref int size);

        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr handle);

        private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

        // Limited-information access works on the same account's processes in any session and,
        // for SYSTEM (an Intune uninstall), on everyone's; Process.MainModule needs far more.
        private static string? ProcessImagePath(int processId)
        {
            IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, processId);
            if (h == IntPtr.Zero) return null;
            try
            {
                var sb = new StringBuilder(1024);
                int size = sb.Capacity;
                return QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString(0, size) : null;
            }
            finally { CloseHandle(h); }
        }

        /// <summary>
        /// How many other TDPdf processes are running out of <paramref name="installDir"/>, in any
        /// session. One whose path cannot be read counts (see UninstallPolicy.BlocksUninstall).
        /// </summary>
        private static int CountRunningFrom(string installDir)
        {
            int count = 0;
            int self = Environment.ProcessId;
            foreach (var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(ExeName)))
            {
                using (p)
                {
                    if (p.Id == self) continue;
                    string? path = null;
                    try { path = ProcessImagePath(p.Id); } catch { /* unreadable: counts below */ }
                    if (UninstallPolicy.BlocksUninstall(path, installDir))
                    {
                        count++;
                        InstallLog.Write($"Running TDPdf blocks uninstall: pid={p.Id} path={path ?? "(unreadable)"}");
                    }
                }
            }
            return count;
        }

        // ============================================================
        // Uninstall
        // ============================================================

        /// <summary>
        /// Removes the detected install. Returns false only when the user declined the prompt.
        /// </summary>
        /// <remarks>
        /// <paramref name="owner"/> is set when a running (portable) TDPdf calls this from its UI: the
        /// prompts are then the themed dialogs, parented to that window. The <c>/uninstall</c> path
        /// keeps the stock MessageBox — it runs before any theme is loaded, and a themed dialog's
        /// window closing there would count as the app's last window closing.
        /// </remarks>
        private static bool Uninstall(bool silent = false, Window? owner = null)
        {
            MessageBoxResult Prompt(string message, string title, MessageBoxButton buttons, MessageBoxImage image) =>
                owner != null
                    ? TdpDialog.Show(owner, message, title, buttons, image)
                    : MessageBox.Show(message, title, buttons, image);

            if (!silent)
            {
                var res = Prompt(
                    "Uninstall TDPdf from this computer?",
                    $"{AppName} Uninstall",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);
                if (res != MessageBoxResult.Yes)
                {
                    InstallLog.Write("User cancelled uninstall");
                    return false;
                }
            }

            // Discover which install actually exists so we delete the right
            // files. The interactive Add/Remove Programs uninstall fires the
            // UninstallString elevated as the user (not SYSTEM), so we can't
            // rely on `IsSystemContextSafe()` here for a per-machine install.
            var scope = DetectInstalledScope() ?? InstallScope.PerUser;
            string installDir   = InstallDirFor(scope);
            string startMenuDir = StartMenuDirFor(scope);
            string startMenuLnk = StartMenuLnkFor(scope);

            InstallLog.Write($"Uninstall scope={scope} installDir={installDir} silent={silent}");

            // Refuse while TDPdf is running from the install folder, BEFORE removing anything.
            // Otherwise the registry and shortcuts go, the user is told it worked, and the
            // deferred delete then fails on the in-use exe, leaving a half-uninstalled folder.
            int running = CountRunningFrom(installDir);
            if (running > 0)
            {
                if (silent)
                {
                    // 1618: Intune retries it on its own, which is what a busy install folder needs.
                    throw new System.ComponentModel.Win32Exception(InstallerExitCodes.Busy,
                        $"TDPdf is still running ({running} process(es)); uninstall postponed.");
                }
                Prompt(running == 1
                        ? "TDPdf is still open. Close it, then uninstall again."
                        : "TDPdf is still open in other windows. Close all of them, then uninstall again.",
                    $"{AppName} Uninstall", MessageBoxButton.OK, MessageBoxImage.Warning);
                InstallLog.Write("Uninstall stopped: TDPdf is running");
                return false;
            }

            // Shortcuts
            try { File.Delete(startMenuLnk); } catch (Exception ex) { InstallLog.WriteError("Delete start menu lnk", ex); }
            try { Directory.Delete(startMenuDir, recursive: false); } catch { /* non-empty or already gone */ }
            // Only OUR desktop shortcut: a TDPdf.lnk the user made for something else stays.
            if (UninstallPolicy.IsInside(ShortcutTarget(DesktopLnk), installDir))
            {
                try { File.Delete(DesktopLnk); } catch (Exception ex) { InstallLog.WriteError("Delete desktop lnk", ex); }
            }
            else if (File.Exists(DesktopLnk))
            {
                InstallLog.Write("Desktop TDPdf.lnk kept: it does not point into the install folder");
            }

            // Registry cleanup — try both hives so we tear down whether the
            // install was machine-wide (HKLM) or per-user (HKCU).
            foreach (var root in new[] { Registry.LocalMachine, Registry.CurrentUser })
            {
                try { root.DeleteSubKeyTree(@"Software\TDPdf"); } catch { }
                try { root.DeleteSubKeyTree(
                    @"Software\Microsoft\Windows\CurrentVersion\Uninstall\TDPdf"); } catch { }
                try { root.DeleteSubKeyTree(@"Software\Classes\TDPdf.pdf"); } catch { }

                try
                {
                    using var k = root.OpenSubKey(
                        @"Software\Classes\.pdf\OpenWithProgids", writable: true);
                    k?.DeleteValue("TDPdf.pdf", throwOnMissingValue: false);
                }
                catch { }

                try
                {
                    using var k = root.OpenSubKey(
                        @"Software\RegisteredApplications", writable: true);
                    k?.DeleteValue(AppName, throwOnMissingValue: false);
                }
                catch { }
            }
            InstallLog.Write("Registry cleanup complete");

            SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, IntPtr.Zero, IntPtr.Zero);

            // Self-delete: deferred via cmd batch so the EXE can exit first. The script and its
            // command line carry no path (UninstallPolicy.DeferredDeleteScript says why); the
            // folder travels in an environment variable. The batch name is unique so nothing
            // pre-placed at a fixed %TEMP% path runs in its place.
            string dir = installDir.TrimEnd('\\');
            string batName = $"tdpdf_uninstall_{Guid.NewGuid():N}.bat";
            string tempDir = Path.GetTempPath();
            string bat = Path.Combine(tempDir, batName);
            File.WriteAllText(bat, UninstallPolicy.DeferredDeleteScript, Encoding.ASCII);

            // The message comes first and the batch starts after it, so the minute of retries is spent
            // after this process is about to exit rather than while a dialog nobody has clicked holds
            // the exe open.
            if (!silent)
            {
                Prompt("TDPdf has been uninstalled.", AppName,
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }

            var psi = new ProcessStartInfo("cmd.exe", UninstallPolicy.DeferredDeleteArguments(batName))
            {
                WorkingDirectory = tempDir,
                UseShellExecute  = false,   // required to pass the environment variable
                CreateNoWindow   = true,
            };
            psi.Environment[UninstallPolicy.DirVariable] = dir;
            Process.Start(psi);
            InstallLog.Write($"Scheduled deferred delete via {bat}");
            return true;
        }

        /// <summary>
        /// Uninstall offered from a portable copy's badge: the same routine and confirmation as
        /// <c>/uninstall</c>, run in this process, which keeps running afterwards (it is not inside
        /// the folder being removed). Only a per-user install — a machine-wide one is managed by the
        /// organisation. Returns true when TDPdf was uninstalled.
        /// </summary>
        internal static bool UninstallFromPortable(Window owner)
        {
            if (DetectInstalledScope() != InstallScope.PerUser)
            {
                InstallLog.Write("Portable uninstall refused: no per-user install");
                return false;
            }
            InstallLog.WriteHeader("UNINSTALL (from portable)", Array.Empty<string>());
            Telemetry.TrackEvent("Uninstall.Start", InstallProps(silent: false));
            try
            {
                bool done = Uninstall(silent: false, owner: owner);
                InstallLog.Write(done ? "UNINSTALL OK" : "UNINSTALL CANCELLED");
                if (done) Telemetry.TrackEvent("Uninstall.Success", InstallProps(silent: false));
                return done;
            }
            catch (Exception ex)
            {
                InstallLog.WriteError("UNINSTALL FAILED", ex);
                Telemetry.TrackCrash(ex, "Uninstall", recoverable: true);
                TdpDialog.Show(owner, $"Uninstall failed:\n{ex.Message}", AppName,
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }
        }
    }
}
