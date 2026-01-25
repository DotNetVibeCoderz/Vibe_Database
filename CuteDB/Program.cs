using System;
using System.Diagnostics;
using System.Linq;
using System.Collections.Generic;

namespace CuteDB
{
    public class User
    {
        public int Id { get; set; }
        public string Name { get; set; }
        public int Age { get; set; }
        public string City { get; set; }
    }

    class Program
    {
        static void Main(string[] args)
        {
            Console.WriteLine("========================================");
            Console.WriteLine("    CuteDB - The Cute Embedded Database ");
            Console.WriteLine("    By Jacky the Code Bender            ");
            Console.WriteLine("========================================");
            Console.WriteLine();

            string dbFile = "my_cute.jdb";
            // Clean up old run
            if (System.IO.File.Exists(dbFile)) System.IO.File.Delete(dbFile);

            var db = new CuteDatabase(dbFile);

            Console.WriteLine("[1] Performance Benchmark starting...");
            
            // 1. Insert Benchmark
            int recordCount = 50000;
            Console.WriteLine($"-> Inserting {recordCount} records...");
            var sw = Stopwatch.StartNew();
            
            for (int i = 0; i < recordCount; i++)
            {
                db.Insert("Users", new User 
                { 
                    Id = i, 
                    Name = $"User_{i}", 
                    Age = i % 80, 
                    City = (i % 2 == 0) ? "Jakarta" : "Bandung" 
                });
            }
            
            sw.Stop();
            Console.WriteLine($"   Done in {sw.ElapsedMilliseconds} ms. ({recordCount / sw.Elapsed.TotalSeconds:N0} ops/sec)");

            // 2. Linq Query Benchmark
            Console.WriteLine("\n[2] Testing LINQ Query (Age > 25 && City == 'Jakarta')...");
            sw.Restart();
            
            var users = db.GetCollection<User>("Users");
            var resultLinq = users.Where(u => u.Age > 25 && u.City == "Jakarta").ToList();
            
            sw.Stop();
            Console.WriteLine($"   Found {resultLinq.Count} records in {sw.ElapsedMilliseconds} ms.");

            // 3. SQL Query Benchmark
            Console.WriteLine("\n[3] Testing SQL Query (SELECT * FROM Users WHERE Age > 25 AND City == \"Jakarta\")..."); 
            sw.Restart();
            
            try 
            {
                var resultSql = db.ExecuteSql("SELECT * FROM Users WHERE Age > 25 AND City == \"Jakarta\"");
                var countSql = resultSql.Count();
                 sw.Stop();
                Console.WriteLine($"   Found {countSql} records in {sw.ElapsedMilliseconds} ms.");
            }
            catch(Exception ex)
            {
                Console.WriteLine("   SQL Error: " + ex.Message);
            }

            // 4. Persistence
            Console.WriteLine("\n[4] Testing Storage Save...");
            sw.Restart();
            db.Save();
            sw.Stop();
            Console.WriteLine($"   Saved {recordCount} records to disk in {sw.ElapsedMilliseconds} ms.");

            // 5. Reload
            Console.WriteLine("\n[5] Testing Storage Load...");
            var db2 = new CuteDatabase(dbFile); // Auto loads
            var count = db2.GetCollection<User>("Users").Count;
            Console.WriteLine($"   Loaded {count} records from disk.");

            // 6. CRUD EXAMPLES
            Console.WriteLine("\n[6] CRUD Operations Demo");
            
            // create
            Console.WriteLine("   [C] Creating/Inserting new user 'Budi'...");
            db.Insert("Users", new User { Id = 99999, Name = "Budi", Age = 30, City = "Surabaya" });
            
            // read
            var budi = db.GetCollection<User>("Users").FirstOrDefault(u => u.Name == "Budi");
            Console.WriteLine($"   [R] Read User: {budi?.Name} from {budi?.City}");

            // update
            Console.WriteLine("   [U] Updating 'Budi' city to 'Bali'...");
            // Option A: Direct Property Update (since it's in-memory reference)
            // budi.City = "Bali"; 
            
            // Option B: Using Update Helper (Recommended for safe bulk updates)
            int updated = db.Update<User>("Users", u => u.Name == "Budi", u => u.City = "Bali");
            Console.WriteLine($"       Updated {updated} record(s).");
            
            var budiNew = db.GetCollection<User>("Users").FirstOrDefault(u => u.Name == "Budi");
            Console.WriteLine($"       Verify: {budiNew?.Name} is now in {budiNew?.City}");

            // delete
            Console.WriteLine("   [D] Deleting 'Budi'...");
            int deleted = db.Delete<User>("Users", u => u.Name == "Budi");
             Console.WriteLine($"       Deleted {deleted} record(s).");
             
             var check = db.GetCollection<User>("Users").FirstOrDefault(u => u.Name == "Budi");
             if (check == null) Console.WriteLine("       Verify: Budi is gone.");

            Console.WriteLine("\n========================================");
            Console.WriteLine("    Done! Jangan lupa kirim pulsa ya!");
            Console.WriteLine("========================================");
        }
    }
}
