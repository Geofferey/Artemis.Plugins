using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using System.Threading.Tasks;
using Artemis.Core;
using Artemis.Core.Services;
using Artemis.UI.Shared;
using Artemis.UI.Shared.Services;
using ReactiveUI;
using RGB.NET.Devices.OpenRGB;
using Serilog;

namespace Artemis.Plugins.Devices.OpenRGB.ViewModels
{
    public class OpenRGBConfigurationDialogViewModel : PluginConfigurationViewModel
    {
        private readonly PluginSetting<List<OpenRGBServerDefinition>> _definitions;
        private readonly PluginSetting<bool> _forceAddAllDevicesSetting;
        private readonly PluginSetting<bool> _rescanOnResumeSetting;
        private readonly PluginSetting<int> _rescanDelaySetting;
        private readonly PluginSetting<bool> _reloadOnDeviceListChangeSetting;
        private readonly IPluginManagementService _pluginManagementService;
        private readonly IWindowService _windowService;
        private readonly ILogger _logger;
        private readonly List<(string ClientName, string Ip, int Port)> _originalDefinitions;
        private bool _forceAddAllDevices;
        private bool _rescanOnResume;
        private double _rescanDelay;
        private bool _reloadOnDeviceListChange;

        public OpenRGBConfigurationDialogViewModel(Plugin plugin, PluginSettings settings, IPluginManagementService pluginManagementService, IWindowService windowService, ILogger logger) : base(plugin)
        {
            _pluginManagementService = pluginManagementService;
            _windowService = windowService;
            _logger = logger;
            _definitions = settings.GetSetting("DeviceDefinitions", new List<OpenRGBServerDefinition>());
            _forceAddAllDevicesSetting = settings.GetSetting("ForceAddAllDevices", false);
            _rescanOnResumeSetting = settings.GetSetting("RescanOnResume", false);
            _rescanDelaySetting = settings.GetSetting("RescanDelay", 0);
            _reloadOnDeviceListChangeSetting = settings.GetSetting("ReloadOnDeviceListChange", false);

            // The grid edits definitions in place
            _originalDefinitions = _definitions.Value.Select(d => (d.ClientName, d.Ip, d.Port)).ToList();
            Definitions = new ObservableCollection<OpenRGBServerDefinition>(_definitions.Value);
            ForceAddAllDevices = _forceAddAllDevicesSetting.Value;
            RescanOnResume = _rescanOnResumeSetting.Value;
            RescanDelay = _rescanDelaySetting.Value;
            ReloadOnDeviceListChange = _reloadOnDeviceListChangeSetting.Value;
            DeleteDefinition = ReactiveCommand.Create<OpenRGBServerDefinition>(ExecuteDeleteDefinition);
        }

        public ReactiveCommand<OpenRGBServerDefinition,Unit> DeleteDefinition { get; }

        public ObservableCollection<OpenRGBServerDefinition> Definitions { get; }

        public bool ForceAddAllDevices
        {
            get => _forceAddAllDevices;
            set => this.RaiseAndSetIfChanged(ref _forceAddAllDevices, value);
        }

        public bool RescanOnResume
        {
            get => _rescanOnResume;
            set => this.RaiseAndSetIfChanged(ref _rescanOnResume, value);
        }

        public double RescanDelay
        {
            get => _rescanDelay;
            set => this.RaiseAndSetIfChanged(ref _rescanDelay, double.IsNaN(value) ? 0 : Math.Max(0, value));
        }

        public bool ReloadOnDeviceListChange
        {
            get => _reloadOnDeviceListChange;
            set => this.RaiseAndSetIfChanged(ref _reloadOnDeviceListChange, value);
        }

        public void AddDefinition()
        {
            Definitions.Add(new OpenRGBServerDefinition());
        }

        private void ExecuteDeleteDefinition(OpenRGBServerDefinition def)
        {
            Definitions.Remove(def);
        }

        public void RescanDevices()
        {
            OpenRGBDeviceProvider deviceProvider = Plugin.GetFeature<OpenRGBDeviceProvider>();
            if (deviceProvider == null)
                return;

            deviceProvider.RescanDevices();
        }

        public void SaveChanges()
        {
            // Ignore empty definitions
            List<OpenRGBServerDefinition> definitions = Definitions.Where(d => !string.IsNullOrWhiteSpace(d.Ip) || !string.IsNullOrWhiteSpace(d.ClientName) || d.Port != 0).ToList();

            bool requiresReload = !_originalDefinitions.SequenceEqual(definitions.Select(d => (d.ClientName, d.Ip, d.Port))) ||
                                  _forceAddAllDevicesSetting.Value != ForceAddAllDevices ||
                                  _reloadOnDeviceListChangeSetting.Value != ReloadOnDeviceListChange;

            _definitions.Value.Clear();
            _definitions.Value.AddRange(definitions);
            _definitions.Save();

            _forceAddAllDevicesSetting.Value = ForceAddAllDevices;
            _forceAddAllDevicesSetting.Save();
            _rescanOnResumeSetting.Value = RescanOnResume;
            _rescanOnResumeSetting.Save();
            _rescanDelaySetting.Value = (int) Math.Round(RescanDelay);
            _rescanDelaySetting.Save();
            _reloadOnDeviceListChangeSetting.Value = ReloadOnDeviceListChange;
            _reloadOnDeviceListChangeSetting.Save();

            if (requiresReload)
            {
                // Fire & forget re-enabling the plugin
                Task.Run(() =>
                {
                    try
                    {
                        OpenRGBDeviceProvider deviceProvider = Plugin.GetFeature<OpenRGBDeviceProvider>();
                        if (deviceProvider == null || !deviceProvider.IsEnabled) return;
                        _pluginManagementService.DisablePluginFeature(deviceProvider, false);
                        _pluginManagementService.EnablePluginFeature(deviceProvider, false);
                    }
                    catch (Exception e)
                    {
                        _logger.Error(e, "Failed to apply OpenRGB settings");
                    }
                });
            }

            Close();
        }

        public async Task Cancel()
        {
            if (!await _windowService.ShowConfirmContentDialog("Discard changes", "Do you want to discard any changes you made?"))
                return;

            _definitions.RejectChanges();
            _forceAddAllDevicesSetting.RejectChanges();
            _rescanOnResumeSetting.RejectChanges();
            _rescanDelaySetting.RejectChanges();
            _reloadOnDeviceListChangeSetting.RejectChanges();
            Close();
        }
    }
}
