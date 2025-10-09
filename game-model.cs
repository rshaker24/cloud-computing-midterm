public class GameModel
{
    public int Id { get; set; } //Unique Identifier for each game
    public string Title { get; set; } = string.Empty;
    public string? Developer { get; set; }
    public string? Genre { get; set; }
    public int? ReleaseYear { get; set; }

}
