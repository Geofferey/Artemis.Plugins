using System;
using Artemis.Core;
using Artemis.Core.DeviceProviders;
using Artemis.Core.Services;
using RGB.NET.Core;
using RGB.NET.Devices.OpenRGB;
using Serilog;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Versioning;
using System.Threading;
using System.Timers;
using Microsoft.Win32;
using RGBDeviceProvider = RGB.NET.Devices.OpenRGB.OpenRGBDeviceProvider;
using Timer = System.Timers.Timer;

namespace Artemis.Plugins.Devices.OpenRGB
{
    [PluginFeature(Name = "OpenRGB Device Provider")]
    public class OpenRGBDeviceProvider : DeviceProvider
    {
        private static readonly TimeSpan RescanTimeout = TimeSpan.FromSeconds(60);
        private static readonly TimeSpan DeviceListChangeDebounce = TimeSpan.FromMilliseconds(2500);
        private static readonly TimeSpan ControllerCountTimeout = TimeSpan.FromSeconds(3);

        private readonly ILogger _logger;
        private readonly IDeviceService _deviceService;
        private readonly IPluginManagementService _pluginManagementService;

        private readonly PluginSetting<List<OpenRGBServerDefinition>> _deviceDefinitionsSettings;
        private readonly PluginSetting<bool> _forceAddAllDevicesSetting;
        private readonly PluginSetting<bool> _rescanOnResumeSetting;
        private readonly PluginSetting<int> _rescanDelaySetting;
        private readonly PluginSetting<bool> _reloadOnDeviceListChangeSetting;
        private readonly Timer _reconnectTimer;
        private readonly System.Threading.Timer _deviceListChangeDebounceTimer;
        private readonly SemaphoreSlim _reloadLock = new(1, 1);
        private readonly List<OpenRGBServerWatcher> _watchers = new();
        private readonly Dictionary<string, int> _loadedControllerCounts = new();

        private bool _enabledBefore;
        private int _rescanInProgress;
        private volatile bool _serverLost;

        public OpenRGBDeviceProvider(IDeviceService deviceService, IPluginManagementService pluginManagementService, PluginSettings settings, ILogger logger)
        {
            _logger = logger;
            _deviceService = deviceService;
            _pluginManagementService = pluginManagementService;
            _forceAddAllDevicesSetting = settings.GetSetting("ForceAddAllDevices", false);
            _rescanOnResumeSetting = settings.GetSetting("RescanOnResume", false);
            _rescanDelaySetting = settings.GetSetting("RescanDelay", 0);
            _reloadOnDeviceListChangeSetting = settings.GetSetting("ReloadOnDeviceListChange", false);
            _deviceDefinitionsSettings = settings.GetSetting("DeviceDefinitions", new List<OpenRGBServerDefinition>
            {
                new()
                {
                    ClientName = "Artemis",
                    Ip = "127.0.0.1",
                    Port = 6742
                }
            });
            CreateMissingLedsSupported = false;
            RemoveExcessiveLedsSupported = true;

            _reconnectTimer = new Timer(30 * 1000) {AutoReset = false};
            _reconnectTimer.Elapsed += OnReconnectTimerElapsed;
            _deviceListChangeDebounceTimer = new System.Threading.Timer(_ => OnDeviceListChangeSettled());
        }

        public override RGBDeviceProvider RgbDeviceProvider => RGBDeviceProvider.Instance;

        public override void Enable()
        {
            RgbDeviceProvider.Exception += Provider_OnException;
            SubscribeToSystemEvents();

            bool starting = !_enabledBefore;
            _enabledBefore = true;

            // Rescanning takes longer than enabling is allowed to take, devices are loaded once it's done
            if (_rescanOnResumeSetting.Value && starting)
            {
                _logger.Information("Starting, loading OpenRGB devices after OpenRGB rescanned them");
                StartRescanThenReload("startup rescan", true);
                return;
            }

            RgbDeviceProvider.DeviceDefinitions.Clear();
            foreach (OpenRGBServerDefinition def in _deviceDefinitionsSettings.Value)
                RgbDeviceProvider.DeviceDefinitions.Add(def);
            RgbDeviceProvider.ForceAddAllDevices = _forceAddAllDevicesSetting.Value;

            _deviceService.AddDeviceProvider(this);
            _serverLost = false;

            bool anyFailedToConnect = false;
            foreach (OpenRGBServerDefinition deviceDefinition in RgbDeviceProvider.DeviceDefinitions.Where(dd => !dd.Connected))
            {
                _logger.Error("OpenRGB server {ip}:{port} failed to connect: {error}", deviceDefinition.Ip, deviceDefinition.Port, deviceDefinition.LastError);
                anyFailedToConnect = true;
            }

            if (_reloadOnDeviceListChangeSetting.Value)
                StartWatchers();

            if (anyFailedToConnect)
            {
                _logger.Information("Failed to connect to at least one OpenRGB server. Retrying in 30secs...");
                _reconnectTimer.Start();
            }
            else
            {
                _reconnectTimer.Stop();
            }
        }

