using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace Artemis.Plugins.Devices.OpenRGB
{
    // OpenRGB.NET can't request a rescan and never raises DeviceListUpdated
    internal static class OpenRGBProtocol
    {
        public const uint RequestControllerCount = 0;
        public const uint SetClientName = 50;
        public const uint DeviceListUpdated = 100;
        public const uint DetectionStarted = 101;
        public const uint DetectionProgressChanged = 102;
        public const uint DetectionComplete = 103;
        public const uint RequestRescanDevices = 140;

        private const int HeaderLength = 16;
        private const uint MaxPayloadLength = 16 * 1024 * 1024;
        private static readonly byte[] Magic = "ORGB"u8.ToArray();

        public static TcpClient Connect(string ip, int port, TimeSpan timeout)
        {
            TcpClient client = new() {NoDelay = true};
            try
            {
                using CancellationTokenSource cts = new(timeout);
                client.ConnectAsync(ip, port, cts.Token).AsTask().GetAwaiter().GetResult();
                return client;
            }
            catch (OperationCanceledException)
            {
                client.Dispose();
                throw new TimeoutException($"Timed out connecting to OpenRGB server {ip}:{port}");
            }
            catch
            {
                client.Dispose();
                throw;
            }
        }

        public static int GetControllerCount(string ip, int port, TimeSpan timeout)
        {
            using TcpClient client = Connect(ip, port, timeout);
            NetworkStream stream = client.GetStream();
            stream.ReadTimeout = (int) timeout.TotalMilliseconds;
            stream.WriteTimeout = (int) timeout.TotalMilliseconds;
            SendClientName(stream, "Artemis (device count)");
            Send(stream, RequestControllerCount);

            while (TryReceive(stream, out uint packetId, out byte[] payload))
            {
                if (packetId == RequestControllerCount && payload.Length >= 4)
                    return (int) BitConverter.ToUInt32(payload);
            }

            throw new IOException($"OpenRGB server {ip}:{port} closed the connection before replying with a controller count");
        }

        public static void SendClientName(Stream stream, string name)
        {
            Send(stream, SetClientName, Encoding.ASCII.GetBytes(name + "\0"));
        }

        public static void Send(Stream stream, uint packetId, ReadOnlySpan<byte> payload = default)
        {
            Span<byte> header = stackalloc byte[HeaderLength];
            Magic.CopyTo(header);
            BitConverter.TryWriteBytes(header[4..], 0u);
            BitConverter.TryWriteBytes(header[8..], packetId);
            BitConverter.TryWriteBytes(header[12..], (uint) payload.Length);

            stream.Write(header);
            if (!payload.IsEmpty)
                stream.Write(payload);
            stream.Flush();
        }

        public static bool TryReceive(Stream stream, out uint packetId)
        {
            return TryReceive(stream, out packetId, out _, false);
        }

        public static bool TryReceive(Stream stream, out uint packetId, out byte[] payload)
        {
            return TryReceive(stream, out packetId, out payload, true);
        }

        private static bool TryReceive(Stream stream, out uint packetId, out byte[] payload, bool keepPayload)
        {
            packetId = 0;
            payload = [];
            Span<byte> header = stackalloc byte[HeaderLength];
            if (!TryReadExactly(stream, header))
                return false;

            if (!header[..4].SequenceEqual(Magic))
                throw new InvalidDataException("Received a packet without a valid OpenRGB header");

            packetId = BitConverter.ToUInt32(header[8..]);
            uint payloadLength = BitConverter.ToUInt32(header[12..]);
            if (payloadLength > MaxPayloadLength)
                throw new InvalidDataException($"Received an OpenRGB packet with an unexpectedly large payload ({payloadLength} bytes)");

            if (keepPayload)
            {
                payload = new byte[payloadLength];
                return TryReadExactly(stream, payload);
            }

            byte[] buffer = new byte[Math.Min(payloadLength, 81920)];
            long remaining = payloadLength;
            while (remaining > 0)
            {
                int read = stream.Read(buffer, 0, (int) Math.Min(remaining, buffer.Length));
                if (read <= 0)
                    return false;
                remaining -= read;
            }

            return true;
        }

        private static bool TryReadExactly(Stream stream, Span<byte> buffer)
        {
            int offset = 0;
            while (offset < buffer.Length)
            {
                int read = stream.Read(buffer[offset..]);
                if (read <= 0)
                    return false;
                offset += read;
            }

            return true;
        }
    }
}
