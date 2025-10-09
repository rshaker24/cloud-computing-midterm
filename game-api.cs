using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using Microsoft.Extensions.Configuration;


namespace Company.Function;

public class Game_api
{
    private readonly ILogger<Game_api> _logger;

    private static readonly List<GameModel> gamesList = new();
    private readonly string _apiKey;


    public Game_api(ILogger<Game_api> logger, IConfiguration config)
    {
        _logger = logger;
        _apiKey = config["API_KEY"] ?? throw new Exception("API_KEY is missing");
    }


    private bool IsAuthorized(HttpRequest req)
    {
        if (!req.Headers.TryGetValue("game_api_key", out var key)) return false;
        return key == _apiKey;
    }


    [Function("game_api")]
    public async Task<IActionResult> Run([HttpTrigger(AuthorizationLevel.Anonymous, "get", "post", "put", "delete")] HttpRequest req)
    {
        _logger.LogInformation("C# HTTP trigger function processed a request.");

        // Check for API key
        if (!IsAuthorized(req))
        {
            return new ObjectResult(new { error = "API Key is Invalid or missing" })
            {
                StatusCode = StatusCodes.Status401Unauthorized
            };
        }

        switch (req.Method)
        {

            case "GET": //get list of all games
                if (req.Query.TryGetValue("id", out var idValues) && int.TryParse(idValues.First(), out var id))
                {
                    var game = gamesList.FirstOrDefault(g => g.Id == id);
                    if (game == null)
                        return new NotFoundObjectResult($"Game with ID \"{id}\" not found.");

                    return new OkObjectResult(game);
                }
                return new OkObjectResult(gamesList);


            case "POST": //create a new game
                {
                    //Read JSON data
                    using var reader = new StreamReader(req.Body);
                    var body = await reader.ReadToEndAsync();

                    var newGame = JsonSerializer.Deserialize<GameModel>(body);
                    if (newGame == null)
                        return new BadRequestObjectResult("Invalid game data");

                    if (string.IsNullOrWhiteSpace(newGame.Title))
                        return new BadRequestObjectResult("Title is required.");

                    // Give a unique ID to the new game 
                    int newId = 1;
                    while (gamesList.Any(g => g.Id == newId))
                        newId++;
                    newGame.Id = newId;

                    gamesList.Add(newGame);

                    return new OkObjectResult(newGame);

                }
            case "PUT":
                {
                    //gather updated game data
                    using var reader = new StreamReader(req.Body);
                    var body = await reader.ReadToEndAsync();

                    var updatedGame = JsonSerializer.Deserialize<GameModel>(body);
                    if (updatedGame == null)
                        return new BadRequestObjectResult("Invalid game id");

                    // Find the game to update
                    id = updatedGame.Id;
                    if (id == 0 && int.TryParse(req.Query["id"], out var queryId))
                        id = queryId;

                    if (id == 0)
                        return new BadRequestObjectResult("Missing game ID in JSON or query string.");

                    var existingGame = gamesList.FirstOrDefault(g => g.Id == id);
                    if (existingGame == null)
                        return new NotFoundObjectResult($"Game with id \"{id}\" not found");

                    // Update fields if they are provided in the request
                    if (!string.IsNullOrEmpty(updatedGame.Title))
                        existingGame.Title = updatedGame.Title;

                    if (!string.IsNullOrEmpty(updatedGame.Developer))
                        existingGame.Developer = updatedGame.Developer;

                    if (!string.IsNullOrEmpty(updatedGame.Genre))
                        existingGame.Genre = updatedGame.Genre;

                    if (updatedGame.ReleaseYear.HasValue)
                        existingGame.ReleaseYear = updatedGame.ReleaseYear.Value;

                    return new OkObjectResult(existingGame);
                }
            case "DELETE": //delete by providing an id
                {
                    //get id from query string
                    if (!req.Query.TryGetValue("id", out idValues) || !int.TryParse(idValues.First(), out id))
                    {
                        return new BadRequestObjectResult("Invalid or missing id");
                    }
                    var gameToDelete = gamesList.FirstOrDefault(g => g.Id == id);

                    if (gameToDelete == null)
                        return new NotFoundObjectResult($"Game with id \"{id}\" not found");

                    gamesList.Remove(gameToDelete);


                    return new OkObjectResult($"Game with id \"{id}\" deleted");
                }
        }
        return new OkObjectResult("Please use GET, POST, PUT or DELETE methods");
    }
}