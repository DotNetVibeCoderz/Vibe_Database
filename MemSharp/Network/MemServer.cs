using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MemSharp.Core;

namespace MemSharp.Network
{
    public class MemServer
    {
        private TcpListener _listener;
        private bool _isRunning;
        private readonly MemDb _db;

        public MemServer(MemDb db, int port = 6379)
        {
            _db = db;
            _listener = new TcpListener(IPAddress.Any, port);
        }

        public void Start()
        {
            _listener.Start();
            _isRunning = true;
            Console.WriteLine($"[Server] MemSharp listening on port 6379...");
            
            // Loop utama menerima client
            Task.Run(async () => 
            {
                while (_isRunning)
                {
                    try
                    {
                        var client = await _listener.AcceptTcpClientAsync();
                        _ = HandleClientAsync(client);
                    }
                    catch (Exception ex)
                    {
                        if (_isRunning) Console.WriteLine($"[Server] Accept error: {ex.Message}");
                    }
                }
            });
        }

        public void Stop()
        {
            _isRunning = false;
            _listener.Stop();
        }

        private async Task HandleClientAsync(TcpClient client)
        {
            using (client)
            using (var stream = client.GetStream())
            {
                var buffer = new byte[4096];
                int bytesRead;

                try
                {
                    while ((bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length)) != 0)
                    {
                        var message = Encoding.UTF8.GetString(buffer, 0, bytesRead).Trim();
                        // Handle multiple commands separated by newLine if strictly needed, 
                        // but for 'Sample' we handle one command per flush mostly.
                        
                        if (string.IsNullOrWhiteSpace(message)) continue;

                        Console.WriteLine($"[Server] Received: {message}");
                        string response = ExecuteCommand(message, stream);
                        
                        // Jika command subscribe, jangan kirim response biasa karena stream dipakai untuk push
                        if (!message.ToUpper().StartsWith("SUBSCRIBE"))
                        {
                            var responseBytes = Encoding.UTF8.GetBytes(response + "\n");
                            await stream.WriteAsync(responseBytes, 0, responseBytes.Length);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Server] Client error: {ex.Message}");
                }
            }
        }

        private string ExecuteCommand(string rawCommand, NetworkStream stream)
        {
            var parts = rawCommand.Split(' ');
            var cmd = parts[0].ToUpper();

            try
            {
                switch (cmd)
                {
                    case "SET":
                        if (parts.Length < 3) return "ERROR: Usage SET key value";
                        _db.Set(parts[1], parts[2]);
                        return "OK";

                    case "GET":
                         if (parts.Length < 2) return "ERROR: Usage GET key";
                         var val = _db.Get(parts[1]);
                         return val ?? "(nil)";

                    case "LPUSH":
                         if (parts.Length < 3) return "ERROR: Usage LPUSH key value";
                         _db.LPush(parts[1], parts[2]);
                         return "OK";

                    case "LRANGE":
                         if (parts.Length < 4) return "ERROR: Usage LRANGE key start stop";
                         var list = _db.LRange(parts[1], int.Parse(parts[2]), int.Parse(parts[3]));
                         return string.Join(", ", list);

                    case "HSET":
                         if (parts.Length < 4) return "ERROR: Usage HSET key field value";
                         _db.HSet(parts[1], parts[2], parts[3]);
                         return "OK";

                    case "HGET":
                         if (parts.Length < 3) return "ERROR: Usage HGET key field";
                         var hval = _db.HGet(parts[1], parts[2]);
                         return hval ?? "(nil)";

                    case "SQL":
                        // Gabungkan sisa parts menjadi query string
                        string query = string.Join(" ", parts, 1, parts.Length - 1);
                        var rows = _db.ExecuteSql(query);
                        return string.Join("\n", rows);

                    case "PUBLISH":
                        if (parts.Length < 3) return "ERROR: Usage PUBLISH channel message";
                        _db.Publish(parts[1], string.Join(" ", parts, 2, parts.Length - 2));
                        return "OK";

                    case "SUBSCRIBE":
                        if (parts.Length < 2) return "ERROR Usage SUBSCRIBE channel";
                        string channel = parts[1];
                        _db.Subscribe(channel, (msg) => 
                        {
                            try {
                                string notification = $"[EVENT] Channel {channel}: {msg}\n";
                                byte[] data = Encoding.UTF8.GetBytes(notification);
                                stream.Write(data, 0, data.Length); // Sync write to lock stream
                            } catch { /* Connection closed likely */ }
                        });
                        return "Subscribed to " + channel;

                    default:
                        return "ERROR: Unknown Command";
                }
            }
            catch (Exception ex)
            {
                return $"ERROR: {ex.Message}";
            }
        }
    }
}