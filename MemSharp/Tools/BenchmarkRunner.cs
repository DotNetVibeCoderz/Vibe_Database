using System;
using System.Diagnostics;
using System.Threading.Tasks;
using MemSharp.Network;

namespace MemSharp.Tools
{
    public class BenchmarkRunner
    {
        private const int TotalRequests = 5000; // Jumlah request per test

        public static async Task RunAsync()
        {
            Console.WriteLine("\n==============================================");
            Console.WriteLine("        Running MemSharp Benchmark            ");
            Console.WriteLine("==============================================");
            Console.WriteLine($"Mode: Single Client (Sequential)");
            Console.WriteLine($"Requests per test: {TotalRequests}");

            using (var client = new MemClient())
            {
                try 
                {
                    client.Connect(); 
                } 
                catch 
                {
                    Console.WriteLine("Error: Could not connect to server for benchmark.");
                    return;
                }

                // Warmup
                Console.WriteLine("Warming up...");
                client.Send("SET warmup 1");

                // Benchmark SET
                await RunTest("SET", () => 
                {
                    for (int i = 0; i < TotalRequests; i++)
                    {
                        var res = client.Send($"SET key{i} value{i}");
                    }
                });

                // Benchmark GET
                await RunTest("GET", () => 
                {
                    for (int i = 0; i < TotalRequests; i++)
                    {
                        var res = client.Send($"GET key{i}");
                    }
                });
                
                // Benchmark LPUSH
                await RunTest("LPUSH", () => 
                {
                     for (int i = 0; i < TotalRequests; i++)
                    {
                        client.Send($"LPUSH listbench item{i}");
                    }
                });

                 // Benchmark SQL (Parsing overhead test)
                 int sqlRequests = 500; // SQL regex parsing is heavy, reduce count
                 await RunTest("SQL (SELECT)", () => 
                 {
                     for (int i = 0; i < sqlRequests; i++)
                     {
                         client.Send("SQL SELECT * FROM KEYS WHERE KEY = 'key100'");
                     }
                 }, sqlRequests);
            }
        }

        private static async Task RunTest(string testName, Action action, int count = TotalRequests)
        {
             Console.Write($"Mapping {testName}... ");
             
             // Force GC
             GC.Collect();

             Stopwatch sw = Stopwatch.StartNew();
             
             // Run sync action in task to not block UI thread if any
             await Task.Run(action);

             sw.Stop();
             
             double seconds = sw.Elapsed.TotalSeconds;
             double ops = count / seconds;

             Console.WriteLine($"Done in {seconds:F4}s");
             Console.WriteLine($"  >> Throughput : {ops:N2} ops/sec");
        }
    }
}