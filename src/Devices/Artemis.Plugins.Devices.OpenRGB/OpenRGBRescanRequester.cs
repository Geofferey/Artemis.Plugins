using System;
using System.Net.Sockets;
using System.Threading;
using Serilog;

namespace Artemis.Plugins.Devices.OpenRGB
{
    internal enum OpenRGBRescanResult
    {
        Completed,
        TimedOut,
        ConnectFailed,
        ConnectionClosed
    }

    internal static class OpenRGBRescanRequester
    {
        private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(3);
        private static readonly TimeSpan QuietPeriod = TimeSpan.FromSeconds(5);

        public static OpenRGBRescanResult RequestRescan(string ip, int port, TimeSpan timeout, ILogger logger)
        {
            TcpClient client;
            try
            {
                client = OpenRGBProtocol.Connect(ip, port, ConnectTimeout);
            }
            catch (Exception e)
            {
                logger.Warning(e, "Could not connect to OpenRGB server {Ip}:{Port} to request a rescan", ip, port);
                return OpenRGBRescanResult.ConnectFailed;
            }

            using (client)
                return WaitForRescan(client, ip, port, timeout, logger);
        }

        private static OpenRGBRescanResult WaitForRescan(TcpClient client, string ip, int port, TimeSpan timeout, ILogger logger)
        {
            NetworkStream stream = client.GetStream();
            stream.WriteTimeout = 5000;
            OpenRGBProtocol.SendClientName(stream, "Artemis (rescan)");
            Thread.Sleep(100);
            OpenRGBProtocol.Send(stream, OpenRGBProtocol.RequestRescanDevices);

            // OpenRGB also sends a device list update when detection starts, so wait for detection to complete. The server
            // rescans on this connection's thread, closing it early isn't safe.
            DateTime deadline = DateTime.UtcNow + timeout;
            DateTime lastActivity = DateTime.UtcNow;
            while (true)
            {
                DateTime now = DateTime.UtcNow;
                if (now >= deadline)
                    return OpenRGBRescanResult.TimedOut;

                // Fallback for servers that don't send a detection complete packet
                if (now - lastActivity >= QuietPeriod)
                {
                    int count = TryGetControllerCount(ip, port, logger);
                    if (count > 0)
                    {
                        logger.Debug("OpenRGB rescan went quiet with {Count} controllers, considering it complete", count);
                        return OpenRGBRescanResult.Completed;
                    }

                    lastActivity = now;
                }

                // A timed out read leaves the stream in an undefined state, poll instead
                TimeSpan wait = Min(deadline - now, QuietPeriod - (now - lastActivity));
                if (!client.Client.Poll(Math.Max(1, (int) wait.TotalMilliseconds) * 1000, SelectMode.SelectRead))
                    continue;

                if (!OpenRGBProtocol.TryReceive(stream, out uint packetId))
                    return OpenRGBRescanResult.ConnectionClosed;

                switch (packetId)
                {
                    case OpenRGBProtocol.DetectionComplete:
                        return OpenRGBRescanResult.Completed;
                    case OpenRGBProtocol.DeviceListUpdated or OpenRGBProtocol.DetectionStarted or OpenRGBProtocol.DetectionProgressChanged:
                        lastActivity = DateTime.UtcNow;
                        break;
                }
            }
        }

        private static int TryGetControllerCount(string ip, int port, ILogger logger)
        {
            try
            {
                return OpenRGBProtocol.GetControllerCount(ip, port, ConnectTimeout);
            }
            catch (Exception e)
            {
                logger.Debug(e, "Could not get the controller count of OpenRGB server {Ip}:{Port}", ip, port);
                return -1;
            }
        }

        private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;
    }
}
