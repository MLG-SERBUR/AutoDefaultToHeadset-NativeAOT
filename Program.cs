using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Threading;
using System.Threading.Tasks;
// no WinForms for NativeAOT

namespace AutoDefaultToHeadset;

internal static partial class Program
{
    private static readonly string[] DefaultRenderMatches = Array.Empty<string>();
    private static readonly string[] DefaultCaptureMatches = Array.Empty<string>();

    private static readonly object LogLock = new();
    private const string InstanceMutexName = @"Local\AutoDefaultToHeadset";
    private const uint AttachParentProcess = 0xFFFFFFFF;
    private const int ErrorAccessDenied = 5;
    private const uint WmQuit = 0x0012;
    private const uint WmApplyDefaults = 0x8000;
    private const uint WmApplyFallbacks = WmApplyDefaults + 1;
    private const uint WmApplyVrFallbacks = WmApplyDefaults + 2;
    private const uint PmNoRemove = 0x0000;
    private static readonly TimeSpan ReplaceExistingTimeout = TimeSpan.FromSeconds(10);
    private static bool ConsoleAvailable;

    private enum EndpointAction
    {
        Primary,
        Fallback,
        VrFallback
    }

    [STAThread]
    private static int Main(string[] args)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, eventArgs) =>
        {
            if (eventArgs.ExceptionObject is Exception ex)
            {
                ShowFatalError("Unhandled domain exception.", ex);
            }
            else
            {
                ShowFatalError("Unhandled domain exception.", new Exception("Unknown exception object."));
            }
        };

        TaskScheduler.UnobservedTaskException += (_, eventArgs) =>
        {
            ShowFatalError("Unhandled task exception.", eventArgs.Exception);
            eventArgs.SetObserved();
        };

        Options options;

        try
        {
            options = Options.Parse(args);
        }
        catch (ArgumentException ex)
        {
            EnsureConsole();
            WriteError(ex.Message);
            PrintHelp();
            return 1;
        }

        if (options.ShowHelp)
        {
            EnsureConsole();
            PrintHelp();
            return 0;
        }

        InitializeConsole(options);

        try
        {
            using var controller = new AudioController(options);

            if (options.ListDevices)
            {
                controller.PrintDevices();
                return 0;
            }

            using var instanceMutex = new Mutex(true, InstanceMutexName, out var createdNew);
            var ownsMutex = createdNew;

            if (!ownsMutex)
            {
                if (!options.ReplaceExisting)
                {
                    WriteInfo("Another instance is already running.");
                    return 0;
                }

                if (!TryReplaceExistingInstance(instanceMutex))
                {
                    return 1;
                }

                ownsMutex = true;
            }

            try
            {
                WriteInfo("AutoDefaultToHeadset starting.");
                WriteInfo("PID: " + Environment.ProcessId.ToString(CultureInfo.InvariantCulture) + ". Logging: console only");
                WriteInfo("Render match: " + options.RenderDescription);
                WriteInfo("Capture match: " + options.CaptureDescription);
                WriteInfo("Fallback render: " + options.FallbackRenderDescription);
                WriteInfo("Fallback capture: " + options.FallbackCaptureDescription);
                WriteInfo("Disconnect render watch: " + options.DisconnectRenderDescription);
                WriteInfo("Args: " + string.Join(" ", args.Select(a => a.Contains(' ') ? "\"" + a + "\"" : a)));
                DumpSystemInfo();
                controller.DumpDiagnostics("startup");
                controller.ApplyDefaults("startup");
                controller.DumpDiagnostics("after startup apply");
                WriteInfo("Tip: This console is intentionally visible in --background so you can copy diagnostics. To hide, close this window and rely on Startup shortcut.");
                return RunEventLoop(controller);
            }
            finally
            {
                if (ownsMutex)
                {
                    try
                    {
                        instanceMutex.ReleaseMutex();
                    }
                    catch (ApplicationException)
                    {
                    }
                }
            }
        }
        catch (Exception ex)
        {
            ShowFatalError("Unhandled fatal error.", ex);
            return 1;
        }
    }

    private static int RunEventLoop(AudioController controller)
    {
        var threadId = NativeMethods.GetCurrentThreadId();
        ConsoleCancelEventHandler cancelHandler = (_, e) =>
        {
            e.Cancel = true;

            if (!NativeMethods.PostThreadMessage(threadId, WmQuit, UIntPtr.Zero, IntPtr.Zero))
            {
                WriteError("Failed to stop event loop. Win32 error: " + Marshal.GetLastWin32Error().ToString(CultureInfo.InvariantCulture));
            }
        };

        Console.CancelKeyPress += cancelHandler;
        NativeMethods.PeekMessage(out _, IntPtr.Zero, 0, 0, PmNoRemove);
        controller.RegisterNotifications();
        WriteInfo("Endpoint hook active. Waiting for headset connect. Press Ctrl+C to stop.");

        try
        {
            while (true)
            {
                var result = NativeMethods.GetMessage(out var message, IntPtr.Zero, 0, 0);
                if (result == 0)
                {
                    WriteInfo("Event loop stopped.");
                    return 0;
                }

                if (result == -1)
                {
                    WriteError("Event loop failed. Win32 error: " + Marshal.GetLastWin32Error().ToString(CultureInfo.InvariantCulture));
                    return 1;
                }

                if (message.Message == WmApplyDefaults)
                {
                    controller.ApplyDefaults("endpoint event");
                    continue;
                }

                if (message.Message == WmApplyFallbacks)
                {
                    controller.ApplyFallbacks("headset/VR disconnect");
                    continue;
                }

                if (message.Message == WmApplyVrFallbacks)
                {
                    controller.ApplyVrFallbacks();
                    continue;
                }

                NativeMethods.TranslateMessage(ref message);
                NativeMethods.DispatchMessage(ref message);
            }
        }
        finally
        {
            controller.UnregisterNotifications();
            Console.CancelKeyPress -= cancelHandler;
        }
    }

    private sealed class Options
    {
        public List<string> RenderMatches { get; } = new(DefaultRenderMatches);
        public List<string> CaptureMatches { get; } = new(DefaultCaptureMatches);
        public List<string> FallbackRenderMatches { get; } = new();
        public List<string> FallbackCaptureMatches { get; } = new();
        public List<string> DisconnectRenderMatches { get; } = new();
        public List<string> DisconnectCaptureMatches { get; } = new();
        public bool Background { get; private set; }
        public bool Verbose { get; private set; }
        public bool ReplaceExisting { get; private set; } = true;
        public bool ListDevices { get; private set; }
        public bool ShowHelp { get; private set; }

        public string RenderDescription => string.Join(", ", RenderMatches.Select(s => "'" + s + "'"));
        public string CaptureDescription => string.Join(", ", CaptureMatches.Select(s => "'" + s + "'"));
        public string FallbackRenderDescription => Describe(FallbackRenderMatches);
        public string FallbackCaptureDescription => Describe(FallbackCaptureMatches);
        public string DisconnectRenderDescription => Describe(DisconnectRenderMatches);
        public string DisconnectCaptureDescription => Describe(DisconnectCaptureMatches);

        public static Options Parse(string[] args)
        {
            var options = new Options();

            for (var i = 0; i < args.Length; i++)
            {
                var argument = args[i];

                switch (argument.ToLowerInvariant())
                {
                    case "--render-match":
                        options.RenderMatches.Clear();
                        options.RenderMatches.Add(RequireValue(args, ref i, argument));
                        break;
                    case "--capture-match":
                        options.CaptureMatches.Clear();
                        options.CaptureMatches.Add(RequireValue(args, ref i, argument));
                        break;
                    case "--fallback-render-match":
                        options.FallbackRenderMatches.Clear();
                        options.FallbackRenderMatches.Add(RequireValue(args, ref i, argument));
                        break;
                    case "--fallback-capture-match":
                        options.FallbackCaptureMatches.Clear();
                        options.FallbackCaptureMatches.Add(RequireValue(args, ref i, argument));
                        break;
                    case "--disconnect-render-match":
                        options.DisconnectRenderMatches.Add(RequireValue(args, ref i, argument));
                        break;
                    case "--disconnect-capture-match":
                        options.DisconnectCaptureMatches.Add(RequireValue(args, ref i, argument));
                        break;
                    case "--match":
                        var match = RequireValue(args, ref i, argument);
                        options.RenderMatches.Clear();
                        options.CaptureMatches.Clear();
                        options.RenderMatches.Add(match);
                        options.CaptureMatches.Add(match);
                        break;
                    case "--background":
                        options.Background = true;
                        break;
                    case "--verbose":
                        options.Verbose = true;
                        EnsureConsole();
                        break;
                    case "--list-devices":
                        options.ListDevices = true;
                        break;
                    case "--replace-existing":
                        options.ReplaceExisting = true;
                        break;
                    case "--exit-if-running":
                        options.ReplaceExisting = false;
                        break;
                    case "--help":
                    case "-h":
                    case "/?":
                        options.ShowHelp = true;
                        break;
                    default:
                        // legacy --render-id/--capture-id/--id no longer supported: exact name only
                        if (argument.Equals("--render-id", StringComparison.OrdinalIgnoreCase) ||
                            argument.Equals("--capture-id", StringComparison.OrdinalIgnoreCase) ||
                            argument.Equals("--id", StringComparison.OrdinalIgnoreCase))
                        {
                            throw new ArgumentException(argument + " no longer supported. Use --render-match / --capture-match with exact friendly name. Run install.bat to recreate the scheduled task.");
                        }
                        throw new ArgumentException("Unknown argument: " + argument);
                }
            }

            return options;
        }

        private static string Describe(IReadOnlyList<string> matches)
        {
            return matches.Count == 0 ? "(disabled)" : string.Join(", ", matches.Select(s => "'" + s + "'"));
        }

        private static string RequireValue(string[] args, ref int index, string argument)
        {
            if (index + 1 >= args.Length)
            {
                throw new ArgumentException(argument + " requires value.");
            }

            var value = args[++index];
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new ArgumentException(argument + " value cannot be empty.");
            }

            return value;
        }
    }

    private sealed class AudioController : IDisposable
    {
        private readonly Options _options;
        private readonly IMMDeviceEnumerator _enumerator;
        private readonly IPolicyConfig? _policyConfig;
        private readonly IPolicyConfigVista? _policyConfigVista;
        private readonly string _policySource;
        private readonly NotificationClient _notificationClient;
        private uint _eventThreadId;
        private bool _registered;
        private string? _lastRenderDefaultId;
        private string? _lastCaptureDefaultId;
        private CancellationTokenSource? _vrRetryCts;
        private static readonly TimeSpan SteamVrRetryDelay = TimeSpan.FromMinutes(11);

        public AudioController(Options options)
        {
            _options = options;
            _enumerator = CreateComInstance<IMMDeviceEnumerator>(ComIds.MMDeviceEnumerator, ComIds.IMMDeviceEnumerator);
            (_policyConfig, _policyConfigVista, _policySource) = CreatePolicyConfig();
            if (_policyConfig == null && _policyConfigVista == null)
            {
                WriteError("Failed to create PolicyConfig client. SetDefaultEndpoint will fail. HR lookup: try running as admin.");
            }
            else
            {
                WriteInfo("PolicyConfig client: " + _policySource);
            }
            _notificationClient = new NotificationClient(HandleDeviceStateChanged, HandleDeviceAdded, HandleDefaultDeviceChanged, RequestEndpointApply);
        }

        private static (IPolicyConfig? primary, IPolicyConfigVista? vista, string source) CreatePolicyConfig()
        {
            // Try primary client (Win7-11) first, then Vista client as fallback.
            try
            {
                var pc = CreateComInstance<IPolicyConfig>(ComIds.PolicyConfigClient, ComIds.IPolicyConfig);
                return (pc, null, "CPolicyConfigClient(870AF99C)+IPolicyConfig(F8679F50)");
            }
            catch (Exception ex)
            {
                WriteError("CPolicyConfigClient create failed: " + ex.Message);
            }

            try
            {
                var vista = CreateComInstance<IPolicyConfigVista>(ComIds.PolicyConfigVistaClient, ComIds.IPolicyConfigVista);
                return (null, vista, "CPolicyConfigVistaClient(294935CE)+IPolicyConfigVista(568B9108)");
            }
            catch (Exception ex)
            {
                WriteError("CPolicyConfigVistaClient create failed: " + ex.Message);
            }

            return (null, null, "none");
        }

        private static readonly ComWrappers ComWrappers = new StrategyBasedComWrappers();

        private static T CreateComInstance<T>(Guid classId, Guid interfaceId) where T : class
        {
            var hr = NativeMethods.CoCreateInstance(
                ref classId,
                IntPtr.Zero,
                NativeMethods.ClsctxInprocServer,
                ref interfaceId,
                out var interfacePointer);
            Marshal.ThrowExceptionForHR(hr);

            // ComWrappers takes ownership of the COM reference returned by CoCreateInstance.
            return (T)ComWrappers.GetOrCreateObjectForComInstance(interfacePointer, CreateObjectFlags.None);
        }

        public void RegisterNotifications()
        {
            if (_registered)
            {
                return;
            }

            _eventThreadId = NativeMethods.GetCurrentThreadId();
            _lastRenderDefaultId = GetDefaultDeviceId(EDataFlow.eRender, ERole.eConsole);
            _lastCaptureDefaultId = GetDefaultDeviceId(EDataFlow.eCapture, ERole.eConsole);
            Marshal.ThrowExceptionForHR(_enumerator.RegisterEndpointNotificationCallback(_notificationClient));
            _registered = true;
        }

        private void RequestEndpointApply(EndpointAction action)
        {
            var threadId = _eventThreadId;
            var message = action switch
            {
                EndpointAction.Primary => WmApplyDefaults,
                EndpointAction.Fallback => WmApplyFallbacks,
                _ => WmApplyVrFallbacks
            };
            if (threadId == 0 || !NativeMethods.PostThreadMessage(threadId, message, UIntPtr.Zero, IntPtr.Zero))
            {
                WriteError("Failed to queue endpoint apply. Win32 error: " + Marshal.GetLastWin32Error().ToString(CultureInfo.InvariantCulture));
            }
        }

        public void UnregisterNotifications()
        {
            if (!_registered)
            {
                return;
            }

            try
            {
                _enumerator.UnregisterEndpointNotificationCallback(_notificationClient);
            }
            catch
            {
            }

            _registered = false;
        }

        public void ApplyDefaults(string source)
        {
            try
            {
                LogCurrentDefaults("before " + source);

                for (var attempt = 0; attempt < 3; attempt++)
                {
                    var render = FindBestDevice(EDataFlow.eRender, _options.RenderMatches);
                    var capture = FindBestDevice(EDataFlow.eCapture, _options.CaptureMatches);

                    var didWork = false;

                    if (render != null)
                    {
                        WriteInfo("Matched render for " + source + " (attempt " + (attempt + 1) + "): " + render.Name + " [" + render.State + "] " + render.Id);
                        didWork |= SetDefaultForAllRoles(render, source);
                    }
                    else
                    {
                        WriteInfo("No active render device matched " + _options.RenderDescription + ". (attempt " + (attempt + 1) + ")");
                    }

                    if (capture != null)
                    {
                        WriteInfo("Matched capture for " + source + " (attempt " + (attempt + 1) + "): " + capture.Name + " [" + capture.State + "] " + capture.Id);
                        didWork |= SetDefaultForAllRoles(capture, source);
                    }
                    else
                    {
                        WriteInfo("No active capture device matched " + _options.CaptureDescription + ". (attempt " + (attempt + 1) + ")");
                    }

                    // verify after set
                    Thread.Sleep(250);
                    LogCurrentDefaults("after " + source + " attempt " + (attempt + 1));

                    // check if verification shows our device is now default
                    var renderOk = render == null || IsDefaultForAllRoles(render);
                    var captureOk = capture == null || IsDefaultForAllRoles(capture);

                    if (renderOk && captureOk)
                    {
                        break;
                    }

                    if (attempt < 2)
                    {
                        WriteInfo("Retrying apply " + source + " in 800ms (renderOk=" + renderOk + " captureOk=" + captureOk + ")");
                        Thread.Sleep(800);
                    }
                }
            }
            catch (Exception ex)
            {
                WriteError("Failed to apply defaults from " + source + ".", ex);
            }
        }

        public void ApplyFallbacks(string source)
        {
            try
            {
                var render = FindBestDevice(EDataFlow.eRender, _options.FallbackRenderMatches);
                var capture = FindBestDevice(EDataFlow.eCapture, _options.FallbackCaptureMatches);

                if (render != null)
                {
                    WriteInfo("Matched fallback render for " + source + ": " + render.Name + " [" + render.State + "] " + render.Id);
                    SetDefaultForAllRoles(render, source);
                }
                else if (_options.FallbackRenderMatches.Count > 0)
                {
                    WriteInfo("No active fallback render matched " + _options.FallbackRenderDescription + ".");
                }

                if (capture != null)
                {
                    WriteInfo("Matched fallback capture for " + source + ": " + capture.Name + " [" + capture.State + "] " + capture.Id);
                    SetDefaultForAllRoles(capture, source);
                }
                else if (_options.FallbackCaptureMatches.Count > 0)
                {
                    WriteInfo("No active fallback capture matched " + _options.FallbackCaptureDescription + ".");
                }
            }
            catch (Exception ex)
            {
                WriteError("Failed to apply fallback defaults from " + source + ".", ex);
            }
        }

        public void ApplyVrFallbacks()
        {
            if (IsSteamVrRunning())
            {
                WriteInfo("Virtual Desktop output left default, but vrserver.exe is running. Retrying fallback in 11 minutes.");
                ScheduleVrFallbackRetry();
                return;
            }

            ApplyFallbacks("Virtual Desktop disconnect after SteamVR exit");
        }

        private static bool IsSteamVrRunning()
        {
            foreach (var process in Process.GetProcessesByName("vrserver"))
            {
                process.Dispose();
                return true;
            }

            return false;
        }

        private void ScheduleVrFallbackRetry()
        {
            var previous = _vrRetryCts;
            var current = new CancellationTokenSource();
            _vrRetryCts = current;
            try { previous?.Cancel(); } catch { }
            previous?.Dispose();

            var token = current.Token;
            Task.Delay(SteamVrRetryDelay, token).ContinueWith(task =>
            {
                if (!task.IsCanceled) RequestEndpointApply(EndpointAction.VrFallback);
            }, TaskScheduler.Default);
        }

        private void CancelVrFallbackRetry()
        {
            var retry = _vrRetryCts;
            _vrRetryCts = null;
            try { retry?.Cancel(); } catch { }
            retry?.Dispose();
        }

        private EndpointAction? HandleDeviceStateChanged(string deviceId, DeviceState newState)
        {
            var name = TryGetFriendlyNameById(deviceId);
            if (name == null)
            {
                return null;
            }

            if (newState == DeviceState.Active && IsVrDisconnectSource(name))
            {
                CancelVrFallbackRetry();
                return null;
            }

            if (newState == DeviceState.Active && IsPrimaryDevice(name))
            {
                return EndpointAction.Primary;
            }

            // Windows can expose several endpoint IDs with one friendly name.
            // Ignore a non-active event while another endpoint with that name is active;
            // otherwise a connect burst can apply the headset and then its fallback.
            if (newState != DeviceState.Active && IsPrimaryDevice(name) && HasActiveEndpointNamed(name))
            {
                return null;
            }

            if (newState != DeviceState.Active && IsVrDisconnectSource(name))
            {
                return EndpointAction.VrFallback;
            }

            if (newState != DeviceState.Active && IsDisconnectSource(name))
            {
                return EndpointAction.Fallback;
            }

            return null;
        }

        private bool HasActiveEndpointNamed(string name)
        {
            try
            {
                return EnumerateDevices(EDataFlow.eRender, DeviceState.Active)
                           .Concat(EnumerateDevices(EDataFlow.eCapture, DeviceState.Active))
                           .Any(device => device.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            }
            catch
            {
                return false;
            }
        }

        private EndpointAction? HandleDeviceAdded(string deviceId)
        {
            var name = TryGetFriendlyNameById(deviceId);
            return name != null && IsPrimaryDevice(name) ? EndpointAction.Primary : null;
        }

        private EndpointAction? HandleDefaultDeviceChanged(EDataFlow flow, string? newDefaultDeviceId)
        {
            // A primary apply can intentionally move the default away from the
            // watched VR endpoint. Do not interpret that expected transition as
            // a VR disconnect and immediately apply the fallback.
            if (IsPrimaryDeviceId(newDefaultDeviceId, flow))
            {
                return null;
            }

            if (flow == EDataFlow.eRender)
            {
                var previous = _lastRenderDefaultId;
                _lastRenderDefaultId = newDefaultDeviceId;
                return IsDisconnectSourceId(previous, EDataFlow.eRender) &&
                       !IsVrDisconnectSourceId(previous, EDataFlow.eRender) ? EndpointAction.Fallback : null;
            }

            if (flow == EDataFlow.eCapture)
            {
                var previous = _lastCaptureDefaultId;
                _lastCaptureDefaultId = newDefaultDeviceId;
                return IsDisconnectSourceId(previous, EDataFlow.eCapture) &&
                       !IsVrDisconnectSourceId(previous, EDataFlow.eCapture) ? EndpointAction.Fallback : null;
            }

            return null;
        }

        private bool IsPrimaryDeviceId(string? deviceId, EDataFlow flow)
        {
            var name = deviceId == null ? null : TryGetFriendlyNameById(deviceId);
            return name != null &&
                   (flow == EDataFlow.eRender
                       ? Matches(name, _options.RenderMatches)
                       : Matches(name, _options.CaptureMatches));
        }

        private bool IsDisconnectSourceId(string? deviceId, EDataFlow flow)
        {
            var name = deviceId == null ? null : TryGetFriendlyNameById(deviceId);
            return name != null && IsDisconnectSource(name, flow);
        }

        private bool IsVrDisconnectSourceId(string? deviceId, EDataFlow flow)
        {
            var name = deviceId == null ? null : TryGetFriendlyNameById(deviceId);
            return name != null && IsVrDisconnectSource(name, flow);
        }

        private bool IsPrimaryDevice(string name)
        {
            return Matches(name, _options.RenderMatches) || Matches(name, _options.CaptureMatches);
        }

        private bool IsDisconnectSource(string name, EDataFlow? flow = null)
        {
            return (flow != EDataFlow.eCapture && (Matches(name, _options.RenderMatches) || Matches(name, _options.DisconnectRenderMatches))) ||
                   (flow != EDataFlow.eRender && (Matches(name, _options.CaptureMatches) || Matches(name, _options.DisconnectCaptureMatches)));
        }

        private bool IsVrDisconnectSource(string name, EDataFlow? flow = null)
        {
            return (flow != EDataFlow.eCapture && Matches(name, _options.DisconnectRenderMatches)) ||
                   (flow != EDataFlow.eRender && Matches(name, _options.DisconnectCaptureMatches));
        }

        private static bool Matches(string name, IReadOnlyList<string> matches)
        {
            return matches.Any(match => name.Equals(match, StringComparison.OrdinalIgnoreCase));
        }

        private void LogCurrentDefaults(string context)
        {
            foreach (var flow in new[] { EDataFlow.eRender, EDataFlow.eCapture })
            {
                foreach (var role in new[] { ERole.eConsole, ERole.eMultimedia, ERole.eCommunications })
                {
                    var id = GetDefaultDeviceId(flow, role);
                    var name = id != null ? TryGetFriendlyNameById(id) : "(none)";
                    WriteInfo("Default " + context + ": " + flow + " " + role + " = " + (name ?? id ?? "(null)") + " [" + (id ?? "null") + "]");
                }
            }
        }

        private string? GetDefaultDeviceId(EDataFlow flow, ERole role)
        {
            try
            {
                var hr = _enumerator.GetDefaultAudioEndpoint(flow, role, out var device);
                if (hr != 0 || device == null)
                {
                    return null;
                }
                device.GetId(out var id);
                return id;
            }
            catch
            {
                return null;
            }
        }

        private bool IsDefault(string id, EDataFlow flow, ERole role)
        {
            var def = GetDefaultDeviceId(flow, role);
            return def != null && string.Equals(def, id, StringComparison.OrdinalIgnoreCase);
        }

        private string? TryGetFriendlyNameById(string id)
        {
            try
            {
                if (_enumerator.GetDevice(id, out var dev) != 0) return null;
                return GetFriendlyName(dev);
            }
            catch { return null; }
        }

        public void DumpDiagnostics(string context)
        {
            try
            {
                WriteInfo("=== Audio Diagnostics: " + context + " ===");
                foreach (var flow in new[] { EDataFlow.eRender, EDataFlow.eCapture })
                {
                    var label = flow == EDataFlow.eRender ? "Output" : "Input";
                    WriteInfo("-- " + label + " (" + flow + ") all states --");
                    var all = EnumerateDevices(flow, DeviceState.All);
                    if (all.Count == 0) WriteInfo("  (no devices)");
                    foreach (var d in all.OrderBy(x => x.State).ThenBy(x => x.Name))
                    {
                        var defaults = "";
                        foreach (var role in new[] { ERole.eConsole, ERole.eMultimedia, ERole.eCommunications })
                        {
                            if (IsDefault(d.Id, flow, role)) defaults += " [DEFAULT " + role + "]";
                        }
                        if (defaults.Length == 0 && d.State != DeviceState.Active) defaults = "";
                        WriteInfo("  [" + d.State + "]" + defaults + " " + d.Name);
                        WriteInfo("       Id: " + d.Id);
                    }
                    // defaults per role summary
                    foreach (var role in new[] { ERole.eConsole, ERole.eMultimedia, ERole.eCommunications })
                    {
                        var defId = GetDefaultDeviceId(flow, role);
                        var defName = defId != null ? TryGetFriendlyNameById(defId) ?? "(unnamed)" : "(none)";
                        WriteInfo("  Default " + flow + " " + role + ": " + defName + " [" + (defId ?? "null") + "]");
                    }
                }
                var render = FindBestDevice(EDataFlow.eRender, _options.RenderMatches);
                var capture = FindBestDevice(EDataFlow.eCapture, _options.CaptureMatches);
                WriteInfo("Match render: " + (render != null ? render.Name + " [" + render.State + "] " + render.Id : "NONE for " + _options.RenderDescription));
                WriteInfo("Match capture: " + (capture != null ? capture.Name + " [" + capture.State + "] " + capture.Id : "NONE for " + _options.CaptureDescription));
                WriteInfo("=== End Audio Diagnostics ===");
            }
            catch (Exception ex) { WriteError("DumpDiagnostics failed", ex); }
        }

        public void PrintDevices()
        {
            PrintDevices(EDataFlow.eRender, "Output");
            PrintDevices(EDataFlow.eCapture, "Input");
        }

        private void PrintDevices(EDataFlow flow, string label)
        {
            EnsureConsole();
            Console.WriteLine(label + " devices:");

            var devices = EnumerateDevices(flow, DeviceState.All);
            foreach (var device in devices)
            {
                var marker = "";
                foreach (var role in new[] { ERole.eConsole, ERole.eMultimedia, ERole.eCommunications })
                {
                    if (IsDefault(device.Id, flow, role))
                    {
                        marker += " [DEFAULT:" + role + "]";
                    }
                }
                Console.WriteLine("  State: " + device.State + marker);
                Console.WriteLine("  Name:  " + device.Name);
                Console.WriteLine("  Id:    " + device.Id);
                Console.WriteLine();
            }

            // also dump current defaults per role
            Console.WriteLine("Current defaults (" + label + "):");
            foreach (var role in new[] { ERole.eConsole, ERole.eMultimedia, ERole.eCommunications })
            {
                var defId = GetDefaultDeviceId(flow, role);
                Console.WriteLine("  " + role + ": " + (defId ?? "(none)") + " -> " + (defId != null ? TryGetFriendlyNameById(defId) : ""));
            }
            Console.WriteLine();
        }

        private AudioDevice? FindBestDevice(EDataFlow flow, IReadOnlyList<string> matches)
        {
            var devices = EnumerateDevices(flow, DeviceState.Active);
            // exact only, no substring, no fallback, no tiebreaker bonus
            return devices.FirstOrDefault(device => matches.Any(match => device.Name.Equals(match, StringComparison.OrdinalIgnoreCase)));
        }

        private List<AudioDevice> EnumerateDevices(EDataFlow flow, DeviceState stateMask)
        {
            Marshal.ThrowExceptionForHR(_enumerator.EnumAudioEndpoints(flow, stateMask, out var collection));
            Marshal.ThrowExceptionForHR(collection.GetCount(out var count));

            var devices = new List<AudioDevice>();
            for (var i = 0u; i < count; i++)
            {
                Marshal.ThrowExceptionForHR(collection.Item(i, out var device));
                Marshal.ThrowExceptionForHR(device.GetId(out var id));
                Marshal.ThrowExceptionForHR(device.GetState(out var state));

                devices.Add(new AudioDevice(id, GetFriendlyName(device), state, flow));
            }

            return devices;
        }

        private static unsafe string GetFriendlyName(IMMDevice device)
        {
            try
            {
                Marshal.ThrowExceptionForHR(device.OpenPropertyStore(StorageAccessMode.Read, out var propertyStore));
                using var property = new PropVariantScope();
                Marshal.ThrowExceptionForHR(propertyStore.GetValue(PropertyKeys.DeviceFriendlyName, property.Value));
                var name = Marshal.PtrToStringUni(property.Value->PointerValue);
                return string.IsNullOrWhiteSpace(name) ? "(unnamed)" : name;
            }
            catch
            {
                return "(unnamed)";
            }
        }

        private bool SetDefaultForAllRoles(AudioDevice device, string source)
        {
            var changed = false;
            foreach (var role in new[] { ERole.eConsole, ERole.eMultimedia, ERole.eCommunications })
            {
                if (IsDefault(device.Id, device.Flow, role))
                {
                    continue;
                }

                changed |= SetDefault(device, role, source);
            }

            return changed;
        }

        private bool IsDefaultForAllRoles(AudioDevice device)
        {
            return IsDefault(device.Id, device.Flow, ERole.eConsole) &&
                   IsDefault(device.Id, device.Flow, ERole.eMultimedia) &&
                   IsDefault(device.Id, device.Flow, ERole.eCommunications);
        }

        private bool SetDefault(AudioDevice device, ERole role, string source)
        {
            int hr;
            string which;
            if (_policyConfig != null)
            {
                hr = _policyConfig.SetDefaultEndpoint(device.Id, role);
                which = "IPolicyConfig";
            }
            else if (_policyConfigVista != null)
            {
                hr = _policyConfigVista.SetDefaultEndpoint(device.Id, role);
                which = "IPolicyConfigVista";
            }
            else
            {
                WriteError("No PolicyConfig available to set " + device.Flow + " " + role + ". Device: " + device.Name);
                return false;
            }

            // Fallback: if primary failed, try the other client once
            if (hr != 0 && _policyConfig != null && _policyConfigVista != null)
            {
                // already tried primary; try vista as second attempt (should not happen since we only have one)
            }
            else if (hr != 0 && _policyConfig == null && _policyConfigVista != null)
            {
                // no fallback needed
            }

            if (hr == 0)
            {
                // verify
                Thread.Sleep(80);
                var isDef = IsDefault(device.Id, device.Flow, role);
                WriteInfo("Set " + device.Flow + " " + role + " from " + source + " via " + which + ": " + device.Name + " HR=0x00000000 verify=" + (isDef ? "OK" : "MISMATCH"));
                if (!isDef)
                {
                    var cur = GetDefaultDeviceId(device.Flow, role);
                    WriteError("Verify failed after SetDefaultEndpoint " + role + ". Expected " + device.Id + " got " + (cur ?? "null") + ". Try running as admin. HR=0x" + hr.ToString("X8", CultureInfo.InvariantCulture));
                }
                return isDef;
            }

            var msg = GetHrMessage(hr);
            WriteError("Failed to set " + device.Flow + " " + role + " via " + which + ". HRESULT: 0x" + hr.ToString("X8", CultureInfo.InvariantCulture) + " (" + msg + "). Device: " + device.Name + " Id: " + device.Id);
            if ((uint)hr == 0x80070005)
            {
                WriteError("Access denied (0x80070005) - try Run as Administrator or Task Scheduler with /RL HIGHEST.");
            }
            if ((uint)hr == 0x80070490) // ERROR_NOT_FOUND
            {
                WriteError("Device not found - device may not be ready yet or Id changed.");
            }
            return false;
        }

        private static string GetHrMessage(int hr)
        {
            try
            {
                var ex = Marshal.GetExceptionForHR(hr);
                return ex?.Message ?? "unknown";
            }
            catch { return "unknown"; }
        }

        public void Dispose()
        {
            UnregisterNotifications();
            CancelVrFallbackRetry();
        }
    }

    [GeneratedComClass]
    private sealed partial class NotificationClient : IMMNotificationClient
    {
        private readonly Func<string, DeviceState, EndpointAction?> _onDeviceStateChanged;
        private readonly Func<string, EndpointAction?> _onDeviceAdded;
        private readonly Func<EDataFlow, string?, EndpointAction?> _onDefaultDeviceChanged;
        private readonly Action<EndpointAction> _onApply;
        private readonly object _gate = new();
        private DateTimeOffset _lastRun = DateTimeOffset.MinValue;
        private CancellationTokenSource? _pendingCts;
        // Coalesce burst events (headset often fires render + capture within ~300ms)
        // Leading edge fires immediately, trailing edge fires 700ms after last burst event
        private static readonly TimeSpan CoalesceWindow = TimeSpan.FromMilliseconds(700);
        private static readonly TimeSpan MinInterval = TimeSpan.FromMilliseconds(250);

        public NotificationClient(
            Func<string, DeviceState, EndpointAction?> onDeviceStateChanged,
            Func<string, EndpointAction?> onDeviceAdded,
            Func<EDataFlow, string?, EndpointAction?> onDefaultDeviceChanged,
            Action<EndpointAction> onApply)
        {
            _onDeviceStateChanged = onDeviceStateChanged;
            _onDeviceAdded = onDeviceAdded;
            _onDefaultDeviceChanged = onDefaultDeviceChanged;
            _onApply = onApply;
        }

        public int OnDeviceStateChanged([MarshalAs(UnmanagedType.LPWStr)] string deviceId, DeviceState newState)
        {
            WriteInfo("Notify OnDeviceStateChanged: " + newState + " id=" + deviceId);
            var fallback = _onDeviceStateChanged(deviceId, newState);
            if (fallback.HasValue) RunDebounced(fallback.Value);
            return 0;
        }

        public int OnDeviceAdded([MarshalAs(UnmanagedType.LPWStr)] string deviceId)
        {
            WriteInfo("Notify OnDeviceAdded: id=" + deviceId);
            var fallback = _onDeviceAdded(deviceId);
            if (fallback.HasValue) RunDebounced(fallback.Value);
            return 0;
        }

        public int OnDeviceRemoved([MarshalAs(UnmanagedType.LPWStr)] string deviceId)
        {
            WriteInfo("Notify OnDeviceRemoved: id=" + deviceId);
            return 0;
        }

        public int OnDefaultDeviceChanged(EDataFlow flow, ERole role, [MarshalAs(UnmanagedType.LPWStr)] string? defaultDeviceId)
        {
            var fallback = _onDefaultDeviceChanged(flow, defaultDeviceId);
            if (fallback.HasValue) RunDebounced(fallback.Value);
            return 0;
        }

        public int OnPropertyValueChanged([MarshalAs(UnmanagedType.LPWStr)] string deviceId, ref PropertyKey key)
        {
            WriteInfo("Notify OnPropertyValueChanged: id=" + deviceId);
            return 0;
        }

        private void RunDebounced(EndpointAction action)
        {
            bool shouldFireImmediately = false;
            CancellationTokenSource? ctsToCancel = null;
            CancellationTokenSource? newCts = null;

            lock (_gate)
            {
                var now = DateTimeOffset.UtcNow;
                if (now - _lastRun >= MinInterval)
                {
                    // leading edge
                    _lastRun = now;
                    shouldFireImmediately = true;
                }

                // always schedule trailing edge to catch burst second device
                ctsToCancel = _pendingCts;
                newCts = new CancellationTokenSource();
                _pendingCts = newCts;
            }

            try { ctsToCancel?.Cancel(); } catch { }
            try { ctsToCancel?.Dispose(); } catch { }

            if (shouldFireImmediately)
            {
                try { RequestApply(action); } catch (Exception ex) { WriteError("RunDebounced immediate failed", ex); }
            }

            // trailing edge after CoalesceWindow - ensures second device in burst gets applied
            var token = newCts.Token;
            Task.Delay(CoalesceWindow, token).ContinueWith(t =>
            {
                if (t.IsCanceled) return;
                lock (_gate)
                {
                    // if another burst already updated _lastRun recently, we still fire trailing to ensure both render+capture handled
                }
                try { RequestApply(action); } catch (Exception ex) { WriteError("RunDebounced trailing failed", ex); }
            }, TaskScheduler.Default);
        }

        private void RequestApply(EndpointAction action)
        {
            _onApply(action);
        }
    }

    private static void InitializeConsole(Options options)
    {
        // NativeAOT WinExe: no console by default (lowest memory, no conhost, hidden)
        // Use --verbose to AllocConsole for diagnostics (paste-able)
        if (options.Verbose)
        {
            EnsureConsole();
            WriteInfo("Verbose console allocated");
        }
        else if (options.Background)
        {
            // background hidden, no console
        }
        else
        {
            EnsureConsole();
        }
    }

    private static void EnsureConsole()
    {
        if (ConsoleAvailable)
        {
            return;
        }

        if (NativeMethods.AttachConsole(AttachParentProcess))
        {
            ConsoleAvailable = true;
            RefreshConsoleStreams();
            return;
        }

        var error = Marshal.GetLastWin32Error();
        if (error == ErrorAccessDenied)
        {
            ConsoleAvailable = true;
            RefreshConsoleStreams();
            return;
        }

        if (NativeMethods.AllocConsole())
        {
            ConsoleAvailable = true;
            RefreshConsoleStreams();
        }
    }

    private static void RefreshConsoleStreams()
    {
        Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
        Console.SetError(new StreamWriter(Console.OpenStandardError()) { AutoFlush = true });
    }

    private static void DumpSystemInfo()
    {
        try
        {
            WriteInfo("=== System Info ===");
            WriteInfo("OSVersion: " + Environment.OSVersion.ToString());
            try { WriteInfo("Runtime OSDescription: " + System.Runtime.InteropServices.RuntimeInformation.OSDescription); } catch { }
            try { WriteInfo("64-bit OS: " + Environment.Is64BitOperatingSystem + " 64-bit Proc: " + Environment.Is64BitProcess); } catch { }
            try
            {
                var winVer = Microsoft.Win32.Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion", "DisplayVersion", null);
                var build = Microsoft.Win32.Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion", "CurrentBuild", null);
                var ubr = Microsoft.Win32.Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion", "UBR", null);
                WriteInfo("DisplayVersion: " + (winVer ?? "n/a") + " Build: " + (build ?? "n/a") + " UBR: " + (ubr ?? "n/a"));
            }
            catch (Exception ex) { WriteInfo("Registry read failed: " + ex.Message); }
            try
            {
                using var id = System.Security.Principal.WindowsIdentity.GetCurrent();
                var p = new System.Security.Principal.WindowsPrincipal(id);
                var isAdmin = p.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
                WriteInfo("Admin: " + isAdmin + " User: " + id.Name);
            }
            catch (Exception ex) { WriteInfo("Admin check failed: " + ex.Message); }
            WriteInfo("Exe: " + (Environment.ProcessPath ?? "n/a"));
            WriteInfo("Startup shortcut: " + Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "AutoDefaultToHeadset.lnk"));
            try
            {
                var startup = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "AutoDefaultToHeadset.lnk");
                if (File.Exists(startup)) WriteInfo("Startup .lnk exists: " + startup);
                else WriteInfo("Startup .lnk NOT found");
            }
            catch { }
            WriteInfo("=== End System Info ===");
        }
        catch (Exception ex) { WriteError("DumpSystemInfo failed", ex); }
    }

    private static bool TryReplaceExistingInstance(Mutex instanceMutex)
    {
        using var currentProcess = Process.GetCurrentProcess();
        var currentProcessPath = GetProcessPath(currentProcess);
        var replaceableProcesses = FindReplaceableProcesses(currentProcess.Id, currentProcessPath);

        try
        {
            if (replaceableProcesses.Count == 0)
            {
                WriteInfo("Another instance is already running. Waiting for it to exit so this copy can take over.");
            }

            foreach (var process in replaceableProcesses)
            {
                if (!TryTerminateProcess(process))
                {
                    return false;
                }
            }

            var deadline = DateTimeOffset.UtcNow + ReplaceExistingTimeout;
            foreach (var process in replaceableProcesses)
            {
                if (!WaitForProcessExit(process, deadline))
                {
                    return false;
                }
            }

            if (WaitForMutexOwnership(instanceMutex, deadline))
            {
                WriteInfo("Existing switcher instance replaced.");
                return true;
            }

            WriteError("Timed out waiting for previous switcher instance to release single-instance lock.");
            return false;
        }
        finally
        {
            foreach (var process in replaceableProcesses)
            {
                process.Dispose();
            }
        }
    }

    private static List<Process> FindReplaceableProcesses(int currentProcessId, string currentProcessPath)
    {
        var matches = new List<Process>();
        var seenProcessIds = new HashSet<int>();

        if (string.IsNullOrWhiteSpace(currentProcessPath))
        {
            return matches;
        }

        var currentProcessName = Path.GetFileNameWithoutExtension(currentProcessPath);
        if (string.IsNullOrWhiteSpace(currentProcessName))
        {
            return matches;
        }

        foreach (var process in Process.GetProcessesByName(currentProcessName))
        {
            if (process.Id == currentProcessId || !seenProcessIds.Add(process.Id))
            {
                process.Dispose();
                continue;
            }

            var processPath = GetProcessPath(process);
            if (!string.Equals(processPath, currentProcessPath, StringComparison.OrdinalIgnoreCase))
            {
                process.Dispose();
                continue;
            }

            matches.Add(process);
        }

        return matches;
    }

    private static bool TryTerminateProcess(Process process)
    {
        try
        {
            if (process.HasExited)
            {
                return true;
            }

            WriteInfo("Stopping running switcher instance PID " + process.Id.ToString(CultureInfo.InvariantCulture) + ".");
            process.Kill(entireProcessTree: true);
            return true;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
        catch (Exception ex)
        {
            WriteError("Failed to stop running switcher instance PID " + process.Id.ToString(CultureInfo.InvariantCulture) + ".", ex);
            return false;
        }
    }

    private static bool WaitForProcessExit(Process process, DateTimeOffset deadline)
    {
        try
        {
            if (process.HasExited)
            {
                return true;
            }

            var remaining = deadline - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                return false;
            }

            if (process.WaitForExit((int)Math.Ceiling(remaining.TotalMilliseconds)))
            {
                return true;
            }

            WriteError("Timed out waiting for switcher instance PID " + process.Id.ToString(CultureInfo.InvariantCulture) + " to exit.");
            return false;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
        catch (Exception ex)
        {
            WriteError("Failed while waiting for switcher instance PID " + process.Id.ToString(CultureInfo.InvariantCulture) + ".", ex);
            return false;
        }
    }

    private static bool WaitForMutexOwnership(Mutex instanceMutex, DateTimeOffset deadline)
    {
        var remaining = deadline - DateTimeOffset.UtcNow;
        if (remaining <= TimeSpan.Zero)
        {
            return false;
        }

        try
        {
            return instanceMutex.WaitOne(remaining);
        }
        catch (AbandonedMutexException)
        {
            return true;
        }
    }

    private static string GetProcessPath(Process process)
    {
        try
        {
            return process.MainModule?.FileName ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static void PrintHelp()
    {
        EnsureConsole();
        Console.WriteLine("Usage: AutoDefaultToHeadset.exe [options]");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  --list-devices          List output/input endpoint names and ids, then exit.");
        Console.WriteLine("  --match <text>          Match same exact friendly name for output and input.");
        Console.WriteLine("  --render-match <text>   Match active output device by exact friendly name (case-insensitive).");
        Console.WriteLine("  --capture-match <text>  Match active input device by exact friendly name.");
        Console.WriteLine("  --fallback-render-match <text>  Set this output when a watched device disconnects.");
        Console.WriteLine("  --fallback-capture-match <text> Set this input when a watched device disconnects.");
        Console.WriteLine("  --disconnect-render-match <text> Watch extra output, e.g. Virtual Desktop Audio.");
        Console.WriteLine("  --disconnect-capture-match <text> Watch extra input device.");
        Console.WriteLine("  --background            Run without opening a console window (NativeAOT: hidden by default).");
        Console.WriteLine("  --verbose               Allocate console for diagnostics (NativeAOT WinExe).");
        Console.WriteLine("  --replace-existing      Replace a running switcher instance (default).");
        Console.WriteLine("  --exit-if-running       Exit instead of replacing an existing switcher instance.");
        Console.WriteLine("  --help                  Show this help.");
    }

    private static void WriteInfo(string message)
    {
        WriteLog("INFO", message);
    }

    private static void WriteError(string message)
    {
        WriteLog("ERROR", message);
    }

    private static void WriteError(string message, Exception ex)
    {
        WriteLog("ERROR", message + " " + ex.GetType().Name + ": " + ex.Message);
    }

    private static void ShowFatalError(string message, Exception ex)
    {
        var text = string.Format(
            CultureInfo.InvariantCulture,
            "{0}\r\n\r\n{1}: {2}\r\nHResult: 0x{3:X8}\r\n\r\n{4}",
            message,
            ex.GetType().FullName,
            ex.Message,
            ex.HResult,
            ex.StackTrace);

        WriteLog("ERROR", text);
        try { NativeMethods.MessageBoxW(IntPtr.Zero, text, "AutoDefaultToHeadset fatal error", 0x00000010u); } catch { }
    }

    private static void WriteLog(string level, string message)
    {
        var line = string.Format(CultureInfo.InvariantCulture, "{0:yyyy-MM-dd HH:mm:ss} [{1}] {2}", DateTime.Now, level, message);

        lock (LogLock)
        {
            try
            {
                // keep lock for console ordering; file logging could be added here
                System.Diagnostics.Debug.WriteLine(line);
            }
            catch
            {
            }
        }

        try
        {
            if (ConsoleAvailable)
            {
                Console.WriteLine(line);
            }
            else
            {
                // background mode: still try to write to attached parent if any
                try { Console.WriteLine(line); } catch { }
            }
        }
        catch
        {
        }
    }
}

internal sealed unsafe class PropVariantScope : IDisposable
{
    public PropVariant* Value { get; }

    public PropVariantScope()
    {
        Value = (PropVariant*)NativeMemory.AllocZeroed((nuint)sizeof(PropVariant));
    }

    public void Dispose()
    {
        if (Value != null)
        {
            NativeMethods.PropVariantClear((IntPtr)Value);
            NativeMemory.Free(Value);
        }
    }
}

internal sealed record AudioDevice(string Id, string Name, DeviceState State, EDataFlow Flow);

[StructLayout(LayoutKind.Sequential)]
internal struct Point
{
    public int X;
    public int Y;
}

[StructLayout(LayoutKind.Sequential)]
internal struct Msg
{
    public IntPtr Hwnd;
    public uint Message;
    public UIntPtr WParam;
    public IntPtr LParam;
    public uint Time;
    public Point Pt;
    public uint LPrivate;
}

[StructLayout(LayoutKind.Sequential)]
internal struct PropertyKey
{
    public Guid FormatId;
    public uint PropertyId;

    public PropertyKey(Guid formatId, uint propertyId)
    {
        FormatId = formatId;
        PropertyId = propertyId;
    }
}

internal static class PropertyKeys
{
    public static readonly PropertyKey DeviceFriendlyName = new(new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"), 14);
}

[StructLayout(LayoutKind.Sequential)]
internal struct PropVariant
{
    public ushort ValueType;
    public ushort Reserved1;
    public ushort Reserved2;
    public ushort Reserved3;
    public IntPtr PointerValue;
}

[Flags]
internal enum DeviceState : uint
{
    Active = 0x00000001,
    Disabled = 0x00000002,
    NotPresent = 0x00000004,
    Unplugged = 0x00000008,
    All = 0x0000000F
}

internal enum EDataFlow
{
    eRender,
    eCapture,
    eAll
}

internal enum ERole
{
    eConsole,
    eMultimedia,
    eCommunications
}

internal enum StorageAccessMode
{
    Read,
    Write,
    ReadWrite
}

internal static class ComIds
{
    public static readonly Guid MMDeviceEnumerator = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    public static readonly Guid IMMDeviceEnumerator = new("A95664D2-9614-4F35-A746-DE8DB63617E6");
    public static readonly Guid PolicyConfigClient = new("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9");
    public static readonly Guid PolicyConfigVistaClient = new("294935CE-F637-4E7C-A41B-AB255460B862");
    public static readonly Guid IPolicyConfig = new("F8679F50-850A-41CF-9C72-430F290290C8");
    public static readonly Guid IPolicyConfigVista = new("568B9108-44BF-40B4-9006-86AFE5B5A620");
}

[GeneratedComInterface]
[Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal partial interface IMMDeviceEnumerator
{
    [PreserveSig]
    int EnumAudioEndpoints(EDataFlow dataFlow, DeviceState stateMask, out IMMDeviceCollection devices);

    [PreserveSig]
    int GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out IMMDevice endpoint);

    [PreserveSig]
    int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);

    [PreserveSig]
    int RegisterEndpointNotificationCallback(IMMNotificationClient client);

    [PreserveSig]
    int UnregisterEndpointNotificationCallback(IMMNotificationClient client);
}

[GeneratedComInterface]
[Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal partial interface IMMDeviceCollection
{
    [PreserveSig]
    int GetCount(out uint count);

    [PreserveSig]
    int Item(uint index, out IMMDevice device);
}

[GeneratedComInterface]
[Guid("D666063F-1587-4E43-81F1-B948E807363F")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal partial interface IMMDevice
{
    [PreserveSig]
    int Activate(ref Guid iid, uint clsCtx, IntPtr activationParams, out IntPtr interfacePointer);

    [PreserveSig]
    int OpenPropertyStore(StorageAccessMode accessMode, out IPropertyStore properties);

    [PreserveSig]
    int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);

    [PreserveSig]
    int GetState(out DeviceState state);
}

[GeneratedComInterface]
[Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal unsafe partial interface IPropertyStore
{
    [PreserveSig]
    int GetCount(out uint propertyCount);

    [PreserveSig]
    int GetAt(uint propertyIndex, out PropertyKey key);

    [PreserveSig]
    int GetValue(PropertyKey key, PropVariant* value);

    [PreserveSig]
    int SetValue(PropertyKey key, PropVariant* value);

    [PreserveSig]
    int Commit();
}

[GeneratedComInterface]
[Guid("7991EEC9-7E89-4D85-8390-6C703CEC60C0")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal partial interface IMMNotificationClient
{
    [PreserveSig]
    int OnDeviceStateChanged([MarshalAs(UnmanagedType.LPWStr)] string deviceId, DeviceState newState);

    [PreserveSig]
    int OnDeviceAdded([MarshalAs(UnmanagedType.LPWStr)] string deviceId);

    [PreserveSig]
    int OnDeviceRemoved([MarshalAs(UnmanagedType.LPWStr)] string deviceId);

    [PreserveSig]
    int OnDefaultDeviceChanged(EDataFlow flow, ERole role, [MarshalAs(UnmanagedType.LPWStr)] string? defaultDeviceId);

    [PreserveSig]
    int OnPropertyValueChanged([MarshalAs(UnmanagedType.LPWStr)] string deviceId, ref PropertyKey key);
}

[GeneratedComInterface]
[Guid("F8679F50-850A-41CF-9C72-430F290290C8")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal partial interface IPolicyConfig
{
    [PreserveSig]
    int GetMixFormat([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, IntPtr ppFormat);

    [PreserveSig]
    int GetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, int bDefault, IntPtr ppFormat);

    [PreserveSig]
    int ResetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName);

    [PreserveSig]
    int SetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, IntPtr pEndpointFormat, IntPtr MixFormat);

    [PreserveSig]
    int GetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, int bDefault, IntPtr pmftDefaultPeriod, IntPtr pmftMinimumPeriod);

    [PreserveSig]
    int SetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, IntPtr pmftPeriod);

    [PreserveSig]
    int GetShareMode([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, IntPtr pMode);

    [PreserveSig]
    int SetShareMode([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, IntPtr mode);

    [PreserveSig]
    int GetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, [MarshalAs(UnmanagedType.Bool)] bool bFxStore, IntPtr key, IntPtr pv);

    [PreserveSig]
    int SetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, [MarshalAs(UnmanagedType.Bool)] bool bFxStore, IntPtr key, IntPtr pv);

    [PreserveSig]
    int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceId, ERole role);

    [PreserveSig]
    int SetEndpointVisibility([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, [MarshalAs(UnmanagedType.Bool)] bool bVisible);
}

[GeneratedComInterface]
[Guid("568B9108-44BF-40B4-9006-86AFE5B5A620")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal partial interface IPolicyConfigVista
{
    [PreserveSig]
    int GetMixFormat([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, IntPtr ppFormat);

    [PreserveSig]
    int GetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, int bDefault, IntPtr ppFormat);

    [PreserveSig]
    int SetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, IntPtr pEndpointFormat, IntPtr MixFormat);

    [PreserveSig]
    int GetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, int bDefault, IntPtr pmftDefaultPeriod, IntPtr pmftMinimumPeriod);

    [PreserveSig]
    int SetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, IntPtr pmftPeriod);

    [PreserveSig]
    int GetShareMode([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, IntPtr pMode);

    [PreserveSig]
    int SetShareMode([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, IntPtr mode);

    [PreserveSig]
    int GetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, [MarshalAs(UnmanagedType.Bool)] bool bFxStore, IntPtr key, IntPtr pv);

    [PreserveSig]
    int SetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, [MarshalAs(UnmanagedType.Bool)] bool bFxStore, IntPtr key, IntPtr pv);

    [PreserveSig]
    int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceId, ERole role);

    [PreserveSig]
    int SetEndpointVisibility([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, [MarshalAs(UnmanagedType.Bool)] bool bVisible);
}

internal static class NativeMethods
{
    public const uint ClsctxInprocServer = 0x1;

    [DllImport("ole32.dll")]
    public static extern int CoCreateInstance(
        ref Guid rclsid,
        IntPtr pUnkOuter,
        uint dwClsContext,
        ref Guid riid,
        out IntPtr ppv);

    [DllImport("kernel32.dll")]
    public static extern uint GetCurrentThreadId();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool AttachConsole(uint dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool AllocConsole();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool PeekMessage(out Msg message, IntPtr hWnd, uint messageFilterMin, uint messageFilterMax, uint removeMessage);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool PostThreadMessage(uint threadId, uint message, UIntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern int GetMessage(out Msg message, IntPtr hWnd, uint messageFilterMin, uint messageFilterMax);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool TranslateMessage(ref Msg message);

    [DllImport("user32.dll")]
    public static extern IntPtr DispatchMessage(ref Msg message);

    [DllImport("ole32.dll")]
    public static extern int PropVariantClear(IntPtr propVariant);
}
