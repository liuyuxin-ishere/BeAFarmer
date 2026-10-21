namespace CloudIsle.Bot;

public sealed class BotOptions
{
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 17777;
    public int Count { get; set; } = 100;
    public int DurationSec { get; set; } = 600;
    public int ConnectRate { get; set; } = 20;
    public double MoveHz { get; set; } = 5;
    public int MapSizeCm { get; set; } = 20000;
    public int Seed { get; set; } = 42;
    public bool EnableChat { get; set; }
    public bool EnableEmote { get; set; }
    public string ReportPath { get; set; } = "";
    public string Password { get; set; } = "bot-pass";
}
