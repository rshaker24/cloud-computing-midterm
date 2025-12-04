using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using System.Net;
using System.IO;
using System.Linq;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Azure.Identity;
using Azure.Security.KeyVault.Secrets;
using Microsoft.Data.SqlClient;
using Azure;
using Microsoft.ApplicationInsights;
using Microsoft.ApplicationInsights.DataContracts;


namespace Company.Function;

public class Game_api
{
    private readonly ILogger<Game_api> _logger;
    private readonly string _connectionString;
    private readonly string _apiKey;
    private readonly TelemetryClient _telemetryClient;




    public Game_api(ILogger<Game_api> logger, IConfiguration config, TelemetryClient telemetryClient)
    {
        _logger = logger;
        _telemetryClient = telemetryClient;
        _connectionString = Environment.GetEnvironmentVariable("SqlConnectionString")
            ?? throw new Exception("SQL Connection string missing from configuration.");


        var vaultUrl = config?.GetValue<string>("KEY_VAULT_URL")?.Trim();
        if (string.IsNullOrWhiteSpace(vaultUrl))
            throw new Exception("KEY_VAULT_URL is missing from configuration. The API key must be retrieved from Key Vault.");

        try
        {
            var credential = new DefaultAzureCredential();
            var client = new SecretClient(new Uri(vaultUrl), credential);

            // Use the synchronous call here (keeps startup behavior deterministic).
            Response<KeyVaultSecret> secretResponse = client.GetSecret("ApiKey");
            _apiKey = secretResponse?.Value?.Value ?? throw new Exception("ApiKey secret in Key Vault is empty.");

            _logger.LogInformation("API Key loaded from Key Vault.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load API key from Key Vault.");
            throw new Exception("Failed to obtain API key from Key Vault. Ensure KEY_VAULT_URL is correct and the identity has access to the secret.", ex);
        }


    }


    private bool IsAuthorized(HttpRequestData req)
    {
        if (!req.Headers.TryGetValues("x-api-key", out var values)) return false;
        return values.FirstOrDefault() == _apiKey;
    }

    private async Task<SqlConnection> GetSqlConnectionAsync()
    {
        var conn = new SqlConnection(_connectionString);


        if (!(_connectionString?.Contains("Password=", StringComparison.OrdinalIgnoreCase) == true
            || _connectionString?.Contains("User ID=", StringComparison.OrdinalIgnoreCase) == true))
        {
            var token = (await new DefaultAzureCredential().GetTokenAsync(
                new Azure.Core.TokenRequestContext(new[] { "https://database.windows.net/.default" })
            )).Token;

            conn.AccessToken = token;
        }

        await conn.OpenAsync();
        return conn;
    }


