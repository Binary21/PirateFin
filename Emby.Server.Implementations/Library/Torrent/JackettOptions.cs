using System;
using System.Collections.Generic;

public sealed class JackettOptions
{
    public string BaseUrl { get; set; } = string.Empty;

    public string ApiKey { get; set; } = string.Empty;

    public Dictionary<string, string> Indexer { get; set; } = new();

    public string AnimeCategory { get; set; } = string.Empty;

    public string ShowCategory { get; set; } = string.Empty;

    public string[] AnimeAuthors { get; set; } = Array.Empty<string>();

    public string[] ShowAuthors { get; set; } = Array.Empty<string>();
}
