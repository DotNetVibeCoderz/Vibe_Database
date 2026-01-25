using System;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

namespace MemSharp.Network
{
    public class MemClient : IDisposable
    {
        private TcpClient _client;
        private NetworkStream _stream;

        public void Connect(string ip = "127.0.0.1", int port = 6379)
        {
            _client = new TcpClient();
            _client.Connect(ip, port);
            _stream = _client.GetStream();
        }

        public string Send(string command)
        {
            if (_client == null || !_client.Connected) return "Error: Not connected";

            byte[] data = Encoding.UTF8.GetBytes(command);
            _stream.Write(data, 0, data.Length);

            // Baca response
            // (Demo sederhana: asumsi response muat di buffer dan cepat)
            byte[] responseBuffer = new byte[4096];
            int bytesRead = _stream.Read(responseBuffer, 0, responseBuffer.Length);
            return Encoding.UTF8.GetString(responseBuffer, 0, bytesRead).Trim();
        }

        // Method khusus untuk demo subscribe (blocking/async read)
        public async Task SubscribeAsync(string channel)
        {
            if (_client == null || !_client.Connected) return;

            byte[] data = Encoding.UTF8.GetBytes($"SUBSCRIBE {channel}");
            await _stream.WriteAsync(data, 0, data.Length);
            Console.WriteLine($"[Client] Subscribed to {channel}. Waiting for messages...");

            byte[] buffer = new byte[4096];
            while (true)
            {
                int bytesRead = await _stream.ReadAsync(buffer, 0, buffer.Length);
                if (bytesRead == 0) break;
                string msg = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                Console.Write(msg);
            }
        }

        public void Dispose()
        {
            _stream?.Dispose();
            _client?.Dispose();
        }
    }
}