    [Function("game_api")]
    public async Task<HttpResponseData> Run([HttpTrigger(AuthorizationLevel.Anonymous, "get", "post", "put", "delete", "patch", Route = "game_api/{*rest}")] HttpRequestData req, FunctionContext context)
    {
        try
        {
            _logger.LogInformation("C# HTTP trigger function processed a request.");

            // Check for API key
            if (!IsAuthorized(req))
            {
                var unauthorized = req.CreateResponse(HttpStatusCode.Unauthorized);
                unauthorized.Headers.Add("Content-Type", "application/json");
                await unauthorized.WriteStringAsync(JsonSerializer.Serialize(new { error = "API Key is Invalid or missing" }));
                return unauthorized;
            }

            async Task<HttpResponseData> JsonResponseAsync(HttpRequestData request, object obj, HttpStatusCode status = HttpStatusCode.OK)
            {
                var res = request.CreateResponse(status);
                res.Headers.Add("Content-Type", "application/json");
                await res.WriteStringAsync(JsonSerializer.Serialize(obj));
                return res;
            }

            switch (req.Method?.ToUpperInvariant())
            {
                case "GET":
                    using (var conn = await GetSqlConnectionAsync())
                    {
                        var queryMap = QueryHelpers.ParseQuery(req.Url.Query);
                        var idParam = queryMap.TryGetValue("id", out var idVals) ? idVals.FirstOrDefault() : null;

                        if (!string.IsNullOrEmpty(idParam) && int.TryParse(idParam, out int id))
                        {
                            var cmd = new SqlCommand(
                                "SELECT Id, Title, Developer, Genre, ReleaseYear, IsRetro FROM Games WHERE Id = @Id",
                                conn
                            );
                            cmd.Parameters.AddWithValue("@Id", id);

                            using var reader = await cmd.ExecuteReaderAsync();

                            if (!reader.Read())
                                return await JsonResponseAsync(req, new { error = $"Game with ID {id} not found" }, HttpStatusCode.NotFound);

                            // 1. Extract Release Year first
                            int? releaseYear = reader.IsDBNull(4) ? (int?)null : reader.GetInt32(4);

                            // 2. Determine IsRetro
                            bool? isRetro;

                            if (!reader.IsDBNull(5))
                            {
                                // If DB has a value, use it
                                isRetro = Convert.ToBoolean(reader["IsRetro"]);
                            }
                            else if (releaseYear.HasValue)
                            {
                                // If DB is NULL, calculate it on the fly
                                isRetro = releaseYear.Value < (DateTime.UtcNow.Year - 25);
                            }
                            else
                            {
                                // If no year and no flag, default to false
                                isRetro = false;
                            }

                            var game = new GameModel
                            {
                                Id = reader.GetInt32(0),
                                Title = reader.GetString(1),
                                Developer = reader.IsDBNull(2) ? null : reader.GetString(2),
                                Genre = reader.IsDBNull(3) ? null : reader.GetString(3),
                                ReleaseYear = releaseYear,
                                IsRetro = isRetro // Assign the calculated value
                            };

                            return await JsonResponseAsync(req, game);
                        }

                        var listCmd = new SqlCommand(
                            "SELECT Id, Title, Developer, Genre, ReleaseYear, IsRetro FROM Games",
                            conn
                        );

                        var listReader = await listCmd.ExecuteReaderAsync();
                        var games = new List<GameModel>();

                        int cutoff = DateTime.UtcNow.Year - 25;
                        while (await listReader.ReadAsync())
                        {
                            int? releaseYear = listReader.IsDBNull(4) ? (int?)null : listReader.GetInt32(4);
                            bool isRetro;
                            if (!listReader.IsDBNull(5))
                            {
                                isRetro = Convert.ToBoolean(listReader["IsRetro"]);
                            }
                            else if (releaseYear.HasValue)
                            {
                                // If IsRetro is null, compute based on cutoff
                                isRetro = releaseYear.Value < cutoff ? true : false;
                            }
                            else
                            {
                                isRetro = false;
                            }
                            games.Add(new GameModel
                            {
                                Id = listReader.GetInt32(0),
                                Title = listReader.GetString(1),
                                Developer = listReader.IsDBNull(2) ? null : listReader.GetString(2),
                                Genre = listReader.IsDBNull(3) ? null : listReader.GetString(3),
                                ReleaseYear = releaseYear,
                                IsRetro = isRetro
                            });
                        }

                        return await JsonResponseAsync(req, games);
                    }

                case "POST":
                    {
                        using var reader = new StreamReader(req.Body);
                        var body = await reader.ReadToEndAsync();

                        var newGame = JsonSerializer.Deserialize<GameModel>(body);
                        if (newGame == null)
                            return await JsonResponseAsync(req, new { error = "Invalid JSON data." }, HttpStatusCode.BadRequest);

                        if (string.IsNullOrWhiteSpace(newGame.Title))
                            return await JsonResponseAsync(req, new { error = "Title is required." }, HttpStatusCode.BadRequest);

                        try
                        {
                            await using var conn = await GetSqlConnectionAsync();


                            string idCalcSql = @"
                                SELECT ISNULL(
                                (SELECT MIN(t.RN)
                                    FROM (
                                    SELECT ROW_NUMBER() OVER (ORDER BY Id) RN, Id
                                    FROM Games
                                    ) t
                                    WHERE t.Id <> t.RN
                                ), (SELECT ISNULL(MAX(Id),0)+1 FROM Games)
                                )";

                            using var idCmd = new SqlCommand(idCalcSql, conn);
                            var idResult = await idCmd.ExecuteScalarAsync();
                            int newId = Convert.ToInt32(idResult);


                            string insertSql = @"
                                SET IDENTITY_INSERT Games ON;
                                INSERT INTO Games (Id, Title, Developer, Genre, ReleaseYear)
                                VALUES (@Id, @Title, @Developer, @Genre, @ReleaseYear);
                                SET IDENTITY_INSERT Games OFF;
                                ";

                            using var insertCmd = new SqlCommand(insertSql, conn);
                            insertCmd.Parameters.AddWithValue("@Id", newId);
                            insertCmd.Parameters.AddWithValue("@Title", newGame.Title);
                            insertCmd.Parameters.AddWithValue("@Developer", newGame.Developer ?? (object)DBNull.Value);
                            insertCmd.Parameters.AddWithValue("@Genre", newGame.Genre ?? (object)DBNull.Value);
                            insertCmd.Parameters.AddWithValue("@ReleaseYear", newGame.ReleaseYear ?? (object)DBNull.Value);

                            await insertCmd.ExecuteNonQueryAsync();

                            newGame.Id = newId;

                            return await JsonResponseAsync(req, newGame);
                        }
                        catch (SqlException sqlEx)
                        {
                            _logger.LogError(sqlEx, "SQL insert failed.");
                            return await JsonResponseAsync(req, new { error = sqlEx.Message }, HttpStatusCode.InternalServerError);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Insert failed.");
                            return await JsonResponseAsync(req, new { error = ex.Message }, HttpStatusCode.InternalServerError);
                        }
                    }
                case "PUT":
                    {
                        using var reader = new StreamReader(req.Body);
                        var body = await reader.ReadToEndAsync();
                        var incomingGame = JsonSerializer.Deserialize<GameModel>(body);

                        if (incomingGame == null || incomingGame.Id == 0)
                            return await JsonResponseAsync(req, new { error = "Missing game Id" }, HttpStatusCode.BadRequest);

                        using var conn = await GetSqlConnectionAsync();


                        // fetch the current values so we can merge them in C#
                        var getCmd = new SqlCommand("SELECT Title, Developer, Genre, ReleaseYear, IsRetro FROM Games WHERE Id = @Id", conn);
                        getCmd.Parameters.AddWithValue("@Id", incomingGame.Id);

                        // Variables to hold current DB state
                        string currentTitle;
                        string? currentDev, currentGenre;
                        int? currentYear;
                        bool? currentRetro;

                        using (var dbReader = await getCmd.ExecuteReaderAsync())
                        {
                            if (!dbReader.Read())
                                return await JsonResponseAsync(req, new { error = $"Game with ID {incomingGame.Id} not found" }, HttpStatusCode.NotFound);

                            currentTitle = dbReader.GetString(0);
                            currentDev = dbReader.IsDBNull(1) ? null : dbReader.GetString(1);
                            currentGenre = dbReader.IsDBNull(2) ? null : dbReader.GetString(2);
                            currentYear = dbReader.IsDBNull(3) ? (int?)null : dbReader.GetInt32(3);
                            currentRetro = dbReader.IsDBNull(4) ? (bool?)null : Convert.ToBoolean(dbReader["IsRetro"]);
                        }

                        var finalTitle = incomingGame.Title ?? currentTitle;
                        var finalDev = incomingGame.Developer ?? currentDev;
                        var finalGenre = incomingGame.Genre ?? currentGenre;
                        var finalYear = incomingGame.ReleaseYear ?? currentYear;
                        var finalRetro = incomingGame.IsRetro ?? currentRetro;

                        var updateCmd = new SqlCommand(
                            @"UPDATE Games 
                            SET Title=@Title, Developer=@Dev, Genre=@Genre, ReleaseYear=@Year, IsRetro=@Retro
                            WHERE Id=@Id", conn);

                        updateCmd.Parameters.AddWithValue("@Id", incomingGame.Id);

                        updateCmd.Parameters.AddWithValue("@Title", (object?)finalTitle ?? DBNull.Value);
                        updateCmd.Parameters.AddWithValue("@Dev", (object?)finalDev ?? DBNull.Value);
                        updateCmd.Parameters.AddWithValue("@Genre", (object?)finalGenre ?? DBNull.Value);
                        updateCmd.Parameters.AddWithValue("@Year", (object?)finalYear ?? DBNull.Value);
                        updateCmd.Parameters.AddWithValue("@Retro", (object?)finalRetro ?? DBNull.Value);

                        await updateCmd.ExecuteNonQueryAsync();

                        // UPDATE RESPONSE OBJECT
                        incomingGame.Title = finalTitle ?? string.Empty; ;
                        incomingGame.Developer = finalDev;
                        incomingGame.Genre = finalGenre;
                        incomingGame.ReleaseYear = finalYear;
                        incomingGame.IsRetro = finalRetro;

                        return await JsonResponseAsync(req, incomingGame);
                    }
                case "DELETE":
                    {
                        var queryMap = QueryHelpers.ParseQuery(req.Url.Query);
                        var idParam = queryMap.TryGetValue("id", out var idVals) ? idVals.FirstOrDefault() : null;

                        if (string.IsNullOrEmpty(idParam) || !int.TryParse(idParam, out int deleteId))
                            return await JsonResponseAsync(req, new { error = "Missing or invalid id" }, HttpStatusCode.BadRequest);

                        using (var conn = await GetSqlConnectionAsync())
                        {
                            var cmd = new SqlCommand("DELETE FROM Games WHERE Id = @Id", conn);
                            cmd.Parameters.AddWithValue("@Id", deleteId);

                            int rows = await cmd.ExecuteNonQueryAsync();

                            if (rows == 0)
                                return await JsonResponseAsync(req, new { error = $"No game with ID {deleteId}" }, HttpStatusCode.NotFound);

                            return await JsonResponseAsync(req, new { message = $"Deleted game with ID {deleteId}" });
                        }
                    }
                case "PATCH":
                    {
                        // set IsRetro = 1 for games older than 25 years
                        var path = (req.Url.AbsolutePath ?? string.Empty).TrimEnd('/').ToLowerInvariant();
                        if (!path.EndsWith("/validate"))
                        {
                            return await JsonResponseAsync(req, new { error = "Invalid PATCH endpoint. Use /api/game_api/validate" }, HttpStatusCode.BadRequest);
                        }

                        try
                        {
                            using var conn = await GetSqlConnectionAsync();
                            int cutoff = DateTime.UtcNow.Year - 25;

                            string sql = @"
                                UPDATE Games
                                SET IsRetro = 1
                                WHERE ReleaseYear IS NOT NULL
                                  AND ReleaseYear < @Cutoff
                                  AND (IsRetro = 0 OR IsRetro IS NULL);
                            ";

                            using var cmd = new SqlCommand(sql, conn);
                            cmd.Parameters.AddWithValue("@Cutoff", cutoff);
                            int updatedCount = await cmd.ExecuteNonQueryAsync();

                            var eventProps = new Dictionary<string, string>
                            {
                                { "TriggerSource", "API" },
                                { "ValidationType", "RetroArchival" }
                            };

                            var eventMetrics = new Dictionary<string, double>
                            {
                                { "RecordsUpdated", updatedCount }
                            };

                            _telemetryClient.TrackEvent("ValidationTriggered", eventProps, eventMetrics);

                            var response = new
                            {
                                updatedCount,
                                timestamp = DateTime.UtcNow.ToString("o")
                            };
                            _logger.LogInformation("PATCH validate updated {Updated} rows with cutoff {Cutoff}.", updatedCount, cutoff);
                            return await JsonResponseAsync(req, response);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "PATCH validate failed.");
                            return await JsonResponseAsync(req, new { error = ex.Message }, HttpStatusCode.InternalServerError);
                        }
                    }


                default:
                    return await JsonResponseAsync(req, new { error = "Use GET, POST, PUT, or DELETE" }, HttpStatusCode.BadRequest);
            }
        }
        catch (Exception ex)
        {
            // Log full exception message and stack trace
            _logger.LogError(ex, "Unhandled exception in Run: {Message} {StackTrace}", ex.Message, ex.StackTrace);

            var res = req.CreateResponse(HttpStatusCode.InternalServerError);
            res.Headers.Add("Content-Type", "application/json");
            await res.WriteStringAsync(JsonSerializer.Serialize(new
            {
                error = "Internal Server Error",
                detail = ex.Message
            }));
            return res;
        }

    }
}