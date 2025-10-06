using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace Company.Function;

public class game_api
{
    private readonly ILogger<game_api> _logger;

    private static readonly List<GameModel> _games = new();
    private static int _nextId = 1;
    private const string ApiKey = "api-key"; // TODO: move to config

    public game_api(ILogger<game_api> logger)
    {
        _logger = logger;
    }

    
    private bool IsAuthorized(HttpRequest req)
    {
        if (!req.Headers.TryGetValue("x-api-key", out var key)) return false;
        return key == ApiKey;
    }
    

    [Function("game_api")]
    public async Task<IActionResult> Run([HttpTrigger(AuthorizationLevel.Anonymous, "get", "post", "put", "delete")] HttpRequest req)
    {
        _logger.LogInformation("C# HTTP trigger function processed a request.");

        
        if (!IsAuthorized(req))
        {
            return new UnauthorizedResult();
        }

        switch (req.Method)
        {

            case "GET": //get list of all games
                return new OkObjectResult(_games);

            case "POST": //create a new game
                {
                    using var reader = new StreamReader(req.Body);
                    var body = await reader.ReadToEndAsync();

                    var newGame = JsonSerializer.Deserialize<GameModel>(body);
                    if (newGame == null)
                        return new BadRequestObjectResult("Invalid game data");
                    newGame.Id = _nextId++;
                    _games.Add(newGame);

                    return new OkObjectResult(newGame);

                }
            case "PUT":
                {
                    //gather new game data
                    //replace existing game data
                    using var reader = new StreamReader(req.Body);
                    var body = await reader.ReadToEndAsync();

                    var updatedGame = JsonSerializer.Deserialize<GameModel>(body);
                    if (updatedGame == null)
                        return new BadRequestObjectResult("Invalid game id");

                    int id = updatedGame.Id;
                    if (id == 0 && int.TryParse(req.Query["id"], out var queryId))
                        id = queryId;

                    var existingGame = _games.FirstOrDefault(g => g.Id == updatedGame.Id);

                    if (existingGame == null)
                        return new NotFoundObjectResult("Game could not be found");

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
                    if (!req.Query.TryGetValue("id", out var idValues) || !int.TryParse(idValues.First(), out var id))
                    {
                        return new BadRequestObjectResult("Missing or invalid id");
                    }
                    var gameToDelete = _games.FirstOrDefault(g => g.Id == id);

                    if (gameToDelete == null)
                        return new NotFoundObjectResult($"Game with id \"{id}\" not found");

                    _games.Remove(gameToDelete);


                    return new OkObjectResult($"Game with id {id} deleted");
                }
        }

        return new OkObjectResult("Welcome to Game API!");
    }
}