        public override void Disable()
        {
            UnsubscribeFromSystemEvents();
            _reconnectTimer.Stop();
            _deviceListChangeDebounceTimer.Change(Timeout.Infinite, Timeout.Infinite);
            StopWatchers();

            _deviceService.RemoveDeviceProvider(this);
            RgbDeviceProvider.Exception -= Provider_OnException;
            RgbDeviceProvider.Dispose();
        }

        private void SubscribeToSystemEvents()
        {
            if (!OperatingSystem.IsWindows())
                return;

            try
            {
                SystemEvents.PowerModeChanged += SystemEventsOnPowerModeChanged;
                SystemEvents.SessionSwitch += SystemEventsOnSessionSwitch;
            }
            catch (Exception e)
            {
                _logger.Warning(e, "Could not subscribe to system events, OpenRGB devices won't be rescanned on wake or unlock");
            }
        }

        private void UnsubscribeFromSystemEvents()
        {
            if (!OperatingSystem.IsWindows())
                return;

            try
            {
                SystemEvents.PowerModeChanged -= SystemEventsOnPowerModeChanged;
                SystemEvents.SessionSwitch -= SystemEventsOnSessionSwitch;
            }
            catch (Exception)
            {
            }
        }

        [SupportedOSPlatform("windows")]
        private void SystemEventsOnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
        {
            if (e.Mode == PowerModes.Resume)
                RescanAfterSystemEvent("wake");
        }

        [SupportedOSPlatform("windows")]
        private void SystemEventsOnSessionSwitch(object sender, SessionSwitchEventArgs e)
        {
            if (e.Reason == SessionSwitchReason.SessionUnlock)
                RescanAfterSystemEvent("unlock");
        }

        private void RescanAfterSystemEvent(string reason)
        {
            if (!IsEnabled || !_rescanOnResumeSetting.Value)
                return;

            _logger.Information("Rescanning OpenRGB devices after {Reason}", reason);
            StartRescanThenReload($"{reason} rescan", true);
        }

        private void Provider_OnException(object sender, ExceptionEventArgs args) => _logger.Debug(args.Exception, "OpenRGB Exception: {message}", args.Exception.Message);

        #region Rescanning

        internal void RescanDevices()
        {
            if (!IsEnabled)
            {
                _logger.Warning("Not rescanning OpenRGB devices, the device provider is disabled");
                return;
            }

            _logger.Information("Rescanning OpenRGB devices on request");
            StartRescanThenReload("manual rescan", false);
        }

        private void StartRescanThenReload(string reason, bool warmUp)
        {
            if (Interlocked.Exchange(ref _rescanInProgress, 1) == 1)
            {
                _logger.Information("OpenRGB rescan already in progress");
                return;
            }

            Thread thread = new(() => RescanThenReload(reason, warmUp)) {IsBackground = true, Name = "OpenRGB rescan"};
            thread.Start();
        }

        private void RescanThenReload(string reason, bool warmUp)
        {
            try
            {
                int delay = warmUp ? Math.Max(0, _rescanDelaySetting.Value) : 0;
                if (delay > 0)
                {
                    _logger.Information("Waiting {Delay} seconds before rescanning OpenRGB devices", delay);
                    Thread.Sleep(TimeSpan.FromSeconds(delay));
                }

                foreach ((string ip, int port) in _deviceDefinitionsSettings.Value.Select(d => (d.Ip, d.Port)).Distinct())
                {
                    _logger.Information("Rescanning OpenRGB devices of {Ip}:{Port}", ip, port);
                    OpenRGBRescanResult result = OpenRGBRescanRequester.RequestRescan(ip, port, RescanTimeout, _logger);
                    _logger.Information("OpenRGB rescan of {Ip}:{Port} finished: {Result}, server reports {Count} controllers", ip, port, result, TryGetControllerCount(ip, port));
                }
            }
            catch (Exception e)
            {
                _logger.Error(e, "OpenRGB rescan failed");
            }
            finally
            {
                Interlocked.Exchange(ref _rescanInProgress, 0);
            }

            try
            {
                if (!IsEnabled)
                {
                    _logger.Information("Skipping loading OpenRGB devices after rescan, the device provider is disabled");
                    return;
                }

                ReloadNow(reason);
            }
            catch (Exception e)
            {
                _logger.Error(e, "Failed to load OpenRGB devices after rescan");
            }
        }

        private int TryGetControllerCount(string ip, int port)
        {
            try
            {
                return OpenRGBProtocol.GetControllerCount(ip, port, ControllerCountTimeout);
            }
            catch (Exception)
            {
                return -1;
            }
        }

        #endregion

        #region Device list changes

