using System;
using System.Net.Sockets;
using System.Threading;
using Serilog;

namespace Artemis.Plugins.Devices.OpenRGB
{
    // OpenRGB.NET never raises DeviceListUpdated (its connection copies the handler before anything can subscribe)
    internal sealed class OpenRGBServerWatcher : IDisposable
    {
        private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(3);

        private readonly ILogger _logger;
        private readonly Action<OpenRGBServerWatcher> _onDeviceListUpdated;
        private readonly Action<OpenRGBServerWatcher> _onConnectionLost;
        private TcpClient _client;
        private volatile bool _disposed;

        public OpenRGBServerWatcher(string ip, int port, ILogger logger, Action<OpenRGBServerWatcher> onDeviceListUpdated, Action<OpenRGBServerWatcher> onConnectionLost)
        {
            Ip = ip;
            Port = port;
            _logger = logger;
            _onDeviceListUpdated = onDeviceListUpdated;
            _onConnectionLost = onConnectionLost;
        }

        public string Ip { get; }
        public int Port { get; }

        public void Start()
        {
            _client = OpenRGBProtocol.Connect(Ip, Port, ConnectTimeout);
            OpenRGBProtocol.SendClientName(_client.GetStream(), "Artemis (device list watcher)");

            Thread thread = new(ReadLoop) {IsBackground = true, Name = $"OpenRGB watcher {Ip}:{Port}"};
            thread.Start();
        }

        private void ReadLoop()
        {
            try
            {
                NetworkStream stream = _client.GetStream();
                while (!_disposed && OpenRGBProtocol.TryReceive(stream, out uint packetId))
                {
                    if (packetId == OpenRGBProtocol.DeviceListUpdated)
                        Invoke(_onDeviceListUpdated);
                }
            }
            catch (Exception e) when (!_disposed)
            {
                _logger.Debug(e, "OpenRGB watcher for {Ip}:{Port} stopped reading", Ip, Port);
            }
            catch (Exception)
            {
            }

            if (!_disposed)
                Invoke(_onConnectionLost);
        }

        private void Invoke(Action<OpenRGBServerWatcher> callback)
        {
            try
            {
                callback(this);
            }
            catch (Exception e)
            {
                _logger.Error(e, "OpenRGB watcher callback for {Ip}:{Port} failed", Ip, Port);
            }
        }

        public void Dispose()
        {
            _disposed = true;
            try
            {
                _client?.Dispose();
            }
            catch (Exception)
            {
            }
        }
    }
}
