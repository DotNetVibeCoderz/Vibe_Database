using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Linq; 
using MemSharp.Core;
using MemSharp.Network;
using MemSharp.Tools;

namespace MemSharp
{
    class Program
    {
        static async Task Main(string[] args)
        {
            Console.WriteLine("==============================================");
            Console.WriteLine("        MemSharp - The Redis Bender           ");
            Console.WriteLine("==============================================");
            Console.WriteLine("Dibuat oleh Jacky (Gravicode Studios)");
            Console.WriteLine("Fitur: In-Memory DB, PubSub, SQL-like Query, LINQ");
            Console.WriteLine("----------------------------------------------\n");

            // 1. Setup Engine & Server
            MemDb db = new MemDb();
            // Jalankan di port sembarang yg kosong agar aman, tapi default 6379 ok.
            MemServer server = new MemServer(db);
            server.Start(); 

            // Tunggu sebentar agar server ready
            await Task.Delay(500);

            // 2. Demo Client Biasa (SET/GET)
            Console.WriteLine("\n[Demo 1: Basic Operations via TCP]");
            using (var client = new MemClient())
            {
                client.Connect();
                
                Console.WriteLine("Sending: SET username jacky");
                Console.WriteLine("Response: " + client.Send("SET username jacky"));

                Console.WriteLine("Sending: GET username");
                Console.WriteLine("Response: " + client.Send("GET username"));

                Console.WriteLine("Sending: HSET user:1 name Fadhil");
                Console.WriteLine("Response: " + client.Send("HSET user:1 name Fadhil"));
            }

            // 3. Demo SQL & LINQ
            Console.WriteLine("\n[Demo 2: SQL-Like Query]");
            using (var client = new MemClient())
            {
                client.Connect();
                client.Send("SET user_a dataA");
                string sql = "SQL SELECT * FROM KEYS WHERE KEY LIKE 'user%'";
                Console.WriteLine($"Executing: {sql}");
                Console.WriteLine(client.Send(sql));
            }

            // 4. Demo LINQ Query in C# (New!)
            Console.WriteLine("\n[Demo 3: Advanced LINQ on Memory]");
            
            // Populate some data directly to DB for LINQ demo
            Console.WriteLine("Populating products data directly to memory...");
            db.Set("product:101:name", "Laptop Gaming ROG");
            db.Set("product:101:price", "15000000");
            db.Set("product:101:category", "Electronics");

            db.Set("product:102:name", "Mechanical Keyboard");
            db.Set("product:102:price", "850000");
            db.Set("product:102:category", "Electronics");

            db.Set("product:103:name", "Smart Coffee Mug");
            db.Set("product:103:price", "2500000");
            db.Set("product:103:category", "Home");

            Console.WriteLine("Running LINQ query: Find Electronics > 1.000.000 IDR");

            // LINQ Power: Join, Filter, Project
            var expensiveElectronics = db.AsEnumerable()
                // Filter only price keys
                .Where(kv => kv.Key.StartsWith("product:") && kv.Key.EndsWith(":price"))
                // Project to anonymous object
                .Select(kv => new 
                { 
                    Id = kv.Key.Split(':')[1], 
                    Price = double.Parse(kv.Value.Value.ToString() ?? "0") 
                })
                // Filter price
                .Where(item => item.Price > 1000000)
                // Order by price
                .OrderByDescending(item => item.Price)
                .ToList();

            foreach (var item in expensiveElectronics)
            {
                // Fetch details for this ID
                string name = db.Get($"product:{item.Id}:name");
                string category = db.Get($"product:{item.Id}:category");
                
                // Secondary Filter in loop (simulating complex logic)
                if (category == "Electronics")
                {
                    Console.WriteLine($" -> Found: [{item.Id}] {name} - Rp {item.Price:N0}");
                }
            }
            Console.WriteLine($"Total found: {expensiveElectronics.Count(x => db.Get($"product:{x.Id}:category") == "Electronics")}");


            // 5. Demo Pub/Sub
            Console.WriteLine("\n[Demo 4: Pub/Sub]");
            var subTask = Task.Run(async () => 
            {
                using (var subClient = new MemClient())
                {
                    subClient.Connect();
                    await subClient.SubscribeAsync("news_channel");
                }
            });

            await Task.Delay(500);
            using (var pubClient = new MemClient())
            {
                pubClient.Connect();
                pubClient.Send("PUBLISH news_channel Hellow_Benchmark_Is_Coming");
            }
            await Task.Delay(1000); // Tunggu pesan sampai

            // 6. Benchmark
            Console.WriteLine("\nApakah Anda ingin menjalankan Benchmark? (y/n)");
            var key = Console.ReadLine();
            if (key?.ToLower() == "y")
            {
                await BenchmarkRunner.RunAsync();
            }
            
            Console.WriteLine("\n==============================================");
            Console.WriteLine("Selesai. Tekan ENTER untuk keluar.");
            Console.ReadLine();
            
            server.Stop();
        }
    }
}