        private void StartWatchers()
        {
            StopWatchers();
            foreach (OpenRGBServerDefinition definition in RgbDeviceProvider.DeviceDefinitions.Where(d => d.Connected))
            {
                OpenRGBServerWatcher watcher = new(definition.Ip, definition.Port, _logger, OnDeviceListUpdated, OnServerConnectionLost);
                try
                {
                    // Loading devices changes modes which also causes device list updates, only reload when the count changes
                    int count = OpenRGBProtocol.GetControllerCount(definition.Ip, definition.Port, ControllerCountTimeout);
                    watcher.Start();
                    lock (_watchers)
                    {
                        _loadedControllerCounts[GetServerKey(definition.Ip, definition.Port)] = count;
                        _watchers.Add(watcher);
                    }
                }
                catch (Exception e)
                {
                    _logger.Warning(e, "Could not watch OpenRGB server {Ip}:{Port} for device list changes", definition.Ip, definition.Port);
                    watcher.Dispose();
                }
            }
        }

        private void StopWatchers()
        {
            lock (_watchers)
            {
                foreach (OpenRGBServerWatcher watcher in _watchers)
                    watcher.Dispose();
                _watchers.Clear();
                _loadedControllerCounts.Clear();
            }
        }

        private void OnDeviceListUpdated(OpenRGBServerWatcher watcher)
        {
            if (_rescanInProgress == 1)
                return;

            _logger.Debug("OpenRGB server {Ip}:{Port} reported its device list changed", watcher.Ip, watcher.Port);
            _deviceListChangeDebounceTimer.Change(DeviceListChangeDebounce, Timeout.InfiniteTimeSpan);
        }

        private void OnDeviceListChangeSettled()
        {
            try
            {
                if (_rescanInProgress == 1 || !IsEnabled)
                    return;

                List<(string Ip, int Port, int LoadedCount)> servers;
                lock (_watchers)
                {
                    servers = _watchers.Select(w => (w.Ip, w.Port, _loadedControllerCounts.GetValueOrDefault(GetServerKey(w.Ip, w.Port), -1))).ToList();
                }

                foreach ((string ip, int port, int loadedCount) in servers)
                {
                    int count = OpenRGBProtocol.GetControllerCount(ip, port, ControllerCountTimeout);
                    if (count == loadedCount)
                    {
                        _logger.Debug("OpenRGB server {Ip}:{Port} device list updated but still has {Count} controllers, not reloading", ip, port, count);
                        continue;
                    }

                    _logger.Information("OpenRGB server {Ip}:{Port} went from {LoadedCount} to {Count} controllers", ip, port, loadedCount, count);
                    ReloadNow("device list changed");
                    return;
                }
            }
            catch (Exception e)
            {
                _logger.Warning(e, "Failed to check OpenRGB device list changes");
            }
        }

        private void OnServerConnectionLost(OpenRGBServerWatcher watcher)
        {
            _logger.Warning("Lost connection to OpenRGB server {Ip}:{Port}, reconnecting once it's back", watcher.Ip, watcher.Port);
            _serverLost = true;
            _reconnectTimer.Start();
        }

        private static string GetServerKey(string ip, int port) => $"{ip}:{port}";

        #endregion

        #region Reloading

        private void ReloadNow(string reason)
        {
            if (!_reloadLock.Wait(0))
                return;

            try
            {
                if (!IsEnabled)
                    return;

                _logger.Information("Reloading OpenRGB devices ({Reason})", reason);
                _pluginManagementService.DisablePluginFeature(this, false);
                _pluginManagementService.EnablePluginFeature(this, false, true);
                _logger.Information("Reloaded OpenRGB devices ({Reason}), {Count} devices loaded", reason, _deviceService.Devices.Count(d => d.DeviceProvider == this));
            }
            catch (Exception e)
            {
                _logger.Error(e, "Failed to reload OpenRGB devices ({Reason})", reason);
            }
            finally
            {
                _reloadLock.Release();
            }
        }

        private void OnReconnectTimerElapsed(object sender, ElapsedEventArgs e)
        {
            try
            {
                List<OpenRGBServerDefinition> disconnected = RgbDeviceProvider.DeviceDefinitions.Where(dd => _serverLost || !dd.Connected).ToList();
                if (!disconnected.Any())
                {
                    _logger.Verbose("OpenRGB reconnect timer elapsed, but all device definitions connected successfully.");
                    return;
                }

                bool reachable = false;
                foreach (OpenRGBServerDefinition definition in disconnected)
                {
                    try
                    {
                        using System.Net.Sockets.TcpClient client = OpenRGBProtocol.Connect(definition.Ip, definition.Port, TimeSpan.FromSeconds(3));
                        reachable = true;
                    }
                    catch (Exception)
                    {
                    }
                }

                if (reachable)
                    ReloadNow("server reachable");
                else if (IsEnabled)
                    _reconnectTimer.Start();
            }
            catch (Exception exception)
            {
                _logger.Error(exception, "OpenRGB reconnect attempt failed");
                if (IsEnabled)
                    _reconnectTimer.Start();
            }
        }

        #endregion
    }
}
