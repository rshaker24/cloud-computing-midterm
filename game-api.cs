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


namespace Company.Function;

public class Game_api
{
    private readonly ILogger<Game_api> _logger;
    private readonly string _connectionString;
    private readonly string _apiKey;




    public Game_api(ILogger<Game_api> logger, IConfiguration config)
    {
        _logger = logger;

        _connectionString = Environment.GetEnvironmentVariable("SqlConnectionString")
            ?? throw new Exception("SQL Connection string missing from configuration.");

        // Always load API key from Key Vault. No local env fallback per project requirement.
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

        // If the connection string does not contain user/password, assume AAD auth and set AccessToken.
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
    public async Task<HttpResponseData> Run([HttpTrigger(AuthorizationLevel.Anonymous, "get", "post", "put", "delete")] HttpRequestData req, FunctionContext context)
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
                                "SELECT Id, Title, Developer, Genre, ReleaseYear FROM Games WHERE Id = @Id",
                                conn
                            );
                            cmd.Parameters.AddWithValue("@Id", id);

                            using var reader = await cmd.ExecuteReaderAsync();

                            if (!reader.Read())
                                return await JsonResponseAsync(req, new { error = $"Game with ID {id} not found" }, HttpStatusCode.NotFound);

                            var game = new GameModel
                            {
                                Id = reader.GetInt32(0),
                                Title = reader.GetString(1),
                                Developer = reader.IsDBNull(2) ? null : reader.GetString(2),
                                Genre = reader.IsDBNull(3) ? null : reader.GetString(3),
                                ReleaseYear = reader.IsDBNull(4) ? null : reader.GetInt32(4)
                            };

                            return await JsonResponseAsync(req, game);
                        }

                        var listCmd = new SqlCommand(
                            "SELECT Id, Title, Developer, Genre, ReleaseYear FROM Games",
                            conn
                        );

                        var listReader = await listCmd.ExecuteReaderAsync();
                        var games = new List<GameModel>();

                        while (await listReader.ReadAsync())
                        {
                            games.Add(new GameModel
                            {
                                Id = listReader.GetInt32(0),
                                Title = listReader.GetString(1),
                                Developer = listReader.IsDBNull(2) ? null : listReader.GetString(2),
                                Genre = listReader.IsDBNull(3) ? null : listReader.GetString(3),
                                ReleaseYear = listReader.IsDBNull(4) ? null : listReader.GetInt32(4)
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

                            string sql = @"
                        INSERT INTO Games (Title, Developer, Genre, ReleaseYear)
                        VALUES (@Title, @Developer, @Genre, @ReleaseYear);
                        SELECT SCOPE_IDENTITY();
                    ";

                            await using var cmd = new SqlCommand(sql, conn);

                            cmd.Parameters.AddWithValue("@Title", newGame.Title);
                            cmd.Parameters.AddWithValue("@Developer", newGame.Developer ?? (object)DBNull.Value);
                            cmd.Parameters.AddWithValue("@Genre", newGame.Genre ?? (object)DBNull.Value);
                            cmd.Parameters.AddWithValue("@ReleaseYear", newGame.ReleaseYear ?? (object)DBNull.Value);

                            var result = await cmd.ExecuteScalarAsync();

                            if (result == null || result == DBNull.Value)
                                throw new Exception("Database did not return a new ID.");

                            newGame.Id = Convert.ToInt32(result);

                            return await JsonResponseAsync(req, newGame);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "SQL insert failed.");
                            return await JsonResponseAsync(req, new { error = ex.Message }, HttpStatusCode.InternalServerError);
                        }
                    }

                case "PUT":
                    {
                        using var reader = new StreamReader(req.Body);
                        var body = await reader.ReadToEndAsync();
                        var game = JsonSerializer.Deserialize<GameModel>(body);

                        if (game == null || game.Id == 0)
                            return await JsonResponseAsync(req, new { error = "Missing game Id" }, HttpStatusCode.BadRequest);

                        using var conn = await GetSqlConnectionAsync();

                        var cmd = new SqlCommand(
                            @"UPDATE Games 
                          SET Title=@Title, Developer=@Dev, Genre=@Genre, ReleaseYear=@Year
                          WHERE Id=@Id",
                            conn
                        );

                        cmd.Parameters.AddWithValue("@Id", game.Id);
                        cmd.Parameters.AddWithValue("@Title", game.Title ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@Dev", (object?)game.Developer ?? DBNull.Value);
                        cmd.Parameters.AddWithValue("@Genre", (object?)game.Genre ?? DBNull.Value);
                        cmd.Parameters.AddWithValue("@Year", (object?)game.ReleaseYear ?? DBNull.Value);

                        int rows = await cmd.ExecuteNonQueryAsync();

                        if (rows == 0)
                            return await JsonResponseAsync(req, new { error = $"Game with ID {game.Id} not found" }, HttpStatusCode.NotFound);

                        return await JsonResponseAsync(req, game);
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