public sealed class QBitTorrentOptions
{
    public string Url { get; set; } = string.Empty;

    public string Username { get; set; } = string.Empty;

    public string Password { get; set; } = string.Empty;

    public string ExecutablePath { get; set; } = string.Empty;

    public int SearchPollMs { get; set; } = 1000;

    public int MonitorPollSeconds { get; set; } = 10;
}
