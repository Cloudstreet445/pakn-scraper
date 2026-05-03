using MongoDB.Driver;
using MongoDB.Bson;
using Microsoft.Extensions.Configuration;
using static Scraper.Program;
using static Scraper.Utilities;

namespace Scraper
{
    public partial class MongoDBHandler
    {
        private static IMongoClient? mongoClient;
        private static IMongoDatabase? mongoDatabase;
        private static IMongoCollection<BsonDocument>? productsCollection;
        private static IMongoCollection<BsonDocument>? scrapeRunsCollection;

        static string today = DateTime.Today.ToString("yyyy-MM-dd");
        static string scrapeRunId = ObjectId.GenerateNewId().ToString();
        static DateTime scrapeStartTime = DateTime.UtcNow;

        // EstablishConnection()
        // ---------------------
        // Connects to MongoDB using MONGO_URI from appsettings.json or environment variables.
        public static async Task<bool> EstablishConnection()
        {
            string? connectionString = config["MONGO_URI"];
            string? dbName = config["MONGO_DB"] ?? "paknsave-pricing";

            if (string.IsNullOrWhiteSpace(connectionString))
            {
                LogError("MONGO_URI in appsettings.json or environment variables is missing");
                return false;
            }

            try
            {
                mongoClient = new MongoClient(connectionString);
                mongoDatabase = mongoClient.GetDatabase(dbName);
                productsCollection = mongoDatabase.GetCollection<BsonDocument>("products");
                scrapeRunsCollection = mongoDatabase.GetCollection<BsonDocument>("scrape_runs");

                // Test connection with a ping
                await mongoDatabase.RunCommandAsync<BsonDocument>(
                    new BsonDocument("ping", 1)
                );

                Log($"\n(Connected to MongoDB) {dbName}", ConsoleColor.Yellow);

                // Insert a scrape_run document to record this run has started
                await scrapeRunsCollection.InsertOneAsync(new BsonDocument
                {
                    { "_id", scrapeRunId },
                    { "runAt", scrapeStartTime },
                    { "storeId", config["STORE_NAME"] ?? "paknsave-lower-hutt" },
                    { "status", "running" },
                    { "productsScraped", 0 },
                    { "newProducts", 0 },
                    { "priceUpdates", 0 },
                    { "upToDate", 0 },
                    { "failed", 0 }
                });

                return true;
            }
            catch (Exception e)
            {
                LogError($"Error connecting to MongoDB: {e.Message}");
                return false;
            }
        }

        // TransformAndUpsertProduct()
        // ---------------------------
        // Takes a scraped Product and upserts it into MongoDB,
        // appending to priceHistory only if the price has changed.
        public static async Task<UpsertResponse> TransformAndUpsertProduct(Product scrapedProduct)
        {
            if (productsCollection == null)
            {
                LogError("MongoDB not connected");
                return UpsertResponse.Failed;
            }

            try
            {
                var filter = Builders<BsonDocument>.Filter.Eq("_id", scrapedProduct.id);
                var existing = await productsCollection.Find(filter).FirstOrDefaultAsync();

                if (existing == null)
                {
                    return await InsertNewProduct(scrapedProduct);
                }
                else
                {
                    return await UpdateExistingProduct(existing, scrapedProduct);
                }
            }
            catch (Exception e)
            {
                LogError($"MongoDB upsert error for {scrapedProduct.name}: {e.Message}");
                return UpsertResponse.Failed;
            }
        }

        // InsertNewProduct()
        // ------------------
        private static async Task<UpsertResponse> InsertNewProduct(Product scrapedProduct)
        {
            try
            {
                var priceHistoryEntry = new BsonDocument
                {
                    { "date", today },
                    { "price", scrapedProduct.currentPrice }
                };

                var newProduct = new BsonDocument
                {
                    { "_id", scrapedProduct.id },
                    { "name", scrapedProduct.name },
                    { "size", scrapedProduct.size ?? "" },
                    { "category", scrapedProduct.category },
                    { "sourceSite", scrapedProduct.sourceSite },
                    { "storeId", config["STORE_NAME"] ?? "paknsave-lower-hutt" },
                    { "currentPrice", scrapedProduct.currentPrice },
                    { "unitPrice", scrapedProduct.unitPrice ?? "" },
                    { "isSpecial", false },
                    { "priceHistory", new BsonArray { priceHistoryEntry } },
                    { "firstSeen", today },
                    { "lastChecked", today },
                    { "lastPriceChange", today },
                    { "avgPrice90d", scrapedProduct.currentPrice },
                    { "minPrice90d", scrapedProduct.currentPrice },
                    { "maxPrice90d", scrapedProduct.currentPrice }
                };

                await productsCollection!.InsertOneAsync(newProduct);

                Log(
                    $"  New Product: {scrapedProduct.id,-8} | " +
                    $"{scrapedProduct.name.PadRight(40).Substring(0, Math.Min(40, scrapedProduct.name.Length))}" +
                    $" | ${scrapedProduct.currentPrice,5} | {scrapedProduct.size}"
                );

                return UpsertResponse.NewProduct;
            }
            catch (Exception e)
            {
                LogError($"MongoDB insert error: {e.Message}");
                return UpsertResponse.Failed;
            }
        }

        // UpdateExistingProduct()
        // -----------------------
        private static async Task<UpsertResponse> UpdateExistingProduct(
            BsonDocument existing,
            Product scrapedProduct
        )
        {
            float lastPrice = existing["currentPrice"].AsDouble > 0
                ? (float)existing["currentPrice"].AsDouble
                : 0f;

            float priceDifference = Math.Abs(lastPrice - scrapedProduct.currentPrice);
            bool priceHasChanged = priceDifference > 0.05f;

            string lastChecked = existing.Contains("lastChecked")
                ? existing["lastChecked"].AsString
                : "";

            var filter = Builders<BsonDocument>.Filter.Eq("_id", scrapedProduct.id);

            if (priceHasChanged && lastChecked != today)
            {
                // Append new price history entry
                var newEntry = new BsonDocument
                {
                    { "date", today },
                    { "price", scrapedProduct.currentPrice }
                };

                // Recalculate 90 day stats from existing history
                var history = existing["priceHistory"].AsBsonArray
                    .Select(e => (float)e["price"].AsDouble)
                    .ToList();
                history.Add(scrapedProduct.currentPrice);

                // Keep only last 90 days worth (approx 3 scrapes/week = ~39 entries)
                var recentHistory = history.TakeLast(39).ToList();
                float avg = recentHistory.Average();
                float min = recentHistory.Min();
                float max = recentHistory.Max();

                // Detect if this is a special (more than 10% below 90d average)
                bool isSpecial = scrapedProduct.currentPrice < (avg * 0.90f);

                var update = Builders<BsonDocument>.Update
                    .Push("priceHistory", newEntry)
                    .Set("currentPrice", scrapedProduct.currentPrice)
                    .Set("unitPrice", scrapedProduct.unitPrice ?? "")
                    .Set("isSpecial", isSpecial)
                    .Set("lastChecked", today)
                    .Set("lastPriceChange", today)
                    .Set("avgPrice90d", Math.Round(avg, 2))
                    .Set("minPrice90d", Math.Round(min, 2))
                    .Set("maxPrice90d", Math.Round(max, 2));

                await productsCollection!.UpdateOneAsync(filter, update);

                bool priceTrendingDown = scrapedProduct.currentPrice < lastPrice;
                Log(
                    $"  Price {(priceTrendingDown ? "Down " : "Up   ")}: " +
                    $"{existing["name"].AsString.PadRight(51).Substring(0, 51)} | " +
                    $"${lastPrice} > ${scrapedProduct.currentPrice}" +
                    (isSpecial ? " 🔥 SPECIAL" : ""),
                    priceTrendingDown ? ConsoleColor.Green : ConsoleColor.Red
                );

                return UpsertResponse.PriceUpdated;
            }
            else
            {
                // Just update lastChecked
                var update = Builders<BsonDocument>.Update
                    .Set("lastChecked", today);

                await productsCollection!.UpdateOneAsync(filter, update);
                return UpsertResponse.AlreadyUpToDate;
            }
        }

        // FinaliseRun()
        // -------------
        // Call at end of scrape to update the scrape_run document with final stats
        public static async Task FinaliseRun(
            int totalScraped, int newProducts, int priceUpdates, int alreadyUpToDate, int failed, int durationSeconds
        )
        {
            if (scrapeRunsCollection == null) return;

            try
            {
                var filter = Builders<BsonDocument>.Filter.Eq("_id", scrapeRunId);
                var duration = durationSeconds;

                var update = Builders<BsonDocument>.Update
                    .Set("status", "completed")
                    .Set("productsScraped", totalScraped)
                    .Set("newProducts", newProducts)
                    .Set("priceUpdates", priceUpdates)
                    .Set("upToDate", alreadyUpToDate)
                    .Set("failed", failed)
                    .Set("durationSeconds", duration);

                await scrapeRunsCollection.UpdateOneAsync(filter, update);
                Log($"\nScrape run saved to MongoDB ({duration}s)", ConsoleColor.Yellow);
            }
            catch (Exception e)
            {
                LogError($"Failed to finalise scrape run: {e.Message}");
            }
        }
    }
}
